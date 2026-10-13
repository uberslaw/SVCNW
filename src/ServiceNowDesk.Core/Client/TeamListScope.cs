namespace ServiceNowDesk.Client;

/// <summary>
/// Shared roster for My Team.
/// Walk-ups, incidents, and request items use the same selected people.
/// All Open is every open row the table returns, with no office clause.
/// My Team keeps rows assigned to the selected people, and also rows in the
/// watched assignment group, including unassigned incidents in that queue.
/// A region or office clause is not applied: it dropped teammates whose ticket
/// location did not match Settings offices while My Tickets (assignee only) still showed.
/// Region offices stay a Hardware concern.
/// </summary>
public static class TeamListScope
{
    public static bool Includes(IReadOnlyList<string>? memberIds, string? assigneeId)
    {
        if (string.IsNullOrWhiteSpace(assigneeId))
            return false;

        foreach (var id in memberIds ?? [])
        {
            if (string.Equals(id?.Trim(), assigneeId, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Place names for a location clause. My Team does not use this.
    /// A saved region replaces the queue offices. An empty region keeps
    /// <paramref name="queueOffices"/> (null when that queue is not limited).
    /// </summary>
    public static IReadOnlyList<string>? Offices(IEnumerable<string>? regionOffices, IReadOnlyList<string>? queueOffices)
    {
        var places = Places(regionOffices);
        if (places.Count > 0)
            return places;
        return queueOffices;
    }

    /// <summary>
    /// Catalog office labels split the same way hardware region matching splits them,
    /// so "Sydney" also covers "Sydney Office" once <see cref="OfficeQueue"/> adds the suffix.
    /// </summary>
    public static IReadOnlyList<string> Places(IEnumerable<string>? regionOffices)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var office in RegionOfficeMatch.NormalizeOffices(regionOffices))
        {
            foreach (var phrase in RegionOfficeMatch.Phrases(office))
            {
                var place = HardwareOfficeNames.CityStem(phrase);
                if (place.Length == 0 || !seen.Add(place))
                    continue;
                names.Add(place);
            }
        }

        return names;
    }
}
