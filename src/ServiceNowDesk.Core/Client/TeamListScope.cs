namespace ServiceNowDesk.Client;

/// <summary>
/// Shared roster and region for My Team.
/// Walk-up My Team and incident / request-item My Team use the same selected people.
/// All Open does not apply an office clause: it is every open row the table returns.
/// Incident and request-item My Team then keep only those people inside the region.
/// Settings region offices win when any are saved. Otherwise the queue office cities
/// (notification offices, or the signed-in user's city) are the scope.
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
    /// Place names for the incident and request-item My Team location clause.
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
