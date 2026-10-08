using System.Net;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class CacheSettingsTests
{
    [Fact]
    public async Task FullyCachedStartupShowsNeitherScreenNorBar()
    {
        var folder = NewFolder();
        var catalog = new FileFormCatalogStore(folder);
        var lists = new MemoryDeskListStore();
        var session = Api.BasicSession();
        catalog.Save(session.InstanceUri, FreshCatalog());
        lists.Save(DeskListScope.ForInstance(session.InstanceUri), FreshLists("INC0099001", "Cached printer"));
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("/sys_user", StringComparison.Ordinal) && !path.Contains("sys_user_group", StringComparison.Ordinal) && !path.Contains("sys_user_grmember", StringComparison.Ordinal))
                return Api.Json("""{"result":[{"sys_id":"sample-user","name":"Alex Rivera","user_name":"alex.rivera","email":"alex@example.com"}]}""");
            if (path.Contains("incident", StringComparison.Ordinal)
                || path.Contains("sc_request", StringComparison.Ordinal)
                || path.Contains("sc_req_item", StringComparison.Ordinal)
                || path.Contains("interaction", StringComparison.Ordinal)
                || path.Contains("sys_choice", StringComparison.Ordinal)
                || path.Contains("sys_user_group", StringComparison.Ordinal)
                || path.Contains("sys_user_grmember", StringComparison.Ordinal))
                return Api.Json("""{"result":[{"sys_id":"inc-network","number":"INC-NETWORK","short_description":"Should not load"}]}""");
            return Api.Json("""{"result":[]}""");
        });
        var main = Desk(catalog, lists, handler);
        main.Connection.DownloadCacheOnLaunch = false;

        await main.ConnectCommand.ExecuteAsync(null);

        Assert.Equal("", main.ErrorMessage);
        Assert.False(main.Startup.ShowScreen);
        Assert.False(main.Startup.ShowBar);
        Assert.False(main.Startup.IsRunning);
        Assert.Empty(main.Startup.Lines);
        Assert.Contains(main.Incidents.Items, row => row.Number == "INC0099001" && row.Title == "Cached printer");
        Assert.DoesNotContain(main.Incidents.Items, row => row.Number == "INC-NETWORK");
        var calls = handler.Snapshot();
        Assert.DoesNotContain(calls, call => call.PathAndQuery.Contains("sys_choice", StringComparison.Ordinal));
        Assert.DoesNotContain(calls, call => call.PathAndQuery.Contains("sys_user_group", StringComparison.Ordinal));
        // Allow My Groups membership for the signed-in user. Block the full member directory download.
        Assert.DoesNotContain(calls, call =>
            call.PathAndQuery.Contains("sys_user_grmember", StringComparison.Ordinal)
            && Uri.UnescapeDataString(call.PathAndQuery).Contains("userISNOTEMPTY", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RefreshingOneCacheClearsOnlyThatCacheAndReportsSuccess()
    {
        var folder = NewFolder();
        var catalog = new FileFormCatalogStore(folder);
        var lists = new MemoryDeskListStore();
        var templates = new MemoryIncidentTemplateStore();
        templates.Save(new IncidentTemplate { Name = "Badge", ShortDescription = "Badge printer" });
        var settings = new MemorySettingsStore();
        var session = Api.BasicSession();
        catalog.Save(session.InstanceUri, FreshCatalog());
        lists.Save(DeskListScope.ForInstance(session.InstanceUri), FreshLists("INC-OLD", "Old printer"));
        var serveNewIncident = false;
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("sys_choice", StringComparison.Ordinal) || path.Contains("sys_user_group", StringComparison.Ordinal) || path.Contains("sys_user_grmember", StringComparison.Ordinal))
                throw new InvalidOperationException("Refreshing incidents should not download " + path);
            if (path.Contains("/sys_user", StringComparison.Ordinal))
                return Api.Json("""{"result":[{"sys_id":"sample-user","name":"Alex Rivera","user_name":"alex.rivera","email":"alex@example.com"}]}""");
            if (serveNewIncident && path.Contains("incident", StringComparison.Ordinal))
                return Api.Json("""{"result":[{"sys_id":"inc-new","number":"INC-NEW","short_description":"New printer","state":"2","sys_updated_on":"2026-10-01 10:00:00"}]}""");
            return Api.Json("""{"result":[]}""");
        });
        var main = Desk(catalog, lists, handler, settings, templates);
        main.Connection.Password = "secret";
        main.Connection.DownloadCacheOnLaunch = false;
        await main.ConnectCommand.ExecuteAsync(null);
        Assert.Contains(main.Incidents.Items, row => row.Number == "INC-OLD");
        handler.Calls.Clear();
        serveNewIncident = true;

        var incidents = Assert.Single(main.Caches, row => row.Name == "Incidents");
        var requests = Assert.Single(main.Caches, row => row.Name == "Requests");
        await main.RefreshCacheCommand.ExecuteAsync(incidents);

        Assert.Contains("Cleared 1 stale", incidents.Status, StringComparison.Ordinal);
        Assert.Contains("Downloaded 1 fresh", incidents.Status, StringComparison.Ordinal);
        Assert.False(incidents.IsFailed);
        Assert.StartsWith("Last good download:", incidents.LastGoodText, StringComparison.Ordinal);
        Assert.DoesNotContain("never", incidents.LastGoodText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("", requests.Status);
        Assert.Contains(main.Incidents.Items, row => row.Number == "INC-NEW");
        Assert.DoesNotContain(main.Incidents.Items, row => row.Number == "INC-OLD");
        var saved = lists.Load(DeskListScope.ForInstance(session.InstanceUri));
        Assert.NotNull(saved);
        Assert.Contains(saved.Incidents!.Items, row => row.Number == "INC-NEW");
        Assert.DoesNotContain(saved.Incidents.Items, row => row.Number == "INC-OLD");
        Assert.Contains(saved.Requests!.Items, row => row.Number == "REQ-KEEP");
        var stillThere = catalog.Load(session.InstanceUri);
        Assert.NotNull(stillThere);
        Assert.Contains(stillThere.Choices, list => list.Table == "incident" && list.Element == "state");
        Assert.Contains(stillThere.Groups, group => group.SysId == "group-aus" && group.Name == "AUS DT - Client Services");
        Assert.Contains(templates.List(), template => template.Name == "Badge");
        Assert.Equal("secret", settings.Current.Password);
        Assert.False(main.Startup.ShowScreen);
        Assert.False(main.Startup.ShowBar);
    }

    [Fact]
    public async Task FailedRedownloadReportsFailureAndKeepsAnUnrelatedCache()
    {
        var folder = NewFolder();
        var catalog = new FileFormCatalogStore(folder);
        var lists = new MemoryDeskListStore();
        var session = Api.BasicSession();
        catalog.Save(session.InstanceUri, FreshCatalog());
        lists.Save(DeskListScope.ForInstance(session.InstanceUri), FreshLists("INC-KEEP", "Keep me"));
        var failChoices = false;
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("/sys_user", StringComparison.Ordinal) && !path.Contains("sys_user_group", StringComparison.Ordinal) && !path.Contains("sys_user_grmember", StringComparison.Ordinal))
                return Api.Json("""{"result":[{"sys_id":"sample-user","name":"Alex Rivera","user_name":"alex.rivera","email":"alex@example.com"}]}""");
            if (failChoices && path.Contains("sys_choice", StringComparison.Ordinal))
            {
                return Api.Json(
                    """{"error":{"message":"unavailable","detail":"choices unavailable"},"status":"failure"}""",
                    HttpStatusCode.InternalServerError);
            }

            if (path.Contains("incident", StringComparison.Ordinal) || path.Contains("sc_request", StringComparison.Ordinal) || path.Contains("interaction", StringComparison.Ordinal))
                return Api.Json("""{"result":[]}""");
            return Api.Json("""{"result":[]}""");
        });
        var main = Desk(catalog, lists, handler);
        main.Connection.DownloadCacheOnLaunch = false;
        await main.ConnectCommand.ExecuteAsync(null);
        failChoices = true;

        var choices = Assert.Single(main.Caches, row => row.Name == "Choices (menus)");
        await main.RefreshCacheCommand.ExecuteAsync(choices);

        Assert.True(choices.IsFailed);
        Assert.Contains("unavailable", choices.Status, StringComparison.OrdinalIgnoreCase);
        var stillThere = catalog.Load(session.InstanceUri);
        Assert.NotNull(stillThere);
        Assert.Contains(stillThere.Groups, group => group.SysId == "group-aus" && group.Name == "AUS DT - Client Services");
        Assert.Contains(stillThere.Members, member => member.UserSysId == "user-jordan");
        var saved = lists.Load(DeskListScope.ForInstance(session.InstanceUri));
        Assert.NotNull(saved);
        Assert.Contains(saved.Incidents!.Items, row => row.Number == "INC-KEEP");
        Assert.Contains(main.Incidents.Assignment.Groups, group => group.Value == "group-aus");
    }

    [Fact]
    public async Task PracticeRefreshReloadsTheSampleCopyAndReportsSuccess()
    {
        var lists = new MemoryDeskListStore();
        var templates = new MemoryIncidentTemplateStore();
        templates.Save(new IncidentTemplate { Name = "Badge", ShortDescription = "Badge printer" });
        var settings = new MemorySettingsStore();
        var main = new MainViewModel(settings, new RecordingDesktopServices(), templates: templates, lists: lists);
        main.Connection.UseSampleData = true;
        await main.ConnectCommand.ExecuteAsync(null);
        Assert.False(main.Startup.ShowScreen);
        Assert.False(main.Startup.ShowBar);

        var saved = lists.Load(DeskListScope.Practice);
        Assert.NotNull(saved);
        saved.Incidents = new CachedTicketList
        {
            CapturedAt = DateTimeOffset.UtcNow,
            TotalCount = 1,
            Items = [new CachedTicketRow { SysId = "bogus", Number = "INC-BOGUS", Title = "Bogus", StateLabel = "Open", Tone = "open" }]
        };
        lists.Save(DeskListScope.Practice, saved);

        var incidents = Assert.Single(main.Caches, row => row.Name == "Incidents");
        await main.RefreshCacheCommand.ExecuteAsync(incidents);

        Assert.Contains("Cleared", incidents.Status, StringComparison.Ordinal);
        Assert.Contains("Downloaded", incidents.Status, StringComparison.Ordinal);
        Assert.False(incidents.IsFailed);
        Assert.StartsWith("Last good download:", incidents.LastGoodText, StringComparison.Ordinal);
        Assert.DoesNotContain(main.Incidents.Items, row => row.Number == "INC-BOGUS");
        Assert.Contains(main.Incidents.Items, row => row.Number == "INC0010001");
        Assert.Contains(templates.List(), template => template.Name == "Badge");
        Assert.True(settings.Current.UseSampleData);
        Assert.False(main.Startup.ShowScreen);
        Assert.False(main.Startup.ShowBar);
        Assert.Contains(main.Caches, row => row.Name == "Request items");
        Assert.Contains(main.Caches, row => row.Name == "Choices (menus)");
        Assert.Contains(main.Caches, row => row.Name == "Assignment groups");
        Assert.Contains(main.Caches, row => row.Name == "Assignment group members");
        Assert.Contains(main.Caches, row => row.Name == "Service offerings");
        Assert.Contains(main.Caches, row => row.Name == "Configuration items");
        Assert.Contains(main.Caches, row => row.Name == "Walk-ups");
        Assert.DoesNotContain(main.Caches, row => row.Name.Contains("SLA", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DownloadCacheOnLaunchDefaultsOnAndRoundTrips()
    {
        Assert.True(new DeskSettings().DownloadCacheOnLaunch);
        var connection = new ConnectionViewModel();
        Assert.True(connection.DownloadCacheOnLaunch);
        connection.InstanceUrl = "https://example.service-now.com";
        connection.DownloadCacheOnLaunch = false;
        var saved = connection.BuildSettings();
        Assert.False(saved.DownloadCacheOnLaunch);

        var again = new ConnectionViewModel();
        again.Load(saved);
        Assert.False(again.DownloadCacheOnLaunch);
        Assert.Equal("https://example.service-now.com", again.BuildSettings().InstanceUrl);
        again.DownloadCacheOnLaunch = true;
        Assert.True(again.BuildSettings().DownloadCacheOnLaunch);
    }

    [Fact]
    public async Task PracticeLaunchDownloadsWhenTheSettingIsOnAndTheCacheIsFresh()
    {
        var lists = new MemoryDeskListStore();
        var settings = new MemorySettingsStore();
        settings.Save(new DeskSettings { UseSampleData = true, DownloadCacheOnLaunch = true });
        lists.Save(DeskListScope.Practice, FreshPractice("INC-BOGUS", "Bogus"));
        var main = new MainViewModel(settings, new RecordingDesktopServices(), lists: lists);

        await main.InitializeAsync();

        Assert.True(main.IsConnected);
        Assert.True(main.IsSample);
        Assert.Equal("", main.ErrorMessage);
        Assert.False(main.Startup.ShowScreen);
        Assert.False(main.Startup.ShowBar);
        Assert.False(main.Startup.IsRunning);
        Assert.Contains(main.Startup.Lines, line => line.Name == "Incidents" && line.Percent == 100);
        Assert.Equal(10, main.Startup.Lines.Count);
        Assert.Equal("Incidents", main.Startup.Lines[0].Name);
        Assert.Equal("Request items", main.Startup.Lines[1].Name);
        Assert.Equal("Walk-ups", main.Startup.Lines[2].Name);
        Assert.Contains(main.Startup.Lines, line => line.Name == "Knowledge" && line.Percent == 100);
        Assert.Contains(main.Startup.Lines, line => line.Name == "Service offerings" && line.Percent == 100);
        Assert.Contains(main.Startup.Lines, line => line.Name == "Configuration items" && line.Percent == 100);
        Assert.DoesNotContain(main.Incidents.Items, row => row.Number == "INC-BOGUS");
        Assert.Contains(main.Incidents.Items, row => row.Number == "INC0010001");
        Assert.True(settings.Current.DownloadCacheOnLaunch);
    }

    [Fact]
    public async Task PracticeLaunchSkipsAFreshCacheWhenTheSettingIsOff()
    {
        var lists = new MemoryDeskListStore();
        var settings = new MemorySettingsStore();
        settings.Save(new DeskSettings { UseSampleData = true, DownloadCacheOnLaunch = false });
        lists.Save(DeskListScope.Practice, FreshPractice("INC-BOGUS", "Bogus"));
        var main = new MainViewModel(settings, new RecordingDesktopServices(), lists: lists);

        await main.InitializeAsync();

        Assert.True(main.IsConnected);
        Assert.False(main.Startup.ShowScreen);
        Assert.False(main.Startup.ShowBar);
        Assert.Empty(main.Startup.Lines);
        Assert.Contains(main.Incidents.Items, row => row.Number == "INC-BOGUS");
        Assert.DoesNotContain(main.Incidents.Items, row => row.Number == "INC0010001");
        Assert.False(settings.Current.DownloadCacheOnLaunch);
    }

    [Fact]
    public async Task NewBrowserSessionDownloadsAFreshCacheWhenTheSettingIsOn()
    {
        var folder = NewFolder();
        var catalog = new FileFormCatalogStore(folder);
        var lists = new MemoryDeskListStore();
        var session = Api.BasicSession();
        catalog.Save(session.InstanceUri, FreshCatalog());
        lists.Save(DeskListScope.ForInstance(session.InstanceUri), FreshLists("INC-KEEP", "Kept"));
        var choiceCalls = 0;
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (IsUser(path))
                return Api.Json("""{"result":[{"sys_id":"sample-user","name":"Alex Rivera","user_name":"alex.rivera","email":"alex@example.com"}]}""");
            if (path.Contains("sys_choice", StringComparison.Ordinal))
            {
                choiceCalls++;
                return Api.Json("""{"result":[{"value":"1","label":"One","sequence":"1"}]}""");
            }

            if (path.Contains("sys_user_group", StringComparison.Ordinal))
                return Api.Json("""{"result":[{"sys_id":"group-aus","name":"AUS DT - Client Services"}]}""");
            if (path.Contains("sys_user_grmember", StringComparison.Ordinal))
                return Api.Json("""{"result":[{"group":{"value":"group-aus","display_value":"AUS DT - Client Services"},"user":{"value":"user-jordan","display_value":"Jordan Lee"}}]}""");
            if (path.Contains("incident", StringComparison.Ordinal))
                return Api.Json("""{"result":[{"sys_id":"inc-new","number":"INC-NEW","short_description":"Downloaded","state":"2","sys_updated_on":"2026-10-06 09:00:00"}]}""");
            return Api.Json("""{"result":[]}""");
        });
        var main = new MainViewModel(
            new MemorySettingsStore(),
            new RecordingDesktopServices(),
            browserSignIn: new ScriptedBrowserSignIn("glide_user_session=replaced"),
            formCatalog: catalog,
            clientFactory: (_, store) => ServiceNowClient.Create(session, handler, store),
            lists: lists);
        main.Connection.InstanceUrl = "https://example.service-now.com";
        main.Connection.Username = "alex";
        main.Connection.Password = "secret";
        main.Connection.DownloadCacheOnLaunch = false;
        await main.ConnectCommand.ExecuteAsync(null);
        Assert.Equal("", main.ErrorMessage);
        Assert.Equal(0, choiceCalls);
        Assert.Empty(main.Startup.Lines);
        Assert.Contains(main.Incidents.Items, row => row.Number == "INC-KEEP");

        main.Connection.DownloadCacheOnLaunch = true;
        await main.SignInWithBrowserCommand.ExecuteAsync(null);

        Assert.Equal("", main.ErrorMessage);
        Assert.True(choiceCalls > 0);
        Assert.Contains(main.Startup.Lines, line => line.Name == "Incidents" && line.Percent == 100);
        Assert.False(main.Startup.ShowScreen);
        Assert.False(main.Startup.IsRunning);
        Assert.Equal("glide_user_session=replaced", main.Connection.SessionCookie);
        Assert.Contains(main.Incidents.Items, row => row.Number == "INC-NEW");
        Assert.DoesNotContain(main.Incidents.Items, row => row.Number == "INC-KEEP");
    }

    [Fact]
    public async Task LaunchDownloadFailureKeepsTheSavedCopy()
    {
        var folder = NewFolder();
        var catalog = new FileFormCatalogStore(folder);
        var lists = new MemoryDeskListStore();
        var session = Api.BasicSession();
        catalog.Save(session.InstanceUri, FreshCatalog());
        lists.Save(DeskListScope.ForInstance(session.InstanceUri), FreshLists("INC-KEEP", "Keep me"));
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            var query = request.RequestUri?.Query ?? "";
            if (IsUser(path))
                return Api.Json("""{"result":[{"sys_id":"sample-user","name":"Alex Rivera","user_name":"alex.rivera","email":"alex@example.com"}]}""");
            if (path.Contains("/incident", StringComparison.Ordinal) && query.Contains("description", StringComparison.Ordinal))
            {
                return Api.Json(
                    """{"error":{"message":"unavailable","detail":"incident list failed"},"status":"failure"}""",
                    HttpStatusCode.InternalServerError);
            }

            if (path.Contains("sys_user_group", StringComparison.Ordinal))
                return Api.Json("""{"result":[{"sys_id":"group-aus","name":"AUS DT - Client Services"}]}""");
            if (path.Contains("sys_user_grmember", StringComparison.Ordinal))
                return Api.Json("""{"result":[{"group":{"value":"group-aus","display_value":"AUS DT - Client Services"},"user":{"value":"user-jordan","display_value":"Jordan Lee"}}]}""");
            return Api.Json("""{"result":[]}""");
        });
        var main = Desk(catalog, lists, handler);
        Assert.True(main.Connection.DownloadCacheOnLaunch);
        await main.ConnectCommand.ExecuteAsync(null);

        Assert.Contains("Incidents", main.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("incident list failed", main.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("could not be downloaded", main.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(main.Startup.ShowScreen);
        Assert.False(main.Startup.IsRunning);
        Assert.True(main.Startup.HasFailures);
        var incidents = Assert.Single(main.Startup.Lines, line => line.Name == "Incidents");
        Assert.Contains("incident list failed", incidents.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(main.Incidents.Items, row => row.Number == "INC-KEEP" && row.Title == "Keep me");
        var saved = lists.Load(DeskListScope.ForInstance(session.InstanceUri));
        Assert.NotNull(saved);
        Assert.Contains(saved.Incidents!.Items, row => row.Number == "INC-KEEP" && row.Title == "Keep me");
        var stillThere = catalog.Load(session.InstanceUri);
        Assert.NotNull(stillThere);
        Assert.Contains(stillThere.Groups, group => group.SysId == "group-aus");
    }

    [Fact]
    public async Task SplashPreloadsMixTicketSectionsFirstAndSeedsMixMyTickets()
    {
        var lists = new MemoryDeskListStore();
        var settings = new MemorySettingsStore();
        settings.Save(new DeskSettings { UseSampleData = true, DownloadCacheOnLaunch = true });
        var main = new MainViewModel(settings, new RecordingDesktopServices(), lists: lists);

        await main.InitializeAsync();

        Assert.True(main.Incidents.HasLoaded);
        Assert.True(main.RequestedItems.HasLoaded);
        Assert.True(main.WalkUps.HasLoaded);
        Assert.Equal(["Incidents", "Request items", "Walk-ups"], main.Startup.Lines.Take(3).Select(line => line.Name).ToArray());
        Assert.Equal("My Tickets", main.Mix.Preset.Label);
        Assert.True(main.Mix.HasLoaded);
        Assert.Contains(main.Mix.Items, row => row.Kind == "INC");
        Assert.Contains(main.Mix.Items, row => row.Kind == "RITM");
        Assert.Contains(main.Mix.Items, row => row.Kind == "IMS");

        var before = lists.Load(DeskListScope.Practice);
        Assert.NotNull(before);
        Assert.NotNull(before.Incidents);
        Assert.NotNull(before.RequestItems);
        Assert.NotNull(before.WalkUps);

        main.SelectedSection = DeskSection.InTheMix;
        await Task.Yield();
        Assert.True(main.Mix.HasLoaded);
        Assert.Contains(main.Mix.Items, row => row.Kind == "INC");
    }

    [Fact]
    public async Task OpeningMixAfterPreloadDoesNotRequireAColdReload()
    {
        var lists = new MemoryDeskListStore();
        var settings = new MemorySettingsStore();
        settings.Save(new DeskSettings { UseSampleData = true, DownloadCacheOnLaunch = false });
        lists.Save(DeskListScope.Practice, FreshPractice("INC-CACHE", "Cached mix incident"));
        var main = new MainViewModel(settings, new RecordingDesktopServices(), lists: lists);

        await main.InitializeAsync();

        Assert.True(main.Incidents.HasLoaded);
        Assert.True(main.RequestedItems.HasLoaded);
        Assert.True(main.WalkUps.HasLoaded);
        Assert.True(main.Mix.HasLoaded);
        Assert.Contains(main.Mix.Items, row => row.Number == "INC-CACHE");
        var seededCount = main.Mix.Items.Count;

        main.SelectedSection = DeskSection.DailyWork;
        await Task.Yield();
        main.SelectedSection = DeskSection.InTheMix;
        await Task.Yield();

        Assert.True(main.Mix.HasLoaded);
        Assert.Equal(seededCount, main.Mix.Items.Count);
        Assert.Contains(main.Mix.Items, row => row.Number == "INC-CACHE");
    }

    [Fact]
    public async Task FailedRefreshKeepsLastGoodDownloadAndShowsError()
    {
        var folder = NewFolder();
        var catalog = new FileFormCatalogStore(folder);
        var lists = new MemoryDeskListStore();
        var session = Api.BasicSession();
        catalog.Save(session.InstanceUri, FreshCatalog());
        lists.Save(DeskListScope.ForInstance(session.InstanceUri), FreshLists("INC-KEEP", "Keep me"));
        var failIncidents = false;
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (IsUser(path))
                return Api.Json("""{"result":[{"sys_id":"sample-user","name":"Alex Rivera","user_name":"alex.rivera","email":"alex@example.com"}]}""");
            if (failIncidents && path.Contains("/incident", StringComparison.Ordinal))
            {
                return Api.Json(
                    """{"error":{"message":"unavailable","detail":"incident list failed"},"status":"failure"}""",
                    HttpStatusCode.InternalServerError);
            }

            return Api.Json("""{"result":[]}""");
        });
        var main = Desk(catalog, lists, handler);
        main.Connection.DownloadCacheOnLaunch = false;
        await main.ConnectCommand.ExecuteAsync(null);

        var incidents = Assert.Single(main.Caches, row => row.Name == "Incidents");
        Assert.StartsWith("Last good download:", incidents.LastGoodText, StringComparison.Ordinal);
        var goodBefore = incidents.LastGoodText;
        failIncidents = true;

        await main.RefreshCacheCommand.ExecuteAsync(incidents);

        Assert.True(incidents.IsFailed);
        Assert.Contains("incident list failed", incidents.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(goodBefore, incidents.LastGoodText);
        Assert.StartsWith("Last attempt:", incidents.LastAttemptText, StringComparison.Ordinal);
        Assert.Contains(main.Incidents.Items, row => row.Number == "INC-KEEP");
    }

    private static MainViewModel Desk(
        FileFormCatalogStore catalog,
        MemoryDeskListStore lists,
        StubHandler handler,
        MemorySettingsStore? settings = null,
        MemoryIncidentTemplateStore? templates = null)
    {
        var session = Api.BasicSession();
        var main = new MainViewModel(
            settings ?? new MemorySettingsStore(),
            new RecordingDesktopServices(),
            formCatalog: catalog,
            templates: templates,
            clientFactory: (_, store) => ServiceNowClient.Create(session, handler, store),
            lists: lists);
        main.Connection.InstanceUrl = "https://example.service-now.com";
        main.Connection.Username = "alex";
        main.Connection.Password = "secret";
        return main;
    }

    private static FormCatalogSnapshot FreshCatalog()
    {
        var now = DateTimeOffset.UtcNow;
        return new FormCatalogSnapshot
        {
            CapturedAt = now,
            DirectoryCapturedAt = now,
            DirectoryComplete = true,
            MembersVerified = true,
            Choices = FormCatalogFields.Independent.Select(field => new CachedChoiceList
            {
                Table = field.Table,
                Element = field.Element,
                Choices = [new Choice("1", "One")]
            }).ToList(),
            Groups = [new CachedAssignmentGroup { SysId = "group-aus", Name = "AUS DT - Client Services" }],
            Members = [new CachedGroupMember { GroupSysId = "group-aus", UserSysId = "user-jordan", Name = "Jordan Lee" }],
            ServiceOfferingsCapturedAt = now,
            ConfigurationItemsCapturedAt = now,
            ServiceOfferings = [new CachedNamedRecord { SysId = "offering-print", Name = "Printing" }],
            ConfigurationItems = [new CachedNamedRecord { SysId = "ci-printer", Name = "HQ-PRINTER-01" }]
        };
    }

    private static DeskListSnapshot FreshLists(string number, string title)
    {
        var now = DateTimeOffset.UtcNow;
        CachedTicketList List(string id, string rowNumber, string rowTitle) => new()
        {
            CapturedAt = now,
            TotalCount = 1,
            Items =
            [
                new CachedTicketRow
                {
                    SysId = id,
                    Number = rowNumber,
                    Title = rowTitle,
                    StateLabel = "Open",
                    Tone = "open"
                }
            ]
        };

        return new DeskListSnapshot
        {
            Incidents = List("inc-cached", number, title),
            Requests = List("req-keep", "REQ-KEEP", "Kept request"),
            RequestItems = List("ritm-keep", "RITM-KEEP", "Kept item"),
            WalkUps = List("ims-keep", "IMS-KEEP", "Kept walk-up"),
            Knowledge = List("kb-keep", "KB-KEEP", "Kept article")
        };
    }

    private static DeskListSnapshot FreshPractice(string number, string title)
    {
        var snapshot = FreshLists(number, title);
        var now = DateTimeOffset.UtcNow;
        snapshot.ChoicesCapturedAt = now;
        snapshot.GroupsCapturedAt = now;
        snapshot.MembersCapturedAt = now;
        snapshot.ServiceOfferingsCapturedAt = now;
        snapshot.ConfigurationItemsCapturedAt = now;
        return snapshot;
    }

    private static bool IsUser(string path) =>
        path.Contains("/sys_user", StringComparison.Ordinal)
        && !path.Contains("sys_user_group", StringComparison.Ordinal)
        && !path.Contains("sys_user_grmember", StringComparison.Ordinal);

    private static string NewFolder() => Path.Combine(Path.GetTempPath(), "snd-cache-" + Guid.NewGuid().ToString("N"));

    private sealed class ScriptedBrowserSignIn(string cookie) : IBrowserSignIn
    {
        public Task<BrowserSignInResult> SignInAsync(Uri instanceUri, CancellationToken cancellationToken) =>
            Task.FromResult(new BrowserSignInResult(cookie, "tok-new", DateTimeOffset.UtcNow.AddHours(4)));
    }
}
