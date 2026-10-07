using ServiceNowDesk.Alerts;

namespace ServiceNowDesk.Client;

/// <summary>
/// Offices for My Team and Unassigned. The list is the watched notification offices when
/// that setting is non-empty, otherwise the signed-in user's city. Tickets are matched on
/// the record location with the same place rule as hardware ("Brisbane" and "Brisbane Office").
/// </summary>
public static class OfficeQueue
{
    public static IReadOnlyList<string> Cities(IReadOnlyList<string>? watchedOffices, string? userCity)
    {
        var watched = (watchedOffices ?? [])
            .Select(HardwareOfficeNames.Normalize)
            .Where(city => city.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (watched.Length > 0)
            return watched;

        var city = HardwareOfficeNames.Normalize(userCity);
        return city.Length == 0 ? [] : [city];
    }

    /// <summary>
    /// A blank location is outside every office. "Brisbane" matches "Brisbane Office".
    /// </summary>
    public static bool Matches(string? ticketLocation, IReadOnlyList<string>? cities)
    {
        var place = HardwareOfficeNames.Normalize(ticketLocation);
        if (place.Length == 0)
            return false;

        foreach (var city in cities ?? [])
        {
            if (HardwareOfficeNames.SamePlace(place, city))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Encoded <c>location.name</c> clause. An empty city list matches nothing, so the queue
    /// does not fall open to every group ticket.
    /// </summary>
    public static string LocationClause(IReadOnlyList<string>? cities)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var city in cities ?? [])
        {
            foreach (var variant in Variants(city))
            {
                if (seen.Add(variant))
                    names.Add(variant);
            }
        }

        if (names.Count == 0)
            return "sys_id=NO_OFFICE";

        var quoted = names.Select(AlertQueryBuilder.Quote).Where(name => name.Length > 0).ToArray();
        if (quoted.Length == 0)
            return "sys_id=NO_OFFICE";
        if (quoted.Length == 1)
            return "location.name=" + quoted[0];
        return "location.nameIN" + string.Join(",", quoted);
    }

    public static IReadOnlyList<string> Variants(string? city)
    {
        var name = HardwareOfficeNames.Normalize(city);
        if (name.Length == 0)
            return [];

        const string suffix = " Office";
        if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            var stem = name[..^suffix.Length].TrimEnd();
            if (stem.Length == 0)
                return [name];
            return [stem, name];
        }

        return [name, name + suffix];
    }
}
