using System.Globalization;

namespace ServiceNowDesk.Alerts;

/// <summary>
/// Unassigned incidents already observed today, stored with the daily snapshot.
/// The first look is a baseline. Later looks report only ids that were not in that set.
/// </summary>
public readonly record struct UnassignedSeen(bool BaselineTaken, IReadOnlyList<string> Seen, IReadOnlyList<string> Announced)
{
    public static UnassignedSeen None { get; } = new(false, [], []);
}

public sealed record GroupQueueStep(IReadOnlyList<string> NewIds, UnassignedSeen State);

/// <summary>
/// Set difference for the group queue. A second pass of the same ids yields nothing new.
/// </summary>
public static class GroupQueueTracker
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    public static IReadOnlyList<string> NewlyAppeared(IEnumerable<string>? seen, IEnumerable<string>? polled)
    {
        var known = new HashSet<string>(Clean(seen), StringComparer.OrdinalIgnoreCase);
        var fresh = new List<string>();
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in Clean(polled))
        {
            if (known.Contains(id) || !added.Add(id))
                continue;
            fresh.Add(id);
        }

        return fresh;
    }

    public static GroupQueueStep Compare(UnassignedSeen previous, IEnumerable<string>? polled)
    {
        var current = Clean(polled).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!previous.BaselineTaken)
            return new GroupQueueStep([], new UnassignedSeen(true, current, []));

        var fresh = NewlyAppeared(previous.Seen, current);
        return new GroupQueueStep(
            fresh,
            new UnassignedSeen(true, Union(previous.Seen, current), Union(previous.Announced, fresh)));
    }

    public static string? StatusText(int newCount)
    {
        if (newCount <= 0)
            return null;
        if (newCount == 1)
            return "1 new unassigned in the group queue.";
        return newCount.ToString(CultureInfo.InvariantCulture) + " new unassigned in the group queue.";
    }

    private static IReadOnlyList<string> Union(IEnumerable<string>? left, IEnumerable<string>? right) =>
        Clean(left).Concat(Clean(right)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static IEnumerable<string> Clean(IEnumerable<string>? ids) =>
        (ids ?? []).Select(id => id?.Trim() ?? "").Where(id => id.Length > 0);
}
