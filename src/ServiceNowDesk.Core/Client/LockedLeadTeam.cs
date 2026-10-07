namespace ServiceNowDesk.Client;

/// <summary>
/// One person on the locked Leads team: a client-services member in the signed-in user's city.
/// </summary>
public sealed record LockedLeadPerson(string SysId, string Name);

/// <summary>
/// A group membership row used to build the locked team. Location is the user's office name.
/// </summary>
public sealed record LockedLeadMembership(string GroupName, string UserId, string Name, string Location);

/// <summary>
/// The locked Leads roster. A client services group is any <c>sys_user_group</c> whose name
/// contains "Client Services". The city match is the same office comparison hardware uses.
/// </summary>
public static class LockedLeadTeam
{
    public const string GroupMarker = "Client Services";

    public const string Explanation = "Locked team: client services in your city.";

    public const string NoLocationPrompt = "Your account has no location, so the locked team is empty.";

    public const string EmptyPrompt = "No client services members are in your city.";

    /// <summary>
    /// Members of groups whose name contains "Client Services". This is not a query for every user.
    /// </summary>
    public const string MembershipQuery = "group.nameLIKEClient Services^ORDERBYsys_id";

    public static bool HasCity(string? city) => !string.IsNullOrWhiteSpace(city);

    public static bool IsClientServicesGroup(string? name) =>
        (name ?? "").Contains(GroupMarker, StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<LockedLeadPerson> Select(string? city, IEnumerable<LockedLeadMembership>? rows)
    {
        if (!HasCity(city))
            return [];

        var people = new Dictionary<string, LockedLeadPerson>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows ?? [])
        {
            if (row is null || !IsClientServicesGroup(row.GroupName))
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
