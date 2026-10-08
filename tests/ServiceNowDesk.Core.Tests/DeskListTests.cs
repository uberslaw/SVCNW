using System.Net;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class DeskListTests
{
    [Fact]
    public async Task CutOffDirectoryLoadsEveryMemberOfTheSelectedGroup()
    {
        var folder = Path.Combine(Path.GetTempPath(), "snd-members-" + Guid.NewGuid().ToString("N"));
        var store = new FileFormCatalogStore(folder);
        var session = Api.BasicSession();
        store.Save(session.InstanceUri, new FormCatalogSnapshot
        {
            DirectoryCapturedAt = DateTimeOffset.UtcNow,
            DirectoryComplete = false,
            Groups = [new CachedAssignmentGroup { SysId = "group-big", Name = "Big Group" }],
            Members = [new CachedGroupMember { GroupSysId = "group-big", UserSysId = "user-1", Name = "Only Cached" }]
        });
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (!path.Contains("sys_user_grmember", StringComparison.Ordinal))
                return Api.Json("""{"result":[]}""");

            var text = Uri.UnescapeDataString(path);
            Assert.Contains("group=group-big", text);
            Assert.DoesNotContain("user.active", text, StringComparison.OrdinalIgnoreCase);
            if (text.Contains("sysparm_offset=0", StringComparison.Ordinal))
            {
                var response = Api.Json(
                    """
                    {"result":[
                      {"group":{"value":"group-big","display_value":"Big Group"},"user":{"value":"user-1","display_value":"Ada One"}},
                      {"group":{"value":"group-big","display_value":"Big Group"},"user":{"value":"user-2","display_value":"Bea Two"}}
                    ]}
                    """,
                    total: 4);
                response.Headers.TryAddWithoutValidation(
                    "Link",
                    "</api/now/table/sys_user_grmember?sysparm_limit=200&sysparm_offset=2&sysparm_query=group%3Dgroup-big>;rel=\"next\"");
                return response;
            }

            return Api.Json(
                """
                {"result":[
                  {"group":{"value":"group-big","display_value":"Big Group"},"user":{"value":"user-3","display_value":"Cora Three"}},
                  {"group":{"value":"group-big","display_value":"Big Group"},"user":{"value":"user-4","display_value":"Drew Four"}}
                ]}
                """,
                total: 4);
        });
        using var client = ServiceNowClient.Create(session, handler, store);
        var fields = new AssignmentFields();
        fields.Use(client);
        fields.GroupId = "group-big";
        await fields.WhenReady;

        Assert.Equal(2, handler.Calls.Count(call => call.PathAndQuery.Contains("sys_user_grmember", StringComparison.Ordinal)));
        Assert.Equal(
            ["Unassigned", "Ada One", "Bea Two", "Cora Three", "Drew Four"],
            fields.Members.Select(member => member.Label).ToArray());
        Assert.Contains(fields.Members, member => member.Value == "" && member.Label == "Unassigned");
        Assert.DoesNotContain(fields.Members, member => member.Label == "Only Cached");

        await client.ListGroupMembersAsync("group-big", CancellationToken.None);
        Assert.Equal(2, handler.Calls.Count(call => call.PathAndQuery.Contains("sys_user_grmember", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AssignedToListsEveryMemberWhenTheGroupIsServedInPagesOfSeven()
    {
        var folder = Path.Combine(Path.GetTempPath(), "snd-members-" + Guid.NewGuid().ToString("N"));
        var store = new FileFormCatalogStore(folder);
        var session = Api.BasicSession();
        store.Save(session.InstanceUri, new FormCatalogSnapshot
        {
            DirectoryCapturedAt = DateTimeOffset.UtcNow,
            DirectoryComplete = true,
            Groups = [new CachedAssignmentGroup { SysId = "group-aus", Name = "AUS DT - Client Services" }],
            Members = Enumerable.Range(1, 7).Select(index => new CachedGroupMember
            {
                GroupSysId = "group-aus",
                UserSysId = "stale-" + index,
                Name = "Stale " + index
            }).ToList()
        });
        var offsets = new List<int>();
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (!path.Contains("sys_user_grmember", StringComparison.Ordinal))
                return Api.Json("""{"result":[]}""");

            var text = Uri.UnescapeDataString(path);
            Assert.Contains("group=group-aus", text);
            Assert.Contains("user.user_name", text);
            Assert.DoesNotContain("user.active", text, StringComparison.OrdinalIgnoreCase);
            var offset = QueryOffset(text);
            lock (offsets)
                offsets.Add(offset);

            const int pageSize = 7;
            const int total = 25;
            var count = Math.Max(0, Math.Min(pageSize, total - offset));
            var rows = new List<string>();
            for (var index = 0; index < count; index++)
            {
                var number = offset + index + 1;
                var id = "user-" + number.ToString("00");
                if (number == 8)
                {
                    rows.Add(
                        "{\"group\":{\"value\":\"group-aus\",\"display_value\":\"AUS DT - Client Services\"},\"user\":{\"value\":\""
                        + id + "\",\"display_value\":\"\"},\"user.user_name\":{\"value\":\"person.08\",\"display_value\":\"person.08\"},\"user.email\":{\"value\":\"person.08@example.com\",\"display_value\":\"person.08@example.com\"}}");
                }
                else
                {
                    var label = "Person " + number.ToString("00");
                    rows.Add(
                        "{\"group\":{\"value\":\"group-aus\",\"display_value\":\"AUS DT - Client Services\"},\"user\":{\"value\":\""
                        + id + "\",\"display_value\":\"" + label + "\"}}");
                }
            }

            var response = Api.Json("{\"result\":[" + string.Join(",", rows) + "]}", total: total);
            var next = offset + count;
            if (next < total)
            {
                response.Headers.TryAddWithoutValidation(
                    "Link",
                    "</api/now/table/sys_user_grmember?sysparm_fields=group,user&sysparm_limit=200&sysparm_offset="
                    + (offset + 200)
                    + "&sysparm_query=group%3Dgroup-aus>;rel=\"next\"");
            }

            return response;
        });
        using var client = ServiceNowClient.Create(session, handler, store);
        var fields = new AssignmentFields();
        fields.Use(client);
        fields.GroupId = "group-aus";
        await fields.WhenReady;

        Assert.Equal([0, 7, 14, 21], offsets);
        Assert.Equal(26, fields.Members.Count);
        Assert.Equal("Unassigned", fields.Members[0].Label);
        Assert.Contains(fields.Members, member => member.Value == "" && member.Label == "Unassigned");
        Assert.Contains(fields.Members, member => member.Value == "user-08" && member.Label == "person.08");
        Assert.DoesNotContain(fields.Members, member => member.Label.StartsWith("Stale", StringComparison.Ordinal));
        for (var number = 1; number <= 25; number++)
        {
            if (number == 8)
                continue;
            var id = "user-" + number.ToString("00");
            var label = "Person " + number.ToString("00");
            Assert.Contains(fields.Members, member => member.Value == id && member.Label == label);
        }

        await client.ListGroupMembersAsync("group-aus", CancellationToken.None);
        Assert.Equal(4, handler.Calls.Count(call => call.PathAndQuery.Contains("sys_user_grmember", StringComparison.Ordinal)));
    }

    [Fact]
    public void RecentGroupsKeepTheLastFiveOnDisk()
    {
        var folder = Path.Combine(Path.GetTempPath(), "snd-recent-" + Guid.NewGuid().ToString("N"));
        var store = new FileRecentAssignmentGroupStore(folder);
        foreach (var name in new[] { "Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot" })
            store.Remember("group-" + name, name);

        var saved = store.Load();
        Assert.Equal(["group-Foxtrot", "group-Echo", "group-Delta", "group-Charlie", "group-Bravo"], saved.Select(group => group.Value).ToArray());
        Assert.DoesNotContain(saved, group => group.Value == "group-Alpha");

        var again = new FileRecentAssignmentGroupStore(folder);
        Assert.Equal("group-Foxtrot", again.Load()[0].Value);
        Assert.True(File.Exists(Path.Combine(folder, "recent-assignment-groups.json")));
    }

    [Fact]
    public async Task PinnedGroupsStayAlphabeticalAcrossPracticeLists()
    {
        var folder = Path.Combine(Path.GetTempPath(), "snd-recent-" + Guid.NewGuid().ToString("N"));
        var store = new FileRecentAssignmentGroupStore(folder);
        var main = new MainViewModel(new MemorySettingsStore(), new RecordingDesktopServices(), recentGroups: store);
        main.Connection.UseSampleData = true;
        await main.ConnectCommand.ExecuteAsync(null);

        main.Incidents.Assignment.GroupId = "group-net";
        await main.Incidents.Assignment.WhenReady;
        main.Incidents.Assignment.GroupId = "group-aus";
        await main.Incidents.Assignment.WhenReady;
        main.WalkUps.Assignment.GroupId = "group-cs";
        await main.WalkUps.Assignment.WhenReady;

        var labels = main.Incidents.Assignment.Groups.Select(group => group.Label).ToArray();
        Assert.Equal(["Unassigned", "Aus DT - Client Services", "Client Services", "Network"], labels);
        Assert.Equal(labels, main.RequestedItems.Assignment.Groups.Select(group => group.Label).ToArray());
        Assert.Equal(labels, main.WalkUps.Assignment.Groups.Select(group => group.Label).ToArray());
        Assert.Equal("group-cs", store.Load()[0].Value);
        Assert.Equal("Client Services", store.Load()[0].Label);
    }

    [Fact]
    public async Task SavingAnIncidentRemembersItsGroupWithoutADropdownPick()
    {
        var folder = Path.Combine(Path.GetTempPath(), "snd-recent-" + Guid.NewGuid().ToString("N"));
        var store = new FileRecentAssignmentGroupStore(folder);
        using var client = new SampleServiceNowClient();
        var workspace = new IncidentWorkspaceViewModel(new RecordingDesktopServices(), recentGroups: store);
        workspace.Attach(client);
        await workspace.EnsureChoicesAsync();
        workspace.NewRecordCommand.Execute(null);
        workspace.ShortDescription = "Badge printer";
        workspace.Caller.Set("user-jordan", "Jordan Lee");
        await workspace.Assignment.ShowAsync("group-net", "Network", "", "");
        Assert.Empty(store.Load());

        await workspace.SaveCommand.ExecuteAsync(null);

        Assert.Equal("", workspace.ErrorMessage);
        Assert.Equal("group-net", Assert.Single(store.Load()).Value);
        Assert.Equal("Network", store.Load()[0].Label);
    }

    [Fact]
    public void UnassignedMeansNoPersonEvenWhenAGroupIsSet()
    {
        var grouped = TicketRow.FromIncident(new IncidentRecord
        {
            Number = "INC1",
            AssignmentGroup = new ReferenceValue("group-net", "Network")
        });
        var personOnly = TicketRow.FromIncident(new IncidentRecord
        {
            Number = "INC2",
            AssignedTo = new ReferenceValue("user-1", "Ada")
        });
        var request = TicketRow.FromRequest(new RequestRecord { Number = "REQ1" });
        var walkUp = TicketRow.FromInteraction(new InteractionRecord
        {
            Number = "IMS1",
            AssignmentGroup = new ReferenceValue("group-cs", "Client Services")
        });
        var item = TicketRow.FromItem(new RequestedItemRecord
        {
            Number = "RITM1",
            AssignedTo = new ReferenceValue("user-1", "Ada")
        });

        Assert.True(grouped.Unassigned);
        Assert.False(personOnly.Unassigned);
        Assert.False(request.Unassigned);
        Assert.True(walkUp.Unassigned);
        Assert.False(item.Unassigned);
    }

    [Fact]
    public async Task OpenListsFlagUnassignedIncidentsItemsAndWalkUps()
    {
        using var client = new SampleServiceNowClient();
        var incidents = await OpenList(new IncidentWorkspaceViewModel(new RecordingDesktopServices()), client);
        var items = await OpenList(new RequestedItemWorkspaceViewModel(new RecordingDesktopServices()), client);
        var walks = await OpenList(new InteractionWorkspaceViewModel(new RecordingDesktopServices()), client);

        Assert.True(incidents.Items.Single(row => row.Number == "INC0010004").Unassigned);
        Assert.False(incidents.Items.Single(row => row.Number == "INC0010005").Unassigned);
        Assert.True(items.Items.Single(row => row.Number == "RITM0010002").Unassigned);
        Assert.False(items.Items.Single(row => row.Number == "RITM0010001").Unassigned);
        Assert.True(walks.Items.Single(row => row.Number == "IMS0010002").Unassigned);
        Assert.False(walks.Items.Single(row => row.Number == "IMS0010001").Unassigned);
    }

    [Fact]
    public async Task TypingAnEmailQueriesUsersAndSaveAcceptsOneMatch()
    {
        var handler = UserHandler(OneUser("user-jordan", "Jordan Lee", "jordan.lee", "jordan.lee@example.com"));
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);
        var workspace = new IncidentWorkspaceViewModel(new RecordingDesktopServices());
        workspace.Attach(client);
        workspace.NewRecordCommand.Execute(null);
        workspace.ShortDescription = "Badge printer";
        workspace.Caller.Text = "j";
        await Task.Delay(400);
        Assert.DoesNotContain(handler.Calls, call => call.PathAndQuery.Contains("/sys_user", StringComparison.Ordinal));

        workspace.Caller.Text = "jordan.lee@example.com";
        await WaitUntilAsync(() => workspace.Caller.HasSuggestions);
        var query = Uri.UnescapeDataString(handler.Calls.First(call => call.PathAndQuery.Contains("/sys_user", StringComparison.Ordinal)).PathAndQuery);
        Assert.Contains(
            "nameLIKEjordan.lee@example.com^ORfirst_nameLIKEjordan.lee@example.com^ORlast_nameLIKEjordan.lee@example.com^ORemailLIKEjordan.lee@example.com^ORuser_nameLIKEjordan.lee@example.com^active=true",
            query);
        Assert.DoesNotContain("^ORactive=true^", query);
        Assert.Contains("sysparm_limit=20", query);
        Assert.Equal("Jordan Lee", workspace.Caller.Suggestions[0].Display);
        Assert.Equal("jordan.lee@example.com", workspace.Caller.Suggestions[0].Detail);

        await workspace.SaveCommand.ExecuteAsync(null);

        Assert.Equal("", workspace.ErrorMessage);
        Assert.Equal("user-jordan", workspace.Caller.SysId);
        Assert.False(workspace.IsNew);
        Assert.Contains(handler.Calls, call => call.Method == "POST" && call.Body.Contains("user-jordan", StringComparison.Ordinal));
        var match = Uri.UnescapeDataString(handler.Calls.Last(call => call.PathAndQuery.Contains("/sys_user", StringComparison.Ordinal)).PathAndQuery);
        Assert.Contains("email=jordan.lee@example.com^active=true", match);
        Assert.Contains("emailSTARTSWITHjordan.lee@example.com^emailENDSWITHjordan.lee@example.com^active=true", match);
        Assert.Contains("^NQ", match);
    }

    [Fact]
    public async Task ChosenCallerKeepsTheSysIdWhenTheLabelIsNotTheEmail()
    {
        var handler = UserHandler(
            OneUser("user-jordan", "Jordan Lee", "jordan.lee", "jordan.lee@example.com"),
            OneUser("user-jordan2", "Jordan Leigh", "jordan.leigh", "jordan.lee@example.com"));
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);
        var workspace = new IncidentWorkspaceViewModel(new RecordingDesktopServices());
        workspace.Attach(client);
        workspace.NewRecordCommand.Execute(null);
        workspace.ShortDescription = "Badge printer";
        workspace.Caller.Choose(new ReferenceSuggestion("user-jordan", "Jordan Lee", "jordan.lee@example.com")
        {
            UserName = "jordan.lee",
            Email = "jordan.lee@example.com"
        });
        workspace.Caller.Text = "jordan.lee@example.com";

        Assert.Equal("user-jordan", workspace.Caller.SysId);
        Assert.Equal("jordan.lee@example.com", workspace.Caller.Text);

        await workspace.SaveCommand.ExecuteAsync(null);

        Assert.Equal("", workspace.ErrorMessage);
        Assert.Equal("user-jordan", workspace.Caller.SysId);
        Assert.False(workspace.IsNew);
        Assert.Contains(handler.Calls, call => call.Method == "POST" && call.Body.Contains("user-jordan", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Calls, call => call.Method == "POST" && call.Body.Contains("user-jordan2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WalkUpAndRequestAcceptOneTypedEmail()
    {
        var user = OneUser("user-jordan", "Jordan Lee", "jordan.lee", "jordan.lee@example.com");
        var handler = new StubHandler((request, body) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.Contains("interaction", StringComparison.Ordinal))
            {
                Assert.Contains("user-jordan", body);
                return Api.Json("""
                    {"result":{"sys_id":{"value":"ims-1","display_value":"ims-1"},"number":{"value":"IMS0001","display_value":"IMS0001"},"short_description":{"value":"Badge printer","display_value":"Badge printer"},"opened_for":{"value":"user-jordan","display_value":"Jordan Lee"}}}
                    """);
            }

            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.Contains("sc_request", StringComparison.Ordinal))
            {
                Assert.Contains("user-jordan", body);
                return Api.Json("""
                    {"result":{"sys_id":{"value":"req-1","display_value":"req-1"},"number":{"value":"REQ0001","display_value":"REQ0001"},"short_description":{"value":"Badge printer","display_value":"Badge printer"},"requested_for":{"value":"user-jordan","display_value":"Jordan Lee"}}}
                    """);
            }

            if (request.RequestUri!.AbsolutePath.Contains("/sys_user", StringComparison.Ordinal))
                return Api.Json("{\"result\":[" + user + "]}");
            return Api.Json("""{"result":[]}""");
        });
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);

        var walkUp = new InteractionWorkspaceViewModel(new RecordingDesktopServices());
        walkUp.Attach(client);
        walkUp.NewRecordCommand.Execute(null);
        walkUp.ShortDescription = "Badge printer";
        walkUp.Caller.Text = "jordan.lee@example.com";
        await walkUp.SaveCommand.ExecuteAsync(null);
        Assert.Equal("", walkUp.ErrorMessage);
        Assert.Equal("user-jordan", walkUp.Caller.SysId);
        Assert.False(walkUp.IsNew);

        var request = new RequestWorkspaceViewModel(new RecordingDesktopServices());
        request.Attach(client);
        request.NewRecordCommand.Execute(null);
        request.ShortDescription = "Badge printer";
        request.RequestedFor.Text = "jordan.lee@example.com";
        await request.SaveCommand.ExecuteAsync(null);
        Assert.Equal("", request.ErrorMessage);
        Assert.Equal("user-jordan", request.RequestedFor.SysId);
        Assert.False(request.IsNew);
    }

    [Fact]
    public async Task UserSearchIncludesTheBrowserSessionCookie()
    {
        var handler = new StubHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/sys_user", StringComparison.Ordinal))
            {
                var cookie = Assert.Single(request.Headers.GetValues("Cookie"));
                Assert.Contains("glide_user_session=abc", cookie);
                Assert.Equal("tok-ck", Assert.Single(request.Headers.GetValues("X-UserToken")));
                var query = Uri.UnescapeDataString(request.RequestUri.Query);
                Assert.Contains(
                    "nameLIKEjordan^ORfirst_nameLIKEjordan^ORlast_nameLIKEjordan^ORemailLIKEjordan^ORuser_nameLIKEjordan^active=true",
                    query);
            }

            return Api.Json("""{"result":[]}""");
        });
        using var client = ServiceNowClient.Create(ServiceNowSession.FromSettings(new DeskSettings
        {
            InstanceUrl = "https://example.service-now.com",
            AuthMode = ServiceNowAuthMode.BrowserSession,
            SessionCookie = "glide_user_session=abc; JSESSIONID=xyz",
            UserToken = "tok-ck"
        }), handler);

        await client.SearchUsersAsync("jordan", CancellationToken.None);
        Assert.Contains(handler.Calls, call => call.PathAndQuery.Contains("/sys_user", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TwoLiveCallerMatchesStillAskForTheList()
    {
        var handler = UserHandler(
            OneUser("user-jordan", "Jordan Lee", "jordan.lee", "jordan.lee@example.com"),
            OneUser("user-jordan2", "Jordan Leigh", "jordan.leigh", "jordan.lee@example.com"));
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);
        var workspace = new IncidentWorkspaceViewModel(new RecordingDesktopServices());
        workspace.Attach(client);
        workspace.NewRecordCommand.Execute(null);
        workspace.ShortDescription = "Badge printer";
        workspace.Caller.Text = "jordan.lee@example.com";

        await workspace.SaveCommand.ExecuteAsync(null);

        Assert.Contains("Choose the caller from the list", workspace.ErrorMessage);
        Assert.Equal("", workspace.Caller.SysId);
        Assert.True(workspace.IsNew);
        Assert.DoesNotContain(handler.Calls, call => call.Method == "POST");
    }

    [Fact]
    public async Task ClickingACallerSuggestionStoresTheSysId()
    {
        var handler = UserHandler(OneUser("user-jordan", "Jordan Lee", "jordan.lee", "jordan.lee@example.com"));
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);
        var workspace = new IncidentWorkspaceViewModel(new RecordingDesktopServices());
        workspace.Attach(client);
        workspace.NewRecordCommand.Execute(null);
        workspace.Caller.Text = "jordan.lee@example.com";
        await WaitUntilAsync(() => workspace.Caller.HasSuggestions);

        Assert.False(workspace.Caller.IsMatched);
        workspace.Caller.Choose(workspace.Caller.Highlighted!);

        Assert.Equal("user-jordan", workspace.Caller.SysId);
        Assert.Equal("Jordan Lee", workspace.Caller.Text);
        Assert.True(workspace.Caller.IsMatched);
        Assert.False(workspace.Caller.HasSuggestions);

        workspace.Caller.Text = "jor";
        Assert.False(workspace.Caller.IsMatched);
    }

    [Fact]
    public async Task CallerTypeaheadMatchesFirstOrLastNamePrefix()
    {
        var handler = new StubHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/sys_user", StringComparison.Ordinal))
            {
                var query = Uri.UnescapeDataString(request.RequestUri.Query);
                Assert.Contains("first_nameLIKEsm", query, StringComparison.Ordinal);
                Assert.Contains("last_nameLIKEsm", query, StringComparison.Ordinal);
                Assert.Contains("nameLIKEsm", query, StringComparison.Ordinal);
                Assert.Contains("sysparm_fields=sys_id,name,first_name,last_name,user_name,email", query, StringComparison.Ordinal);
                return Api.Json("{\"result\":[" + OneUser("user-smith", "Sam Smith", "sam.smith", "sam.smith@example.com") + "]}");
            }

            return Api.Json("""{"result":[]}""");
        });
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);
        var workspace = new IncidentWorkspaceViewModel(new RecordingDesktopServices());
        workspace.Attach(client);
        workspace.NewRecordCommand.Execute(null);
        workspace.Caller.Text = "sm";
        await WaitUntilAsync(() => workspace.Caller.HasSuggestions);

        var suggestion = Assert.Single(workspace.Caller.Suggestions);
        Assert.Equal("user-smith", suggestion.SysId);
        Assert.Equal("Sam Smith", suggestion.Display);

        workspace.Caller.Choose(suggestion);

        Assert.Equal("user-smith", workspace.Caller.SysId);
        Assert.Equal("Sam Smith", workspace.Caller.Text);
        Assert.False(workspace.Caller.HasSuggestions);
    }

    [Fact]
    public async Task PracticeAttachmentAsksForALiveInstance()
    {
        var desktop = new RecordingDesktopServices();
        using var client = new SampleServiceNowClient();
        var incidents = new IncidentWorkspaceViewModel(desktop);
        incidents.Attach(client);
        incidents.NewRecordCommand.Execute(null);
        Assert.Contains("Save", incidents.AttachmentNote);
        Assert.Empty(incidents.Attachments);
        Assert.DoesNotContain(client.RecentActivity, activity => activity.Path.Contains("attachment", StringComparison.OrdinalIgnoreCase));

        await incidents.OpenFromSearchAsync("inc-printer");
        var file = Assert.Single(incidents.Attachments);
        Assert.Equal("practice-attachment.txt", file.FileName);
        Assert.Contains("live instance", incidents.AttachmentNote, StringComparison.OrdinalIgnoreCase);
        incidents.OpenAttachmentCommand.Execute(file);
        Assert.Empty(desktop.OpenedFiles);
        Assert.Empty(desktop.OpenedUrls);
        Assert.Empty(desktop.CopiedText);
        Assert.Contains("live instance", incidents.EditorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("live instance", incidents.AttachmentNote, StringComparison.OrdinalIgnoreCase);

        var items = new RequestedItemWorkspaceViewModel(new RecordingDesktopServices());
        items.Attach(client);
        await items.OpenFromSearchAsync("ritm-laptop");
        Assert.Equal("practice-attachment.txt", Assert.Single(items.Attachments).FileName);
        Assert.Contains("live instance", items.AttachmentNote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AttachmentQueryUsesTheTableApiAndOpensTheInstanceUrl()
    {
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/attachment/", StringComparison.Ordinal) && path.EndsWith("/file", StringComparison.Ordinal))
            {
                return Api.Json(
                    """{"error":{"message":"blocked","detail":"Attachment download denied"},"status":"failure"}""",
                    HttpStatusCode.Forbidden);
            }

            if (path.Contains("/attachment", StringComparison.Ordinal))
            {
                var query = Uri.UnescapeDataString(request.RequestUri.Query);
                Assert.Contains("table_name=incident^table_sys_id=inc-printer", query);
                return Api.Json("""{"result":[{"sys_id":"att-1","file_name":"photo.png"}]}""");
            }

            if (path.Contains("/incident/", StringComparison.Ordinal))
                return Api.Json("{\"result\":" + Api.IncidentObject + "}");
            return Api.Json("""{"result":[]}""");
        });
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);
        var desktop = new RecordingDesktopServices();
        var workspace = new IncidentWorkspaceViewModel(desktop);
        workspace.Attach(client);
        await workspace.OpenFromSearchAsync("inc-printer");

        Assert.Equal("photo.png", Assert.Single(workspace.Attachments).FileName);
        workspace.OpenAttachmentCommand.Execute(workspace.Attachments[0]);
        Assert.Equal("https://example.service-now.com/sys_attachment.do?sys_id=att-1", Assert.Single(desktop.OpenedUrls));
        Assert.Empty(desktop.OpenedFiles);
        Assert.DoesNotContain(handler.Calls, call => call.PathAndQuery.Contains("/api/now/attachment/att-1/file", StringComparison.Ordinal));

        workspace.OpenAttachmentCommand.Execute(new AttachmentSummary("att-1", "photo.png", "https://files.example.com/photo.png"));
        Assert.Equal("https://files.example.com/photo.png", desktop.OpenedUrls[^1]);

        workspace.OpenAttachmentCommand.Execute(new AttachmentSummary("att-2", "notes.txt", "/sys_attachment.do?sys_id=att-2"));
        Assert.Equal("https://example.service-now.com/sys_attachment.do?sys_id=att-2", desktop.OpenedUrls[^1]);
    }

    private static async Task<RecordWorkspaceViewModel> OpenList(RecordWorkspaceViewModel workspace, IServiceNowClient client)
    {
        workspace.Preset = workspace.Presets.First(preset => preset.Assignment == AssignmentScope.Any && preset.Activity == ActivityFilter.Open);
        workspace.Attach(client);
        await workspace.RefreshAsync();
        return workspace;
    }

    private static StubHandler UserHandler(params string[] users) => new((request, _) =>
    {
        if (request.Method == HttpMethod.Post)
            return Api.Json("{\"result\":" + Api.IncidentObject + "}");
        if (request.RequestUri!.AbsolutePath.Contains("/sys_user", StringComparison.Ordinal))
            return Api.Json("{\"result\":[" + string.Join(",", users) + "]}");
        return Api.Json("""{"result":[]}""");
    });

    private static string OneUser(string sysId, string name, string userName, string email) =>
        "{\"sys_id\":{\"value\":\"" + sysId + "\",\"display_value\":\"" + sysId + "\"},\"name\":{\"value\":\"" + name + "\",\"display_value\":\"" + name + "\"},\"user_name\":{\"value\":\"" + userName + "\",\"display_value\":\"" + userName + "\"},\"email\":{\"value\":\"" + email + "\",\"display_value\":\"" + email + "\"}}";

    private static int QueryOffset(string text)
    {
        const string marker = "sysparm_offset=";
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
            return 0;
        var start = index + marker.Length;
        var end = start;
        while (end < text.Length && char.IsDigit(text[end]))
            end++;
        return end > start ? int.Parse(text[start..end], System.Globalization.CultureInfo.InvariantCulture) : 0;
    }

    private static async Task WaitUntilAsync(Func<bool> ready)
    {
        for (var attempt = 0; attempt < 40 && !ready(); attempt++)
            await Task.Delay(50);
    }
}
