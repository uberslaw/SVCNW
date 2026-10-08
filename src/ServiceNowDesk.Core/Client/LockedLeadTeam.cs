using ServiceNowDesk.Alerts;

namespace ServiceNowDesk.Client;

/// <summary>
/// One person on the locked Leads team: a member of the watched group in the signed-in user's city.
/// </summary>
public sealed record LockedLeadPerson(string SysId, string Name);

/// <summary>
/// A group membership row used to build the locked team. Location is the user's office name.
/// </summary>
public sealed record LockedLeadMembership(string GroupName, string UserId, string Name, string Location);

/// <summary>
/// The locked Leads roster. Members come from the watched assignment group by exact name
/// (default <see cref="NotificationPreferences.DefaultGroupName"/>), never from a
/// <c>LIKE Client Services</c> match that would also pull in APAC DT - Client Services.
/// The city match is the same office comparison hardware uses.
/// </summary>
public static class LockedLeadTeam
{
    public const string Explanation = "Locked team: watched group members in your city.";

    public const string NoLocationPrompt = "Your account has no location, so the locked team is empty.";

    public const string EmptyPrompt = "No watched-group members are in your city.";

    /// <summary>
    /// Exact group-name query for the watched group. Defaults to Aus DT - Client Services.
    /// Does not use <c>LIKE</c>, so APAC DT - Client Services is never included by name similarity.
    /// </summary>
    public static string MembershipQuery(string? groupName = null)
    {
        var name = NormalizeGroupName(groupName);
        return "group.name=" + AlertQueryBuilder.Quote(name) + "^ORDERBYsys_id";
    }

    public static string NormalizeGroupName(string? groupName)
    {
        var trimmed = (groupName ?? "").Trim();
        return trimmed.Length == 0 ? NotificationPreferences.DefaultGroupName : trimmed;
    }

    public static bool HasCity(string? city) => !string.IsNullOrWhiteSpace(city);

    /// <summary>
    /// True when the membership row is for the watched group by exact name (case-insensitive).
    /// </summary>
    public static bool IsWatchedGroup(string? membershipGroupName, string? watchedGroupName = null)
    {
        var watched = NormalizeGroupName(watchedGroupName);
        var actual = (membershipGroupName ?? "").Trim();
        return actual.Length > 0 && actual.Equals(watched, StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<LockedLeadPerson> Select(
        string? city,
        IEnumerable<LockedLeadMembership>? rows,
        string? watchedGroupName = null)
    {
        if (!HasCity(city))
            return [];

        var watched = NormalizeGroupName(watchedGroupName);
        var people = new Dictionary<string, LockedLeadPerson>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows ?? [])
        {
            if (row is null || !IsWatchedGroup(row.GroupName, watched))
                continue;
            var id = (row.UserId ?? "").Trim();
            if (id.Length == 0 || people.ContainsKey(id))
                continue;
            if (!HardwareOfficeNames.SamePlace(city, row.Location))
                continue;
            var name = (row.Name ?? "").Trim();
            people[id] = new LockedLeadPerson(id, name.Length == 0 ? id : name);
        }

        return people.Values
            .OrderBy(person => person.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
