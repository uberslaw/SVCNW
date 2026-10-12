using System.Globalization;
using ServiceNowDesk.Client;
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
        var fields = new List<string> { "sys_id", "number", "short_description" };
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
    /// <summary>
    /// List rows read for one person, in one office, in one download step.
    /// The team is fetched one person at a time and combined.
    /// </summary>
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

    /// <summary>
    /// One opener search for this person. Resolve, close, and update fields still come back
    /// on the row. Tickets the person only saved are not searched.
    /// </summary>
    public static IReadOnlyList<string> OpenedBranches(WorkEffortTablePlan plan, WorkEffortPerson person, WorkEffortWindow window)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(person);
        var id = SafeId(person.SysId);
        if (id.Length == 0)
            return [];

        var openedAt = Between("opened_at", Stamp(window.Start), Stamp(window.End));
        var branches = new List<string>();
        if (plan.OpenedBy)
            branches.Add(In("opened_by", [id]) + "^" + openedAt + "^ORDERBYsys_id");
        if (plan.OpenedFor)
        {
            var openedFor = In("opened_for", [id]) + "^" + openedAt;
            if (plan.OpenedBy)
                openedFor = "opened_byISEMPTY^" + openedFor;
            branches.Add(openedFor + "^ORDERBYsys_id");
        }

        return branches;
    }

    /// <summary>
    /// The opener search limited to one region office. Stem and "Office" share one request.
    /// One office per call: a long chain of offices comes back empty from the Table API.
    /// </summary>
    public static IReadOnlyList<string> OpenedInOffice(
        WorkEffortTablePlan plan,
        WorkEffortPerson person,
        WorkEffortWindow window,
        string office)
    {
        var queries = new List<string>();
        foreach (var branch in OpenedBranches(plan, person, window))
        {
            foreach (var query in OfficeQueue.Queries(branch, [office]))
            {
                if (!IsUnscoped(query) && !query.Contains("NO_OFFICE", StringComparison.Ordinal))
                    queries.Add(query);
            }
        }

        return queries;
    }

    /// <summary>
    /// Notes on the tickets opened in the region. Not every note this person wrote.
    /// </summary>
    public static string JournalForRecords(IReadOnlyList<string> recordIds, WorkEffortWindow window)
    {
        var ids = SafeIds(recordIds);
        if (ids.Count == 0)
            return "sys_id=NO_WORK_EFFORT^ORDERBYsys_id";
        return "elementINcomments,additional_comments,work_notes^"
            + In("element_id", ids)
            + "^"
            + Between("sys_created_on", Stamp(window.Start), Stamp(window.End))
            + "^ORDERBYsys_id";
    }

    /// <summary>
    /// Field history for the tickets opened in the region. Rows that share a second are one moment.
    /// </summary>
    public static string AuditForRecords(IReadOnlyList<string> recordIds, WorkEffortWindow window)
    {
        var ids = SafeIds(recordIds);
        if (ids.Count == 0)
            return "sys_id=NO_WORK_EFFORT^ORDERBYsys_id";
        return "tablenameINincident,sc_req_item,interaction^"
            + In("documentkey", ids)
            + "^"
            + Between("sys_created_on", Stamp(window.Start), Stamp(window.End))
            + "^ORDERBYsys_id";
    }

    public static string IdClause(IReadOnlyList<string> ids)
    {
        var safe = SafeIds(ids);
        if (safe.Count == 0)
            return "sys_id=NO_WORK_EFFORT^ORDERBYsys_id";
        return In("sys_id", safe) + "^ORDERBYsys_id";
    }

    private static IReadOnlyList<string> SafeIds(IReadOnlyList<string>? ids)
    {
        var safe = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids ?? [])
        {
            var token = SafeId(id);
            if (token.Length == 0 || !seen.Add(token))
                continue;
            safe.Add(token);
        }

        return safe;
    }

    public static bool IsUnscoped(string? clause) =>
        (clause ?? "").StartsWith("sys_id=NO_WORK_EFFORT", StringComparison.Ordinal);

    public static bool HasScope(IReadOnlyList<WorkEffortPerson> people)
    {
        ArgumentNullException.ThrowIfNull(people);
        foreach (var person in people)
        {
            if (SafeId(person.SysId).Length > 0)
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

}
