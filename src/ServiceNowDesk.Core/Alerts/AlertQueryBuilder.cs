using ServiceNowDesk.Models;
using ServiceNowDesk.Query;

namespace ServiceNowDesk.Alerts;

public static class AlertQueryBuilder
{
    public static string AssignedToMe(string userSysId, DeskSection section = DeskSection.Incidents)
    {
        var id = EncodedQuery.SafeToken(userSysId, "user id");
        return "assigned_to=" + id + "^" + StillWorking(section) + "^ORDERBYDESCsys_updated_on";
    }

    /// <summary>
    /// Active, and not resolved, closed, or cancelled. On hold stays.
    /// Incident 6, 7, and 8 are Resolved, Closed, and Canceled. Request-item 3, 4, and 7 are the closed states.
    /// </summary>
    public static string StillWorking(DeskSection section) => section switch
    {
        DeskSection.RequestedItems => "active=true^stateNOT IN3,4,7",
        DeskSection.Requests => "active=true^request_stateNOT LIKEclosed^request_stateNOT LIKEcancel",
        DeskSection.WalkUps => "active=true^stateNOT LIKEclosed^stateNOT LIKEcancel",
        _ => "active=true^stateNOT IN6,7,8"
    };

    /// <summary>
    /// Encoded query for open incidents in the watched group whose location name is one of the cities.
    /// Returns null when the group or the city list is blank, so the caller sends no request.
    /// </summary>
    public static string? WatchedGroup(string? groupName, IEnumerable<string>? locations)
    {
        var scope = WatchedScope(groupName, locations);
        return scope is null ? null : scope + "^" + StillWorking(DeskSection.Incidents) + "^ORDERBYDESCsys_updated_on";
    }

    /// <summary>
    /// ServiceNow will not build pagination header URLs once sysparm_query grows past this.
    /// Each alert request stays under the limit. A 32-character sys_id is the long token.
    /// </summary>
    public const int MaxQueryLength = 900;

    /// <summary>
    /// Open records for the signed-in user: assigned to them, in one of their groups, or in the watched group at the office locations.
    /// Each role is its own request. Group ids and office names are split so the roles are not one ^NQ string.
    /// Lead-team members and the regional group (every open ticket, with no office filter) are not included.
    /// </summary>
    public static IReadOnlyList<string> PopulationQueries(string userSysId, IReadOnlyList<string>? groupIds, string? groupName, IEnumerable<string>? locations, DeskSection section = DeskSection.Incidents)
    {
        var tail = "^" + StillWorking(section) + "^ORDERBYDESCsys_updated_on";
        var budget = MaxQueryLength - tail.Length;
        var user = EncodedQuery.SafeToken(userSysId, "user id");
        var queries = new List<string> { "assigned_to=" + user + tail };
        var groups = (groupIds ?? [])
            .Select(id => EncodedQuery.SafeToken(id, "group id"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var clause in InClauses("assignment_group", groups, budget))
            queries.Add(clause + tail);

        foreach (var scope in WatchedLocationScopes(groupName, locations, budget))
            queries.Add(scope + tail);
        return queries;
    }

    /// <summary>
    /// Open incidents with an empty assignee whose group is one of the user's groups,
    /// or the watched group by name. My Groups does not filter location, so this check does not either.
    /// Null when both scopes are blank, so the caller sends no request.
    /// </summary>
    public static string? UnassignedInGroups(IReadOnlyList<string>? groupIds, string? watchedGroupName)
    {
        var open = StillWorking(DeskSection.Incidents);
        var groups = new List<string>();
        foreach (var id in groupIds ?? [])
        {
            if (string.IsNullOrWhiteSpace(id))
                continue;
            try
            {
                groups.Add(EncodedQuery.SafeToken(id, "group id"));
            }
            catch (InvalidOperationException)
            {
            }
        }

        var segments = new List<string>();
        var distinct = groups.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (distinct.Length > 0)
            segments.Add("assigned_toISEMPTY^assignment_groupIN" + string.Join(",", distinct) + "^" + open);

        var watched = Quote(watchedGroupName);
        if (watched.Length > 0)
            segments.Add("assigned_toISEMPTY^assignment_group.name=" + watched + "^" + open);

        return segments.Count == 0 ? null : string.Join("^NQ", segments) + "^ORDERBYDESCsys_updated_on";
    }

    /// <summary>
    /// Open tickets in the watched group (regional), and open tickets assigned to the selected team.
    /// Each scope is its own request. Empty when both are blank, so the caller sends nothing.
    /// </summary>
    public static IReadOnlyList<string> LeadQueries(string? groupName, IEnumerable<string>? memberIds, DeskSection section = DeskSection.Incidents)
    {
        var tail = "^" + StillWorking(section) + "^ORDERBYDESCsys_updated_on";
        var budget = MaxQueryLength - tail.Length;
        var queries = new List<string>();
        var group = Quote(groupName);
        if (group.Length > 0)
            queries.Add("assignment_group.name=" + group + tail);

        var ids = new List<string>();
        foreach (var id in memberIds ?? [])
        {
            if (string.IsNullOrWhiteSpace(id))
                continue;
            try
            {
                ids.Add(EncodedQuery.SafeToken(id, "user id"));
            }
            catch (InvalidOperationException)
            {
            }
        }

        var distinctIds = ids.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var clause in InClauses("assigned_to", distinctIds, budget))
            queries.Add(clause + tail);
        return queries;
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

    /// <summary>
    /// Latest comment or work note for each task. Task ids are split so one element_id list cannot
    /// include every personal ticket and every lead ticket in a single sysparm_query.
    /// </summary>
    public static IReadOnlyList<string> LatestJournalQueries(IReadOnlyList<string>? taskIds)
    {
        var tokens = (taskIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => EncodedQuery.SafeToken(id, "record id"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        const string suffix = "^elementINcomments,work_notes^ORDERBYDESCsys_created_on";
        return InClauses("element_id", tokens, MaxQueryLength - suffix.Length)
            .Select(clause => clause + suffix)
            .ToArray();
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

    /// <summary>
    /// fieldIN lists that stay within <paramref name="budget"/> characters.
    /// One token that is already longer than the budget is still returned on its own.
    /// </summary>
    internal static IReadOnlyList<string> InClauses(string field, IReadOnlyList<string> tokens, int budget)
    {
        if (tokens.Count == 0)
            return [];

        var prefix = field + "IN";
        var limit = Math.Max(prefix.Length + 1, budget);
        var queries = new List<string>();
        var batch = new List<string>();
        var length = prefix.Length;
        foreach (var token in tokens)
        {
            var extra = token.Length + (batch.Count == 0 ? 0 : 1);
            if (batch.Count > 0 && length + extra > limit)
            {
                queries.Add(prefix + string.Join(",", batch));
                batch.Clear();
                length = prefix.Length;
                extra = token.Length;
            }

            batch.Add(token);
            length += extra;
        }

        if (batch.Count > 0)
            queries.Add(prefix + string.Join(",", batch));
        return queries;
    }

    private static IReadOnlyList<string> WatchedLocationScopes(string? groupName, IEnumerable<string>? locations, int budget)
    {
        var group = Quote(groupName);
        var cities = (locations ?? [])
            .Select(Quote)
            .Where(city => city.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (group.Length == 0 || cities.Length == 0)
            return [];

        var head = "assignment_group.name=" + group + "^";
        var queries = new List<string>();
        var batch = new List<string>();
        foreach (var city in cities)
        {
            var next = LocationClause(head, [.. batch, city]);
            if (batch.Count > 0 && next.Length > budget)
            {
                queries.Add(LocationClause(head, batch));
                batch.Clear();
            }

            batch.Add(city);
        }

        if (batch.Count > 0)
            queries.Add(LocationClause(head, batch));
        return queries;
    }

    private static string LocationClause(string head, IReadOnlyList<string> cities) =>
        cities.Count == 1
            ? head + "location.name=" + cities[0]
            : head + "location.nameIN" + string.Join(",", cities);

    public static string Quote(string? value)
    {
        var sanitized = EncodedQuery.Sanitize(value);
        if (sanitized.Length == 0)
            return "";

        var escaped = sanitized.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        return "\"" + escaped + "\"";
    }
}
