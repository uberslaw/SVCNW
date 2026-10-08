using System.Net;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class AssignmentDirectoryTests
{
    [Fact]
    public async Task FirstRunKeepsTheDeskClosedUntilTheDirectoryFinishes()
    {
        var folder = NewFolder();
        var store = new FileFormCatalogStore(folder);
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var handler = new GatedDirectoryHandler(started, release);

        var main = new MainViewModel(
            new MemorySettingsStore(),
            new RecordingDesktopServices(),
            formCatalog: store,
            clientFactory: (session, catalog) => ServiceNowClient.Create(session, handler, catalog));
        main.Connection.InstanceUrl = "https://example.service-now.com";
        main.Connection.Username = "alex";
        main.Connection.Password = "secret";
        main.Connection.UseSampleData = false;

        var connect = main.ConnectCommand.ExecuteAsync(null);
        try
        {
            var startedOrGaveUp = await Task.WhenAny(started.Task, Task.Delay(TimeSpan.FromSeconds(20)));
            Assert.Same(started.Task, startedOrGaveUp);
            Assert.False(connect.IsCompleted);
            Assert.True(main.Startup.ShowScreen);
            Assert.False(main.Startup.ShowBar);
            Assert.Contains(main.Startup.Lines, line => line.Name == "Assignment groups");
            Assert.Contains("Downloading data", main.Startup.Title);
        }
        finally
        {
            release.TrySetResult();
        }

        await connect;
        await main.AssignmentDirectoryRefresh;
        Assert.False(main.Startup.ShowScreen);
        Assert.False(main.Startup.ShowBar);
        Assert.False(main.Startup.IsRunning);
        Assert.True(main.Incidents.HasLoaded);
        Assert.Equal("", main.ErrorMessage);
        Assert.Contains(main.Incidents.Assignment.Groups, group => group.Value == "group-cs" && group.Label == "Client Services");
        Assert.DoesNotContain("Saved assignment lists", main.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(main.Startup.Lines, line => line.Name == "Incidents" && line.Percent == 100);
        Assert.Contains(main.Startup.Lines, line => line.Name == "Walk-ups" && line.Percent == 100);

        var offline = new StubHandler((_, _) => throw new InvalidOperationException("The saved assignment lists should not call ServiceNow."));
        using var again = ServiceNowClient.Create(Api.BasicSession(), offline, store);
        var groups = await again.ListAssignmentGroupsAsync(CancellationToken.None);
        var members = await again.ListGroupMembersAsync("group-cs", CancellationToken.None);

        Assert.Contains(groups, group => group.Value == "group-cs" && group.Label == "Client Services");
        Assert.Equal("Alex Rivera", Assert.Single(members).Label);
        Assert.Empty(offline.Calls);
    }

    [Fact]
    public async Task ClosingTheLogDoesNotCancelTheDownload()
    {
        var folder = NewFolder();
        var store = new FileFormCatalogStore(folder);
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var handler = new GatedDirectoryHandler(started, release);
        var main = new MainViewModel(
            new MemorySettingsStore(),
            new RecordingDesktopServices(),
            formCatalog: store,
            clientFactory: (session, catalog) => ServiceNowClient.Create(session, handler, catalog));
        main.Connection.InstanceUrl = "https://example.service-now.com";
        main.Connection.Username = "alex";
        main.Connection.Password = "secret";
        main.Connection.UseSampleData = false;

        var connect = main.ConnectCommand.ExecuteAsync(null);
        try
        {
            var startedOrGaveUp = await Task.WhenAny(started.Task, Task.Delay(TimeSpan.FromSeconds(20)));
            Assert.Same(started.Task, startedOrGaveUp);
            Assert.False(connect.IsCompleted);
            Assert.True(main.Startup.ShowScreen);
            Assert.True(main.Startup.IsRunning);
            Assert.Contains("Downloading data", main.Startup.Title);

            main.CloseStartupCommand.Execute(null);

            Assert.False(main.Startup.ShowScreen);
            Assert.True(main.Startup.ShowBar);
            Assert.True(main.Startup.IsRunning);
            Assert.False(connect.IsCompleted);
            Assert.Contains("Downloading data", main.Startup.Title);
        }
        finally
        {
            release.TrySetResult();
        }

        await connect;
        await main.AssignmentDirectoryRefresh;

        Assert.True(main.IsConnected);
        Assert.False(main.Startup.ShowScreen);
        Assert.False(main.Startup.ShowBar);
        Assert.False(main.Startup.IsRunning);
        Assert.Equal("", main.ErrorMessage);
        Assert.True(main.Incidents.HasLoaded);
        Assert.Contains(main.Startup.Lines, line => line.Name == "Assignment groups" && line.Percent == 100);
        Assert.Contains(main.Startup.Lines, line => line.Name == "Walk-ups" && line.Percent == 100);
        Assert.Contains(main.Incidents.Assignment.Groups, group => group.Value == "group-cs" && group.Label == "Client Services");
    }

    [Fact]
    public async Task FreshDirectoryIsNotDownloadedAgain()
    {
        var folder = NewFolder();
        var store = new FileFormCatalogStore(folder);
        var session = Api.BasicSession();
        store.Save(session.InstanceUri, Directory(DateTimeOffset.UtcNow, "group-cs", "Client Services"));
        var handler = new StubHandler((_, _) => throw new InvalidOperationException("A fresh directory should not call ServiceNow."));
        using var client = ServiceNowClient.Create(session, handler, store);

        Assert.False(client.AssignmentDirectoryIsStale);
        Assert.False(await client.RefreshAssignmentDirectoryIfStaleAsync(CancellationToken.None));
        Assert.Empty(handler.Calls);

        var groups = await client.ListAssignmentGroupsAsync(CancellationToken.None);
        Assert.Contains(groups, group => group.Value == "group-cs");
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task StaleDirectoryIsRefreshedAndThenReused()
    {
        var folder = NewFolder();
        var store = new FileFormCatalogStore(folder);
        var session = Api.BasicSession();
        store.Save(session.InstanceUri, Directory(DateTimeOffset.UtcNow.AddHours(-25), "group-old", "Old Group"));
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("sys_user_group", StringComparison.Ordinal))
                return Groups();
            if (path.Contains("sys_user_grmember", StringComparison.Ordinal))
                return Members();
            return Api.Json("""{"result":[]}""");
        });
        using (var client = ServiceNowClient.Create(session, handler, store))
        {
            Assert.True(client.AssignmentDirectoryIsStale);
            Assert.True(await client.RefreshAssignmentDirectoryIfStaleAsync(CancellationToken.None));
            Assert.False(client.AssignmentDirectoryIsStale);
            Assert.Contains(handler.Calls, call => call.PathAndQuery.Contains("sys_user_group", StringComparison.Ordinal));
            Assert.Contains(handler.Calls, call => call.PathAndQuery.Contains("sys_user_grmember", StringComparison.Ordinal));
        }

        var offline = new StubHandler((_, _) => throw new InvalidOperationException("The refreshed directory should not call ServiceNow."));
        using var again = ServiceNowClient.Create(session, offline, store);
        var groups = await again.ListAssignmentGroupsAsync(CancellationToken.None);
        var members = await again.ListGroupMembersAsync("group-cs", CancellationToken.None);

        Assert.Contains(groups, group => group.Value == "group-cs" && group.Label == "Client Services");
        Assert.DoesNotContain(groups, group => group.Value == "group-old");
        Assert.Equal("Alex Rivera", Assert.Single(members).Label);
        Assert.Empty(offline.Calls);
        Assert.False(again.AssignmentDirectoryIsStale);
    }

    [Fact]
    public async Task FailedBackgroundRefreshKeepsTheSavedListsAndDoesNotBlock()
    {
        var folder = NewFolder();
        var store = new FileFormCatalogStore(folder);
        var session = Api.BasicSession();
        store.Save(session.InstanceUri, Directory(DateTimeOffset.UtcNow.AddHours(-25), "group-cs", "Client Services"));
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("sys_user_group", StringComparison.Ordinal) || path.Contains("sys_user_grmember", StringComparison.Ordinal))
            {
                return Api.Json(
                    """{"error":{"message":"unavailable","detail":"unavailable"},"status":"failure"}""",
                    HttpStatusCode.InternalServerError);
            }

            if (path.Contains("/sys_user", StringComparison.Ordinal))
                return Api.Json("""{"result":[{"sys_id":"sample-user","name":"Alex Rivera","user_name":"alex.rivera","email":"alex@example.com"}]}""");

            return Api.Json("""{"result":[]}""");
        });
        var main = new MainViewModel(
            new MemorySettingsStore(),
            new RecordingDesktopServices(),
            formCatalog: store,
            clientFactory: (_, catalog) => ServiceNowClient.Create(session, handler, catalog));
        main.Connection.InstanceUrl = "https://example.service-now.com";
        main.Connection.Username = "alex";
        main.Connection.Password = "secret";

        await main.ConnectCommand.ExecuteAsync(null);
        await main.AssignmentDirectoryRefresh;

        Assert.Equal("", main.ErrorMessage);
        Assert.Contains("Saved assignment lists are still in use", main.StatusMessage);
        Assert.True(main.Incidents.HasLoaded);
        Assert.Contains(main.Incidents.Assignment.Groups, group => group.Value == "group-cs" && group.Label == "Client Services");
        main.Incidents.NewRecordCommand.Execute(null);
        Assert.True(main.Incidents.IsNew);
    }

    [Fact]
    public async Task FailedRefreshKeepsTheSavedDirectory()
    {
        var folder = NewFolder();
        var store = new FileFormCatalogStore(folder);
        var session = Api.BasicSession();
        store.Save(session.InstanceUri, Directory(DateTimeOffset.UtcNow.AddHours(-25), "group-cs", "Client Services"));
        var handler = new StubHandler((_, _) => Api.Json(
            """{"error":{"message":"unavailable","detail":"unavailable"},"status":"failure"}""",
            HttpStatusCode.InternalServerError));
        using (var client = ServiceNowClient.Create(session, handler, store))
        {
            await Assert.ThrowsAsync<ServiceNowException>(() => client.RefreshAssignmentDirectoryIfStaleAsync(CancellationToken.None));
            var stillThere = await client.ListAssignmentGroupsAsync(CancellationToken.None);
            Assert.Contains(stillThere, group => group.Value == "group-cs" && group.Label == "Client Services");
        }

        var offline = new StubHandler((_, _) => throw new InvalidOperationException("The previous directory should still load."));
        using var again = ServiceNowClient.Create(session, offline, store);
        var groups = await again.ListAssignmentGroupsAsync(CancellationToken.None);
        Assert.Contains(groups, group => group.Value == "group-cs");
        Assert.Empty(offline.Calls);
    }

    [Fact]
    public async Task MembersAttachToTheGroupSysIdWhenServiceNowSendsTheGroupName()
    {
        var folder = NewFolder();
        var store = new FileFormCatalogStore(folder);
        var ticks = new TickList();
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            if (path.Contains("sys_user_group", StringComparison.Ordinal))
            {
                return Api.Json(
                    """{"result":[{"sys_id":"group-aus","name":"AUS DT - Client Services"}]}""",
                    total: 1);
            }

            if (path.Contains("sys_user_grmember", StringComparison.Ordinal))
            {
                Assert.DoesNotContain("user.active", query, StringComparison.OrdinalIgnoreCase);
                return Api.Json(
                    """
                    {"result":[
                      {"group":"AUS DT - Client Services","user":{"value":"user-jordan","display_value":"Jordan Lee"}},
                      {"group":{"value":"aus dt - client services","display_value":"aus dt - client services"},"user":{"value":"user-sam","display_value":"Sam Patel"}}
                    ]}
                    """,
                    total: 2);
            }

            return Api.Json("""{"result":[]}""");
        });
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler, store);
        await client.RefreshAssignmentDirectoryAsync(ticks, ticks, CancellationToken.None);

        Assert.Contains(ticks.Ticks, tick => tick.Percent == 0);
        var members = await client.ListGroupMembersAsync("group-aus", CancellationToken.None);
        Assert.Contains(members, member => member.Value == "user-jordan" && member.Label == "Jordan Lee");
        Assert.Contains(members, member => member.Value == "user-sam" && member.Label == "Sam Patel");
        Assert.Equal(1, handler.Calls.Count(call => call.PathAndQuery.Contains("sys_user_grmember", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task FreshCacheShowsCachedLinesAndDoesNotDownloadTheDirectory()
    {
        var folder = NewFolder();
        var store = new FileFormCatalogStore(folder);
        var session = Api.BasicSession();
        var snapshot = Directory(DateTimeOffset.UtcNow, "group-aus", "AUS DT - Client Services");
        snapshot.CapturedAt = DateTimeOffset.UtcNow;
        snapshot.Choices = FormCatalogFields.Independent.Select(field => new CachedChoiceList
        {
            Table = field.Table,
            Element = field.Element,
            Choices = [new Choice("1", "One")]
        }).ToList();
        snapshot.MembersVerified = true;
        snapshot.Members = [new CachedGroupMember { GroupSysId = "group-aus", UserSysId = "user-jordan", Name = "Jordan Lee" }];
        store.Save(session.InstanceUri, snapshot);
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("sys_user_group", StringComparison.Ordinal)
                || path.Contains("sys_user_grmember", StringComparison.Ordinal)
                || path.Contains("sys_choice", StringComparison.Ordinal))
                throw new InvalidOperationException("A fresh cache should not download " + path);
            if (path.Contains("/sys_user", StringComparison.Ordinal))
                return Api.Json("""{"result":[{"sys_id":"sample-user","name":"Alex Rivera","user_name":"alex.rivera","email":"alex@example.com"}]}""");
            return Api.Json("""{"result":[]}""");
        });
        var main = new MainViewModel(
            new MemorySettingsStore(),
            new RecordingDesktopServices(),
            formCatalog: store,
            clientFactory: (_, catalog) => ServiceNowClient.Create(session, handler, catalog));
        main.Connection.InstanceUrl = "https://example.service-now.com";
        main.Connection.Username = "alex";
        main.Connection.Password = "secret";
        main.Connection.DownloadCacheOnLaunch = false;

        await main.ConnectCommand.ExecuteAsync(null);

        Assert.False(main.Startup.ShowScreen);
        Assert.False(main.Startup.ShowBar);
        Assert.False(main.Startup.IsRunning);
        Assert.Equal("", main.ErrorMessage);
        Assert.Contains(main.Startup.Lines, line => line.Name == "Form choices" && line.Text.Contains("cached") && line.Percent == 100);
        Assert.Contains(main.Startup.Lines, line => line.Name == "Assignment groups" && line.Text.Contains("cached"));
        Assert.Contains(main.Startup.Lines, line => line.Name == "Assignment group members" && line.Text.Contains("cached"));
        Assert.Contains(main.Startup.Lines, line => line.Name == "Incidents" && line.Percent == 100);
        Assert.Contains(main.Startup.Lines, line => line.Name == "Requests" && line.Percent == 100);
        Assert.Contains(main.Startup.Lines, line => line.Name == "Walk-ups" && line.Percent == 100);
        Assert.Equal(9, main.Startup.Lines.Count);
        Assert.DoesNotContain(main.Startup.Lines, line => line.Name == "Knowledge");
        Assert.Contains(main.Startup.Lines, line => line.Name == "Service offerings" && line.Text.Contains("cached"));
        Assert.Contains(main.Startup.Lines, line => line.Name == "Configuration items" && line.Text.Contains("cached"));
        main.Incidents.NewRecordCommand.Execute(null);
        main.Incidents.Assignment.GroupId = "aus dt - client services";
        await main.Incidents.Assignment.WhenReady;
        Assert.Equal("group-aus", main.Incidents.Assignment.GroupId);
        Assert.Contains(main.Incidents.Assignment.Members, member => member.Value == "user-jordan" && member.Label == "Jordan Lee");
        Assert.Contains(main.Incidents.Assignment.Members, member => member.Label == "Unassigned");
    }

    [Fact]
    public async Task ChoosingAMemberWithAGuidSysIdLeavesTheNameVisible()
    {
        const string guid = "6ba7b8109dad11d180b400c04fd430c8";
        var fields = new AssignmentFields();
        fields.Members.Add(new Choice(guid, "Alex Rivera"));
        fields.MemberId = guid;

        Assert.Equal("Alex Rivera", fields.SelectedMemberLabel);
        Assert.DoesNotContain(guid, fields.SelectedMemberLabel, StringComparison.OrdinalIgnoreCase);
        var chosen = Assert.Single(fields.Members, choice => choice.Value.Equals(guid, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Alex Rivera", chosen.ToString());
        Assert.DoesNotContain(guid, chosen.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Choice {", chosen.ToString(), StringComparison.Ordinal);
        await Task.CompletedTask;
    }

    private static FormCatalogSnapshot Directory(DateTimeOffset capturedAt, string sysId, string name) => new()
    {
        CapturedAt = DateTimeOffset.UtcNow,
        DirectoryCapturedAt = capturedAt,
        DirectoryComplete = true,
        MembersVerified = true,
        Groups = [new CachedAssignmentGroup { SysId = sysId, Name = name }],
        Members =
        [
            new CachedGroupMember { GroupSysId = sysId, UserSysId = "user-alex", Name = "Alex Rivera" }
        ],
        ServiceOfferingsCapturedAt = capturedAt,
        ConfigurationItemsCapturedAt = capturedAt,
        ServiceOfferings = [new CachedNamedRecord { SysId = "offering-print", Name = "Printing" }],
        ConfigurationItems = [new CachedNamedRecord { SysId = "ci-printer", Name = "HQ-PRINTER-01" }]
    };

    private static HttpResponseMessage Groups() =>
        Api.Json("""{"result":[{"sys_id":"group-cs","name":"Client Services"}]}""");

    private static HttpResponseMessage Members() =>
        Api.Json("""{"result":[{"group":{"value":"group-cs","display_value":"Client Services"},"user":{"value":"user-alex","display_value":"Alex Rivera"}}]}""");

    private static string NewFolder() => Path.Combine(Path.GetTempPath(), "snd-directory-" + Guid.NewGuid().ToString("N"));

    private sealed class TickList : IProgress<DownloadTick>
    {
        public List<DownloadTick> Ticks { get; } = [];

        public void Report(DownloadTick value) => Ticks.Add(value);
    }

    private sealed class GatedDirectoryHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _started;
        private readonly TaskCompletionSource _release;

        public GatedDirectoryHandler(TaskCompletionSource started, TaskCompletionSource release)
        {
            _started = started;
            _release = release;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("sys_user_grmember", StringComparison.Ordinal))
            {
                var query = Uri.UnescapeDataString(request.RequestUri?.Query ?? "");
                // My Groups membership (script or signed-in sys_id) must not start the directory gate.
                if (query.Contains("getUserID", StringComparison.Ordinal)
                    || (query.Contains("user=", StringComparison.Ordinal)
                        && !query.Contains("userISNOTEMPTY", StringComparison.Ordinal)))
                    return Api.Json("""{"result":[{"group":{"value":"group-cs","display_value":"Client Services"}}]}""");

                _started.TrySetResult();
                await _release.Task;
                return Members();
            }

            if (path.Contains("sys_user_group", StringComparison.Ordinal))
            {
                _started.TrySetResult();
                await _release.Task;
                return Groups();
            }

            if (path.Contains("/sys_user", StringComparison.Ordinal))
                return Api.Json("""{"result":[{"sys_id":"sample-user","name":"Alex Rivera","user_name":"alex.rivera","email":"alex@example.com"}]}""");

            return Api.Json("""{"result":[]}""");
        }
    }
}
