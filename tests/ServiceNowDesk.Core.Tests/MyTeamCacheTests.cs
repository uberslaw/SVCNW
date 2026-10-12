using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;
using ServiceNowDesk.WorkEffort;

namespace ServiceNowDesk.Tests;

public class MyTeamCacheTests
{
    [Fact]
    public async Task MyTeamCacheKeepsATeammateAndLeavesMyTicketsOnTheSignedInUser()
    {
        var lists = new MemoryDeskListStore();
        var settings = new MemorySettingsStore();
        settings.Save(new DeskSettings
        {
            UseSampleData = true,
            DownloadCacheOnLaunch = true,
            RegionOffices = ["Sydney"],
            OfficeLocations = ["Brisbane"],
            LeadTeamMemberIds = ["user-jordan", "sample-user"],
            LeadTeamSaved = true
        });
        var main = new MainViewModel(settings, new RecordingDesktopServices(), lists: lists);
        await main.InitializeAsync();

        var saved = lists.Load(DeskListScope.Practice);
        Assert.NotNull(saved);
        Assert.NotNull(saved.Incidents);
        Assert.NotNull(saved.IncidentTeam);
        Assert.NotNull(saved.RequestItemTeam);
        Assert.NotNull(saved.WalkUpTeam);

        Assert.Contains(saved.Incidents.Items, row => row.Number == "INC0010001" && row.AssigneeId == "sample-user");
        Assert.DoesNotContain(saved.Incidents.Items, row => row.AssigneeId == "user-jordan");
        Assert.DoesNotContain(saved.Incidents.Items, row => row.Number == "INC0010019");
        Assert.Contains("assigned_to=sample-user", saved.Incidents.EncodedQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("location.name", saved.Incidents.EncodedQuery, StringComparison.Ordinal);

        Assert.Contains(saved.IncidentTeam.Items, row => row.Number == "INC0010019" && row.AssigneeId == "user-jordan");
        Assert.Contains(saved.IncidentTeam.Items, row => row.Number == "INC0010023");
        Assert.Contains(saved.IncidentTeam.Items, row => row.Number == "INC0010024" && row.AssigneeId == "sample-user");
        Assert.DoesNotContain(saved.IncidentTeam.Items, row => row.AssigneeId == "user-sam");
        Assert.DoesNotContain(saved.IncidentTeam.Items, row => row.Number == "INC0010008");
        Assert.Contains("assigned_toIN", saved.IncidentTeam.EncodedQuery, StringComparison.Ordinal);
        Assert.Contains("user-jordan", saved.IncidentTeam.EncodedQuery, StringComparison.Ordinal);
        Assert.Contains("sample-user", saved.IncidentTeam.EncodedQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("location.name", saved.IncidentTeam.EncodedQuery, StringComparison.Ordinal);
        Assert.False(saved.IncidentTeam.FilteredByRegion);
        Assert.Equal(["sample-user", "user-jordan"], saved.IncidentTeam.TeamMemberIds!.OrderBy(id => id, StringComparer.Ordinal).ToArray());

        Assert.Contains(saved.RequestItemTeam.Items, row => row.Number == "RITM0010007" && row.AssigneeId == "user-jordan");
        Assert.DoesNotContain(saved.RequestItemTeam.Items, row => row.AssigneeId == "user-sam");
        Assert.DoesNotContain("location.name", saved.RequestItemTeam.EncodedQuery, StringComparison.Ordinal);

        var incidents = Assert.Single(main.Caches, row => row.Name == "Incidents");
        Assert.Contains("My Tickets", incidents.QueryText, StringComparison.Ordinal);
        Assert.Contains("assigned to you", incidents.QueryText, StringComparison.Ordinal);
        Assert.Contains("stored · My Team · selected people · open", incidents.TeamText, StringComparison.Ordinal);
        Assert.Contains(saved.IncidentTeam.Items.Count.ToString(), incidents.TeamText, StringComparison.Ordinal);
        var items = Assert.Single(main.Caches, row => row.Name == "Request items");
        Assert.Contains("My Team · selected people · open", items.TeamText, StringComparison.Ordinal);
        var walks = Assert.Single(main.Caches, row => row.Name == "Walk-ups");
        Assert.Contains("My Team · selected people · open", walks.TeamText, StringComparison.Ordinal);
        Assert.Equal("", Assert.Single(main.Caches, row => row.Name == "Requests").TeamText);
        Assert.Equal("", Assert.Single(main.Caches, row => row.Name == "Knowledge").TeamText);
        Assert.Equal("", Assert.Single(main.Caches, row => row.Name == "Hardware").TeamText);
    }

    [Fact]
    public async Task MyTeamListReadsTheTeamCacheWhenMyTicketsDoesNotContainTheTeammate()
    {
        var lists = new MemoryDeskListStore();
        var settings = new MemorySettingsStore();
        settings.Save(new DeskSettings
        {
            UseSampleData = true,
            DownloadCacheOnLaunch = false,
            RegionOffices = ["Sydney", "Brisbane"],
            LeadTeamMemberIds = ["user-jordan"],
            LeadTeamSaved = true
        });
        var now = DateTimeOffset.UtcNow;
        lists.Save(DeskListScope.Practice, new DeskListSnapshot
        {
            Incidents = new CachedTicketList
            {
                CapturedAt = now,
                TotalCount = 1,
                EncodedQuery = "assigned_to=sample-user^active=true^stateNOT IN6,7,8",
                FilterSummary = "My Tickets · assigned to you · open",
                Items =
                [
                    new CachedTicketRow
                    {
                        SysId = "inc-mine",
                        Number = "INC-MINE",
                        Title = "Only the signed-in user",
                        StateLabel = "In Progress",
                        AssigneeId = "sample-user",
                        AssigneeDisplay = "Alex Rivera"
                    }
                ]
            },
            IncidentTeam = new CachedTicketList
            {
                CapturedAt = now,
                TotalCount = 1,
                EncodedQuery = "assigned_toINuser-jordan^active=true^stateNOT IN6,7,8",
                FilterSummary = "My Team · selected people · open",
                FilteredByRegion = false,
                TeamMemberIds = ["user-jordan"],
                Items =
                [
                    new CachedTicketRow
                    {
                        SysId = "inc-team",
                        Number = "INC-TEAM-ONLY",
                        Title = "Secret teammate laptop",
                        StateLabel = "In Progress",
                        AssigneeId = "user-jordan",
                        AssigneeDisplay = "Jordan Lee"
                    }
                ]
            },
            RequestItems = Fresh("RITM-MINE"),
            RequestItemTeam = Fresh("RITM-TEAM"),
            WalkUps = Fresh("IMS-MINE"),
            WalkUpTeam = Fresh("IMS-TEAM")
        });
        var desktop = new RecordingDesktopServices();
        var main = new MainViewModel(settings, desktop, lists: lists);
        await main.InitializeAsync();

        Assert.Contains(main.Incidents.Items, row => row.Number == "INC-MINE");
        Assert.DoesNotContain(main.Incidents.Items, row => row.Number == "INC-TEAM-ONLY");

        main.Incidents.UseTeamNames([new WorkEffortPerson("user-jordan", "Jordan Lee", "jordan.lee")]);
        main.Incidents.Preset = PresetCatalog.Incidents.Single(preset => preset.Label == "My Team");

        var shown = Assert.Single(main.Incidents.Items);
        Assert.Equal("INC-TEAM-ONLY", shown.Number);
        Assert.Equal("Secret teammate laptop", shown.Title);
        Assert.Equal("My Team", main.Incidents.CachePopulationName);
        Assert.Equal(1, main.Incidents.CacheRowsStored);

        var still = lists.Load(DeskListScope.Practice);
        Assert.NotNull(still);
        Assert.DoesNotContain(still.Incidents!.Items, row => row.Number == "INC-TEAM-ONLY");
        Assert.Contains(still.IncidentTeam!.Items, row => row.Number == "INC-TEAM-ONLY");

        main.Incidents.CopyDetailsCommand.Execute(null);
        var copied = Assert.Single(desktop.CopiedText);
        Assert.Contains("Preset: My Team", copied, StringComparison.Ordinal);
        Assert.Contains("Team count: 1", copied, StringComparison.Ordinal);
        Assert.Contains("Jordan Lee", copied, StringComparison.Ordinal);
        Assert.Contains("Cache population: My Team", copied, StringComparison.Ordinal);
        Assert.Contains("assigned_toINuser-jordan^active=true^stateNOT IN6,7,8", copied, StringComparison.Ordinal);
        Assert.Contains("Region offices: 2", copied, StringComparison.Ordinal);
        Assert.Contains("Region filter: no", copied, StringComparison.Ordinal);
        Assert.DoesNotContain("Secret teammate laptop", copied, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoSelectedPeopleDoesNotRecordAnEmptyMyTeamDownload()
    {
        var lists = new MemoryDeskListStore();
        var settings = new MemorySettingsStore();
        settings.Save(new DeskSettings
        {
            UseSampleData = true,
            DownloadCacheOnLaunch = true,
            LeadTeamMemberIds = []
        });
        var main = new MainViewModel(settings, new RecordingDesktopServices(), lists: lists);
        await main.InitializeAsync();

        var saved = lists.Load(DeskListScope.Practice);
        Assert.NotNull(saved);
        Assert.NotNull(saved.Incidents);
        Assert.True(saved.Incidents.Items.Count > 0);
        Assert.Null(saved.IncidentTeam);
        Assert.Null(saved.RequestItemTeam);
        Assert.Null(saved.WalkUpTeam);

        var incidents = Assert.Single(main.Caches, row => row.Name == "Incidents");
        Assert.Contains("My Tickets", incidents.QueryText, StringComparison.Ordinal);
        Assert.Contains("not downloaded", incidents.TeamText, StringComparison.Ordinal);
        Assert.Contains("no team selected", incidents.TeamText, StringComparison.Ordinal);
        Assert.DoesNotContain("0 stored", incidents.TeamText, StringComparison.Ordinal);

        main.Incidents.Preset = PresetCatalog.Incidents.Single(preset => preset.Label == "My Team");
        await main.Incidents.RefreshAsync();
        Assert.Empty(main.Incidents.Items);
        Assert.Equal(WalkUpTeam.EmptyPrompt, main.Incidents.SearchHint);
        var after = lists.Load(DeskListScope.Practice);
        Assert.Null(after!.IncidentTeam);
    }

    private static CachedTicketList Fresh(string number) => new()
    {
        CapturedAt = DateTimeOffset.UtcNow,
        TotalCount = 1,
        Items = [new CachedTicketRow { SysId = number, Number = number, Title = number, StateLabel = "In Progress" }]
    };
}
