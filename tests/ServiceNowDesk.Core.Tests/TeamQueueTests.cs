using ServiceNowDesk.Alerts;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Navigation;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class TeamQueueTests
{
    [Fact]
    public async Task MyTeamIncludesTheWatchedGroupThatFindsTheImsRecord()
    {
        using var client = new SampleServiceNowClient();
        var walks = await client.SearchInteractionsAsync(new TicketQuery
        {
            Assignment = AssignmentScope.MyGroups,
            Activity = ActivityFilter.Open,
            ListSection = DeskSection.WalkUps,
            TeamMemberIds = ["sample-user"]
        }, CancellationToken.None);

        var ims = Assert.Single(walks.Items, row => row.Number == "IMS0010010");
        Assert.Equal("Aus DT - Client Services", ims.AssignmentGroup.Display);
        var watched = ims.AssignmentGroup.Display;

        var incidents = await client.SearchIncidentsAsync(new TicketQuery
        {
            Assignment = AssignmentScope.MyGroups,
            Activity = ActivityFilter.Open,
            ListSection = DeskSection.Incidents,
            OfficeLocations = ["Brisbane"],
            WatchedGroupName = watched
        }, CancellationToken.None);
        Assert.Contains(incidents.Items, row => row.Number == "INC0010007");
        Assert.Contains(incidents.Items, row => row.Number == "INC0010019");
        Assert.DoesNotContain(incidents.Items, row => row.Number == "INC0010008");
        Assert.DoesNotContain(incidents.Items, row => row.Number == "INC0010002");
        Assert.DoesNotContain(incidents.Items, row => row.Number == "INC0010014");

        var items = await client.SearchRequestedItemsAsync(new TicketQuery
        {
            Assignment = AssignmentScope.MyGroups,
            Activity = ActivityFilter.Open,
            ListSection = DeskSection.RequestedItems,
            OfficeLocations = ["Brisbane"],
            WatchedGroupName = watched
        }, CancellationToken.None);
        Assert.Contains(items.Items, row => row.Number == "RITM0010009");
        Assert.Contains(items.Items, row => row.Number == "RITM0010007");
        Assert.DoesNotContain(items.Items, row => row.Number == "RITM0010010");
        Assert.DoesNotContain(items.Items, row => row.Number == "RITM0010004");
    }

    [Fact]
    public async Task LiveMyTeamQueriesTheWatchedGroupNameWhenMembershipIsADifferentGroup()
    {
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            var query = QueryOf(path);
            if (path.Contains("/table/sys_user?", StringComparison.Ordinal)
                && !path.Contains("sys_user_grmember", StringComparison.Ordinal)
                && !path.Contains("sys_user_group", StringComparison.Ordinal))
            {
                return Api.Json("""{"result":[{"sys_id":{"value":"sample-user","display_value":"sample-user"},"name":{"value":"Alex","display_value":"Alex"},"user_name":{"value":"alex","display_value":"alex"},"email":{"value":"a@b.c","display_value":"a@b.c"},"location":{"value":"","display_value":"Brisbane"}}]}""");
            }

            if (path.Contains("sys_user_grmember", StringComparison.Ordinal))
                return Api.Json("""{"result":[]}""");

            if (query.Contains("group-net", StringComparison.Ordinal) || query.Contains("Network", StringComparison.Ordinal))
                return Api.Json("{\"result\":[" + Row("net-1", "INC0099001", "group-net", "Network") + "]}");

            if (path.Contains("/incident", StringComparison.Ordinal)
                && query.Contains("assignment_group.name=\"Aus DT - Client Services\"", StringComparison.Ordinal))
                return Api.Json("{\"result\":[" + Row("inc-aus", "INC0010007", "group-aus", "Aus DT - Client Services") + "]}");

            if (path.Contains("/sc_req_item", StringComparison.Ordinal)
                && query.Contains("assignment_group.name=\"Aus DT - Client Services\"", StringComparison.Ordinal))
                return Api.Json("{\"result\":[" + Row("ritm-aus", "RITM0010009", "group-aus", "Aus DT - Client Services") + "]}");

            return Api.Json("""{"result":[]}""");
        });
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);
        await client.GetCurrentUserAsync(CancellationToken.None);

        var incidents = await client.SearchIncidentsAsync(new TicketQuery
        {
            Assignment = AssignmentScope.MyGroups,
            Activity = ActivityFilter.Open,
            ListSection = DeskSection.Incidents,
            OfficeLocations = ["Brisbane"],
            WatchedGroupName = "Aus DT - Client Services"
        }, CancellationToken.None);
        var items = await client.SearchRequestedItemsAsync(new TicketQuery
        {
            Assignment = AssignmentScope.MyGroups,
            Activity = ActivityFilter.Open,
            ListSection = DeskSection.RequestedItems,
            OfficeLocations = ["Brisbane"],
            WatchedGroupName = "Aus DT - Client Services"
        }, CancellationToken.None);

        Assert.Equal("INC0010007", Assert.Single(incidents.Items).Number);
        Assert.Equal("RITM0010009", Assert.Single(items.Items).Number);
        var queries = handler.Calls.Select(call => QueryOf(call.PathAndQuery)).Where(query => query.Length > 0).ToArray();
        Assert.Contains(queries, query => query.Contains("assignment_group.name=\"Aus DT - Client Services\"", StringComparison.Ordinal)
            && query.Contains("stateNOT IN6,7,8", StringComparison.Ordinal));
        Assert.Contains(queries, query => query.Contains("assignment_group.name=\"Aus DT - Client Services\"", StringComparison.Ordinal)
            && query.Contains("stateNOT IN3,4,7", StringComparison.Ordinal));
        Assert.DoesNotContain(queries, query => query.Contains("group-net", StringComparison.Ordinal));
        Assert.DoesNotContain(queries, query => query.Contains("sys_id=NO_GROUP_MEMBERSHIP", StringComparison.Ordinal));
        Assert.DoesNotContain(incidents.Items, row => row.Number == "INC0099001");
        Assert.DoesNotContain(items.Items, row => row.AssignmentGroup.Display == "Network");
    }

    [Fact]
    public async Task GroupQueueRitmIsCountedInNotifications()
    {
        using var client = new SampleServiceNowClient();
        var user = await client.GetCurrentUserAsync(CancellationToken.None);
        var snapshot = await client.GetOpenAlertsAsync(
            new AlertSearch(user.SysId, "Aus DT - Client Services", NotificationPreferences.DefaultLocations),
            CancellationToken.None);

        var group = snapshot.Bucket(AlertKind.WatchedGroup).Rows;
        var ritm = Assert.Single(group, row => row.Number == "RITM0010009");
        Assert.Equal(DeskSection.RequestedItems, ritm.Section);
        Assert.Contains(group, row => row.Number == "RITM0010005" && row.Section == DeskSection.RequestedItems);
        Assert.Contains(group, row => row.Number == "INC0010007");
        Assert.DoesNotContain(group, row => row.Number == "RITM0010004");
        Assert.DoesNotContain(group, row => row.Number == "RITM0010010");
        Assert.DoesNotContain(group, row => row.Number == "INC0010008");
        Assert.DoesNotContain(group, row => row.Number == "INC0010014");
        Assert.True(snapshot.Count(AlertKind.WatchedGroup) >= 3);

        var blank = await client.GetOpenAlertsAsync(new AlertSearch(user.SysId, "", ["Brisbane"]), CancellationToken.None);
        Assert.Empty(blank.Bucket(AlertKind.WatchedGroup).Rows);

        var queue = await client.ListUnassignedGroupQueueAsync(
            "Aus DT - Client Services",
            NotificationPreferences.DefaultLocations,
            CancellationToken.None);
        var queued = Assert.Single(queue, row => row.Number == "RITM0010009");
        Assert.Equal(DeskSection.RequestedItems, queued.Section);
        Assert.Contains(queue, row => row.Number == "RITM0010005" && row.Section == DeskSection.RequestedItems);
        Assert.Contains(queue, row => row.Number == "INC0010004" && row.Section == DeskSection.Incidents);
        Assert.DoesNotContain(queue, row => row.Number == "RITM0010004");
        Assert.DoesNotContain(queue, row => row.Number == "RITM0010010");
        Assert.DoesNotContain(queue, row => row.Number == "INC0010018");
        Assert.All(queue, row => Assert.True(string.IsNullOrWhiteSpace(row.AssignedToSysId)));
    }

    [Fact]
    public async Task GroupQueueShowsAssignedTicketsWhenEachOfficeIsRequestedAlone()
    {
        var offices = NotificationPreferences.DefaultLocations;
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            var query = QueryOf(path);
            if (!path.Contains("/api/now/table/incident", StringComparison.Ordinal))
                return Api.Json("""{"result":[]}""");
            if (!query.Contains("assignment_group.name=\"Aus DT - Client Services\"", StringComparison.Ordinal))
                return Api.Json("""{"result":[]}""");
            if (query.Contains("assigned_toISEMPTY", StringComparison.Ordinal))
                return Api.Json("""{"result":[]}""");
            if (!IsOneOffice(query, "Brisbane", offices))
                return Api.Json("""{"result":[]}""");

            return Api.Json("{\"result\":[" + AssignedGroupIncident() + "]}");
        });
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);
        var snapshot = await client.GetOpenAlertsAsync(
            new AlertSearch("sample-user", "Aus DT - Client Services", offices),
            CancellationToken.None);

        var groupQueries = handler.Calls
            .Select(call => QueryOf(call.PathAndQuery))
            .Where(query => query.Contains("assignment_group.name=\"Aus DT - Client Services\"", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(groupQueries);
        Assert.All(groupQueries, query =>
        {
            Assert.True(query.Length <= AlertQueryBuilder.MaxQueryLength, query);
            Assert.True(offices.Count(city => query.Contains(city, StringComparison.Ordinal)) <= 1, query);
        });
        Assert.Contains(groupQueries, query => IsOneOffice(query, "Brisbane", offices)
            && query.Contains("stateNOT IN6,7,8", StringComparison.Ordinal)
            && !query.Contains("assigned_toISEMPTY", StringComparison.Ordinal));

        var ticket = Assert.Single(snapshot.Bucket(AlertKind.WatchedGroup).Rows, row => row.Number == "INC0010007");
        Assert.Equal("Jordan Lee", ticket.Assignee);
        Assert.Equal("Aus DT - Client Services", ticket.Group);
        Assert.Equal(DeskSection.Incidents, ticket.Section);

        var notifications = new NotificationWorkspaceViewModel();
        notifications.Show(snapshot);
        notifications.SelectQueueCommand.Execute(AlertKind.WatchedGroup);
        var shown = Assert.Single(notifications.DashboardRows, row => row.Number == "INC0010007");
        Assert.Equal("Jordan Lee", shown.Assignee);
        Assert.Equal("Group queue", shown.QueueLabel);
    }

    [Fact]
    public async Task GroupQueueUsesTheCachedGroupIdWhenTheSavedNameDiffersByCase()
    {
        var catalog = new MemoryCatalog(new FormCatalogSnapshot
        {
            Groups = [new CachedAssignmentGroup { SysId = "group-aus", Name = "AUS DT - Client Services" }]
        });
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            var query = QueryOf(path);
            if (!path.Contains("/api/now/table/incident", StringComparison.Ordinal))
                return Api.Json("""{"result":[]}""");
            if (!query.Contains("assignment_group=group-aus", StringComparison.Ordinal)
                || query.Contains("assignment_group.name", StringComparison.Ordinal))
                return Api.Json("""{"result":[]}""");
            if (!query.Contains("location.name=\"Brisbane\"", StringComparison.Ordinal))
                return Api.Json("""{"result":[]}""");
            return Api.Json("{\"result\":[" + AssignedGroupIncident("AUS DT - Client Services") + "]}");
        });
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler, catalog);
        var snapshot = await client.GetOpenAlertsAsync(
            new AlertSearch("sample-user", "Aus DT - Client Services", ["Brisbane"]),
            CancellationToken.None);

        var ticket = Assert.Single(snapshot.Bucket(AlertKind.WatchedGroup).Rows);
        Assert.Equal("INC0010007", ticket.Number);
        Assert.Equal("Jordan Lee", ticket.Assignee);
        Assert.Equal("AUS DT - Client Services", ticket.Group);
        Assert.Contains(handler.Calls, call => QueryOf(call.PathAndQuery).Contains("assignment_group=group-aus", StringComparison.Ordinal)
            && !QueryOf(call.PathAndQuery).Contains("assignment_group.name", StringComparison.Ordinal));
    }

    private static bool IsOneOffice(string query, string city, IReadOnlyList<string> offices)
    {
        if (!query.Contains(city, StringComparison.Ordinal))
            return false;
        return offices.All(office =>
            office.Equals(city, StringComparison.OrdinalIgnoreCase)
            || !query.Contains(office, StringComparison.Ordinal));
    }

    private static string AssignedGroupIncident(string groupName = "Aus DT - Client Services") =>
        "{\"sys_id\":{\"value\":\"inc-group\",\"display_value\":\"inc-group\"},"
        + "\"number\":{\"value\":\"INC0010007\",\"display_value\":\"INC0010007\"},"
        + "\"short_description\":{\"value\":\"Badge printer\",\"display_value\":\"Badge printer\"},"
        + "\"state\":{\"value\":\"2\",\"display_value\":\"In Progress\"},"
        + "\"assigned_to\":{\"value\":\"user-jordan\",\"display_value\":\"Jordan Lee\"},"
        + "\"assignment_group\":{\"value\":\"group-aus\",\"display_value\":\"" + groupName + "\"},"
        + "\"location\":{\"value\":\"loc-bne\",\"display_value\":\"Brisbane Office\"},"
        + "\"active\":{\"value\":\"true\",\"display_value\":\"true\"},"
        + "\"sys_updated_on\":{\"value\":\"2026-10-06 09:00:00\",\"display_value\":\"2026-10-06 09:00:00\"}}";

    private sealed class MemoryCatalog(FormCatalogSnapshot snapshot) : IFormCatalogStore
    {
        public FormCatalogSnapshot? Load(Uri instanceUri) => snapshot;

        public void Save(Uri instanceUri, FormCatalogSnapshot next)
        {
        }

        public long StoredBytes(Uri instanceUri) => 0;
    }

    private static string Row(string id, string number, string groupId, string groupName) =>
        "{\"sys_id\":{\"value\":\"" + id + "\",\"display_value\":\"" + id + "\"},"
        + "\"number\":{\"value\":\"" + number + "\",\"display_value\":\"" + number + "\"},"
        + "\"short_description\":{\"value\":\"Group ticket\",\"display_value\":\"Group ticket\"},"
        + "\"state\":{\"value\":\"2\",\"display_value\":\"In Progress\"},"
        + "\"assigned_to\":{\"value\":\"user-jordan\",\"display_value\":\"Jordan Lee\"},"
        + "\"assignment_group\":{\"value\":\"" + groupId + "\",\"display_value\":\"" + groupName + "\"},"
        + "\"location\":{\"value\":\"loc-bne\",\"display_value\":\"Brisbane Office\"},"
        + "\"active\":{\"value\":\"true\",\"display_value\":\"true\"},"
        + "\"sys_updated_on\":{\"value\":\"2026-10-06 09:00:00\",\"display_value\":\"2026-10-06 09:00:00\"}}";

    private static string QueryOf(string pathAndQuery)
    {
        var marker = "sysparm_query=";
        var start = pathAndQuery.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return "";
        var encoded = pathAndQuery[(start + marker.Length)..];
        var end = encoded.IndexOf('&');
        if (end >= 0)
            encoded = encoded[..end];
        return Uri.UnescapeDataString(encoded);
    }
}
