using System.Net;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
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
    public void CorruptCatalogFileIsIgnored()
    {
        var folder = NewFolder();
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "form-catalog.example.service-now.com.json"), "{");
        var store = new FileFormCatalogStore(folder);

        Assert.Null(store.Load(new Uri("https://example.service-now.com/")));
    }

    private static ServiceNowSession BrowserSession() => ServiceNowSession.FromSettings(new DeskSettings
    {
        InstanceUrl = "https://example.service-now.com",
        AuthMode = ServiceNowAuthMode.BrowserSession,
        SessionCookie = "glide_user_session=abc; JSESSIONID=xyz",
        UserToken = "tok-ck"
    });

    private static string NewFolder() => Path.Combine(Path.GetTempPath(), "snd-" + Guid.NewGuid().ToString("N"));
}
