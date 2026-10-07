using System.Globalization;

namespace ServiceNowDesk.WorkEffort;

public enum WorkEffortKind
{
    Incident,
    RequestedItem,
    Interaction
}

/// <summary>
/// Daily counts one update per person, per ticket, per local calendar day.
/// Multiple counts each distinct update moment, including several on the same day.
/// </summary>
public enum WorkEffortUpdateMode
{
    Daily,
    Multiple
}

/// <summary>
/// One update moment on a record, besides the header fields. The author is a user_name or a sys_id.
/// </summary>
public sealed record WorkEffortUpdate(string By, DateTime At);

public sealed record WorkEffortPerson(string SysId, string Name, string UserName)
{
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Name))
                return Name.Trim();
            if (!string.IsNullOrWhiteSpace(UserName))
                return UserName.Trim();
            return (SysId ?? "").Trim();
        }
    }
}

public readonly record struct WorkEffortMembership(string GroupSysId, WorkEffortPerson Person);

/// <summary>
/// One record's opener, resolver, closer, and update history. Dates are local wall times.
/// <see cref="UpdatedBy"/> and <see cref="UpdatedAt"/> are the header's latest update.
/// <see cref="Updates"/> holds earlier journal, audit, and header moments that must not be dropped.
/// </summary>
public sealed record WorkEffortTouch(
    string SysId,
    WorkEffortKind Kind,
    string? OpenedBySysId,
    DateTime? OpenedAt,
    string? ResolvedBySysId,
    DateTime? ResolvedAt,
    string? ClosedBySysId,
    DateTime? ClosedAt,
    string? UpdatedBy,
    DateTime? UpdatedAt,
    IReadOnlyList<WorkEffortUpdate>? Updates = null)
{
    public WorkEffortTouch Combine(WorkEffortTouch other)
    {
        var keepHeader = HasHeader(UpdatedBy, UpdatedAt);
        var otherHeader = HasHeader(other.UpdatedBy, other.UpdatedAt);
        List<WorkEffortUpdate>? updates = null;
        if (Updates is { Count: > 0 } || other.Updates is { Count: > 0 } || (keepHeader && otherHeader))
        {
            updates = new List<WorkEffortUpdate>();
            if (Updates is not null)
                updates.AddRange(Updates);
            if (other.Updates is not null)
                updates.AddRange(other.Updates);
            if (keepHeader && otherHeader)
                updates.Add(new WorkEffortUpdate(other.UpdatedBy!, other.UpdatedAt!.Value));
        }

        return this with
        {
            OpenedBySysId = First(OpenedBySysId, other.OpenedBySysId),
            OpenedAt = OpenedAt ?? other.OpenedAt,
            ResolvedBySysId = First(ResolvedBySysId, other.ResolvedBySysId),
            ResolvedAt = ResolvedAt ?? other.ResolvedAt,
            ClosedBySysId = First(ClosedBySysId, other.ClosedBySysId),
            ClosedAt = ClosedAt ?? other.ClosedAt,
            UpdatedBy = First(UpdatedBy, other.UpdatedBy),
            UpdatedAt = UpdatedAt ?? other.UpdatedAt,
            Updates = updates
        };
    }

    private static bool HasHeader(string? by, DateTime? at) =>
        at is not null && !string.IsNullOrWhiteSpace(by);

    private static string? First(string? left, string? right) =>
        string.IsNullOrWhiteSpace(left) ? right : left;
}

/// <summary>
/// The loaded touches for one scale. The daily and multiple figures are scored from this
/// without another ServiceNow read.
/// </summary>
public sealed class WorkEffortLedger
{
    public WorkEffortLedger(
        IReadOnlyList<WorkEffortPerson> people,
        IReadOnlyList<WorkEffortTouch> touches,
        WorkEffortWindow window)
    {
        ArgumentNullException.ThrowIfNull(people);
        ArgumentNullException.ThrowIfNull(touches);
        People = people;
        Touches = touches;
        Window = window;
    }

    public IReadOnlyList<WorkEffortPerson> People { get; }

    public IReadOnlyList<WorkEffortTouch> Touches { get; }

    public WorkEffortWindow Window { get; }
}

/// <summary>
/// Merges header rows and later update events for one Work Effort load.
/// </summary>
public sealed class WorkEffortBatch
{
    private readonly Dictionary<string, WorkEffortTouch> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WorkEffortKind> _kindById = new(StringComparer.OrdinalIgnoreCase);

    public void Add(WorkEffortTouch touch)
    {
        var id = (touch.SysId ?? "").Trim();
        if (id.Length == 0)
            return;
        var stored = touch with { SysId = id };
        var key = (int)stored.Kind + "\n" + id;
        if (_rows.TryGetValue(key, out var existing))
            _rows[key] = existing.Combine(stored);
        else
            _rows[key] = stored;
        _kindById.TryAdd(id, stored.Kind);
    }

    public void AddUpdate(WorkEffortKind kind, string sysId, string by, DateTime at)
    {
        var id = (sysId ?? "").Trim();
        var author = (by ?? "").Trim();
        if (id.Length == 0 || author.Length == 0)
            return;
        if (_kindById.TryGetValue(id, out var known))
            kind = known;
        Add(new WorkEffortTouch(id, kind, null, null, null, null, null, null, null, null, new WorkEffortUpdate[] { new(author, at) }));
    }

    public WorkEffortKind? KindOf(string sysId)
    {
        var id = (sysId ?? "").Trim();
        if (id.Length == 0)
            return null;
        return _kindById.TryGetValue(id, out var kind) ? kind : null;
    }

    public IReadOnlyList<WorkEffortTouch> Touches() => _rows.Values.ToArray();
}

public sealed record WorkEffortRow
{
    public string Name { get; init; } = "";
    public int IncOpened { get; init; }
    public int IncResolved { get; init; }
    public int IncUpdated { get; init; }
    public int RitmOpened { get; init; }
    public int RitmResolved { get; init; }
    public int RitmUpdated { get; init; }
    public int ImsOpened { get; init; }
    public int ImsResolved { get; init; }
    public int ImsUpdated { get; init; }
    public decimal Weighted { get; init; }
    public string WeightedText => Weighted.ToString("0.0", CultureInfo.InvariantCulture);
}

public sealed record WorkEffortReport(
    IReadOnlyList<WorkEffortRow> Rows,
    string Status,
    string EmptyMessage,
    WorkEffortLedger? Ledger = null,
    string Shift = "")
{
    public const string EmptyGroupsMessage = "You are not in a group, so there is nobody to count.";

    public const string DefineTeamMessage =
        "Define your team on My team. Tick the people to include. Work Effort does not run until you do.";

    public static WorkEffortReport NoGroups() => new([], "", EmptyGroupsMessage);

    public static WorkEffortReport NoTeam() => new([], "", DefineTeamMessage);
}

public static class WorkEffortRoster
{
    public static IReadOnlyList<WorkEffortPerson> Collect(
        IEnumerable<WorkEffortMembership> memberships,
        WorkEffortPerson signedIn)
    {
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(signedIn);
        var signedInId = (signedIn.SysId ?? "").Trim();
        var rows = new List<WorkEffortMembership>();
        foreach (var membership in memberships)
        {
            var groupId = (membership.GroupSysId ?? "").Trim();
            var personId = (membership.Person?.SysId ?? "").Trim();
            if (groupId.Length == 0 || personId.Length == 0 || membership.Person is null)
                continue;
            rows.Add(new WorkEffortMembership(groupId, membership.Person));
        }

        var myGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (row.Person.SysId.Trim().Equals(signedInId, StringComparison.OrdinalIgnoreCase))
                myGroups.Add(row.GroupSysId);
        }

        if (myGroups.Count == 0)
            return [];

        var people = new Dictionary<string, WorkEffortPerson>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!myGroups.Contains(row.GroupSysId))
                continue;
            var id = row.Person.SysId.Trim();
            if (!people.ContainsKey(id))
                people[id] = row.Person;
        }

        if (signedInId.Length > 0 && !people.ContainsKey(signedInId))
            people[signedInId] = signedIn;

        return people.Values.ToArray();
    }
}

public static class WorkEffortScore
{
    public const decimal IncidentWeight = 1m;
    public const decimal RequestedItemWeight = 0.7m;
    public const decimal InteractionWeight = 0.6m;
    public const decimal UpdateWeight = 0.3m;

    public static IReadOnlyList<WorkEffortRow> Build(
        IReadOnlyList<WorkEffortPerson> people,
        IEnumerable<WorkEffortTouch> touches,
        WorkEffortWindow window,
        WorkEffortUpdateMode mode = WorkEffortUpdateMode.Daily)
    {
        ArgumentNullException.ThrowIfNull(people);
        ArgumentNullException.ThrowIfNull(touches);
        var distinct = new List<WorkEffortPerson>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var person in people)
        {
            var id = (person.SysId ?? "").Trim();
            if (id.Length == 0 || !seen.Add(id))
                continue;
            distinct.Add(person);
        }

        var merged = Merge(touches);
        var attempt = new WorkEffortAttempt(distinct, window, int.MaxValue, mode);
        foreach (var touch in merged)
            attempt.TakeRow(touch);
        return attempt.ToRows();
    }

    /// <summary>
    /// Scores the cached ledger in <paramref name="mode"/> and fills the shift line.
    /// A report with no ledger is returned as it was loaded.
    /// </summary>
    public static WorkEffortReport Present(WorkEffortReport report, WorkEffortUpdateMode mode)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Ledger is null)
            return report with { Shift = report.Shift };
        var daily = Build(report.Ledger.People, report.Ledger.Touches, report.Ledger.Window, WorkEffortUpdateMode.Daily);
        var multiple = Build(report.Ledger.People, report.Ledger.Touches, report.Ledger.Window, WorkEffortUpdateMode.Multiple);
        var rows = mode == WorkEffortUpdateMode.Multiple ? multiple : daily;
        return report with { Rows = rows, Shift = ShiftLine(daily, multiple) };
    }

    public static int UpdateCredits(IReadOnlyList<WorkEffortRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var total = 0;
        foreach (var row in rows)
            total += row.IncUpdated + row.RitmUpdated + row.ImsUpdated;
        return total;
    }

    public static string ShiftLine(IReadOnlyList<WorkEffortRow> daily, IReadOnlyList<WorkEffortRow> multiple)
    {
        var extra = UpdateCredits(multiple) - UpdateCredits(daily);
        if (extra < 0)
            extra = 0;
        if (extra == 0)
            return "Allow multiple updates adds no extra update credits versus daily updates.";
        var noun = extra == 1 ? "credit" : "credits";
        return "Allow multiple updates adds "
            + extra.ToString(CultureInfo.InvariantCulture)
            + " extra update "
            + noun
            + " versus daily updates.";
    }

    public static List<WorkEffortTouch> Merge(IEnumerable<WorkEffortTouch> touches)
    {
        var map = new Dictionary<string, WorkEffortTouch>(StringComparer.OrdinalIgnoreCase);
        foreach (var touch in touches)
        {
            var id = (touch.SysId ?? "").Trim();
            if (id.Length == 0)
                continue;
            var key = touch.Kind + "\n" + id;
            if (!map.TryGetValue(key, out var existing))
                map[key] = touch with { SysId = id };
            else
                map[key] = existing.Combine(touch);
        }

        return map.Values.ToList();
    }
}
