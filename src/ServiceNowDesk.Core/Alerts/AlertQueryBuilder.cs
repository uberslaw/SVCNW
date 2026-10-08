using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Query;

namespace ServiceNowDesk.Alerts;

public static class AlertQueryBuilder
{
    public static string AssignedToMe(string userSysId, DeskSection section = DeskSection.Incidents) =>
        AssignedToMe(userSysId, section, locations: null);

    /// <summary>
    /// Open tickets assigned to the user. Matches My Tickets: assignee-only, no office filter.
    /// <paramref name="locations"/> is kept for call-site compatibility and ignored.
    /// </summary>
    public static string AssignedToMe(string userSysId, DeskSection section, IEnumerable<string>? locations)
    {
        _ = locations;
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
        var group = Quote(groupName);
        var cities = OfficeCities(locations);
        if (group.Length == 0 || cities.Length == 0)
            return null;

        return OfficeQueue.ApplyTo(
            "assignment_group.name=" + group + "^" + StillWorking(DeskSection.Incidents) + "^ORDERBYDESCsys_updated_on",
            cities);
    }

    public const int MaxQueryLength = 900;

    /// <summary>
    /// Open records for the signed-in user: assigned to them (any office, matching My Tickets),
    /// in one of their groups, or in the watched group at the office locations. Each role is
    /// its own request. Group ids and office names are split so pagination URLs stay short.
    /// Lead-team members and the regional group (every open ticket, with no office filter) are not included.
    /// </summary>
    public static IReadOnlyList<string> PopulationQueries(string userSysId, IReadOnlyList<string>? groupIds, string? groupName, IEnumerable<string>? locations, DeskSection section = DeskSection.Incidents)
    {
        var openOrder = StillWorking(section) + "^ORDERBYDESCsys_updated_on";
        var tail = "^" + openOrder;
        var budget = MaxQueryLength - tail.Length;
        var user = EncodedQuery.SafeToken(userSysId, "user id");
        var queries = new List<string>
        {
            "assigned_to=" + user + tail
        };
        var groups = (groupIds ?? [])
            .Select(id => EncodedQuery.SafeToken(id, "group id"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var clause in InClauses("assignment_group", groups, budget))
            queries.Add(clause + tail);

        foreach (var scope in WatchedLocationScopes(groupName, locations, section, MaxQueryLength))
            queries.Add(scope);
        return queries;
    }

    /// <summary>
    /// Open unassigned incidents in the signed-in user's groups and/or the watched group.
    /// When <paramref name="officeLocations"/> is non-null, each group segment is narrowed with
    /// <see cref="OfficeQueue.ApplyTo"/> (blank location matches nothing). Null offices keep the
    /// legacy unfiltered group queue for callers that only want membership.
    /// </summary>
    public static string? UnassignedInGroups(
        IReadOnlyList<string>? groupIds,
        string? watchedGroupName,
        IReadOnlyList<string>? officeLocations = null)
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

        if (segments.Count == 0)
            return null;

        if (officeLocations is not null)
        {
            // Apply location per segment so an existing ^NQ in the group filter is not
            // split across offices (which would leave unscoped branches).
            var cities = OfficeCities(officeLocations);
            segments = segments.Select(segment => OfficeQueue.ApplyTo(segment, cities)).ToList();
        }

        return string.Join("^NQ", segments) + "^ORDERBYDESCsys_updated_on";
    }

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

    public static string? AssignedToAny(IEnumerable<string>? memberIds)
    {
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

        var distinct = ids.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return distinct.Length == 0 ? null : "assigned_toIN" + string.Join(",", distinct);
    }

    public const string SlaUnavailableStatus = "SLA data is not available to this user";

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
        var cities = OfficeCities(locations);
        if (group.Length == 0 || cities.Length == 0)
            return null;

        return OfficeQueue.ApplyTo("assignment_group.name=" + group, cities);
    }

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

    private static IReadOnlyList<string> WatchedLocationScopes(
        string? groupName,
        IEnumerable<string>? locations,
        DeskSection section,
        int budget)
    {
        var group = Quote(groupName);
        var cities = OfficeCities(locations);
        if (group.Length == 0 || cities.Length == 0)
            return [];

        var head = "assignment_group.name=" + group + "^" + StillWorking(section) + "^ORDERBYDESCsys_updated_on";
        var queries = new List<string>();
        foreach (var city in cities)
        {
            var encoded = OfficeQueue.ApplyTo(head, [city]);
            if (encoded.Length <= budget)
                queries.Add(encoded);
        }

        return queries;
    }

    private static string[] OfficeCities(IEnumerable<string>? locations) =>
        (locations ?? [])
            .Select(HardwareOfficeNames.Normalize)
            .Where(city => city.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static string Quote(string? value)
    {
        var sanitized = EncodedQuery.Sanitize(value);
        if (sanitized.Length == 0)
            return "";

        var escaped = sanitized.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        return "\"" + escaped + "\"";
    }
}
