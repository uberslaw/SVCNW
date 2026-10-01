using System.Net;
using System.Text;
using System.Text.Json;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.Tests;

public class ServiceNowClientTests
{
    [Fact]
    public async Task SearchBuildsASafeEncodedQueryAndSendsBasicAuth()
    {
        var handler = new StubHandler((_, _) => Api.Json(Api.IncidentList(), total: 12));
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);

        var page = await client.SearchIncidentsAsync(new TicketQuery
        {
            Text = "vpn^active=false",
            Assignment = AssignmentScope.Mine,
            Activity = ActivityFilter.Open
        }, CancellationToken.None);

        Assert.Equal(12, page.TotalCount);
        Assert.Equal("INC0010001", page.Items[0].Number);
        var call = handler.Calls.Single();
        Assert.Equal("Basic", call.Scheme);
        Assert.Equal("alex:secret", Decode(call.Parameter));
        var query = QueryOf(call.PathAndQuery);
        Assert.Contains("123TEXTQUERY321=vpn active=false", query);
        Assert.Contains("assigned_to=javascript:gs.getUserID()", query);
        Assert.Contains("active=true", query);
        Assert.DoesNotContain("vpn^", query);
        Assert.Equal("GET", client.RecentActivity[0].Method);
        Assert.DoesNotContain("secret", client.RecentActivity[0].Path);
    }

    [Fact]
    public async Task CreateAndResolveSendOnlyTheFieldsTheAgentChanged()
    {
        var handler = new StubHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            return Api.Json("{\"result\":" + Api.IncidentObject + "}");
        });
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);
        await client.CreateIncidentAsync(new IncidentChanges
        {
            ShortDescription = "Headset failed",
            CallerId = "user-sam",
            Impact = "2"
        }, CancellationToken.None);

        using var created = JsonDocument.Parse(handler.Calls[0].Body);
        Assert.Equal("Headset failed", created.RootElement.GetProperty("short_description").GetString());
        Assert.Equal("user-sam", created.RootElement.GetProperty("caller_id").GetString());
        Assert.False(created.RootElement.TryGetProperty("assigned_to", out _));

        var resolveHandler = new StubHandler((_, _) => Api.Json("{\"result\":" + Api.IncidentObject + "}"));
        using var resolveClient = ServiceNowClient.Create(Api.BasicSession(), resolveHandler);
        await resolveClient.ResolveIncidentAsync("inc-printer", "Solved (Permanently)", "Cleared the jam.", "6", CancellationToken.None);
        using var resolved = JsonDocument.Parse(resolveHandler.Calls[0].Body);
        Assert.Equal("6", resolved.RootElement.GetProperty("state").GetString());
        Assert.Equal("Solved (Permanently)", resolved.RootElement.GetProperty("close_code").GetString());
        Assert.Equal("Cleared the jam.", resolved.RootElement.GetProperty("close_notes").GetString());
        Assert.False(resolved.RootElement.TryGetProperty("incident_state", out _));
    }

    [Fact]
    public async Task OAuthPasswordGetsATokenAndRetriesOnceAfterUnauthorized()
    {
        var apiCalls = 0;
        var handler = new StubHandler((request, body) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("oauth_token", StringComparison.Ordinal))
            {
                Assert.Contains("grant_type=password", body);
                Assert.Contains("username=alex", body);
                return Api.Json("""{"access_token":"tok-1","expires_in":1800}""");
            }

            apiCalls++;
            if (apiCalls == 1)
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            return Api.Json(Api.IncidentList());
        });

        var session = ServiceNowSession.FromSettings(new DeskSettings
        {
            InstanceUrl = "https://example.service-now.com",
            AuthMode = ServiceNowAuthMode.OAuthPassword,
            Username = "alex",
            Password = "secret",
            ClientId = "client",
            ClientSecret = "topsecret"
        });
        using var client = ServiceNowClient.Create(session, handler);
        var page = await client.SearchIncidentsAsync(new TicketQuery(), CancellationToken.None);

        Assert.Single(page.Items);
        Assert.Equal(2, handler.Calls.Count(call => call.PathAndQuery.Contains("oauth_token")));
        Assert.Equal("Bearer", handler.Calls.Last().Scheme);
        Assert.DoesNotContain("topsecret", client.RecentActivity[0].Path);
    }

    [Fact]
    public async Task MyGroupsAreLoadedOnceAndReused()
    {
        var handler = new StubHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("sys_user_grmember", StringComparison.Ordinal))
            {
                return Api.Json("""{"result":[{"group":{"value":"group-cs","display_value":"Client Services"}}]}""");
            }

            return Api.Json(Api.IncidentList());
        });
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);
        var query = new TicketQuery { Assignment = AssignmentScope.MyGroups, Activity = ActivityFilter.Open };
        await client.SearchIncidentsAsync(query, CancellationToken.None);
        await client.SearchRequestedItemsAsync(query, CancellationToken.None);

        Assert.Equal(1, handler.Calls.Count(call => call.PathAndQuery.Contains("sys_user_grmember")));
        Assert.Contains("assignment_groupINgroup-cs", QueryOf(handler.Calls[^1].PathAndQuery));
    }

    [Fact]
    public async Task ApiFailuresSurfaceTheServiceNowDetail()
    {
        var handler = new StubHandler((_, _) => Api.Json(
            """{"error":{"message":"Operation Failed","detail":"Caller is mandatory"},"status":"failure"}""",
            HttpStatusCode.BadRequest));
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);

        var error = await Assert.ThrowsAsync<ServiceNowException>(() =>
            client.CreateIncidentAsync(new IncidentChanges { ShortDescription = "No caller" }, CancellationToken.None));
        Assert.Contains("Caller is mandatory", error.Message);
    }

    [Fact]
    public async Task CatalogOrderPostsQuantityRequestedForAndVariables()
    {
        var handler = new StubHandler((_, _) => Api.Json("""{"result":{"request_id":"req-9","request_number":"REQ0090001"}}"""));
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);
        var result = await client.OrderCatalogItemAsync(
            "cat-laptop",
            2,
            "user-jordan",
            new Dictionary<string, string> { ["preferred_os"] = "win11" },
            CancellationToken.None);

        Assert.Equal("REQ0090001", result.RequestNumber);
        Assert.Equal("req-9", result.RequestSysId);
        using var body = JsonDocument.Parse(handler.Calls.Single().Body);
        Assert.Equal("2", body.RootElement.GetProperty("sysparm_quantity").GetString());
        Assert.Equal("user-jordan", body.RootElement.GetProperty("sysparm_requested_for").GetString());
        Assert.Equal("win11", body.RootElement.GetProperty("variables").GetProperty("preferred_os").GetString());
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

    private static string Decode(string? parameter) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(parameter ?? ""));
}
