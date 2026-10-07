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
    /// Audit rows for the moment <paramref name="userSysId"/> became <c>assigned_to</c>.
    /// Incidents have no assigned-on column. This is not <c>sys_updated_on</c> and not <c>opened_at</c>.
    /// </summary>
    public static IReadOnlyList<string> AssignmentAuditQueries(string userSysId, IEnumerable<string>? documentKeys)
    {
        string user;
        try
        {
            user = EncodedQuery.SafeToken(userSysId, "user id");
        }
        catch (InvalidOperationException)
        {
            return [];
        }

        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in documentKeys ?? [])
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;
            try
            {
                var token = EncodedQuery.SafeToken(key, "record id");
                if (seen.Add(token))
                    ids.Add(token);
            }
            catch (InvalidOperationException)
            {
            }
        }

        if (ids.Count == 0)
            return [];

        const int chunkSize = 40;
        var queries = new List<string>();
        for (var start = 0; start < ids.Count; start += chunkSize)
        {
            var chunk = string.Join(",", ids.Skip(start).Take(chunkSize));
            queries.Add(
                "tablenameINincident,sc_request,sc_req_item^fieldname=assigned_to^newvalue="
                + user
                + "^documentkeyIN"
                + chunk
                + "^ORDERBYDESCsys_created_on");
        }

        return queries;
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
    /// Open records assigned to the user, in the user's groups, or in the watched group at the office locations.
    /// Each segment is its own query so a location filter cannot leak onto the other populations.
    /// </summary>
    public static string Population(string userSysId, IReadOnlyList<string>? groupIds, string? groupName, IEnumerable<string>? locations, DeskSection section = DeskSection.Incidents)
    {
        var open = StillWorking(section);
        var user = EncodedQuery.SafeToken(userSysId, "user id");
        var segments = new List<string> { "assigned_to=" + user + "^" + open };
        var groups = (groupIds ?? [])
            .Select(id => EncodedQuery.SafeToken(id, "group id"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (groups.Length > 0)
            segments.Add("assignment_groupIN" + string.Join(",", groups) + "^" + open);

        var watched = WatchedScope(groupName, locations);
        if (watched is not null)
            segments.Add(watched + "^" + open);
        return string.Join("^NQ", segments);
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
    /// Open tickets in the watched group, plus open tickets assigned to the selected team.
    /// Null when both scopes are blank, so the caller sends no request.
    /// </summary>
    public static string? LeadPopulation(string? groupName, IEnumerable<string>? memberIds, DeskSection section = DeskSection.Incidents)
    {
        var open = StillWorking(section);
        var segments = new List<string>();
        var group = Quote(groupName);
        if (group.Length > 0)
            segments.Add("assignment_group.name=" + group + "^" + open);

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
        if (distinctIds.Length > 0)
            segments.Add("assigned_toIN" + string.Join(",", distinctIds) + "^" + open);

        return segments.Count == 0 ? null : string.Join("^NQ", segments);
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
