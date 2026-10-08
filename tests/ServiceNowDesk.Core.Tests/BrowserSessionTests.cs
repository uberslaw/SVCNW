using System.Net;
using ServiceNowDesk.Client;
using ServiceNowDesk.GuidedSetup;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class BrowserSessionTests
{
    [Fact]
    public void CookieHeaderKeepsTheInstanceSessionAndDropsOtherSites()
    {
        var header = BrowserSessionCookies.BuildHeader(
        [
            new BrowserCookie("glide_user_session", "abc", ".service-now.com", "/"),
            new BrowserCookie("JSESSIONID", "xyz", "example.service-now.com", "/"),
            new BrowserCookie("ESTSAUTH", "idp", "login.microsoftonline.com", "/"),
            new BrowserCookie("bad", "a;\r\nSet-Cookie: x", "example.service-now.com", "/")
        ],
        new Uri("https://example.service-now.com/"));

        Assert.Contains("glide_user_session=abc", header);
        Assert.Contains("JSESSIONID=xyz", header);
        Assert.DoesNotContain("ESTSAUTH", header);
        Assert.DoesNotContain("Set-Cookie", header);
    }

    [Fact]
    public void BrowserSignInRequiresASavedSession()
    {
        var ex = Assert.Throws<ArgumentException>(() => ServiceNowSession.FromSettings(new DeskSettings
        {
            InstanceUrl = "https://example.service-now.com",
            AuthMode = ServiceNowAuthMode.BrowserSession
        }));

        Assert.Contains("browser", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BrowserSignInHidesThePasswordFields()
    {
        var connection = new ConnectionViewModel { AuthMode = ServiceNowAuthMode.BrowserSession };

        Assert.True(connection.ShowBrowserSignIn);
        Assert.False(connection.ShowUserPassword);
        Assert.False(connection.ShowOAuth);
        Assert.Equal("No browser sign-in yet.", connection.BrowserSessionStatus);

        connection.SessionCookie = "glide_user_session=abc";
        connection.SessionCapturedAt = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        Assert.Contains("Browser sign-in saved", connection.BrowserSessionStatus);
    }

    [Fact]
    public async Task BrowserSessionSendsTheCookieAndUserToken()
    {
        var handler = new StubHandler((request, _) =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.DoesNotContain("oauth_token", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            var cookie = Assert.Single(request.Headers.GetValues("Cookie"));
            Assert.Contains("glide_user_session=abc", cookie);
            Assert.Equal("tok-ck", Assert.Single(request.Headers.GetValues("X-UserToken")));
            return Api.Json("""{"result":[{"sys_id":"user-1","name":"Alex Rivera","user_name":"alex","email":"alex@example.com"}]}""");
        });

        using var client = ServiceNowClient.Create(BrowserSession(), handler);
        var user = await client.GetCurrentUserAsync(CancellationToken.None);

        Assert.Equal("Alex Rivera", user.Name);
        Assert.DoesNotContain("abc", client.RecentActivity[0].Path);
        Assert.DoesNotContain("tok-ck", client.RecentActivity[0].Path);
    }

    [Fact]
    public async Task PasswordRefusalPointsAtBrowserSignIn()
    {
        var handler = new StubHandler((_, _) => Api.Json(
            """{"error":{"message":"Required to provide Auth information","detail":"Required to provide Auth information"},"status":"failure"}""",
            HttpStatusCode.Unauthorized));
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);

        var ex = await Assert.ThrowsAsync<ServiceNowException>(() => client.GetCurrentUserAsync(CancellationToken.None));

        Assert.Contains("Browser sign-in", ex.Message);
    }

    [Fact]
    public async Task ExpiredBrowserSessionAsksForAnotherSignIn()
    {
        var handler = new StubHandler((_, _) => Api.Json(
            """{"error":{"message":"Required to provide Auth information","detail":"User Not Authenticated"},"status":"failure"}""",
            HttpStatusCode.Unauthorized));
        using var client = ServiceNowClient.Create(BrowserSession(), handler);

        var ex = await Assert.ThrowsAsync<ServiceNowException>(() => client.GetCurrentUserAsync(CancellationToken.None));

        Assert.Contains("sign in with the browser again", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TableAclForbiddenDoesNotRejectTheBrowserSession()
    {
        var handler = new StubHandler((_, _) => Api.Json(
            """{"error":{"message":"Insufficient rights","detail":"ACL blocked incident"},"status":"failure"}""",
            HttpStatusCode.Forbidden));
        using var client = ServiceNowClient.Create(BrowserSession(), handler);
        var rejected = 0;
        client.BrowserSessionRejected += (_, _) => rejected++;

        var ex = await Assert.ThrowsAsync<ServiceNowException>(() => client.GetCurrentUserAsync(CancellationToken.None));

        Assert.Equal(0, rejected);
        Assert.DoesNotContain("sign in with the browser again", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ACL blocked incident", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnauthenticatedForbiddenStillRejectsTheBrowserSession()
    {
        var handler = new StubHandler((_, _) => Api.Json(
            """{"error":{"message":"Required to provide Auth information","detail":"User Not Authenticated"},"status":"failure"}""",
            HttpStatusCode.Forbidden));
        using var client = ServiceNowClient.Create(BrowserSession(), handler);
        var rejected = 0;
        client.BrowserSessionRejected += (_, _) => rejected++;

        var ex = await Assert.ThrowsAsync<ServiceNowException>(() => client.GetCurrentUserAsync(CancellationToken.None));

        Assert.Equal(1, rejected);
        Assert.Contains("sign in with the browser again", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SavedFormListsLoadWithoutCallingServiceNow()
    {
        var folder = NewFolder();
        var store = new FileFormCatalogStore(folder);
        var handler = new StubHandler((_, _) => Api.Json("""{"result":[{"value":"6","label":"Resolved"},{"value":"hardware","label":"Hardware"}]}"""));
        using (var client = ServiceNowClient.Create(Api.BasicSession(), handler, store))
        {
            Assert.True(client.FormCatalogIsStale);
            await client.RefreshFormCatalogAsync(CancellationToken.None);
            Assert.False(client.FormCatalogIsStale);
            Assert.True(client.HasCachedChoices);
        }

        var offline = new StubHandler((_, _) => throw new InvalidOperationException("The saved form list should not call ServiceNow."));
        using var again = ServiceNowClient.Create(Api.BasicSession(), offline, store);
        var states = await again.GetChoicesAsync("incident", "state", null, CancellationToken.None);
        var subs = await again.GetChoicesAsync("incident", "subcategory", "hardware", CancellationToken.None);

        Assert.Contains(states, choice => choice.Value == "6" && choice.Label == "Resolved");
        Assert.Contains(subs, choice => choice.Value == "hardware");
        Assert.Empty(offline.Calls);
    }

    [Fact]
    public async Task CatalogQuestionsAreReusedUntilTheNextRefresh()
    {
        var folder = NewFolder();
        var store = new FileFormCatalogStore(folder);
        var handler = new StubHandler((_, _) => Api.Json("""
            {"result":{"sys_id":"item-1","variables":[{"name":"department","label":"Department","type":"select","mandatory":true,"choices":[{"value":"finance","label":"Finance"}]}]}}
            """));
        using (var client = ServiceNowClient.Create(Api.BasicSession(), handler, store))
        {
            var first = await client.GetCatalogVariablesAsync("item-1", CancellationToken.None);
            Assert.Equal("Department", first[0].Label);
            Assert.Equal("finance", first[0].Choices[0].Value);
        }

        var offline = new StubHandler((_, _) => throw new InvalidOperationException("The saved catalog form should not call ServiceNow."));
        using var again = ServiceNowClient.Create(Api.BasicSession(), offline, store);
        var second = await again.GetCatalogVariablesAsync("item-1", CancellationToken.None);

        Assert.Equal("Department", second[0].Label);
        Assert.Empty(offline.Calls);
    }

    [Fact]
    public async Task SavedAssignmentDirectoryLoadsWithoutCallingServiceNow()
    {
        var folder = NewFolder();
        var store = new FileFormCatalogStore(folder);
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("sys_user_group", StringComparison.Ordinal))
                return Api.Json("""{"result":[{"sys_id":"group-cs","name":"Client Services"}]}""");
            if (path.Contains("sys_user_grmember", StringComparison.Ordinal))
            {
                return Api.Json("""
                    {"result":[{"group":{"value":"group-cs","display_value":"Client Services"},"user":{"value":"user-alex","display_value":"Alex Rivera"}}]}
                    """);
            }

            return Api.Json("""{"result":[]}""");
        });
        using (var client = ServiceNowClient.Create(Api.BasicSession(), handler, store))
            await client.RefreshFormCatalogAsync(CancellationToken.None);

        var offline = new StubHandler((_, _) => throw new InvalidOperationException("The saved assignment lists should not call ServiceNow."));
        using var again = ServiceNowClient.Create(Api.BasicSession(), offline, store);
        var groups = await again.ListAssignmentGroupsAsync(CancellationToken.None);
        var members = await again.ListGroupMembersAsync("group-cs", CancellationToken.None);

        Assert.Contains(groups, group => group.Value == "group-cs" && group.Label == "Client Services");
        Assert.Equal("Alex Rivera", Assert.Single(members).Label);
        Assert.Empty(offline.Calls);
    }

    [Fact]
    public void CorruptCatalogFileIsIgnored()
    {
        var folder = NewFolder();
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "form-catalog.example.service-now.com.json"), "{");
        var store = new FileFormCatalogStore(folder);

        Assert.Null(store.Load(new Uri("https://example.service-now.com/")));
    }

    [Fact]
    public void BrowserSessionExpiresTwentyFourHoursAfterItWasSaved()
    {
        var now = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        var expired = BrowserSettings(now.AddHours(-24).AddMinutes(-1));
        var current = BrowserSettings(now.AddHours(-1));
        var boundary = BrowserSettings(now.AddHours(-24));

        Assert.True(BrowserSignInClock.IsSavedSessionExpired(expired, now));
        Assert.False(BrowserSignInClock.IsSavedSessionExpired(current, now));
        Assert.True(BrowserSignInClock.IsSavedSessionExpired(boundary, now));
        Assert.Equal(now.AddHours(-1).AddHours(24), new BrowserSignInClock(now.AddHours(-1)).ExpiresAtUtc);
    }

    [Fact]
    public void LegacyCapturedAtIsTheSignInClock()
    {
        var now = DateTimeOffset.UtcNow;
        var captured = now.AddHours(-25).ToOffset(TimeSpan.FromHours(10));
        var settings = new DeskSettings
        {
            AuthMode = ServiceNowAuthMode.BrowserSession,
            SessionCookie = "glide_user_session=abc",
            UserToken = "tok",
            SessionCapturedAt = captured
        };

        Assert.Null(settings.SignedInAt);
        Assert.True(BrowserSignInClock.IsSavedSessionExpired(settings, now));

        settings.SessionCapturedAt = now.AddHours(-1);
        Assert.False(BrowserSignInClock.IsSavedSessionExpired(settings, now));
    }

    [Fact]
    public void EarlierTokenExpiryBeatsTheTwentyFourHourCap()
    {
        var signedIn = new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.Zero);
        var clock = BrowserSignInClock.FromSignIn(signedIn, expiresInSeconds: 3600, absoluteExpiry: signedIn.AddHours(48));

        Assert.Equal(signedIn, clock.SignedInAtUtc);
        Assert.Equal(signedIn.AddHours(1), clock.ExpiresAtUtc);
        Assert.False(clock.IsExpiredAt(signedIn.AddMinutes(59)));
        Assert.True(clock.IsExpiredAt(signedIn.AddHours(1)));

        var longLived = BrowserSignInClock.FromSignIn(signedIn, expiresInSeconds: 48 * 3600);
        Assert.Equal(signedIn.AddHours(24), longLived.ExpiresAtUtc);
    }

    [Fact]
    public void TokenRefreshDoesNotExtendTheTwentyFourHourClock()
    {
        var signedIn = new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
        var settings = BrowserSettings(signedIn);
        settings.SessionExpiresAt = signedIn.AddMinutes(30);

        BrowserSignInClock.ApplyTokenRefresh(settings, signedIn.AddHours(48));

        Assert.Equal(signedIn, settings.SignedInAt);
        Assert.Equal(signedIn, settings.SessionCapturedAt);
        Assert.Equal(signedIn.AddHours(24), settings.SessionExpiresAt);
        Assert.False(BrowserSignInClock.IsSavedSessionExpired(settings, signedIn.AddHours(24).AddMinutes(-1)));
        Assert.True(BrowserSignInClock.IsSavedSessionExpired(settings, signedIn.AddHours(24)));

        BrowserSignInClock.ApplyTokenRefresh(settings, signedIn.AddHours(2));
        Assert.Equal(signedIn, settings.SignedInAt);
        Assert.Equal(signedIn.AddHours(2), settings.SessionExpiresAt);
    }

    [Fact]
    public void PasswordAndClientCredentialsIgnoreTheBrowserCutoff()
    {
        var signedIn = DateTimeOffset.UtcNow.AddHours(-30);
        var now = DateTimeOffset.UtcNow;
        Assert.False(BrowserSignInClock.IsSavedSessionExpired(new DeskSettings
        {
            AuthMode = ServiceNowAuthMode.Basic,
            Username = "alex",
            Password = "secret",
            SignedInAt = signedIn,
            SessionCapturedAt = signedIn
        }, now));
        Assert.False(BrowserSignInClock.IsSavedSessionExpired(new DeskSettings
        {
            AuthMode = ServiceNowAuthMode.OAuthPassword,
            Username = "alex",
            Password = "secret",
            ClientId = "id",
            ClientSecret = "secret",
            SignedInAt = signedIn
        }, now));
        Assert.False(BrowserSignInClock.IsSavedSessionExpired(new DeskSettings
        {
            AuthMode = ServiceNowAuthMode.OAuthClientCredentials,
            ClientId = "id",
            ClientSecret = "secret",
            SignedInAt = signedIn,
            SessionExpiresAt = signedIn.AddMinutes(30)
        }, now));
    }

    [Fact]
    public async Task ExpiredBrowserSessionIsClearedOnLaunchAndNotUsed()
    {
        var signedIn = DateTimeOffset.UtcNow.AddHours(-24).AddMinutes(-1);
        var store = new MemorySettingsStore();
        store.Save(SavedBrowser(signedIn, signedInOnly: false));
        var used = 0;
        var main = new MainViewModel(
            store,
            new RecordingDesktopServices(),
            clientFactory: (_, _) =>
            {
                used++;
                throw new InvalidOperationException("The expired browser session must not be sent to ServiceNow.");
            });

        await main.InitializeAsync();

        Assert.Equal(0, used);
        Assert.False(main.IsConnected);
        Assert.False(main.IsBusy);
        Assert.Equal(DeskSection.Connection, main.SelectedSection);
        Assert.Equal(BrowserSignInClock.ExpiredStatus, main.ErrorMessage);
        Assert.Equal(BrowserSignInClock.ExpiredStatus, main.StatusMessage);
        Assert.Equal("No browser sign-in yet.", main.Connection.BrowserSessionStatus);
        Assert.True(main.ConnectCommand.CanExecute(null));
        Assert.Equal("", store.Current.SessionCookie);
        Assert.Equal("", store.Current.UserToken);
        Assert.Null(store.Current.SignedInAt);
        Assert.Null(store.Current.SessionCapturedAt);
        Assert.Null(store.Current.SessionExpiresAt);
        Assert.Equal("https://kept.service-now.com", store.Current.InstanceUrl);
        Assert.Equal(ServiceNowAuthMode.BrowserSession, store.Current.AuthMode);
        Assert.Equal("kept-password", store.Current.Password);
        AssertNotificationSettings(store.Current);
    }

    [Fact]
    public async Task RecentBrowserSessionStillConnects()
    {
        var signedIn = DateTimeOffset.UtcNow.AddHours(-1);
        var store = new MemorySettingsStore();
        store.Save(SavedBrowser(signedIn, signedInOnly: true));
        var handler = new StubHandler((request, _) => LiveOrEmpty(request));
        var main = Desk(store, handler);

        await main.InitializeAsync();

        Assert.True(main.IsConnected);
        Assert.Equal("", main.ErrorMessage);
        Assert.Contains("Connected as", main.StatusMessage);
        Assert.NotEmpty(handler.Calls);
        Assert.Equal("glide_user_session=abc", store.Current.SessionCookie);
        Assert.Equal("tok-ck", store.Current.UserToken);
        Assert.Equal(signedIn, store.Current.SignedInAt);
        Assert.Equal(signedIn.AddHours(24), store.Current.SessionExpiresAt);
        Assert.Equal("https://kept.service-now.com", store.Current.InstanceUrl);
        AssertNotificationSettings(store.Current);
    }

    [Fact]
    public async Task RejectedBrowserSessionReturnsToConnectionWithoutAManualDisconnect()
    {
        var signedIn = DateTimeOffset.UtcNow.AddHours(-1);
        var store = new MemorySettingsStore();
        store.Save(SavedBrowser(signedIn, signedInOnly: true));
        var reject = false;
        var handler = new StubHandler((request, _) =>
        {
            if (reject)
            {
                return Api.Json(
                    """{"error":{"message":"Required to provide Auth information","detail":"User Not Authenticated"},"status":"failure"}""",
                    HttpStatusCode.Unauthorized);
            }

            return LiveOrEmpty(request);
        });
        var main = Desk(store, handler);

        await main.InitializeAsync();
        Assert.True(main.IsConnected);

        reject = true;
        await main.RefreshActiveCommand.ExecuteAsync(null);

        Assert.False(main.IsConnected);
        Assert.Equal(DeskSection.Connection, main.SelectedSection);
        Assert.Equal(BrowserSignInClock.ExpiredStatus, main.ErrorMessage);
        Assert.Equal(BrowserSignInClock.ExpiredStatus, main.StatusMessage);
        Assert.Equal("No browser sign-in yet.", main.Connection.BrowserSessionStatus);
        Assert.True(main.ConnectCommand.CanExecute(null));
        Assert.Equal("", store.Current.SessionCookie);
        Assert.Equal("", store.Current.UserToken);
        Assert.Equal("https://kept.service-now.com", store.Current.InstanceUrl);
        AssertNotificationSettings(store.Current);

        var callsAfterClear = handler.Calls.Count;
        await main.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(callsAfterClear, handler.Calls.Count);
        Assert.False(main.IsConnected);
        Assert.True(main.ConnectCommand.CanExecute(null));
        Assert.Contains("browser", main.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidGrantClearsTheBrowserSession()
    {
        var signedIn = DateTimeOffset.UtcNow.AddMinutes(-20);
        var saved = SavedBrowser(signedIn, signedInOnly: true);
        saved.SessionExpiresAt = signedIn.AddHours(2);
        var store = new MemorySettingsStore();
        store.Save(saved);
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (IsUserLookup(path))
                return User();
            return Api.Json(
                """{"error":"invalid_grant","error_description":"Token is no longer valid"}""",
                HttpStatusCode.BadRequest);
        });
        var main = Desk(store, handler);

        await main.InitializeAsync();

        Assert.NotEmpty(handler.Calls);
        Assert.False(main.IsConnected);
        Assert.Equal(DeskSection.Connection, main.SelectedSection);
        Assert.Equal(BrowserSignInClock.ExpiredStatus, main.ErrorMessage);
        Assert.Equal(BrowserSignInClock.ExpiredStatus, main.StatusMessage);
        Assert.Equal("No browser sign-in yet.", main.Connection.BrowserSessionStatus);
        Assert.True(main.ConnectCommand.CanExecute(null));
        Assert.Equal("", store.Current.SessionCookie);
        Assert.Equal("", store.Current.UserToken);
        Assert.Null(store.Current.SignedInAt);
        Assert.Equal("https://kept.service-now.com", store.Current.InstanceUrl);
        AssertNotificationSettings(store.Current);
    }

    [Fact]
    public async Task AclForbiddenDuringBrowserBootstrapKeepsTheSavedSession()
    {
        var signedIn = DateTimeOffset.UtcNow.AddMinutes(-5);
        var store = new MemorySettingsStore();
        store.Save(SavedBrowser(signedIn, signedInOnly: true));
        var rejected = 0;
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (IsUserLookup(path))
                return User();
            if (path.Contains("/incident", StringComparison.Ordinal)
                || path.Contains("kb_knowledge", StringComparison.Ordinal)
                || path.Contains("interaction", StringComparison.Ordinal))
            {
                return Api.Json(
                    """{"error":{"message":"Insufficient rights","detail":"ACL blocked"},"status":"failure"}""",
                    HttpStatusCode.Forbidden);
            }

            return Api.Json("""{"result":[]}""");
        });
        var main = new MainViewModel(
            store,
            new RecordingDesktopServices(),
            clientFactory: (session, catalog) =>
            {
                var client = ServiceNowClient.Create(session, handler, catalog);
                client.BrowserSessionRejected += (_, _) => rejected++;
                return client;
            });
        main.Connection.DownloadCacheOnLaunch = true;

        await main.InitializeAsync();

        Assert.Equal(0, rejected);
        Assert.True(main.IsConnected);
        Assert.Contains("Connected as", main.StatusMessage);
        Assert.Equal("glide_user_session=abc", store.Current.SessionCookie);
        Assert.Equal("tok-ck", store.Current.UserToken);
        Assert.Contains("Browser sign-in saved", main.Connection.BrowserSessionStatus);
        Assert.NotEqual("No browser sign-in yet.", main.Connection.BrowserSessionStatus);
    }

    [Fact]
    public async Task BrowserSignInSaveIsRecognizedForAutoConnect()
    {
        var store = new MemorySettingsStore();
        store.Save(new DeskSettings
        {
            InstanceUrl = DeskSettings.DefaultInstanceUrl,
            AuthMode = ServiceNowAuthMode.BrowserSession
        });
        var connects = 0;
        var signIn = new CountingBrowserSignIn(() =>
        {
            connects++;
            return new BrowserSignInResult("glide_user_session=fresh", "tok-fresh", DateTimeOffset.UtcNow.AddHours(4));
        });
        var handler = new StubHandler((request, _) => LiveOrEmpty(request));
        var main = new MainViewModel(
            store,
            new RecordingDesktopServices(),
            browserSignIn: signIn,
            clientFactory: (session, catalog) => ServiceNowClient.Create(session, handler, catalog));

        await main.InitializeAsync();
        Assert.False(main.IsConnected);
        Assert.Equal(0, signIn.Calls);
        Assert.Equal("No browser sign-in yet.", main.Connection.BrowserSessionStatus);

        await main.SignInWithBrowserCommand.ExecuteAsync(null);

        Assert.Equal(1, signIn.Calls);
        Assert.True(main.IsConnected);
        Assert.Equal("glide_user_session=fresh", store.Current.SessionCookie);
        Assert.Equal("tok-fresh", store.Current.UserToken);
        Assert.Contains("Browser sign-in saved", main.Connection.BrowserSessionStatus);
        Assert.Contains("Connected as", main.StatusMessage);

        // A second Connect while the first auth owned the gate must be ignored.
        // After sign-in finishes the gate is free; overlapping Connect during sign-in
        // is covered by ConcurrentConnectIsIgnoredWhileBrowserSignInRuns.
        var again = new MainViewModel(
            store,
            new RecordingDesktopServices(),
            clientFactory: (session, catalog) =>
            {
                connects++;
                return ServiceNowClient.Create(session, handler, catalog);
            });
        await again.InitializeAsync();
        Assert.True(again.IsConnected);
        Assert.Equal(2, connects);
        Assert.Contains("Connected as", again.StatusMessage);
    }

    [Fact]
    public async Task ConcurrentConnectIsIgnoredWhileBrowserSignInRuns()
    {
        var releaseSignIn = new TaskCompletionSource();
        var signInStarted = new TaskCompletionSource();
        var store = new MemorySettingsStore();
        store.Save(new DeskSettings
        {
            InstanceUrl = "https://example.service-now.com",
            AuthMode = ServiceNowAuthMode.BrowserSession
        });
        var clients = 0;
        var signIn = new GatedBrowserSignIn(signInStarted, releaseSignIn);
        var handler = new StubHandler((request, _) => LiveOrEmpty(request));
        var main = new MainViewModel(
            store,
            new RecordingDesktopServices(),
            browserSignIn: signIn,
            clientFactory: (session, catalog) =>
            {
                Interlocked.Increment(ref clients);
                return ServiceNowClient.Create(session, handler, catalog);
            });

        var signInTask = main.SignInWithBrowserCommand.ExecuteAsync(null);
        await signInStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await main.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(0, clients);

        releaseSignIn.TrySetResult();
        await signInTask;

        Assert.Equal(1, clients);
        Assert.Equal(1, signIn.Calls);
        Assert.True(main.IsConnected);
        Assert.Contains("Browser sign-in saved", main.Connection.BrowserSessionStatus);
    }

    [Fact]
    public async Task DefaultInstanceUrlAloneDoesNotAutoConnect()
    {
        var store = new MemorySettingsStore();
        store.Save(new DeskSettings
        {
            InstanceUrl = DeskSettings.DefaultInstanceUrl,
            AuthMode = ServiceNowAuthMode.BrowserSession
        });
        var used = 0;
        var main = new MainViewModel(
            store,
            new RecordingDesktopServices(),
            clientFactory: (_, _) =>
            {
                used++;
                throw new InvalidOperationException("Default URL must not auto-connect.");
            });

        await main.InitializeAsync();

        Assert.Equal(0, used);
        Assert.False(main.IsConnected);
        Assert.Equal("No browser sign-in yet.", main.Connection.BrowserSessionStatus);
        Assert.Equal(GuidedSetupOfferKind.Full, main.Guided.OfferKind);
    }

    [Fact]
    public async Task DisconnectClearsTheSavedBrowserToken()
    {
        var signedIn = DateTimeOffset.UtcNow.AddMinutes(-5);
        var store = new MemorySettingsStore();
        store.Save(SavedBrowser(signedIn, signedInOnly: true));
        var handler = new StubHandler((request, _) => LiveOrEmpty(request));
        var main = Desk(store, handler);

        await main.InitializeAsync();
        Assert.True(main.IsConnected);

        main.DisconnectCommand.Execute(null);

        Assert.False(main.IsConnected);
        Assert.Equal("Disconnected.", main.StatusMessage);
        Assert.Equal("", store.Current.SessionCookie);
        Assert.Equal("", store.Current.UserToken);
        Assert.Null(store.Current.SignedInAt);
        Assert.Equal("https://kept.service-now.com", store.Current.InstanceUrl);
        AssertNotificationSettings(store.Current);
        Assert.True(main.ConnectCommand.CanExecute(null));

        var used = 0;
        var again = new MainViewModel(
            store,
            new RecordingDesktopServices(),
            clientFactory: (_, _) =>
            {
                used++;
                throw new InvalidOperationException("A cleared browser session must not be sent to ServiceNow.");
            });
        await again.InitializeAsync();

        Assert.Equal(0, used);
        Assert.False(again.IsConnected);
        Assert.Equal("", again.ErrorMessage);
    }

    [Fact]
    public async Task BasicSignInIsNotCutOffAfterTwentyFourHours()
    {
        var store = new MemorySettingsStore();
        store.Save(new DeskSettings
        {
            InstanceUrl = "https://example.service-now.com",
            AuthMode = ServiceNowAuthMode.Basic,
            Username = "alex",
            Password = "secret",
            SignedInAt = DateTimeOffset.UtcNow.AddHours(-30),
            SessionCapturedAt = DateTimeOffset.UtcNow.AddHours(-30)
        });
        var handler = new StubHandler((request, _) => LiveOrEmpty(request));
        var main = new MainViewModel(
            store,
            new RecordingDesktopServices(),
            clientFactory: (session, catalog) => ServiceNowClient.Create(session, handler, catalog));

        await main.InitializeAsync();

        Assert.True(main.IsConnected);
        Assert.Equal("secret", store.Current.Password);
        Assert.Equal(ServiceNowAuthMode.Basic, store.Current.AuthMode);
        Assert.NotEmpty(handler.Calls);
    }

    private static MainViewModel Desk(MemorySettingsStore store, StubHandler handler) =>
        new(
            store,
            new RecordingDesktopServices(),
            clientFactory: (session, catalog) => ServiceNowClient.Create(session, handler, catalog));

    private static DeskSettings SavedBrowser(DateTimeOffset signedIn, bool signedInOnly) => new()
    {
        InstanceUrl = "https://kept.service-now.com",
        AuthMode = ServiceNowAuthMode.BrowserSession,
        Password = "kept-password",
        SessionCookie = "glide_user_session=abc",
        UserToken = "tok-ck",
        SessionCapturedAt = signedInOnly ? null : signedIn,
        SignedInAt = signedInOnly ? signedIn : null,
        JiggleFrequency = "00:05:00",
        JiggleDurationSeconds = 4,
        MaximizeWhenJiggling = false,
        PlaySoundWhenJiggling = true,
        PlaySoundOnAlertMetric = false,
        AlertSoundPath = "alert.wav",
        WatchedGroupName = "Desk queue",
        OfficeLocations = ["Cairns"],
        NotificationPollSeconds = 30
    };

    private static void AssertNotificationSettings(DeskSettings settings)
    {
        Assert.Equal("00:05:00", settings.JiggleFrequency);
        Assert.Equal(4, settings.JiggleDurationSeconds);
        Assert.False(settings.MaximizeWhenJiggling);
        Assert.True(settings.PlaySoundWhenJiggling);
        Assert.False(settings.PlaySoundOnAlertMetric);
        Assert.Equal("alert.wav", settings.AlertSoundPath);
        Assert.Equal("Desk queue", settings.WatchedGroupName);
        Assert.Equal("Cairns", Assert.Single(settings.OfficeLocations!));
        Assert.Equal(30, settings.NotificationPollSeconds);
    }

    private static HttpResponseMessage LiveOrEmpty(HttpRequestMessage request)
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        return IsUserLookup(path) ? User() : Api.Json("""{"result":[]}""");
    }

    private static bool IsUserLookup(string path) =>
        path.Contains("/sys_user", StringComparison.Ordinal)
        && !path.Contains("sys_user_group", StringComparison.Ordinal)
        && !path.Contains("sys_user_grmember", StringComparison.Ordinal);

    private static HttpResponseMessage User() =>
        Api.Json("""{"result":[{"sys_id":"user-1","name":"Alex Rivera","user_name":"alex","email":"alex@example.com"}]}""");

    private static DeskSettings BrowserSettings(DateTimeOffset signedIn) => new()
    {
        InstanceUrl = "https://example.service-now.com",
        AuthMode = ServiceNowAuthMode.BrowserSession,
        SessionCookie = "glide_user_session=abc",
        UserToken = "tok-ck",
        SignedInAt = signedIn,
        SessionCapturedAt = signedIn
    };

    private static ServiceNowSession BrowserSession() => ServiceNowSession.FromSettings(new DeskSettings
    {
        InstanceUrl = "https://example.service-now.com",
        AuthMode = ServiceNowAuthMode.BrowserSession,
        SessionCookie = "glide_user_session=abc; JSESSIONID=xyz",
        UserToken = "tok-ck"
    });

    private static string NewFolder() => Path.Combine(Path.GetTempPath(), "snd-" + Guid.NewGuid().ToString("N"));

    private sealed class CountingBrowserSignIn(Func<BrowserSignInResult> factory) : IBrowserSignIn
    {
        public int Calls { get; private set; }

        public Task<BrowserSignInResult> SignInAsync(Uri instanceUri, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(factory());
        }
    }

    private sealed class GatedBrowserSignIn(TaskCompletionSource started, TaskCompletionSource release) : IBrowserSignIn
    {
        public int Calls { get; private set; }

        public async Task<BrowserSignInResult> SignInAsync(Uri instanceUri, CancellationToken cancellationToken)
        {
            Calls++;
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return new BrowserSignInResult("glide_user_session=gated", "tok-gated", DateTimeOffset.UtcNow.AddHours(4));
        }
    }
}
