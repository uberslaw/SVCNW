using System.Text.Json;
using ServiceNowDesk.Mapping;

namespace ServiceNowDesk.Client;

public sealed partial class ServiceNowClient
{
    /// <summary>
    /// Client services members in <paramref name="city"/>. An empty city returns nobody and does not call ServiceNow.
    /// The query is group membership for names containing "Client Services", never every user on the instance.
    /// </summary>
    public async Task<IReadOnlyList<LockedLeadPerson>> ListLockedLeadTeamAsync(string? city, CancellationToken cancellationToken)
    {
        if (!LockedLeadTeam.HasCity(city))
            return [];

        var rows = new List<LockedLeadMembership>();
        await PageRowsAsync(
            "sys_user_grmember",
            "sys_id,group,group.name,user,user.name,user.location",
            LockedLeadTeam.MembershipQuery,
            5000,
            null,
            row =>
            {
                var membership = ReadLockedMembership(row);
                if (membership is not null)
                    rows.Add(membership);
            },
            cancellationToken).ConfigureAwait(false);
        return LockedLeadTeam.Select(city, rows);
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
