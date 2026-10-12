using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

/// <summary>
/// Incident and request-item My Team is the selected Leads people.
/// All Open is every open row. My Team uses that same scope, then keeps selected assignees.
/// A Settings region office list does not drop a teammate whose ticket is somewhere else.
/// </summary>
public class MyTeamMemberTests
{
    [Fact]
    public async Task InRegionIncidentAssignedToSelectedMemberIsOnMyTeam()
    {
        using var client = new SampleServiceNowClient();
        var team = await client.SearchIncidentsAsync(People("user-jordan", DeskSection.Incidents), CancellationToken.None);

        Assert.Contains(team.Items, row => row.Number == "INC0010019");
        Assert.Contains(team.Items, row => row.Number == "INC0010007");
        Assert.Contains(team.Items, row => row.Number == "INC0010023");
        Assert.Contains("assigned_toINuser-jordan", client.LastTicketEncodedQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("location.name", client.LastTicketEncodedQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("assignment_group", client.LastTicketEncodedQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InRegionRequestItemAssignedToSelectedMemberIsOnMyTeam()
    {
        using var client = new SampleServiceNowClient();
        var team = await client.SearchRequestedItemsAsync(People("user-jordan", DeskSection.RequestedItems), CancellationToken.None);

        Assert.Contains(team.Items, row => row.Number == "RITM0010007");
        Assert.Contains("assigned_toINuser-jordan", client.LastTicketEncodedQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("location.name", client.LastTicketEncodedQuery, StringComparison.Ordinal);
        Assert.Contains("stateNOT IN3,4,7", client.LastTicketEncodedQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("assignment_group", client.LastTicketEncodedQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InRegionTicketAssignedToSomeoneElseIsExcluded()
    {
        using var client = new SampleServiceNowClient();
        var incidents = await client.SearchIncidentsAsync(People("user-sam", DeskSection.Incidents), CancellationToken.None);
        var items = await client.SearchRequestedItemsAsync(People("user-sam", DeskSection.RequestedItems), CancellationToken.None);

        Assert.DoesNotContain(incidents.Items, row => row.Number == "INC0010019");
        Assert.DoesNotContain(incidents.Items, row => row.Number == "INC0010007");
        Assert.Contains(items.Items, row => row.Number == "RITM0010010");
        Assert.DoesNotContain(items.Items, row => row.Number == "RITM0010007");
        Assert.DoesNotContain(items.Items, row => row.Number == "RITM0010009");
    }

    [Fact]
    public async Task SelectedMemberOutsideTheRegionStaysOnMyTeam()
    {
        using var client = new SampleServiceNowClient();
        var incidents = await client.SearchIncidentsAsync(People("user-jordan", DeskSection.Incidents), CancellationToken.None);
        var incidentQuery = client.LastTicketEncodedQuery;
        var items = await client.SearchRequestedItemsAsync(People("user-sam", DeskSection.RequestedItems), CancellationToken.None);

        Assert.Contains(incidents.Items, row => row.Number == "INC0010019");
        Assert.Contains(incidents.Items, row => row.Number == "INC0010023");
        Assert.DoesNotContain("location.name", incidentQuery, StringComparison.Ordinal);
        Assert.Contains(items.Items, row => row.Number == "RITM0010010");
        Assert.DoesNotContain("location.name", client.LastTicketEncodedQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllOpenReturnsTheBroaderInRegionSet()
    {
        using var client = new SampleServiceNowClient();
        var team = await client.SearchIncidentsAsync(People("user-sam", DeskSection.Incidents), CancellationToken.None);
        Assert.Contains(team.Items, row => row.Number == "INC0010008");
        Assert.DoesNotContain(team.Items, row => row.Number == "INC0010020");
        Assert.DoesNotContain(team.Items, row => row.Number == "INC0010019");

        var open = await client.SearchIncidentsAsync(new TicketQuery
        {
            Assignment = AssignmentScope.Any,
            Activity = ActivityFilter.Open,
            ListSection = DeskSection.Incidents,
            Limit = 100
        }, CancellationToken.None);

        Assert.Contains(open.Items, row => row.Number == "INC0010008");
        Assert.Contains(open.Items, row => row.Number == "INC0010020");
        Assert.Contains(open.Items, row => row.Number == "INC0010019");
        Assert.Contains(open.Items, row => row.Number == "INC0010023");
        Assert.DoesNotContain("location.name", client.LastTicketEncodedQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("assigned_toIN", client.LastTicketEncodedQuery, StringComparison.Ordinal);
        Assert.True(open.Items.Count > team.Items.Count);
    }

    [Fact]
    public async Task SettingsRegionOfficesFeedMyTeamScope()
    {
        var main = await OpenDeskAsync(new DeskSettings
        {
            RegionOffices = ["Sydney"],
            OfficeLocations = ["Brisbane", "Hong Kong"],
            LeadTeamMemberIds = ["user-sam"],
            WatchedGroupName = "Aus DT - Client Services"
        });

        Assert.Equal(["Sydney"], main.Incidents.RegionOffices);
        Assert.Equal(["Sydney"], main.RequestedItems.RegionOffices);
        Assert.Equal(["Sydney"], main.Mix.RegionOffices);
        Assert.Equal(["user-sam"], main.Incidents.TeamMemberIds);
        Assert.Equal(["user-sam"], main.RequestedItems.TeamMemberIds);
        Assert.Equal(["Brisbane", "Hong Kong"], main.Incidents.OfficeCities);

        main.Incidents.Preset = MyTeam();
        await main.Incidents.RefreshAsync();

        Assert.Contains(main.Incidents.Items, row => row.Number == "INC0010008");
        Assert.Contains(main.Incidents.Items, row => row.Number == "INC0010012");
        Assert.DoesNotContain(main.Incidents.Items, row => row.Number == "INC0010019");
        Assert.DoesNotContain(main.Incidents.Items, row => row.Number == "INC0010023");
        Assert.DoesNotContain(main.Incidents.Items, row => row.Number == "INC0010020");
        Assert.Contains("assigned_toINuser-sam", main.Incidents.LastEncodedQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("location.name", main.Incidents.LastEncodedQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("assignment_group", main.Incidents.LastEncodedQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("offices:", main.Incidents.FilterSummary, StringComparison.Ordinal);

        main.Incidents.Preset = AllOpen();
        await main.Incidents.RefreshAsync();
        Assert.Contains(main.Incidents.Items, row => row.Number == "INC0010008");
        Assert.Contains(main.Incidents.Items, row => row.Number == "INC0010020");
        Assert.Contains(main.Incidents.Items, row => row.Number == "INC0010019");
        Assert.DoesNotContain("location.name", main.Incidents.LastEncodedQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("assigned_toIN", main.Incidents.LastEncodedQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoSelectedTeamMembersLeavesMyTeamEmpty()
    {
        var main = await OpenDeskAsync(new DeskSettings
        {
            RegionOffices = ["Brisbane", "Sydney", "Hong Kong"],
            OfficeLocations = ["Brisbane", "Hong Kong"],
            LeadTeamMemberIds = []
        });

        main.Incidents.Preset = MyTeam();
        await main.Incidents.RefreshAsync();
        Assert.Empty(main.Incidents.Items);
        Assert.Equal(WalkUpTeam.EmptyPrompt, main.Incidents.SearchHint);

        main.RequestedItems.Preset = MyTeam();
        await main.RequestedItems.RefreshAsync();
        Assert.Empty(main.RequestedItems.Items);
        Assert.Equal(WalkUpTeam.EmptyPrompt, main.RequestedItems.SearchHint);
    }

    [Fact]
    public async Task WalkUpMyTeamStillIncludesAMemberOutsideTheRegion()
    {
        var main = await OpenDeskAsync(new DeskSettings
        {
            RegionOffices = ["Brisbane"],
            OfficeLocations = ["Brisbane"],
            LeadTeamMemberIds = ["user-jordan"]
        });

        main.WalkUps.Preset = PresetCatalog.WalkUps.Single(preset => preset.Label == "My Team");
        await main.WalkUps.RefreshAsync();

        Assert.Contains(main.WalkUps.Items, row => row.Number == "IMS0010006");
        Assert.Contains("assigned_toINuser-jordan", main.WalkUps.LastEncodedQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("location.name", main.WalkUps.LastEncodedQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LiveMyTeamQueryUsesSelectedPeopleInsideRegionOffices()
    {
        var handler = new StubHandler((_, _) => Api.Json("""{"result":[]}"""));
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);

        await client.SearchIncidentsAsync(People("user-jordan", DeskSection.Incidents), CancellationToken.None);
        await client.SearchRequestedItemsAsync(People("user-sam", DeskSection.RequestedItems), CancellationToken.None);

        var queries = handler.Calls.Select(call => QueryOf(call.PathAndQuery)).Where(query => query.Length > 0).ToArray();
        Assert.Contains(queries, query => query.Contains("assigned_toINuser-jordan", StringComparison.Ordinal)
            && query.Contains("stateNOT IN6,7,8", StringComparison.Ordinal)
            && !query.Contains("location.name", StringComparison.Ordinal)
            && !query.Contains("assignment_group", StringComparison.Ordinal));
        Assert.Contains(queries, query => query.Contains("assigned_toINuser-sam", StringComparison.Ordinal)
            && query.Contains("stateNOT IN3,4,7", StringComparison.Ordinal)
            && !query.Contains("location.name", StringComparison.Ordinal)
            && !query.Contains("assignment_group", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Calls, call => call.PathAndQuery.Contains("sys_user_grmember", StringComparison.Ordinal));
    }

    [Fact]
    public void RegionOfficesReplaceTheQueueOfficeList()
    {
        Assert.Equal(["Sydney"], TeamListScope.Places(["Sydney"]));
        Assert.Equal(["Sydney"], TeamListScope.Offices([" Sydney "], ["Brisbane", "Hong Kong"]));
        Assert.Equal(["Brisbane", "Hong Kong"], TeamListScope.Offices(null, ["Brisbane", "Hong Kong"]));
        Assert.Equal(["Brisbane"], TeamListScope.Offices([], ["Brisbane"]));
        Assert.False(TeamListScope.Includes(["user-jordan"], ""));
        Assert.False(TeamListScope.Includes(["user-jordan"], "user-sam"));
        Assert.True(TeamListScope.Includes(["user-jordan"], "user-jordan"));
    }

    private static TicketQuery People(string memberId, DeskSection section) => new()
    {
        Assignment = AssignmentScope.MyGroups,
        Activity = ActivityFilter.Open,
        ListSection = section,
        TeamMemberIds = [memberId],
        WatchedGroupName = "Aus DT - Client Services",
        Limit = 100
    };

    private static PresetOption MyTeam() =>
        PresetCatalog.Incidents.Single(preset => preset.Label == "My Team");

    private static PresetOption AllOpen() =>
        PresetCatalog.Incidents.Single(preset => preset.Label == "All open");

    private static async Task<MainViewModel> OpenDeskAsync(DeskSettings settings)
    {
        settings.UseSampleData = true;
        var store = new MemorySettingsStore();
        store.Save(settings);
        var main = new MainViewModel(store, new RecordingDesktopServices(), sampleClientFactory: () => new SampleServiceNowClient());
        await main.InitializeAsync();
        return main;
    }

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
