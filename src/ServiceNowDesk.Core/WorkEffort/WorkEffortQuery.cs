using System.Globalization;
using ServiceNowDesk.Query;

namespace ServiceNowDesk.WorkEffort;

public sealed record WorkEffortTablePlan(
    string Table,
    WorkEffortKind Kind,
    string Fields,
    bool OpenedBy,
    bool OpenedFor,
    bool Resolved,
    bool Closed)
{
    public static WorkEffortTablePlan Incident { get; } = Plan(
        "incident",
        WorkEffortKind.Incident,
        openedBy: true,
        openedFor: false,
        resolved: true,
        closed: true);

    public static IReadOnlyList<WorkEffortTablePlan> RequestedItemAttempts { get; } =
    [
        Plan("sc_req_item", WorkEffortKind.RequestedItem, openedBy: true, openedFor: false, resolved: true, closed: true),
        Plan("sc_req_item", WorkEffortKind.RequestedItem, openedBy: true, openedFor: false, resolved: false, closed: true)
    ];

    /// <summary>
    /// IMS is the walk-up interaction table. <c>opened_by</c> is preferred. <c>opened_for</c> is the
    /// opener field this app already stores when <c>opened_by</c> is absent. Resolve fields are tried
    /// first; a close is the completion when the table has no <c>resolved_by</c>.
    /// </summary>
    public static IReadOnlyList<WorkEffortTablePlan> InteractionAttempts { get; } =
    [
        Plan("interaction", WorkEffortKind.Interaction, openedBy: true, openedFor: true, resolved: true, closed: true),
        Plan("interaction", WorkEffortKind.Interaction, openedBy: true, openedFor: true, resolved: false, closed: true),
        Plan("interaction", WorkEffortKind.Interaction, openedBy: false, openedFor: true, resolved: false, closed: true)
    ];

    public static IReadOnlyList<WorkEffortTablePlan> IncidentPlans { get; } = [Incident];

    private static WorkEffortTablePlan Plan(string table, WorkEffortKind kind, bool openedBy, bool openedFor, bool resolved, bool closed)
    {
        var fields = new List<string> { "sys_id" };
        if (openedBy)
            fields.Add("opened_by");
        if (openedFor)
            fields.Add("opened_for");
        fields.Add("opened_at");
        if (resolved)
        {
            fields.Add("resolved_by");
            fields.Add("resolved_at");
        }

        if (closed)
        {
            fields.Add("closed_by");
            fields.Add("closed_at");
        }

        fields.Add("sys_updated_by");
        fields.Add("sys_updated_on");
        return new WorkEffortTablePlan(table, kind, string.Join(",", fields), openedBy, openedFor, resolved, closed);
    }
}

public static class WorkEffortQuery
{
    public const int SafetyCap = 8000;
    public const int ChunkSize = 40;
    /// <summary>Small pages keep each JSON body off the large-object heap.</summary>
    public const int PageSize = 30;
    public const string CapNotice = "These figures are partial. The safety cap was reached.";
    public const string JournalTable = "sys_journal_field";
    public const string AuditTable = "sys_audit";
    public const string JournalFields = "sys_id,element_id,element,name,sys_created_by,sys_created_on";
    public const string AuditFields = "sys_id,documentkey,tablename,user,sys_created_on";
    public const string HistoryNotice =
        "Update history could not be read, so only the latest update on each record is counted.";

    public static IEnumerable<IReadOnlyList<WorkEffortPerson>> Chunks(IReadOnlyList<WorkEffortPerson> people)
    {
        ArgumentNullException.ThrowIfNull(people);
        if (people.Count == 0)
            yield break;
        for (var index = 0; index < people.Count; index += ChunkSize)
            yield return people.Skip(index).Take(ChunkSize).ToArray();
    }

    public static string Clause(WorkEffortTablePlan plan, IReadOnlyList<WorkEffortPerson> people, WorkEffortWindow window)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(people);
        var ids = people
            .Select(person => SafeId(person.SysId))
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var names = people
            .Select(person => SafeName(person.UserName))
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var start = Stamp(window.Start);
        var end = Stamp(window.End);
        var parts = new List<string>();
        if (ids.Length > 0 && plan.OpenedBy)
            parts.Add(In("opened_by", ids) + "^" + Between("opened_at", start, end));
        if (ids.Length > 0 && plan.OpenedFor)
        {
            var openedFor = In("opened_for", ids) + "^" + Between("opened_at", start, end);
            if (plan.OpenedBy)
                openedFor = "opened_byISEMPTY^" + openedFor;
            parts.Add(openedFor);
        }

        if (ids.Length > 0 && plan.Resolved)
            parts.Add(In("resolved_by", ids) + "^" + Between("resolved_at", start, end));
        if (ids.Length > 0 && plan.Closed)
            parts.Add(In("closed_by", ids) + "^" + Between("closed_at", start, end));
        if (names.Length > 0)
            parts.Add(In("sys_updated_by", names) + "^" + Between("sys_updated_on", start, end));
        if (parts.Count == 0)
            return "sys_id=NO_WORK_EFFORT^ORDERBYsys_id";
        return string.Join("^NQ", parts) + "^ORDERBYsys_id";
    }

    /// <summary>
    /// Work notes and comments in the window, by the team's user_name or sys_id.
    /// The journal name is often task, so the caller still has to place each element_id.
    /// </summary>
    public static string JournalClause(IReadOnlyList<WorkEffortPerson> people, WorkEffortWindow window)
    {
        ArgumentNullException.ThrowIfNull(people);
        var authors = Authors(people);
        if (authors.Length == 0)
            return "sys_id=NO_WORK_EFFORT^ORDERBYsys_id";
        var start = Stamp(window.Start);
        var end = Stamp(window.End);
        return "elementINcomments,additional_comments,work_notes^"
            + In("sys_created_by", authors)
            + "^"
            + Between("sys_created_on", start, end)
            + "^ORDERBYsys_id";
    }

    /// <summary>
    /// Field history for incidents, request items, and walk-ups. Rows that share a second are one moment.
    /// </summary>
    public static string AuditClause(IReadOnlyList<WorkEffortPerson> people, WorkEffortWindow window)
    {
        ArgumentNullException.ThrowIfNull(people);
        var authors = Authors(people);
        if (authors.Length == 0)
            return "sys_id=NO_WORK_EFFORT^ORDERBYsys_id";
        var start = Stamp(window.Start);
        var end = Stamp(window.End);
        return "tablenameINincident,sc_req_item,interaction^"
            + In("user", authors)
            + "^"
            + Between("sys_created_on", start, end)
            + "^ORDERBYsys_id";
    }

    public static string IdClause(IReadOnlyList<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var safe = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            var token = SafeId(id);
            if (token.Length == 0 || !seen.Add(token))
                continue;
            safe.Add(token);
        }

        if (safe.Count == 0)
            return "sys_id=NO_WORK_EFFORT^ORDERBYsys_id";
        return In("sys_id", safe) + "^ORDERBYsys_id";
    }

    public static bool IsUnscoped(string? clause) =>
        (clause ?? "").StartsWith("sys_id=NO_WORK_EFFORT", StringComparison.Ordinal);

    public static bool HasScope(IReadOnlyList<WorkEffortPerson> people)
    {
        ArgumentNullException.ThrowIfNull(people);
        foreach (var person in people)
        {
            if (SafeId(person.SysId).Length > 0 || SafeName(person.UserName).Length > 0)
                return true;
        }

        return false;
    }

    public static string Status(WorkEffortScale scale, bool truncated, IEnumerable<string>? problems)
    {
        var parts = new List<string> { WorkEffortWindow.CountsLabel(scale) };
        if (truncated)
            parts.Add(CapNotice);
        foreach (var problem in problems ?? [])
        {
            if (!string.IsNullOrWhiteSpace(problem))
                parts.Add(problem.Trim());
        }

        return string.Join(" ", parts);
    }

    private static string Between(string field, string start, string end) =>
        field + ">=" + start + "^" + field + "<=" + end;

    private static string Stamp(DateTime moment) =>
        moment.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        + "@"
        + moment.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    private static string In(string field, IReadOnlyList<string> values) =>
        field + "IN" + string.Join(",", values);

    private static string SafeId(string? value)
    {
        var trimmed = (value ?? "").Trim();
        if (trimmed.Length == 0)
            return "";
        try
        {
            return EncodedQuery.SafeToken(trimmed, "user id");
        }
        catch (InvalidOperationException)
        {
            return "";
        }
    }

    private static string[] Authors(IReadOnlyList<WorkEffortPerson> people)
    {
        var values = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var person in people)
        {
            var name = SafeName(person.UserName);
            if (name.Length > 0 && seen.Add(name))
                values.Add(name);
            var id = SafeId(person.SysId);
            if (id.Length > 0 && seen.Add(id))
                values.Add(id);
        }

        return values.ToArray();
    }

    private static string SafeName(string? value)
    {
        var cleaned = EncodedQuery.Sanitize(value);
        if (cleaned.Length == 0 || cleaned.IndexOf(',') >= 0)
            return "";
        return cleaned;
    }
}
