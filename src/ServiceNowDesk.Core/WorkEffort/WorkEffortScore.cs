using System.Globalization;

namespace ServiceNowDesk.WorkEffort;

public enum WorkEffortKind
{
    Incident,
    RequestedItem,
    Interaction
}

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
/// One record's opener, resolver, closer, and last updater. Dates are local wall times.
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
    DateTime? UpdatedAt)
{
    public WorkEffortTouch Combine(WorkEffortTouch other) => this with
    {
        OpenedBySysId = First(OpenedBySysId, other.OpenedBySysId),
        OpenedAt = OpenedAt ?? other.OpenedAt,
        ResolvedBySysId = First(ResolvedBySysId, other.ResolvedBySysId),
        ResolvedAt = ResolvedAt ?? other.ResolvedAt,
        ClosedBySysId = First(ClosedBySysId, other.ClosedBySysId),
        ClosedAt = ClosedAt ?? other.ClosedAt,
        UpdatedBy = First(UpdatedBy, other.UpdatedBy),
        UpdatedAt = UpdatedAt ?? other.UpdatedAt
    };

    private static string? First(string? left, string? right) =>
        string.IsNullOrWhiteSpace(left) ? right : left;
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

public sealed record WorkEffortReport(IReadOnlyList<WorkEffortRow> Rows, string Status, string EmptyMessage)
{
    public const string EmptyGroupsMessage = "You are not in a group, so there is nobody to count.";

    public static WorkEffortReport NoGroups() => new([], "", EmptyGroupsMessage);
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
        WorkEffortWindow window)
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
        var rows = new List<WorkEffortRow>(distinct.Count);
        foreach (var person in distinct)
        {
            var incOpened = 0;
            var incResolved = 0;
            var incUpdated = 0;
            var ritmOpened = 0;
            var ritmResolved = 0;
            var ritmUpdated = 0;
            var imsOpened = 0;
            var imsResolved = 0;
            var imsUpdated = 0;
            foreach (var touch in merged)
            {
                var opened = InWindow(touch.OpenedAt, window) && Same(person.SysId, touch.OpenedBySysId);
                var resolved = InWindow(touch.ResolvedAt, window) && Same(person.SysId, touch.ResolvedBySysId);
                var closed = InWindow(touch.ClosedAt, window) && Same(person.SysId, touch.ClosedBySysId);
                var updated = InWindow(touch.UpdatedAt, window)
                    && (Same(person.UserName, touch.UpdatedBy) || Same(person.SysId, touch.UpdatedBy))
                    && !opened
                    && !resolved
                    && !closed;
                var completed = resolved || closed;
                switch (touch.Kind)
                {
                    case WorkEffortKind.RequestedItem:
                        if (opened) ritmOpened++;
                        if (completed) ritmResolved++;
                        if (updated) ritmUpdated++;
                        break;
                    case WorkEffortKind.Interaction:
                        if (opened) imsOpened++;
                        if (completed) imsResolved++;
                        if (updated) imsUpdated++;
                        break;
                    default:
                        if (opened) incOpened++;
                        if (completed) incResolved++;
                        if (updated) incUpdated++;
                        break;
                }
            }

            var weighted = (incOpened + incResolved) * IncidentWeight
                + (ritmOpened + ritmResolved) * RequestedItemWeight
                + (imsOpened + imsResolved) * InteractionWeight
                + (incUpdated + ritmUpdated + imsUpdated) * UpdateWeight;
            rows.Add(new WorkEffortRow
            {
                Name = person.DisplayName,
                IncOpened = incOpened,
                IncResolved = incResolved,
                IncUpdated = incUpdated,
                RitmOpened = ritmOpened,
                RitmResolved = ritmResolved,
                RitmUpdated = ritmUpdated,
                ImsOpened = imsOpened,
                ImsResolved = imsResolved,
                ImsUpdated = imsUpdated,
                Weighted = weighted
            });
        }

        return rows
            .OrderByDescending(row => row.Weighted)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

    private static bool InWindow(DateTime? moment, WorkEffortWindow window) =>
        moment is DateTime value && window.Contains(value);

    private static bool Same(string? left, string? right)
    {
        var a = (left ?? "").Trim();
        var b = (right ?? "").Trim();
        return a.Length > 0 && a.Equals(b, StringComparison.OrdinalIgnoreCase);
    }
}
