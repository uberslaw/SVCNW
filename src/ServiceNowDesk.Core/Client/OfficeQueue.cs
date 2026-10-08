using ServiceNowDesk.Alerts;

namespace ServiceNowDesk.Client;

/// <summary>
/// Offices for My Team, Unassigned, and watched-group alerts. The list is the watched
/// notification offices when that setting is non-empty, otherwise the signed-in user's city.
/// Tickets are matched on the record location with the same place rule as hardware
/// ("Brisbane" and "Brisbane Office"). My Tickets stays assignee-only.
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
    /// Location predicate only. Prefer <see cref="ApplyTo"/> so the group and open filters
    /// are repeated on every branch.
    /// </summary>
    public static string LocationClause(IReadOnlyList<string>? cities) => ApplyTo("", cities);

    /// <summary>
    /// Keeps <paramref name="encodedQuery"/> and adds one <c>location.name</c> equality per
    /// place, including the " Office" form. Branches are joined with <c>^NQ</c> and the
    /// original filters are copied onto each branch.
    /// An <c>IN</c> list keeps the quote characters, so <c>location.nameIN"Brisbane","Brisbane Office"</c>
    /// matches neither name and the queue comes back empty. A parenthesized <c>OR</c> is not
    /// used: ServiceNow drops that group. An empty city list matches nothing.
    /// </summary>
    public static string ApplyTo(string? encodedQuery, IReadOnlyList<string>? cities)
    {
        var (body, order) = SplitOrder(encodedQuery);
        var branches = Equalities(cities).Select(equality => body.Length == 0 ? equality : body + "^" + equality);
        return string.Join("^NQ", branches) + order;
    }

    private static IReadOnlyList<string> Equalities(IReadOnlyList<string>? cities)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var city in cities ?? [])
        {
            foreach (var variant in Variants(city))
            {
                var quoted = AlertQueryBuilder.Quote(variant);
                if (quoted.Length == 0 || !seen.Add(quoted))
                    continue;
                names.Add("location.name=" + quoted);
            }
        }

        return names.Count == 0 ? ["sys_id=NO_OFFICE"] : names;
    }

    private static (string Body, string Order) SplitOrder(string? encodedQuery)
    {
        var encoded = encodedQuery?.Trim() ?? "";
        if (encoded.Length == 0)
            return ("", "");

        var orderAt = encoded.LastIndexOf("^ORDERBY", StringComparison.Ordinal);
        if (orderAt >= 0)
            return (encoded[..orderAt], encoded[orderAt..]);

        if (encoded.StartsWith("ORDERBY", StringComparison.Ordinal))
            return ("", "^" + encoded);

        return (encoded, "");
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
