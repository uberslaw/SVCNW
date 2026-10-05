using System.Net;
using System.Text.Json;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class WalkUpTests
{
    private const string InteractionObject = """
        {
          "sys_id": {"value": "ims-desk", "display_value": "ims-desk"},
          "number": {"value": "IMS0010001", "display_value": "IMS0010001"},
          "short_description": {"value": "Password reset at the desk", "display_value": "Password reset at the desk"},
          "description": {"value": "Sam is locked out.", "display_value": "Sam is locked out."},
          "state": {"value": "new", "display_value": "New"},
          "type": {"value": "walkup", "display_value": "Walk-up"},
          "opened_for": {"value": "user-sam", "display_value": "Sam Patel"},
          "assigned_to": {"value": "sample-user", "display_value": "Alex Rivera"},
          "assignment_group": {"value": "group-cs", "display_value": "Client Services"},
          "opened_at": {"value": "2026-10-01 09:00:00", "display_value": "2026-10-01 09:00"},
          "sys_updated_on": {"value": "2026-10-01 09:05:00", "display_value": "2026-10-01 09:05"},
          "active": {"value": "true", "display_value": "true"}
        }
        """;

    [Fact]
    public async Task ListWalkUpsQueriesTheInteractionTableForWalkUpType()
    {
        var handler = new StubHandler((_, _) => Api.Json("{\"result\":[" + InteractionObject + "]}", total: 1));
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);

        var page = await client.SearchInteractionsAsync(new TicketQuery
        {
            Assignment = AssignmentScope.Mine,
            Activity = ActivityFilter.Open
        }, CancellationToken.None);

        var row = Assert.Single(page.Items);
        Assert.Equal("IMS0010001", row.Number);
        Assert.Equal("walkup", row.Type);
        Assert.Equal("user-sam", row.OpenedFor.SysId);
        var call = handler.Calls.Single();
        Assert.Contains("/api/now/table/interaction", call.PathAndQuery);
        Assert.DoesNotContain("interaction_related_record", call.PathAndQuery);
        var query = QueryOf(call.PathAndQuery);
        Assert.Contains("type=walkup", query);
        Assert.Contains("active=true", query);
        Assert.Contains("assigned_to=javascript:gs.getUserID()", query);
    }

    [Fact]
    public async Task CreateWalkUpSendsWalkUpType()
    {
        var handler = new StubHandler((_, _) => Api.Json("{\"result\":" + InteractionObject + "}"));
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);

        var created = await client.CreateInteractionAsync(new InteractionChanges
        {
            ShortDescription = "Badge help",
            OpenedForId = "user-sam",
            Description = "The printer is out of cards.",
            State = "new"
        }, CancellationToken.None);

        Assert.Equal("IMS0010001", created.Number);
        Assert.Contains("/api/now/table/interaction", handler.Calls.Single().PathAndQuery);
        Assert.Equal(HttpMethod.Post.Method, handler.Calls.Single().Method);
        using var body = JsonDocument.Parse(handler.Calls.Single().Body);
        Assert.Equal("walkup", body.RootElement.GetProperty("type").GetString());
        Assert.Equal("Badge help", body.RootElement.GetProperty("short_description").GetString());
        Assert.Equal("user-sam", body.RootElement.GetProperty("opened_for").GetString());
        Assert.Equal("new", body.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task ConvertCreatesAnIncidentAndRelatedRecordThenReturnsTheSameIncident()
    {
        var linked = false;
        var incidentPosts = 0;
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("interaction_related_record", StringComparison.Ordinal))
            {
                if (request.Method == HttpMethod.Post)
                {
                    linked = true;
                    return Api.Json("""{"result":{"sys_id":"rel-1"}}""");
                }

                return linked
                    ? Api.Json("""
                        {"result":[{
                          "document_id":{"value":"inc-printer","display_value":"INC0010001"},
                          "document_table":{"value":"incident","display_value":"incident"},
                          "interaction":{"value":"ims-desk","display_value":"IMS0010001"}
                        }]}
                        """)
                    : Api.Json("""{"result":[]}""");
            }

            if (path.Contains("/incident", StringComparison.Ordinal))
            {
                if (request.Method == HttpMethod.Post)
                    incidentPosts++;
                return Api.Json("{\"result\":" + Api.IncidentObject + "}");
            }

            return Api.Json("{\"result\":" + InteractionObject + "}");
        });
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);

        var first = await client.ConvertInteractionToIncidentAsync("ims-desk", CancellationToken.None);
        var second = await client.ConvertInteractionToIncidentAsync("ims-desk", CancellationToken.None);

        Assert.True(first.Created);
        Assert.Null(first.LinkError);
        Assert.Equal("INC0010001", first.Incident.Number);
        Assert.False(second.Created);
        Assert.Equal(first.Incident.SysId, second.Incident.SysId);
        Assert.Equal(1, incidentPosts);

        var incidentPost = handler.Calls.Single(call => call.Method == "POST" && call.PathAndQuery.Contains("/incident", StringComparison.Ordinal));
        using var incidentBody = JsonDocument.Parse(incidentPost.Body);
        Assert.Equal("Password reset at the desk", incidentBody.RootElement.GetProperty("short_description").GetString());
        Assert.Equal("Sam is locked out.", incidentBody.RootElement.GetProperty("description").GetString());
        Assert.Equal("user-sam", incidentBody.RootElement.GetProperty("caller_id").GetString());
        Assert.Equal("sample-user", incidentBody.RootElement.GetProperty("assigned_to").GetString());
        Assert.Equal("group-cs", incidentBody.RootElement.GetProperty("assignment_group").GetString());

        var linkPost = handler.Calls.Single(call => call.Method == "POST" && call.PathAndQuery.Contains("interaction_related_record", StringComparison.Ordinal));
        using var linkBody = JsonDocument.Parse(linkPost.Body);
        Assert.Equal("ims-desk", linkBody.RootElement.GetProperty("interaction").GetString());
        Assert.Equal("incident", linkBody.RootElement.GetProperty("document_table").GetString());
        Assert.Equal("inc-printer", linkBody.RootElement.GetProperty("document_id").GetString());
    }

    [Fact]
    public async Task ConvertStillReturnsTheIncidentWhenTheLinkTableIsRejected()
    {
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("interaction_related_record", StringComparison.Ordinal))
            {
                if (request.Method == HttpMethod.Post)
                {
                    return Api.Json(
                        """{"error":{"message":"Operation Failed","detail":"Invalid table interaction_related_record"},"status":"failure"}""",
                        HttpStatusCode.BadRequest);
                }

                return Api.Json("""{"result":[]}""");
            }

            if (path.Contains("/incident", StringComparison.Ordinal))
                return Api.Json("{\"result\":" + Api.IncidentObject + "}");

            return Api.Json("{\"result\":" + InteractionObject + "}");
        });
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);

        var conversion = await client.ConvertInteractionToIncidentAsync("ims-desk", CancellationToken.None);

        Assert.True(conversion.Created);
        Assert.Equal("INC0010001", conversion.Incident.Number);
        Assert.Contains("Invalid table interaction_related_record", conversion.LinkError);
        Assert.Contains(handler.Calls, call => call.Method == "POST" && call.PathAndQuery.Contains("/incident", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SampleConvertCreatesOneIncidentAndReusesTheLink()
    {
        using var client = new SampleServiceNowClient();
        var before = await CountIncidentsAsync(client);

        var first = await client.ConvertInteractionToIncidentAsync("ims-password", CancellationToken.None);
        var second = await client.ConvertInteractionToIncidentAsync("ims-password", CancellationToken.None);

        Assert.True(first.Created);
        Assert.Null(first.LinkError);
        Assert.StartsWith("INC", first.Incident.Number);
        Assert.Equal("user-sam", first.Incident.Caller.SysId);
        Assert.Equal("Password reset at the front desk", first.Incident.ShortDescription);
        Assert.Equal("Sam walked up and is locked out of payroll.", first.Incident.Description);
        Assert.Equal("group-cs", first.Incident.AssignmentGroup.SysId);
        Assert.Equal("sample-user", first.Incident.AssignedTo.SysId);
        Assert.False(second.Created);
        Assert.Equal(first.Incident.SysId, second.Incident.SysId);
        Assert.Equal(before + 1, await CountIncidentsAsync(client));
    }

    [Fact]
    public async Task UnsavedWalkUpIsNotConverted()
    {
        using var client = new SampleServiceNowClient();
        var before = await CountIncidentsAsync(client);
        var workspace = new InteractionWorkspaceViewModel(new RecordingDesktopServices());
        InteractionConversion? opened = null;
        workspace.IncidentRequested += (_, conversion) => opened = conversion;
        workspace.Attach(client);
        await workspace.EnsureChoicesAsync();
        workspace.NewRecordCommand.Execute(null);
        workspace.ShortDescription = "Headset at the desk";
        workspace.Description = "The cable is frayed.";
        workspace.Caller.Set("user-sam", "Sam Patel");

        await workspace.ConvertToIncidentCommand.ExecuteAsync(null);

        Assert.Contains("Save", workspace.EditorMessage);
        Assert.Null(opened);
        Assert.True(workspace.IsNew);
        Assert.Equal(before, await CountIncidentsAsync(client));

        await workspace.RefreshAsync();
        await workspace.OpenFromSearchAsync("ims-password");
        workspace.ShortDescription = "Changed before save";
        await workspace.ConvertToIncidentCommand.ExecuteAsync(null);

        Assert.Contains("Save", workspace.EditorMessage);
        Assert.Null(opened);
        Assert.Equal(before, await CountIncidentsAsync(client));
    }

    [Fact]
    public async Task CreateWalkUpKeepsWalkUpTypeAndCopiesTheImsNumber()
    {
        using var client = new SampleServiceNowClient();
        var desktop = new RecordingDesktopServices();
        var workspace = new InteractionWorkspaceViewModel(desktop);
        workspace.Attach(client);
        await workspace.EnsureChoicesAsync();
        await workspace.RefreshAsync();
        Assert.Contains(workspace.Items, row => row.Number == "IMS0010001");
        Assert.DoesNotContain(workspace.Items, row => row.Number == "IMS0010002");

        workspace.NewRecordCommand.Execute(null);
        workspace.ShortDescription = "Monitor cable";
        workspace.Description = "The cable at the desk is frayed.";
        workspace.Caller.Set("user-jordan", "Jordan Lee");
        workspace.Type = "phone";
        await workspace.SaveCommand.ExecuteAsync(null);

        Assert.StartsWith("IMS", workspace.Number);
        Assert.Equal(DefaultChoices.WalkUpType, workspace.Type);
        workspace.CopyNumberCommand.Execute(null);
        Assert.Equal(workspace.Number, desktop.CopiedText.Single());
        var saved = await client.GetInteractionAsync(workspace.Items.First(row => row.Number == workspace.Number).SysId, CancellationToken.None);
        Assert.Equal(DefaultChoices.WalkUpType, saved.Type);
        Assert.Equal("user-jordan", saved.OpenedFor.SysId);
        Assert.Equal("The cable at the desk is frayed.", saved.Description);
    }

    [Fact]
    public async Task CreateNewWalkUpSurvivesAClearedStateSelection()
    {
        using var client = new SampleServiceNowClient();
        var workspace = new InteractionWorkspaceViewModel(new RecordingDesktopServices());
        workspace.Attach(client);
        await workspace.OpenFromSearchAsync("ims-password");
        workspace.NewRecordCommand.Execute(null);
        Assert.Equal("new", workspace.State);
        Assert.Equal(DefaultChoices.WalkUpType, workspace.Type);
        Assert.Equal("", workspace.Assignment.GroupId);
        Assert.Equal("", workspace.Assignment.MemberId);

        workspace.State = null!;
        workspace.Type = null!;
        Assert.True(workspace.IsNew);
        Assert.Equal("", workspace.Number);
    }

    [Fact]
    public async Task PracticeConvertOpensTheNewIncident()
    {
        var main = new MainViewModel(new MemorySettingsStore(), new RecordingDesktopServices());
        main.Connection.UseSampleData = true;
        await main.ConnectCommand.ExecuteAsync(null);
        main.SelectedSection = DeskSection.WalkUps;
        await WaitUntilAsync(() => main.WalkUps.HasLoaded && !main.WalkUps.IsLoading);
        await main.WalkUps.Assignment.WhenReady;
        await main.WalkUps.OpenFromSearchAsync("ims-badge");
        await main.WalkUps.Assignment.WhenReady;
        Assert.False(main.WalkUps.IsDirty);

        await main.WalkUps.ConvertToIncidentCommand.ExecuteAsync(null);
        await main.ConvertOpenTask;

        Assert.Equal(DeskSection.Incidents, main.SelectedSection);
        Assert.Equal("Badge will not print", main.Incidents.ShortDescription);
        Assert.Equal("The front desk printer feeds a blank card.", main.Incidents.Description);
        Assert.Equal("user-jordan", main.Incidents.Caller.SysId);
        Assert.StartsWith("INC", main.Incidents.Number);
        var createdNumber = main.Incidents.Number;

        main.SelectedSection = DeskSection.WalkUps;
        await WaitUntilAsync(() => !main.WalkUps.IsLoading);
        await main.WalkUps.ConvertToIncidentCommand.ExecuteAsync(null);
        await main.ConvertOpenTask;

        Assert.Equal(createdNumber, main.Incidents.Number);
        Assert.Contains("already linked", main.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WaitUntilAsync(Func<bool> ready)
    {
        for (var attempt = 0; attempt < 40 && !ready(); attempt++)
            await Task.Delay(50);
    }

    private static async Task<int> CountIncidentsAsync(IServiceNowClient client)
    {
        var page = await client.SearchIncidentsAsync(new TicketQuery
        {
            Activity = ActivityFilter.Any,
            Assignment = AssignmentScope.Any,
            Limit = 100
        }, CancellationToken.None);
        return page.Items.Count;
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
}
