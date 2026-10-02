using System.Net;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class AssignmentDirectoryTests
{
    [Fact]
    public async Task FirstRunDownloadsTheDirectoryWithoutBlockingTheIncidentList()
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
            var done = await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(20)));
            Assert.True(started.Task.IsCompleted);
            Assert.Same(connect, done);
            await connect;
            Assert.True(main.Incidents.HasLoaded);
            Assert.Equal("", main.ErrorMessage);
            main.Incidents.NewRecordCommand.Execute(null);
            main.Incidents.ShortDescription = "While lists download";
            Assert.True(main.Incidents.IsNew);
            Assert.DoesNotContain(main.Incidents.Assignment.Groups, group => group.Value == "group-cs");
        }
        finally
        {
            release.TrySetResult();
        }

        await main.AssignmentDirectoryRefresh;
        Assert.Equal("While lists download", main.Incidents.ShortDescription);
        Assert.True(main.Incidents.IsNew);
        Assert.Equal("", main.Incidents.Assignment.GroupId);
        Assert.Contains(main.Incidents.Assignment.Groups, group => group.Value == "group-cs" && group.Label == "Client Services");
        Assert.DoesNotContain("Saved assignment lists", main.StatusMessage, StringComparison.OrdinalIgnoreCase);

        var offline = new StubHandler((_, _) => throw new InvalidOperationException("The saved assignment lists should not call ServiceNow."));
        using var again = ServiceNowClient.Create(Api.BasicSession(), offline, store);
        var groups = await again.ListAssignmentGroupsAsync(CancellationToken.None);
        var members = await again.ListGroupMembersAsync("group-cs", CancellationToken.None);

        Assert.Contains(groups, group => group.Value == "group-cs" && group.Label == "Client Services");
        Assert.Equal("Alex Rivera", Assert.Single(members).Label);
        Assert.Empty(offline.Calls);
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

    private static FormCatalogSnapshot Directory(DateTimeOffset capturedAt, string sysId, string name) => new()
    {
        CapturedAt = DateTimeOffset.UtcNow,
        DirectoryCapturedAt = capturedAt,
        DirectoryComplete = true,
        Groups = [new CachedAssignmentGroup { SysId = sysId, Name = name }],
        Members =
        [
            new CachedGroupMember { GroupSysId = sysId, UserSysId = "user-alex", Name = "Alex Rivera" }
        ]
    };

    private static HttpResponseMessage Groups() =>
        Api.Json("""{"result":[{"sys_id":"group-cs","name":"Client Services"}]}""");

    private static HttpResponseMessage Members() =>
        Api.Json("""{"result":[{"group":{"value":"group-cs","display_value":"Client Services"},"user":{"value":"user-alex","display_value":"Alex Rivera"}}]}""");

    private static string NewFolder() => Path.Combine(Path.GetTempPath(), "snd-directory-" + Guid.NewGuid().ToString("N"));

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
