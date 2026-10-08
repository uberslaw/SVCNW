using System.Text.Json;
using ServiceNowDesk.Mapping;

namespace ServiceNowDesk.Client;

public sealed partial class ServiceNowClient
{
    /// <summary>
    /// Members of the watched group in <paramref name="city"/>. An empty city returns nobody and does not call ServiceNow.
    /// The query uses the exact watched group name (default Aus DT - Client Services), never a LIKE that would include APAC.
    /// </summary>
    public async Task<IReadOnlyList<LockedLeadPerson>> ListLockedLeadTeamAsync(
        string? city,
        CancellationToken cancellationToken) =>
        await ListLockedLeadTeamAsync(city, watchedGroupName: null, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<LockedLeadPerson>> ListLockedLeadTeamAsync(
        string? city,
        string? watchedGroupName,
        CancellationToken cancellationToken)
    {
        if (!LockedLeadTeam.HasCity(city))
            return [];

        var groupName = LockedLeadTeam.NormalizeGroupName(watchedGroupName);
        var rows = new List<LockedLeadMembership>();
        await PageRowsAsync(
            "sys_user_grmember",
            "sys_id,group,group.name,user,user.name,user.location",
            LockedLeadTeam.MembershipQuery(groupName),
            5000,
            null,
            row =>
            {
                var membership = ReadLockedMembership(row);
                if (membership is not null)
                    rows.Add(membership);
            },
            cancellationToken).ConfigureAwait(false);
        return LockedLeadTeam.Select(city, rows, groupName);
    }

    private static LockedLeadMembership? ReadLockedMembership(JsonElement row)
    {
        var user = SnowField.Read(row, "user");
        var id = user.Value.Trim();
        if (id.Length == 0)
            return null;

        var groupName = FirstDisplay(row, "group.name", "group");
        var name = FirstDisplay(row, "user.name", "user");
        if (name.Length == 0 || name.Equals(id, StringComparison.OrdinalIgnoreCase))
            name = string.IsNullOrWhiteSpace(user.Display) ? id : user.Display.Trim();
        var location = FirstDisplay(row, "user.location");
        return new LockedLeadMembership(groupName, id, name, location);
    }

    private static string FirstDisplay(JsonElement row, params string[] names)
    {
        foreach (var name in names)
        {
            var display = DisplayOnly(row, name);
            if (display.Length > 0)
                return display;
        }

        return "";
    }
}
