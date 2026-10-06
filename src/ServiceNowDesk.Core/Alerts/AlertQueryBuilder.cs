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
        var scope = WatchedScope(groupName, locations);
        return scope is null ? null : scope + "^active=true^ORDERBYDESCsys_updated_on";
    }

    /// <summary>
    /// Open records assigned to the user, in the user's groups, or in the watched group at the office locations.
    /// Each segment is its own query so a location filter cannot leak onto the other populations.
    /// </summary>
    public static string Population(string userSysId, IReadOnlyList<string>? groupIds, string? groupName, IEnumerable<string>? locations)
    {
        var user = EncodedQuery.SafeToken(userSysId, "user id");
        var segments = new List<string> { "assigned_to=" + user + "^active=true" };
        var groups = (groupIds ?? [])
            .Select(id => EncodedQuery.SafeToken(id, "group id"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (groups.Length > 0)
            segments.Add("assignment_groupIN" + string.Join(",", groups) + "^active=true");

        var watched = WatchedScope(groupName, locations);
        if (watched is not null)
            segments.Add(watched + "^active=true");
        return string.Join("^NQ", segments);
    }

    public const string SlaUnavailableStatus = "SLA data is not available to this user";

    /// <summary>
    /// Table API queries for breached or in-progress SLAs on the already scoped tasks.
    /// A "&lt;" date comparison in this URL makes ServiceNow return an HTML page, so
    /// planned-end is applied after the JSON comes back. Each query stays short so the
    /// instance does not answer the Table API with an error page.
    /// </summary>
    public static IReadOnlyList<string> TaskSlaQueries(IReadOnlyList<string>? taskIds)
    {
        var tokens = (taskIds ?? [])
            .Select(id => EncodedQuery.SafeToken(id, "task id"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (tokens.Length == 0)
            return [];

        const int chunkSize = 40;
        var queries = new List<string>();
        for (var start = 0; start < tokens.Length; start += chunkSize)
        {
            var ids = string.Join(",", tokens.Skip(start).Take(chunkSize));
            var scope = "taskIN" + ids + "^task.sys_class_nameINincident,sc_req_item,interaction";
            queries.Add(scope + "^has_breached=true^NQ" + scope + "^stage=in_progress");
        }

        return queries;
    }

    public static string? LatestJournal(IReadOnlyList<string>? taskIds)
    {
        var ids = JoinIds(taskIds, "record id");
        if (ids is null)
            return null;
        return "element_idIN" + ids + "^elementINcomments,work_notes^ORDERBYDESCsys_created_on";
    }

    public static string? WatchedScope(string? groupName, IEnumerable<string>? locations)
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
        return "assignment_group.name=" + group + "^" + location;
    }

    private static string? JoinIds(IReadOnlyList<string>? ids, string label)
    {
        var tokens = (ids ?? [])
            .Select(id => EncodedQuery.SafeToken(id, label))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return tokens.Length == 0 ? null : string.Join(",", tokens);
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
