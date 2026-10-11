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

        Assert.DoesNotContain(main.Startup.Lines, line => line.Name == "Knowledge");
        Assert.False(main.Startup.ShowScreen);
        Assert.False(main.Startup.IsRunning);
        await WaitUntilAsync(() =>
            main.Knowledge.Articles.Any(row => row.Number == "KB0001001")
            && main.Knowledge.Articles.Any(row => row.Number == "KB0001002")
            && main.Knowledge.Articles.Any(row => row.Number == "KB0001003"));
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
    public async Task SearchShowsCachedTicketsAndKnowledgeFromTheLocalLists()
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
            Knowledge = Fresh("kb-keep", "KB-KEEP", "Kept article")
        });
        var settings = new MemorySettingsStore();
        settings.Save(new DeskSettings { UseSampleData = true, DownloadCacheOnLaunch = false });
        var main = new MainViewModel(settings, new RecordingDesktopServices(), lists: lists);

        await main.InitializeAsync();

        Assert.Equal(5, main.Search.CachedCount);
        Assert.Equal("5 cached · 5 shown", main.Search.CacheStatus);
        Assert.Contains(main.Search.Results, hit => hit.Number == "INC-KEEP" && hit.Section == DeskSection.Incidents);
        Assert.Contains(main.Search.Results, hit => hit.Number == "KB-KEEP" && hit.Section == DeskSection.Knowledge);

        main.SelectedSection = DeskSection.Search;
        main.SearchText = "article";
        Assert.Equal("KB-KEEP", Assert.Single(main.Search.Results).Number);
        Assert.Equal(5, main.Search.CachedCount);
        Assert.Equal(1, main.Search.DisplayedCount);
        Assert.Equal("5 cached · 1 shown", main.Search.CacheStatus);
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
        Assert.DoesNotContain(main.Startup.Lines, line => line.Name == "Knowledge");
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

    [Fact]
    public async Task EmptyKnowledgeRefreshKeepsTheSavedArticles()
    {
        var folder = Path.Combine(Path.GetTempPath(), "snd-kb-" + Guid.NewGuid().ToString("N"));
        var catalog = new FileFormCatalogStore(folder);
        var lists = new MemoryDeskListStore();
        var session = Api.BasicSession();
        var now = DateTimeOffset.UtcNow;
        catalog.Save(session.InstanceUri, new FormCatalogSnapshot
        {
            CapturedAt = now,
            DirectoryCapturedAt = now,
            DirectoryComplete = true,
            Choices = FormCatalogFields.Independent.Select(field => new CachedChoiceList
            {
                Table = field.Table,
                Element = field.Element,
                Choices = [new Choice("1", "One")]
            }).ToList(),
            Groups = [new CachedAssignmentGroup { SysId = "group-aus", Name = "AUS DT - Client Services" }],
            Members = [new CachedGroupMember { GroupSysId = "group-aus", UserSysId = "user-jordan", Name = "Jordan Lee" }],
            ServiceOfferingsCapturedAt = now,
            ConfigurationItemsCapturedAt = now
        });
        lists.Save(DeskListScope.ForInstance(session.InstanceUri), new DeskListSnapshot
        {
            Incidents = Fresh("inc-keep", "INC-KEEP", "Kept incident"),
            Requests = Fresh("req-keep", "REQ-KEEP", "Kept request"),
            RequestItems = Fresh("ritm-keep", "RITM-KEEP", "Kept item"),
            WalkUps = Fresh("ims-keep", "IMS-KEEP", "Kept walk-up"),
            Knowledge = Fresh("kb-keep", "KB-KEEP", "Kept article")
        });
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("/sys_user", StringComparison.Ordinal)
                && !path.Contains("sys_user_group", StringComparison.Ordinal)
                && !path.Contains("sys_user_grmember", StringComparison.Ordinal))
            {
                return Api.Json("""{"result":[{"sys_id":"sample-user","name":"Alex Rivera","user_name":"alex.rivera","email":"alex@example.com"}]}""");
            }

            return Api.Json("""{"result":[]}""");
        });
        var main = new MainViewModel(
            new MemorySettingsStore(),
            new RecordingDesktopServices(),
            formCatalog: catalog,
            clientFactory: (_, store) => ServiceNowClient.Create(session, handler, store),
            lists: lists);
        main.Connection.InstanceUrl = "https://example.service-now.com";
        main.Connection.Username = "alex";
        main.Connection.Password = "secret";
        main.Connection.DownloadCacheOnLaunch = false;
        await main.ConnectCommand.ExecuteAsync(null);
        Assert.Equal("KB-KEEP", Assert.Single(main.Knowledge.Articles).Number);

        var knowledge = Assert.Single(main.Caches, row => row.Name == "Knowledge");
        await main.RefreshCacheCommand.ExecuteAsync(knowledge);

        Assert.Equal("KB-KEEP", Assert.Single(main.Knowledge.Articles).Number);
        var saved = lists.Load(DeskListScope.ForInstance(session.InstanceUri));
        Assert.NotNull(saved);
        Assert.Contains(saved.Knowledge!.Items, row => row.Number == "KB-KEEP");
        Assert.Contains("kept the saved list", knowledge.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PracticeLaunchRefreshesKnowledgeInTheBackgroundWithoutASplashLine()
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
            Knowledge = Fresh("kb-stale", "KB-STALE", "Stale article")
        });
        // Age the knowledge list past the one-day freshness window.
        var snapshot = lists.Load(DeskListScope.Practice)!;
        snapshot.Knowledge!.CapturedAt = now.AddDays(-2);
        lists.Save(DeskListScope.Practice, snapshot);

        var settings = new MemorySettingsStore();
        settings.Save(new DeskSettings { UseSampleData = true, DownloadCacheOnLaunch = false });
        var main = new MainViewModel(settings, new RecordingDesktopServices(), lists: lists);

        await main.InitializeAsync();

        // Splash finished without a Knowledge line; ticket lists stayed on the saved copy.
        Assert.Empty(main.Startup.Lines);
        Assert.DoesNotContain(main.Startup.Lines, line => line.Name == "Knowledge");
        Assert.False(main.Startup.ShowScreen);
        Assert.False(main.Startup.ShowBar);
        Assert.False(main.Startup.IsRunning);
        Assert.Contains(main.Incidents.Items, row => row.Number == "INC-KEEP");

        await WaitUntilAsync(() =>
            main.Knowledge.Articles.Any(row => row.Number == "KB0001001")
            && main.Knowledge.Articles.All(row => row.Number != "KB-STALE"));

        Assert.Empty(main.Startup.Lines);
        Assert.False(main.Startup.ShowScreen);
        Assert.Contains(main.Knowledge.Articles, row => row.Number == "KB0001002");
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
