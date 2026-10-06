using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class KnowledgeCacheTests
{
    [Fact]
    public void LocalFilterNarrowsTheCachedArticlesAndARefreshAppliesDifferences()
    {
        var knowledge = new KnowledgeWorkspaceViewModel();
        knowledge.ShowArticles(
        [
            Row("kb-1", "KB0001001", "Outlook blank folders", "Email"),
            Row("kb-2", "KB0001002", "Replace a toner cartridge", "Printers")
        ]);
        Assert.Equal(2, knowledge.Articles.Count);

        knowledge.FilterText = "toner";
        var toner = Assert.Single(knowledge.Articles);
        Assert.Equal("KB0001002", toner.Number);

        knowledge.FilterText = "";
        Assert.Equal(2, knowledge.Articles.Count);

        knowledge.FilterText = "toner";
        knowledge.ShowArticles(
        [
            Row("kb-2", "KB0001002", "Toner cartridge updated", "Printers"),
            Row("kb-3", "KB0001003", "VPN client disconnects", "Network")
        ]);

        var updated = Assert.Single(knowledge.Articles);
        Assert.Equal("KB0001002", updated.Number);
        Assert.Equal("Toner cartridge updated", updated.Title);

        knowledge.FilterText = "";
        Assert.Equal(2, knowledge.Articles.Count);
        Assert.Contains(knowledge.Articles, row => row.Number == "KB0001003");
        Assert.DoesNotContain(knowledge.Articles, row => row.Number == "KB0001001");
        Assert.Equal("No articles match that filter.", NoteFor(knowledge, "missing"));
    }

    [Fact]
    public async Task PracticeModeShowsSampleArticlesFiltersThemAndReusesTheCache()
    {
        var lists = new MemoryDeskListStore();
        var settings = new MemorySettingsStore();
        settings.Save(new DeskSettings { UseSampleData = true, DownloadCacheOnLaunch = true });
        var main = new MainViewModel(settings, new RecordingDesktopServices(), lists: lists);

        await main.InitializeAsync();

        Assert.Contains(main.Startup.Lines, line => line.Name == "Knowledge" && line.Percent == 100);
        Assert.Contains(main.Knowledge.Articles, row => row.Number == "KB0001001");
        Assert.Contains(main.Knowledge.Articles, row => row.Number == "KB0001002");
        Assert.Contains(main.Knowledge.Articles, row => row.Number == "KB0001003");

        main.SelectedSection = DeskSection.Knowledge;
        main.Knowledge.FilterText = "toner";
        Assert.Equal("KB0001002", Assert.Single(main.Knowledge.Articles).Number);
        Assert.Equal("Replace a toner cartridge", main.Knowledge.Articles[0].Title);

        main.Knowledge.FilterText = "KB0001003";
        Assert.Equal("KB0001003", Assert.Single(main.Knowledge.Articles).Number);

        main.Knowledge.FilterText = "";
        Assert.Equal(3, main.Knowledge.Articles.Count);

        main.Knowledge.SelectedArticle = main.Knowledge.Articles.First(row => row.Number == "KB0001001");
        await WaitUntilAsync(() => main.Knowledge.Body.Contains("zephyrmail", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Close Outlook", main.Knowledge.Body);
        Assert.DoesNotContain("<", main.Knowledge.Body);

        var saved = lists.Load(DeskListScope.Practice);
        Assert.NotNull(saved);
        Assert.NotNull(saved.Knowledge);
        saved.Knowledge.Items.Single(row => row.Number == "KB0001001").Title = "Cached outlook";
        lists.Save(DeskListScope.Practice, saved);

        var againSettings = new MemorySettingsStore();
        againSettings.Save(new DeskSettings { UseSampleData = true, DownloadCacheOnLaunch = false });
        var again = new MainViewModel(againSettings, new RecordingDesktopServices(), lists: lists);
        await again.InitializeAsync();

        Assert.Empty(again.Startup.Lines);
        Assert.False(again.Startup.ShowScreen);
        Assert.False(again.Startup.ShowBar);
        Assert.Contains(again.Knowledge.Articles, row => row.Number == "KB0001001" && row.Title == "Cached outlook");
        Assert.Contains(again.Knowledge.Articles, row => row.Number == "KB0001002");
    }

    [Fact]
    public async Task KnowledgeSearchRefreshesTheSavedListWithoutADownloadLog()
    {
        var lists = new MemoryDeskListStore();
        var now = DateTimeOffset.UtcNow;
        lists.Save(DeskListScope.Practice, new DeskListSnapshot
        {
            ChoicesCapturedAt = now,
            GroupsCapturedAt = now,
            MembersCapturedAt = now,
            ServiceOfferingsCapturedAt = now,
            ConfigurationItemsCapturedAt = now,
            Incidents = Fresh("inc-keep", "INC-KEEP", "Kept incident"),
            Requests = Fresh("req-keep", "REQ-KEEP", "Kept request"),
            RequestItems = Fresh("ritm-keep", "RITM-KEEP", "Kept item"),
            WalkUps = Fresh("ims-keep", "IMS-KEEP", "Kept walk-up"),
            Knowledge = Fresh("kb-bogus", "KB-BOGUS", "Bogus article")
        });
        var settings = new MemorySettingsStore();
        settings.Save(new DeskSettings { UseSampleData = true, DownloadCacheOnLaunch = false });
        var main = new MainViewModel(settings, new RecordingDesktopServices(), lists: lists);

        await main.InitializeAsync();

        Assert.Empty(main.Startup.Lines);
        Assert.Equal("KB-BOGUS", Assert.Single(main.Knowledge.Articles).Number);
        Assert.Contains(main.Incidents.Items, row => row.Number == "INC-KEEP");

        main.SelectedSection = DeskSection.Search;
        main.SearchText = "KB0001001";
        await WaitUntilAsync(() =>
            main.Search.Results.Any(hit => hit.Number == "KB0001001")
            && main.Knowledge.Articles.Any(row => row.Number == "KB0001001")
            && main.Knowledge.Articles.All(row => row.Number != "KB-BOGUS"));

        Assert.Empty(main.Startup.Lines);
        Assert.False(main.Startup.ShowScreen);
        Assert.False(main.Startup.ShowBar);
        Assert.Contains(main.Search.Results, hit => hit.Number == "KB0001001");
        Assert.Contains(main.Knowledge.Articles, row => row.Number == "KB0001002");
        Assert.Contains(main.Knowledge.Articles, row => row.Number == "KB0001003");
        var saved = lists.Load(DeskListScope.Practice);
        Assert.NotNull(saved);
        Assert.NotNull(saved.Knowledge);
        Assert.Contains(saved.Knowledge.Items, row => row.Number == "KB0001001");
        Assert.DoesNotContain(saved.Knowledge.Items, row => row.Number == "KB-BOGUS");
        Assert.Contains(main.Incidents.Items, row => row.Number == "INC-KEEP");
    }

    private static KnowledgeListRow Row(string sysId, string number, string title, string meta) => new()
    {
        SysId = sysId,
        Number = number,
        Title = title,
        Meta = meta,
        StateLabel = "Published"
    };

    private static CachedTicketList Fresh(string sysId, string number, string title) => new()
    {
        CapturedAt = DateTimeOffset.UtcNow,
        TotalCount = 1,
        Items = [new CachedTicketRow { SysId = sysId, Number = number, Title = title, StateLabel = "Open", Tone = "open" }]
    };

    private static string NoteFor(KnowledgeWorkspaceViewModel knowledge, string filter)
    {
        knowledge.FilterText = filter;
        return knowledge.ListNote;
    }

    private static async Task WaitUntilAsync(Func<bool> ready)
    {
        for (var attempt = 0; attempt < 40 && !ready(); attempt++)
            await Task.Delay(50);
    }
}
