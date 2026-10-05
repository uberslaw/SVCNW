using ServiceNowDesk.Query;

namespace ServiceNowDesk.Alerts;

public static class AlertQueryBuilder
{
    public static string AssignedToMe(string userSysId)
    {
        var id = EncodedQuery.SafeToken(userSysId, "user id");
        return "assigned_to=" + id + "^active=true^ORDERBYDESCsys_updated_on";
    }

    /// <summary>
    /// Encoded query for open incidents in the watched group whose location name is one of the cities.
    /// Returns null when the group or the city list is blank, so the caller sends no request.
    /// </summary>
    public static string? WatchedGroup(string? groupName, IEnumerable<string>? locations)
    {
        var group = Quote(groupName);
        var cities = (locations ?? [])
            .Select(Quote)
            .Where(city => city.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (group.Length == 0 || cities.Length == 0)
            return null;

        var location = cities.Length == 1
            ? "location.name=" + cities[0]
            : "location.nameIN" + string.Join(",", cities);
        return "assignment_group.name=" + group + "^" + location + "^active=true^ORDERBYDESCsys_updated_on";
    }

    public static string Quote(string? value)
    {
        var sanitized = EncodedQuery.Sanitize(value);
        if (sanitized.Length == 0)
            return "";

        var escaped = sanitized.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        return "\"" + escaped + "\"";
    }
}
