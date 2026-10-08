using System.Net;
using ServiceNowDesk.Client;
using ServiceNowDesk.GuidedSetup;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class StartupDownloadLifecycleTests
{
    [Fact]
    public async Task SplashStaysOpenWhileBootstrapRunsAndClosesWhenItFinishes()
    {
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var main = LiveDesk(new GatedListHandler(started, release));

        var connect = main.ConnectCommand.ExecuteAsync(null);
        try
        {
            var startedOrGaveUp = await Task.WhenAny(started.Task, Task.Delay(TimeSpan.FromSeconds(20)));
            Assert.Same(started.Task, startedOrGaveUp);
            Assert.False(connect.IsCompleted);
            Assert.True(main.Startup.ShowScreen);
            Assert.True(main.Startup.IsRunning);
            Assert.False(main.Startup.ClosedByUser);
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
        Assert.False(main.Startup.HasFailures);
        Assert.Equal("", main.ErrorMessage);
        Assert.Contains(main.Incidents.Items, row => row.Number == "INC-NEW");
    }

    [Fact]
    public async Task DismissingTheSplashEarlyLeavesTheLoadRunningAndRowsStillAppear()
    {
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var main = LiveDesk(new GatedListHandler(started, release));

        var connect = main.ConnectCommand.ExecuteAsync(null);
        try
        {
            var startedOrGaveUp = await Task.WhenAny(started.Task, Task.Delay(TimeSpan.FromSeconds(20)));
            Assert.Same(started.Task, startedOrGaveUp);
            Assert.True(main.Startup.ShowScreen);

            main.CloseStartupCommand.Execute(null);

            Assert.True(main.Startup.ClosedByUser);
            Assert.False(main.Startup.ShowScreen);
            Assert.True(main.Startup.ShowBar);
            Assert.True(main.Startup.IsRunning);
            Assert.False(connect.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        await connect;
        await main.AssignmentDirectoryRefresh;

        Assert.False(main.Startup.IsRunning);
        Assert.False(main.Startup.ShowBar);
        Assert.Equal("", main.ErrorMessage);
        Assert.True(main.Incidents.HasLoaded);
        Assert.Contains(main.Incidents.Items, row => row.Number == "INC-NEW");
    }

    [Fact]
    public async Task GuidedSetupClosingTheSplashDoesNotCancelBootstrap()
    {
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var main = LiveDesk(new GatedListHandler(started, release));
        main.Guided.ConsiderLaunch(signedIn: false);
        main.Guided.ChooseYes(splashVisible: false);
        main.Guided.NotifyAuthMode(ServiceNowAuthMode.BrowserSession);
        Assert.Equal(GuidedSetupPhase.SignIn, main.Guided.Phase);

        var connect = main.ConnectCommand.ExecuteAsync(null);
        try
        {
            var startedOrGaveUp = await Task.WhenAny(started.Task, Task.Delay(TimeSpan.FromSeconds(20)));
            Assert.Same(started.Task, startedOrGaveUp);
            Assert.Equal(GuidedSetupPhase.Splash, main.Guided.Phase);
            Assert.True(main.Startup.ShowScreen);

            main.Startup.Dismiss();

            Assert.Equal(GuidedSetupPhase.Tabs, main.Guided.Phase);
            Assert.True(main.Startup.IsRunning);
            Assert.False(connect.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        await connect;
        await main.AssignmentDirectoryRefresh;

        Assert.False(main.Startup.IsRunning);
        Assert.Contains(main.Incidents.Items, row => row.Number == "INC-NEW");
        Assert.Equal("", main.ErrorMessage);
    }

    [Fact]
    public async Task FailedBootstrapSurfacesAnErrorAndDoesNotPretendSuccess()
    {
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (IsUser(path))
                return Api.Json("""{"result":[{"sys_id":"sample-user","name":"Alex Rivera","user_name":"alex.rivera","email":"alex@example.com"}]}""");
            if (path.Contains("/incident", StringComparison.Ordinal))
            {
                return Api.Json(
                    """{"error":{"message":"unavailable","detail":"incident list failed"},"status":"failure"}""",
                    HttpStatusCode.InternalServerError);
            }

            if (path.Contains("sys_user_group", StringComparison.Ordinal))
                return Api.Json("""{"result":[{"sys_id":"group-cs","name":"Client Services"}]}""");
            if (path.Contains("sys_user_grmember", StringComparison.Ordinal))
                return Api.Json("""{"result":[{"group":{"value":"group-cs","display_value":"Client Services"},"user":{"value":"user-alex","display_value":"Alex Rivera"}}]}""");
            return Api.Json("""{"result":[]}""");
        });
        var main = LiveDesk(handler);

        await main.ConnectCommand.ExecuteAsync(null);

        Assert.False(main.Startup.ShowScreen);
        Assert.False(main.Startup.IsRunning);
        Assert.True(main.Startup.HasFailures);
        Assert.Contains("Incidents", main.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("incident list failed", main.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("could not be downloaded", main.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(main.Incidents.Items, row => row.Number == "INC-NEW");
        Assert.Contains(main.Startup.FailureNotes, note => note.Contains("Incidents", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BrowserSignInFailedListsStayExplainedAfterSplashCloses()
    {
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (IsUser(path))
                return Api.Json("""{"result":[{"sys_id":"sample-user","name":"Alex Rivera","user_name":"alex.rivera","email":"alex@example.com"}]}""");
            if (path.Contains("/incident", StringComparison.Ordinal)
                || path.Contains("interaction", StringComparison.Ordinal)
                || path.Contains("sc_request", StringComparison.Ordinal))
            {
                return Api.Json(
                    """{"error":{"message":"acl","detail":"list denied"},"status":"failure"}""",
                    HttpStatusCode.Forbidden);
            }

            if (path.Contains("sys_user_group", StringComparison.Ordinal))
                return Api.Json("""{"result":[{"sys_id":"group-cs","name":"Client Services"}]}""");
            if (path.Contains("sys_user_grmember", StringComparison.Ordinal))
                return Api.Json("""{"result":[{"group":{"value":"group-cs","display_value":"Client Services"},"user":{"value":"user-alex","display_value":"Alex Rivera"}}]}""");
            return Api.Json("""{"result":[]}""");
        });
        var session = Api.BasicSession();
        var main = new MainViewModel(
            new MemorySettingsStore(),
            new RecordingDesktopServices(),
            browserSignIn: new ScriptedBrowserSignIn(),
            clientFactory: (_, catalog) => ServiceNowClient.Create(session, handler, catalog));
        main.Connection.InstanceUrl = "https://example.service-now.com";
        main.Guided.ConsiderLaunch(signedIn: false);
        main.Guided.ChooseYes(splashVisible: false);
        main.Guided.NotifyAuthMode(ServiceNowAuthMode.BrowserSession);

        await main.SignInWithBrowserCommand.ExecuteAsync(null);

        Assert.True(main.IsConnected);
        Assert.False(main.Startup.ShowScreen);
        Assert.True(main.Startup.HasFailures);
        Assert.False(string.IsNullOrWhiteSpace(main.ErrorMessage));
        Assert.Contains("could not be downloaded", main.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(GuidedSetupPhase.SignIn, main.Guided.Phase);
    }

    private static MainViewModel LiveDesk(HttpMessageHandler handler)
    {
        var session = Api.BasicSession();
        var main = new MainViewModel(
            new MemorySettingsStore(),
            new RecordingDesktopServices(),
            clientFactory: (_, catalog) => ServiceNowClient.Create(session, handler, catalog));
        main.Connection.InstanceUrl = "https://example.service-now.com";
        main.Connection.Username = "alex";
        main.Connection.Password = "secret";
        main.Connection.UseSampleData = false;
        main.Connection.DownloadCacheOnLaunch = true;
        return main;
    }

    private static bool IsUser(string path) =>
        path.Contains("/sys_user", StringComparison.Ordinal)
        && !path.Contains("sys_user_group", StringComparison.Ordinal)
        && !path.Contains("sys_user_grmember", StringComparison.Ordinal);

    private sealed class ScriptedBrowserSignIn : IBrowserSignIn
    {
        public Task<BrowserSignInResult> SignInAsync(Uri instanceUri, CancellationToken cancellationToken) =>
            Task.FromResult(new BrowserSignInResult("glide_user_session=ok", "tok", DateTimeOffset.UtcNow.AddHours(4)));
    }

    /// <summary>
    /// Holds the first assignment-group page so the splash is mid-bootstrap, matching
    /// <see cref="AssignmentDirectoryTests"/>.
    /// </summary>
    private sealed class GatedListHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _started;
        private readonly TaskCompletionSource _release;

        public GatedListHandler(TaskCompletionSource started, TaskCompletionSource release)
        {
            _started = started;
            _release = release;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("sys_user_group", StringComparison.Ordinal))
            {
                _started.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
                return Api.Json("""{"result":[{"sys_id":"group-cs","name":"Client Services"}]}""", total: 1);
            }

            if (path.Contains("sys_user_grmember", StringComparison.Ordinal))
                return Api.Json("""{"result":[{"group":{"value":"group-cs","display_value":"Client Services"},"user":{"value":"user-alex","display_value":"Alex Rivera"}}]}""");
            if (path.Contains("/incident", StringComparison.Ordinal))
            {
                return Api.Json(
                    """{"result":[{"sys_id":"inc-new","number":"INC-NEW","short_description":"Downloaded","state":"2","sys_updated_on":"2026-10-06 09:00:00"}]}""",
                    total: 1);
            }

            if (IsUser(path))
                return Api.Json("""{"result":[{"sys_id":"sample-user","name":"Alex Rivera","user_name":"alex.rivera","email":"alex@example.com"}]}""");
            return Api.Json("""{"result":[]}""");
        }
    }
}
