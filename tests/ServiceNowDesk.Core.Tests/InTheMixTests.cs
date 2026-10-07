using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Navigation;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class InTheMixTests
{
    [Fact]
    public void BrisbaneMatchesBrisbaneOfficeAndABlankLocationDoesNot()
    {
        Assert.True(OfficeQueue.Matches("Brisbane Office", ["Brisbane"]));
        Assert.True(OfficeQueue.Matches("Brisbane", ["Brisbane Office"]));
        Assert.False(OfficeQueue.Matches("", ["Brisbane"]));
        Assert.False(OfficeQueue.Matches("Sydney", ["Brisbane"]));
        Assert.Equal(["Brisbane", "Brisbane Office"], OfficeQueue.Variants("Brisbane"));
        Assert.Equal(["Cairns"], OfficeQueue.Cities(["Cairns"], "Brisbane"));
        Assert.Equal(["Brisbane"], OfficeQueue.Cities([], " Brisbane "));
        Assert.Empty(OfficeQueue.Cities(null, "  "));
        Assert.Contains("location.nameIN", OfficeQueue.LocationClause(["Brisbane"]));
        Assert.Contains("\"Brisbane Office\"", OfficeQueue.LocationClause(["Brisbane"]));
        Assert.Equal("sys_id=NO_OFFICE", OfficeQueue.LocationClause([]));
    }

    [Fact]
    public async Task MyTeamAndUnassignedStayInsideTheOfficeAndKeepTheGroup()
    {
        using var client = new SampleServiceNowClient();
        var incidents = await OpenAsync(client, ["Brisbane"]);

        incidents.Preset = Preset(AssignmentScope.MyGroups);
        await incidents.RefreshAsync();
        Assert.Contains(incidents.Items, row => row.Number == "INC0010019");
        Assert.Contains(incidents.Items, row => row.Number == "INC0010021");
        Assert.DoesNotContain(incidents.Items, row => row.Number == "INC0010020");
        Assert.DoesNotContain(incidents.Items, row => row.Number == "INC0010022");
        Assert.DoesNotContain(incidents.Items, row => row.Number == "INC0010011");

        incidents.Preset = Preset(AssignmentScope.Unassigned);
        await incidents.RefreshAsync();
        Assert.Contains(incidents.Items, row => row.Number == "INC0010021");
        Assert.DoesNotContain(incidents.Items, row => row.Number == "INC0010020");
        Assert.DoesNotContain(incidents.Items, row => row.Number == "INC0010022");
        Assert.DoesNotContain(incidents.Items, row => row.Number == "INC0010019");

        incidents.UseOfficeCities(["Brisbane Office"]);
        incidents.Preset = Preset(AssignmentScope.MyGroups);
        await incidents.RefreshAsync();
        Assert.Contains(incidents.Items, row => row.Number == "INC0010021");
        Assert.Contains(incidents.Items, row => row.Number == "INC0010019");
        Assert.DoesNotContain(incidents.Items, row => row.Number == "INC0010020");
    }

    [Fact]
    public async Task MyTicketsStayWithTheSignedInUser()
    {
        using var client = new SampleServiceNowClient();
        var incidents = await OpenAsync(client, ["Brisbane"]);
        Assert.Equal("My Tickets", incidents.Preset?.Label);
        await incidents.RefreshAsync();

        Assert.Contains(incidents.Items, row => row.Number == "INC0010001");
        Assert.DoesNotContain(incidents.Items, row => row.Number == "INC0010019");
        Assert.DoesNotContain(incidents.Items, row => row.Number == "INC0010020");
    }

    [Fact]
    public async Task EmptyWatchedOfficesUseTheSignedInUsersCity()
    {
        var sample = new SampleServiceNowClient
        {
            SignedInUser = new CurrentUser("sample-user", "Alex Rivera", "alex.rivera", "alex.rivera@example.com")
            {
                Location = "Brisbane"
            }
        };
        var store = new MemorySettingsStore();
        store.Save(new DeskSettings { UseSampleData = true, OfficeLocations = [] });
        var main = new MainViewModel(store, new RecordingDesktopServices(), sampleClientFactory: () => sample);
        await main.InitializeAsync();

        Assert.Equal(["Brisbane"], main.Incidents.OfficeCities);
        Assert.Equal(["Brisbane"], main.RequestedItems.OfficeCities);
        Assert.Equal(["Brisbane"], main.WalkUps.OfficeCities);
        Assert.Equal(["Brisbane"], main.Mix.OfficeCities);

        main.Incidents.Preset = Preset(AssignmentScope.MyGroups);
        await main.Incidents.RefreshAsync();
        Assert.Contains(main.Incidents.Items, row => row.Number == "INC0010019");
        Assert.DoesNotContain(main.Incidents.Items, row => row.Number == "INC0010020");
        Assert.DoesNotContain(main.Incidents.Items, row => row.Number == "INC0010011");

        Assert.Equal("My Team", main.Mix.Preset.Label);
        await main.Mix.RefreshAsync();
        Assert.Contains(main.Mix.Items, row => row.Kind == "INC" && row.Number == "INC0010019");
        Assert.Contains(main.Mix.Items, row => row.Kind == "RITM" && row.Number == "RITM0010007");
        Assert.Contains(main.Mix.Items, row => row.Kind == "IMS" && row.Number == "IMS0010005");
        Assert.DoesNotContain(main.Mix.Items, row => row.Number == "INC0010020");
        Assert.DoesNotContain(main.Mix.Items, row => row.Kind == "INC" && row.Number == "INC0010011");

        var row = main.Mix.Items.Single(item => item.Number == "INC0010019");
        main.Mix.Selected = row;
        await main.MixOpenTask;
        Assert.Equal(DeskSection.Incidents.ToString(), main.Mix.EditorKey);
        Assert.True(main.Incidents.HasEditor);
        Assert.Equal("INC0010019", main.Incidents.Number);

        main.Incidents.ShortDescription = "Updated from the mix";
        await main.Incidents.SaveCommand.ExecuteAsync(null);
        Assert.True(string.IsNullOrEmpty(main.Incidents.ErrorMessage), main.Incidents.ErrorMessage);
        var saved = await sample.GetIncidentAsync("inc-mix-bne", CancellationToken.None);
        Assert.Equal("Updated from the mix", saved.ShortDescription);
    }

    [Fact]
    public void LabelsAndNavPutInTheMixAboveIncidents()
    {
        Assert.Equal(["My Tickets", "My Team", "Unassigned"], PresetCatalog.Incidents.Take(3).Select(preset => preset.Label).ToArray());
        Assert.Equal(["My Tickets", "My Team", "Unassigned"], PresetCatalog.RequestedItems.Take(3).Select(preset => preset.Label).ToArray());
        Assert.Equal(["My Tickets", "My Team", "Unassigned"], PresetCatalog.WalkUps.Take(3).Select(preset => preset.Label).ToArray());
        Assert.Equal(["My Tickets", "My Team", "Unassigned"], PresetCatalog.Mix.Select(preset => preset.Label).ToArray());

        var labels = DeskNavigation.Visible(leadsEnabled: false).Select(item => item.Label).ToArray();
        var incidents = Array.IndexOf(labels, "Incidents");
        Assert.Equal("In The Mix", labels[incidents - 1]);
        Assert.DoesNotContain(labels, label => label == "Order catalog");
        Assert.Equal(DeskSection.Leads, DeskNavigation.Visible(true).Reverse().Skip(1).First().Section);
    }

    [Fact]
    public async Task OfficeQueriesKeepTheGroupAndTheLocationOnEveryTable()
    {
        var handler = new StubHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("sys_user_grmember", StringComparison.Ordinal))
                return Api.Json("""{"result":[{"group":{"value":"group-cs","display_value":"Client Services"}}]}""");
            return Api.Json("""{"result":[]}""");
        });
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);
        var offices = new TicketQuery
        {
            Assignment = AssignmentScope.MyGroups,
            Activity = ActivityFilter.Open,
            OfficeLocations = ["Brisbane"]
        };
        await client.SearchIncidentsAsync(offices, CancellationToken.None);
        await client.SearchRequestedItemsAsync(offices, CancellationToken.None);
        await client.SearchInteractionsAsync(offices, CancellationToken.None);

        var queries = handler.Calls
            .Select(call => call.PathAndQuery)
            .Where(path => path.Contains("/api/now/table/incident", StringComparison.Ordinal)
                || path.Contains("/api/now/table/sc_req_item", StringComparison.Ordinal)
                || path.Contains("/api/now/table/interaction", StringComparison.Ordinal))
            .Select(QueryOf)
            .ToArray();
        Assert.Equal(3, queries.Length);
        Assert.All(queries, query =>
        {
            Assert.Contains("assignment_groupINgroup-cs", query);
            Assert.Contains("location.nameIN\"Brisbane\",\"Brisbane Office\"", query);
        });
        Assert.Contains(queries, query => query.Contains("type=walkup", StringComparison.Ordinal));

        await client.SearchIncidentsAsync(new TicketQuery
        {
            Assignment = AssignmentScope.Unassigned,
            Activity = ActivityFilter.Open,
            OfficeLocations = ["Brisbane"]
        }, CancellationToken.None);
        var unassigned = QueryOf(handler.Calls[^1].PathAndQuery);
        Assert.Contains("assigned_toISEMPTY", unassigned);
        Assert.Contains("assignment_groupINgroup-cs", unassigned);
        Assert.Contains("location.nameIN\"Brisbane\",\"Brisbane Office\"", unassigned);

        await client.SearchIncidentsAsync(new TicketQuery
        {
            Assignment = AssignmentScope.Mine,
            Activity = ActivityFilter.Open,
            OfficeLocations = ["Brisbane"]
        }, CancellationToken.None);
        var mine = QueryOf(handler.Calls[^1].PathAndQuery);
        Assert.Contains("assigned_to=javascript:gs.getUserID()", mine);
        Assert.DoesNotContain("location.name", mine);

        await client.SearchIncidentsAsync(new TicketQuery
        {
            Assignment = AssignmentScope.MyGroups,
            Activity = ActivityFilter.Open,
            OfficeLocations = []
        }, CancellationToken.None);
        var none = QueryOf(handler.Calls[^1].PathAndQuery);
        Assert.Contains("assignment_groupINgroup-cs", none);
        Assert.Contains("sys_id=NO_OFFICE", none);
    }

    private static PresetOption Preset(AssignmentScope scope) =>
        PresetCatalog.Incidents.Single(preset => preset.Assignment == scope && preset.Activity == ActivityFilter.Open && preset.AssignmentClause is null);

    private static async Task<IncidentWorkspaceViewModel> OpenAsync(SampleServiceNowClient client, IReadOnlyList<string> cities)
    {
        var workspace = new IncidentWorkspaceViewModel(new RecordingDesktopServices());
        workspace.UseOfficeCities(cities);
        workspace.Attach(client);
        await workspace.EnsureChoicesAsync();
        return workspace;
    }

    private static string QueryOf(string pathAndQuery)
    {
        var marker = "sysparm_query=";
        var start = pathAndQuery.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0);
        var encoded = pathAndQuery[(start + marker.Length)..];
        var end = encoded.IndexOf('&');
        if (end >= 0)
            encoded = encoded[..end];
        return Uri.UnescapeDataString(encoded);
    }
}
