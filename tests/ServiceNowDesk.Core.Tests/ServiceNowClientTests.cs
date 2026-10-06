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
        Assert.Contains("stateNOT IN6,7,8", query);
        Assert.DoesNotContain("<", query);
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
    public async Task KnowledgeSearchUsesTheTableApi()
    {
        var handler = new StubHandler((_, _) => Api.Json("""
            {"result":[{
              "sys_id":"kb-zephyr",
              "number":"KB0001001",
              "short_description":"Blank folders",
              "text":"<p>zephyrmail</p>",
              "topic":"Email",
              "workflow_state":"published",
              "kb_category":"Email",
              "kb_knowledge_base":"IT",
              "author":"sample-user",
              "sys_updated_on":"2026-09-18 14:22:00",
              "published":"2026-09-18"
            }]}
            """, total: 1));
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);
        var page = await client.SearchKnowledgeAsync(new TicketQuery
        {
            Text = "kb0001234",
            Activity = ActivityFilter.Any,
            Assignment = AssignmentScope.Any
        }, CancellationToken.None);

        Assert.Equal("KB0001001", page.Items[0].Number);
        Assert.Contains("zephyrmail", page.Items[0].Text);
        var call = handler.Calls.Single();
        Assert.Contains("/api/now/table/kb_knowledge", call.PathAndQuery);
        Assert.Contains("sysparm_display_value=all", call.PathAndQuery);
        var query = QueryOf(call.PathAndQuery);
        Assert.Contains("number=KB0001234", query);
        Assert.DoesNotContain("^workflow_state", query);
        Assert.Equal("GET", client.RecentActivity[0].Method);
        Assert.Contains("kb_knowledge", client.RecentActivity[0].Path);
        Assert.DoesNotContain("secret", client.RecentActivity[0].Path);
    }

    [Fact]
    public async Task OpenListQueriesExcludeFinishedStatesAndSearchDoesNot()
    {
        var handler = new StubHandler((_, _) => Api.Json("""{"result":[]}"""));
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);

        await client.SearchIncidentsAsync(new TicketQuery { Activity = ActivityFilter.Open, Assignment = AssignmentScope.Mine }, CancellationToken.None);
        await client.SearchRequestsAsync(new TicketQuery { Activity = ActivityFilter.Open }, CancellationToken.None);
        await client.SearchRequestedItemsAsync(new TicketQuery { Activity = ActivityFilter.Open, Assignment = AssignmentScope.Any }, CancellationToken.None);
        await client.SearchInteractionsAsync(new TicketQuery { Activity = ActivityFilter.Open, Assignment = AssignmentScope.Mine }, CancellationToken.None);
        await client.SearchIncidentsAsync(new TicketQuery
        {
            Text = "INC0010015",
            Activity = ActivityFilter.Any,
            Assignment = AssignmentScope.Any
        }, CancellationToken.None);

        var queries = handler.Calls.Select(call => QueryOf(call.PathAndQuery)).ToArray();
        Assert.Contains("active=true^stateNOT IN6,7,8", queries[0]);
        Assert.DoesNotContain("<", queries[0]);
        Assert.Contains("request_stateNOT LIKEclosed", queries[1]);
        Assert.Contains("request_stateNOT LIKEcancel", queries[1]);
        Assert.DoesNotContain("<", queries[1]);
        Assert.Contains("stateNOT IN3,4,7", queries[2]);
        Assert.DoesNotContain("<", queries[2]);
        Assert.Contains("stateNOT LIKEclosed", queries[3]);
        Assert.Contains("stateNOT LIKEcancel", queries[3]);
        Assert.Contains("type=walkup", queries[3]);
        Assert.DoesNotContain("<", queries[3]);
        Assert.Contains("number=INC0010015", queries[4]);
        Assert.DoesNotContain("stateNOT IN", queries[4]);
        Assert.DoesNotContain("NOT LIKE", queries[4]);
    }

    [Fact]
    public async Task CreateRequestedItemPostsShortDescriptionAndRequestedFor()
    {
        var handler = new StubHandler((_, _) => Api.Json("""
            {"result":{
              "sys_id":{"value":"ritm-new","display_value":"ritm-new"},
              "number":{"value":"RITM0099001","display_value":"RITM0099001"},
              "short_description":{"value":"Order a spare dock","display_value":"Order a spare dock"},
              "state":{"value":"1","display_value":"Open"},
              "active":{"value":"true","display_value":"true"}
            }}
            """));
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);

        var created = await client.CreateRequestedItemAsync(new RequestedItemChanges
        {
            ShortDescription = "Order a spare dock",
            RequestedForId = "user-alex"
        }, CancellationToken.None);

        Assert.Equal("RITM0099001", created.Number);
        Assert.Equal("Order a spare dock", created.ShortDescription);
        var call = handler.Calls.Single();
        Assert.Equal("POST", call.Method);
        Assert.Contains("/api/now/table/sc_req_item", call.PathAndQuery);
        using var body = JsonDocument.Parse(call.Body);
        Assert.Equal("Order a spare dock", body.RootElement.GetProperty("short_description").GetString());
        Assert.Equal("user-alex", body.RootElement.GetProperty("requested_for").GetString());
        Assert.False(body.RootElement.TryGetProperty("cat_item", out _));
        Assert.False(body.RootElement.TryGetProperty("request", out _));
    }

    [Fact]
    public async Task JournalQueryReturnsWorkNotesAndCustomerComments()
    {
        var handler = new StubHandler((_, _) => Api.Json("""
            {
              "result": [
                {
                  "sys_id": {"value":"journal-new","display_value":"journal-new"},
                  "name": {"value":"incident","display_value":"incident"},
                  "element": {"value":"work_notes","display_value":"Work notes"},
                  "value": {"value":"Sending this back with what I found.","display_value":"Sending this back with what I found."},
                  "sys_created_on": {"value":"2026-10-05 15:00:00","display_value":"2026-10-05 15:00:00"},
                  "sys_created_by": {"value":"casey.ng","display_value":"Casey Ng"}
                },
                {
                  "sys_id": {"value":"journal-old","display_value":"journal-old"},
                  "name": {"value":"incident","display_value":"incident"},
                  "element": {"value":"comments","display_value":"Additional comments"},
                  "value": {"value":"I added a comment first.","display_value":""},
                  "sys_created_on": {"value":"2026-10-04 09:00:00","display_value":"2026-10-04 09:00:00"},
                  "sys_created_by": {"value":"jordan.lee","display_value":"Jordan Lee"}
                }
              ]
            }
            """));
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);

        var notes = await client.GetJournalAsync("incident", "inc-printer", CancellationToken.None);

        var call = handler.Calls.Single();
        Assert.Contains("/api/now/table/sys_journal_field", call.PathAndQuery);
        Assert.Contains("sysparm_limit=100", call.PathAndQuery);
        Assert.DoesNotContain("sysparm_limit=0", call.PathAndQuery);
        var fields = FieldsOf(call.PathAndQuery);
        Assert.Contains("name", fields);
        Assert.Contains("element", fields);
        Assert.Contains("value", fields);
        Assert.Contains("sys_created_on", fields);
        Assert.Contains("sys_created_by", fields);
        var query = QueryOf(call.PathAndQuery);
        Assert.Contains("element_id=inc-printer", query);
        Assert.Contains("comments", query);
        Assert.Contains("work_notes", query);
        Assert.Contains("ORDERBYDESCsys_created_on", query);

        Assert.Equal(2, notes.Count);
        Assert.Equal("work_notes", notes[0].Kind);
        Assert.Equal("Work note", notes[0].KindLabel);
        Assert.Equal("Casey Ng", notes[0].Author);
        Assert.Equal("Sending this back with what I found.", notes[0].Text);
        Assert.Equal("2026-10-05 15:00:00", notes[0].CreatedDisplay);
        Assert.Equal("comments", notes[1].Kind);
        Assert.Equal("Customer comment", notes[1].KindLabel);
        Assert.True(notes[1].IsCustomer);
        Assert.Equal("Jordan Lee", notes[1].Author);
        Assert.Equal("I added a comment first.", notes[1].Text);
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

    private static string FieldsOf(string pathAndQuery)
    {
        const string marker = "sysparm_fields=";
        var start = pathAndQuery.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0);
        var encoded = pathAndQuery[(start + marker.Length)..];
        var end = encoded.IndexOf('&');
        if (end >= 0)
            encoded = encoded[..end];
        return Uri.UnescapeDataString(encoded);
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
