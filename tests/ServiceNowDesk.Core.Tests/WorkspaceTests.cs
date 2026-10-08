using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class WorkspaceTests
{
    [Fact]
    public async Task MyOpenListIsLimitedToTheSignedInAgent()
    {
        using var client = new SampleServiceNowClient();
        var workspace = await OpenIncidentsAsync(client);

        Assert.Equal(5, workspace.Items.Count);
        Assert.Contains(workspace.Items, row => row.Number == "INC0010001");
        Assert.Contains(workspace.Items, row => row.Number == "INC0010002");
        Assert.Contains(workspace.Items, row => row.Number == "INC0010006");
        Assert.Contains(workspace.Items, row => row.Number == "INC0010010");
        Assert.Contains(workspace.Items, row => row.Number == "INC0010024");
        Assert.DoesNotContain(workspace.Items, row => row.Number == "INC0010015");
        Assert.DoesNotContain(workspace.Items, row => row.Number == "INC0010004");

        workspace.SearchText = "INC0010015";
        await workspace.RefreshAsync();
        Assert.Empty(workspace.Items);

        workspace.SearchText = "";
        workspace.ApplyPresetCommand.Execute(PresetCatalog.Incidents.Single(preset => preset.Label == "All open"));
        await WaitUntilAsync(() => workspace.Items.Any(row => row.Number == "INC0010011"));
        Assert.Contains(workspace.Items, row => row.Number == "INC0010011" && row.StateLabel == "On Hold");
        Assert.DoesNotContain(workspace.Items, row => row.Number == "INC0010015");
        Assert.DoesNotContain(workspace.Items, row => row.Number == "INC0010003");

        var search = new SearchWorkspaceViewModel();
        await search.RunAsync(client, "INC0010015");
        var found = Assert.Single(search.Results);
        Assert.Equal("INC0010015", found.Number);
        Assert.Equal(DeskSection.Incidents, found.Section);
    }

    [Fact]
    public void OpenListDropsResolvedLabelsAndKeepsOnHold()
    {
        var incidents = new IncidentWorkspaceViewModel(new RecordingDesktopServices());
        incidents.ShowCachedRows(
        [
            Row("inc-resolved-today", "INC0010015", "Resolved", "6"),
            Row("inc-hold", "INC0010011", "On Hold", "3"),
            Row("inc-printer", "INC0010001", "In Progress", "2"),
            Row("inc-label", "INC0010099", "Closed", "2")
        ], 4);

        Assert.Equal(2, incidents.Items.Count);
        Assert.Contains(incidents.Items, row => row.Number == "INC0010011");
        Assert.Contains(incidents.Items, row => row.Number == "INC0010001");
        Assert.DoesNotContain(incidents.Items, row => row.Number == "INC0010015");
        Assert.DoesNotContain(incidents.Items, row => row.Number == "INC0010099");

        incidents.Preset = PresetCatalog.Incidents.Single(preset => preset.Label == "Closed");
        incidents.ShowCachedRows([Row("inc-resolved-today", "INC0010015", "Resolved", "6")], 1);
        Assert.Contains(incidents.Items, row => row.Number == "INC0010015");

        var items = new RequestedItemWorkspaceViewModel(new RecordingDesktopServices());
        items.ShowCachedRows(
        [
            Row("ritm-closed", "RITM0010004", "Closed Complete", "3"),
            Row("ritm-hold", "RITM0010006", "On Hold", "on_hold")
        ], 2);
        var held = Assert.Single(items.Items);
        Assert.Equal("RITM0010006", held.Number);

        var requests = new RequestWorkspaceViewModel(new RecordingDesktopServices());
        requests.ShowCachedRows(
        [
            Row("req-closed", "REQ0010003", "Closed Complete", "closed_complete"),
            Row("req-open", "REQ0010001", "In Process", "in_process")
        ], 2);
        var openRequest = Assert.Single(requests.Items);
        Assert.Equal("REQ0010001", openRequest.Number);

        var walkUps = new InteractionWorkspaceViewModel(new RecordingDesktopServices());
        walkUps.ShowCachedRows(
        [
            Row("ims-closed", "IMS0010099", "Closed Complete", "closed_complete"),
            Row("ims-hold", "IMS0010004", "On Hold", "on_hold")
        ], 2);
        var openWalkUp = Assert.Single(walkUps.Items);
        Assert.Equal("IMS0010004", openWalkUp.Number);
    }

    private static TicketRow Row(string sysId, string number, string stateLabel, string stateValue) => new()
    {
        SysId = sysId,
        Number = number,
        Title = number,
        StateLabel = stateLabel,
        Tone = "open",
        Meta = "",
        When = "",
        StateValue = stateValue
    };

    [Fact]
    public async Task SearchFindsClosedTextAndJournalNotes()
    {
        using var client = new SampleServiceNowClient();
        var workspace = await OpenIncidentsAsync(client);
        workspace.ApplyPresetCommand.Execute(PresetCatalog.Incidents.Single(preset => preset.Label == "Closed"));
        await FlushAsync();
        workspace.SearchText = "password";
        await workspace.RefreshAsync();

        Assert.Contains(workspace.Items, row => row.Number == "INC0010003");

        workspace.ApplyPresetCommand.Execute(PresetCatalog.Incidents[0]);
        await FlushAsync();
        workspace.SearchText = "bugcheck";
        await workspace.RefreshAsync();
        var match = Assert.Single(workspace.Items);
        Assert.Equal("INC0010006", match.Number);
    }

    [Fact]
    public async Task OpeningAnIncidentShowsWorkNotesAndCustomerComments()
    {
        using var client = new SampleServiceNowClient();
        var workspace = await OpenIncidentsAsync(client);
        await workspace.OpenFromSearchAsync("inc-printer");

        Assert.Equal("The HP printer by finance is jammed and the queue is stuck.", workspace.Description);
        Assert.True(workspace.HasJournal);
        Assert.Equal(2, workspace.Journal.Count);

        var work = Assert.Single(workspace.Journal, note => note.Kind == "work_notes");
        Assert.Equal("Work note", work.KindLabel);
        Assert.Equal("alex.rivera", work.Author);
        Assert.Equal("2026-09-28 10:40", work.CreatedDisplay);
        Assert.Contains("reprint", work.Text, StringComparison.OrdinalIgnoreCase);
        Assert.False(work.IsCustomer);

        var comment = Assert.Single(workspace.Journal, note => note.Kind == "comments");
        Assert.Equal("Customer comment", comment.KindLabel);
        Assert.Equal("jordan.lee", comment.Author);
        Assert.Equal("2026-09-28 09:30", comment.CreatedDisplay);
        Assert.Contains("call me", comment.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(comment.IsCustomer);

        Assert.Equal("work_notes", workspace.Journal[0].Kind);
        Assert.Equal("comments", workspace.Journal[1].Kind);

        await workspace.OpenFromSearchAsync("inc-vpn");
        Assert.Empty(workspace.Journal);
        Assert.False(workspace.HasJournal);
        Assert.Contains("VPN", workspace.Description);
    }

    [Fact]
    public async Task PostingAWorkNoteAppendsItToTheHistory()
    {
        using var client = new SampleServiceNowClient();
        var workspace = await OpenIncidentsAsync(client);
        await workspace.OpenFromSearchAsync("inc-printer");
        var before = workspace.Journal.Select(note => note.Text).ToArray();

        workspace.JournalKind = JournalKind.WorkNotes;
        workspace.JournalText = "Asked the user to reprint from their desk.";
        await workspace.PostJournalCommand.ExecuteAsync(null);

        Assert.Equal("", workspace.JournalText);
        Assert.Equal(before.Length + 1, workspace.Journal.Count);
        Assert.Equal("Asked the user to reprint from their desk.", workspace.Journal[0].Text);
        Assert.Equal("Work note", workspace.Journal[0].KindLabel);
        Assert.Equal("alex.rivera", workspace.Journal[0].Author);
        Assert.All(before, text => Assert.Contains(workspace.Journal, note => note.Text == text));
        Assert.Equal("The HP printer by finance is jammed and the queue is stuck.", workspace.Description);
    }

    [Fact]
    public async Task RequestItemAndWalkUpEditorsLoadBothJournalKinds()
    {
        using var client = new SampleServiceNowClient();

        var requests = new RequestWorkspaceViewModel(new RecordingDesktopServices());
        requests.Attach(client);
        await requests.OpenFromSearchAsync("req-laptop");
        Assert.Equal("Replacement laptop for the finance analyst.", requests.Description);
        Assert.Contains(requests.Journal, note => note.Kind == "work_notes" && note.Author == "alex.rivera" && note.Text.Contains("dock", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(requests.Journal, note => note.IsCustomer && note.Author == "jordan.lee" && note.Text.Contains("finance software", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("work_notes", requests.Journal[0].Kind);

        var items = new RequestedItemWorkspaceViewModel(new RecordingDesktopServices());
        items.Attach(client);
        await items.OpenFromSearchAsync("ritm-laptop");
        Assert.Equal("Standard laptop for the finance analyst.", items.Description);
        Assert.Contains(items.Journal, note => note.Kind == "work_notes" && note.Text.Contains("delivery", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(items.Journal, note => note.IsCustomer && note.Author == "jordan.lee");

        var walkUps = new InteractionWorkspaceViewModel(new RecordingDesktopServices());
        walkUps.Attach(client);
        await walkUps.OpenFromSearchAsync("ims-password");
        Assert.Contains("locked out", walkUps.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(walkUps.Journal, note => note.Kind == "work_notes" && note.Author == "alex.rivera");
        Assert.Contains(walkUps.Journal, note => note.IsCustomer && note.Author == "sam.patel");
        Assert.Equal("work_notes", walkUps.Journal[0].Kind);
    }

    [Fact]
    public async Task CreateUpdateResolveAndJournalRoundTrip()
    {
        using var client = new SampleServiceNowClient();
        var desktop = new RecordingDesktopServices();
        var workspace = await OpenIncidentsAsync(client, desktop);

        workspace.NewRecordCommand.Execute(null);
        workspace.ShortDescription = "   ";
        workspace.Caller.Set("user-sam", "Sam Patel");
        await workspace.SaveCommand.ExecuteAsync(null);
        Assert.Contains("short description", workspace.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        workspace.ShortDescription = "Headset failed";
        workspace.ServiceOffering.Show("offering-print", "Printing");
        await workspace.SaveCommand.ExecuteAsync(null);
        Assert.StartsWith("INC", workspace.Number);
        Assert.False(workspace.IsNew);
        Assert.False(workspace.IsDirty);

        workspace.ShortDescription = "Headset failed for finance";
        Assert.True(workspace.IsDirty);
        var other = workspace.Items.First(row => row.SysId != workspace.Selected?.SysId);
        workspace.Selected = other;
        Assert.Equal(workspace.Number, workspace.Selected?.Number);
        Assert.True(workspace.ShowUnsavedBanner);

        await workspace.SaveCommand.ExecuteAsync(null);
        Assert.False(workspace.IsDirty);
        var saved = await client.GetIncidentAsync(workspace.Items.First(row => row.Number == workspace.Number).SysId, CancellationToken.None);
        Assert.Equal("Headset failed for finance", saved.ShortDescription);

        await workspace.OpenFromSearchAsync("inc-printer");
        workspace.JournalKind = JournalKind.WorkNotes;
        workspace.JournalText = "Asked the user to reprint.";
        await workspace.PostJournalCommand.ExecuteAsync(null);
        Assert.Contains(workspace.Journal, note => note.Text.Contains("reprint", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("", workspace.JournalText);

        workspace.ResolveCode = "Solved (Permanently)";
        workspace.ResolveNotes = "Cleared the jam and reprinted the queue.";
        await workspace.ConfirmResolveCommand.ExecuteAsync(null);
        var resolved = await client.GetIncidentAsync("inc-printer", CancellationToken.None);
        Assert.Equal("6", resolved.State);
        Assert.False(resolved.Active);
        Assert.Equal("Cleared the jam and reprinted the queue.", resolved.CloseNotes);
        workspace.CopyNumberCommand.Execute(null);
        Assert.Equal("INC0010001", desktop.CopiedText.Single());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateNewOpensWhenAnIncidentIsOpenAndChoiceListsMayBeEmpty(bool listsLoaded)
    {
        using var client = new SampleServiceNowClient();
        var workspace = new IncidentWorkspaceViewModel(new RecordingDesktopServices());
        workspace.Attach(client);
        if (listsLoaded)
            await workspace.EnsureChoicesAsync();
        await workspace.OpenFromSearchAsync("inc-printer");
        Assert.Equal("INC0010001", workspace.Number);
        Assert.False(workspace.IsNew);
        if (!listsLoaded)
        {
            Assert.Empty(workspace.ImpactChoices);
            Assert.Empty(workspace.PriorityChoices);
            Assert.Empty(workspace.ContactChoices);
            Assert.DoesNotContain(workspace.Assignment.Groups, group => string.IsNullOrEmpty(group.Value));
        }

        // WPF combos write null when SelectedValue is not in ItemsSource. Clearing the
        // member list, or pointing at a group list that has not loaded the blank row, does that.
        MimicComboClearingMissingValue(workspace.Assignment);

        workspace.NewRecordCommand.Execute(null);

        Assert.True(workspace.IsNew);
        Assert.True(workspace.HasEditor);
        Assert.False(workspace.IsDirty);
        Assert.Equal("", workspace.Number);
        Assert.Equal("New", workspace.StateLabel);
        Assert.Equal("1", workspace.State);
        Assert.Equal("3", workspace.Impact);
        Assert.Equal("3", workspace.Urgency);
        Assert.Equal("", workspace.Priority);
        Assert.Equal("", workspace.Category);
        Assert.Equal("", workspace.Subcategory);
        Assert.Contains(workspace.SubcategoryChoices, choice => choice.Value == "" && choice.Label == "None");
        Assert.Equal("direct", workspace.ContactType);
        Assert.Equal("", workspace.Caller.SysId);
        Assert.Equal("", workspace.Caller.Text);
        Assert.Equal("", workspace.Assignment.GroupId);
        Assert.Equal("", workspace.Assignment.MemberId);
        Assert.Contains(workspace.Assignment.Members, member => member.Value == "" && member.Label == "Unassigned");
        Assert.Equal("Choose a group to list its members.", workspace.Assignment.MemberHint);

        workspace.ShortDescription = "Replacement headset";
        Assert.True(workspace.IsNew);
        Assert.True(workspace.IsDirty);
        Assert.Equal("", workspace.Number);
    }

    [Fact]
    public async Task DiscardRestoresTheLoadedIncident()
    {
        using var client = new SampleServiceNowClient();
        var workspace = await OpenIncidentsAsync(client);
        await workspace.OpenFromSearchAsync("inc-vpn");
        workspace.ShortDescription = "Changed";
        workspace.DiscardCommand.Execute(null);

        Assert.Equal("VPN drops every few minutes", workspace.ShortDescription);
        Assert.False(workspace.IsDirty);
    }

    [Fact]
    public async Task RequestsShowRelatedItemsAndCanBeClosed()
    {
        using var client = new SampleServiceNowClient();
        var requests = new RequestWorkspaceViewModel(new RecordingDesktopServices());
        requests.Attach(client);
        await requests.EnsureChoicesAsync();
        await requests.RefreshAsync();

        Assert.Contains(requests.Items, row => row.Number == "REQ0010001");
        Assert.DoesNotContain(requests.Items, row => row.Number == "REQ0010003");

        await requests.OpenFromSearchAsync("req-laptop");
        Assert.Equal(2, requests.RelatedItems.Count);
        requests.SpecialInstructions = "Ship to the finance floor.";
        await requests.SaveCommand.ExecuteAsync(null);
        requests.ResolveCode = "closed_complete";
        requests.ResolveNotes = "Laptop and dock delivered.";
        await requests.ConfirmResolveCommand.ExecuteAsync(null);

        var closed = await client.GetRequestAsync("req-laptop", CancellationToken.None);
        Assert.Equal("closed_complete", closed.RequestState);
        Assert.False(closed.Active);
    }

    [Fact]
    public async Task RequestItemsCanBeReassignedAndClosed()
    {
        using var client = new SampleServiceNowClient();
        var items = new RequestedItemWorkspaceViewModel(new RecordingDesktopServices());
        items.Attach(client);
        await items.EnsureChoicesAsync();
        await items.RefreshAsync();
        await items.OpenFromSearchAsync("ritm-dock");
        await items.Assignment.WhenReady;

        Assert.Equal("group-cs", items.Assignment.GroupId);
        Assert.Contains(items.Assignment.Members, member => member.Value == "sample-user");
        Assert.DoesNotContain(items.Assignment.Members, member => member.Value == "user-sam");
        items.Assignment.GroupId = "group-net";
        await items.Assignment.WhenReady;
        Assert.Equal("", items.Assignment.MemberId);
        Assert.Contains(items.Assignment.Members, member => member.Value == "user-sam");

        items.Assignment.MemberId = "user-sam";
        await items.SaveCommand.ExecuteAsync(null);
        items.ResolveCode = "3";
        items.ResolveNotes = "Dock imaged and delivered.";
        await items.ConfirmResolveCommand.ExecuteAsync(null);

        var closed = await client.GetRequestedItemAsync("ritm-dock", CancellationToken.None);
        Assert.Equal("3", closed.State);
        Assert.Equal("group-net", closed.AssignmentGroup.SysId);
        Assert.Equal("user-sam", closed.AssignedTo.SysId);
        Assert.False(items.AllowCreate);
    }

    [Fact]
    public async Task OnHoldRequestedItemStoresReasonAndFollowUp()
    {
        using var client = new SampleServiceNowClient();
        var items = new RequestedItemWorkspaceViewModel(new RecordingDesktopServices());
        items.Attach(client);
        await items.EnsureChoicesAsync();
        await items.OpenFromSearchAsync("ritm-hold");

        Assert.True(items.ShowHoldReason);
        Assert.Equal("on_hold", items.State);
        Assert.Equal("awaiting_vendor", items.HoldReason);
        Assert.Equal("2026-10-01 11:00:00", items.FollowUp);
        Assert.Contains(items.HoldReasonChoices, choice => choice.Value == "awaiting_vendor" && choice.Label == "Awaiting Vendor");

        items.FollowUp = "2026-11-20 10:10:23";
        await items.SaveCommand.ExecuteAsync(null);

        Assert.Equal("", items.ErrorMessage);
        var saved = await client.GetRequestedItemAsync("ritm-hold", CancellationToken.None);
        Assert.Equal("on_hold", saved.State);
        Assert.Equal("awaiting_vendor", saved.HoldReason);
        Assert.Equal("Awaiting Vendor", saved.HoldReasonLabel);
        Assert.Equal("2026-11-20 10:10:23", saved.FollowUp);

        items.State = "2";
        Assert.False(items.ShowHoldReason);
        Assert.Equal("awaiting_vendor", items.HoldReason);
        Assert.Equal("2026-11-20 10:10:23", items.FollowUp);
        await items.SaveCommand.ExecuteAsync(null);

        var released = await client.GetRequestedItemAsync("ritm-hold", CancellationToken.None);
        Assert.Equal("2", released.State);
        Assert.Equal("awaiting_vendor", released.HoldReason);
        Assert.Equal("2026-11-20 10:10:23", released.FollowUp);
    }

    [Fact]
    public async Task OnHoldRequestedItemRequiresReasonAndFollowUpBeforeSave()
    {
        using var client = new SampleServiceNowClient();
        var items = new RequestedItemWorkspaceViewModel(new RecordingDesktopServices());
        items.Attach(client);
        await items.EnsureChoicesAsync();
        await items.OpenFromSearchAsync("ritm-dock");

        items.StateChoices.Add(new Choice("8", "On Hold"));
        items.State = "8";
        Assert.True(items.ShowHoldReason);
        items.State = "2";
        Assert.False(items.ShowHoldReason);

        items.State = "on_hold";
        items.HoldReason = "";
        items.FollowUp = "";
        Assert.True(items.ShowHoldReason);
        var patches = ItemPatches(client);
        await items.SaveCommand.ExecuteAsync(null);

        Assert.Equal(patches, ItemPatches(client));
        Assert.Contains("on hold reason", items.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("follow up", items.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        var unchanged = await client.GetRequestedItemAsync("ritm-dock", CancellationToken.None);
        Assert.Equal("1", unchanged.State);

        items.HoldReason = "awaiting_vendor";
        await items.SaveCommand.ExecuteAsync(null);
        Assert.Equal(patches, ItemPatches(client));
        Assert.Contains("follow up", items.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        items.HoldReason = "";
        items.FollowUp = "2026-11-20 10:10:23";
        await items.SaveCommand.ExecuteAsync(null);
        Assert.Equal(patches, ItemPatches(client));
        Assert.Contains("on hold reason", items.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RequestItemOffHoldDoesNotRequireReasonOrFollowUp()
    {
        using var client = new SampleServiceNowClient();
        var items = new RequestedItemWorkspaceViewModel(new RecordingDesktopServices());
        items.Attach(client);
        await items.EnsureChoicesAsync();
        await items.OpenFromSearchAsync("ritm-dock");

        Assert.False(items.ShowHoldReason);
        Assert.Equal("", items.HoldReason);
        Assert.Equal("", items.FollowUp);
        items.ShortDescription = "USB-C dock for finance";
        await items.SaveCommand.ExecuteAsync(null);

        Assert.Equal("", items.ErrorMessage);
        Assert.StartsWith("Saved ", items.EditorMessage);
        var saved = await client.GetRequestedItemAsync("ritm-dock", CancellationToken.None);
        Assert.Equal("1", saved.State);
        Assert.Equal("USB-C dock for finance", saved.ShortDescription);
        Assert.Equal("", saved.HoldReason);
        Assert.Equal("", saved.FollowUp);
    }

    private static int ItemPatches(SampleServiceNowClient client) =>
        client.RecentActivity.Count(entry => entry.Method == "PATCH" && entry.Path.Contains("sc_req_item", StringComparison.Ordinal));

    [Fact]
    public async Task UnifiedSearchFindsOldIncidentsByNumberOrText()
    {
        using var client = new SampleServiceNowClient();
        var search = new SearchWorkspaceViewModel();
        await search.RunAsync(client, "bugcheck");
        Assert.Contains(search.Results, hit => hit.Number == "INC0010006" && hit.Section == DeskSection.Incidents);

        await search.RunAsync(client, "INC0010003");
        var match = Assert.Single(search.Results);
        Assert.Equal("INC0010003", match.Number);
        Assert.Equal(DeskSection.Incidents, match.Section);
    }

    [Fact]
    public async Task UnifiedSearchFindsKnowledgeByTextAndByNumber()
    {
        using var client = new SampleServiceNowClient();
        var search = new SearchWorkspaceViewModel();
        await search.RunAsync(client, "zephyrmail");
        var article = Assert.Single(search.Results);
        Assert.Equal("KB0001001", article.Number);
        Assert.Equal(DeskSection.Knowledge, article.Section);
        Assert.Equal("zephyrmail", search.Query);

        await search.RunAsync(client, "KB0001002");
        var byNumber = Assert.Single(search.Results);
        Assert.Equal("KB0001002", byNumber.Number);
        Assert.Equal(DeskSection.Knowledge, byNumber.Section);
        Assert.Equal("Replace a toner cartridge", byNumber.Title);
    }

    [Fact]
    public async Task DoubleClickOpensARequestForUpdateAndBackKeepsTheResults()
    {
        var main = new MainViewModel(new MemorySettingsStore(), new RecordingDesktopServices());
        main.Connection.UseSampleData = true;
        await main.ConnectCommand.ExecuteAsync(null);
        Assert.False(main.ShowBack);

        main.SelectedSection = DeskSection.Search;
        await FlushAsync();
        main.SearchText = "REQ0010001";
        await WaitUntilAsync(() => main.Search.Results.Any(hit => hit.Number == "REQ0010001"));

        var hit = Assert.Single(main.Search.Results);
        Assert.Equal(DeskSection.Requests, hit.Section);
        Assert.Equal("REQ0010001", main.Search.Query);
        main.Search.Selected = hit;
        main.Search.OpenSelectedCommand.Execute(null);
        await main.SearchOpenTask;

        Assert.Equal(DeskSection.Requests, main.SelectedSection);
        Assert.True(main.ShowBack);
        Assert.True(main.Requests.HasEditor);
        Assert.Equal("REQ0010001", main.Requests.Number);
        Assert.Same(hit, main.Search.Results.Single());

        main.Requests.SpecialInstructions = "Leave at reception.";
        Assert.True(main.Requests.IsDirty);
        await main.Requests.SaveCommand.ExecuteAsync(null);
        Assert.False(main.Requests.IsDirty);
        Assert.Contains("Saved", main.Requests.EditorMessage);

        main.SearchText = "printer";
        await Task.Delay(400);
        Assert.Same(hit, main.Search.Results.Single());
        Assert.Equal("REQ0010001", main.Search.Query);

        main.BackCommand.Execute(null);
        Assert.Equal(DeskSection.Search, main.SelectedSection);
        Assert.False(main.ShowBack);
        Assert.Equal("REQ0010001", main.SearchText);
        Assert.Same(hit, main.Search.Results.Single());
        Assert.Equal(hit, main.Search.Selected);

        main.Search.OpenSelectedCommand.Execute(null);
        await main.SearchOpenTask;
        Assert.Equal("Leave at reception.", main.Requests.SpecialInstructions);
        Assert.True(main.Requests.HasEditor);
    }

    [Fact]
    public async Task DoubleClickOpensAKnowledgeArticleAndBackKeepsTheResults()
    {
        var main = new MainViewModel(new MemorySettingsStore(), new RecordingDesktopServices());
        main.Connection.UseSampleData = true;
        await main.ConnectCommand.ExecuteAsync(null);
        main.SelectedSection = DeskSection.Search;
        await FlushAsync();
        main.SearchText = "zephyrmail";
        await WaitUntilAsync(() => main.Search.Results.Any(hit => hit.Number == "KB0001001"));

        var hit = Assert.Single(main.Search.Results);
        main.Search.Selected = hit;
        main.Search.OpenSelectedCommand.Execute(null);
        await main.SearchOpenTask;

        Assert.Equal(DeskSection.Knowledge, main.SelectedSection);
        Assert.True(main.ShowBack);
        Assert.Equal("KB0001001", main.Knowledge.Number);
        Assert.Contains("zephyrmail", main.Knowledge.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Close Outlook", main.Knowledge.Body);
        Assert.DoesNotContain("alert", main.Knowledge.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<", main.Knowledge.Body);
        Assert.Same(hit, main.Search.Results.Single());

        main.BackCommand.Execute(null);
        Assert.Equal(DeskSection.Search, main.SelectedSection);
        Assert.False(main.ShowBack);
        Assert.Equal("zephyrmail", main.SearchText);
        Assert.Equal("zephyrmail", main.Search.Query);
        Assert.Same(hit, main.Search.Results.Single());
        Assert.Equal(hit, main.Search.Selected);
    }

    [Fact]
    public async Task CatalogOrderCreatesARequestForTheCaller()
    {
        using var client = new SampleServiceNowClient();
        var catalog = new CatalogWorkspaceViewModel();
        CatalogOrderResult? ordered = null;
        catalog.RequestOrdered += (_, result) => ordered = result;
        await catalog.RunAsync(client, "laptop");
        catalog.SelectedItem = Assert.Single(catalog.Items);
        Assert.Equal(2, catalog.Variables.Count);

        await catalog.OrderCommand.ExecuteAsync(null);
        Assert.Contains("Department", catalog.ErrorMessage);

        catalog.Variables.Single(variable => variable.Name == "department").Value = "Finance";
        catalog.Variables.Single(variable => variable.Name == "preferred_os").Value = "win11";
        catalog.RequestedFor.Set("user-jordan", "Jordan Lee");
        await catalog.OrderCommand.ExecuteAsync(null);

        Assert.NotNull(ordered);
        Assert.StartsWith("REQ", ordered.RequestNumber);
        var request = await client.GetRequestAsync(ordered.RequestSysId, CancellationToken.None);
        Assert.Equal("user-jordan", request.RequestedFor.SysId);
    }

    [Fact]
    public async Task OpeningAClosedSearchHitStaysOnThatIncident()
    {
        var main = new MainViewModel(new MemorySettingsStore(), new RecordingDesktopServices());
        main.Connection.UseSampleData = true;
        await main.ConnectCommand.ExecuteAsync(null);
        main.SelectedSection = DeskSection.Search;
        await FlushAsync();

        await main.OpenSearchResultAsync(new SearchHit
        {
            Section = DeskSection.Incidents,
            TableLabel = "Incident",
            SysId = "inc-password",
            Number = "INC0010003",
            Title = "Password reset",
            StateLabel = "Resolved",
            Tone = "resolved",
            Meta = "",
            When = "",
            SortKey = ""
        });

        Assert.Equal(DeskSection.Incidents, main.SelectedSection);
        Assert.Equal("INC0010003", main.Incidents.Number);
        Assert.Equal("6", main.Incidents.State);
        await Task.Delay(100);
        Assert.Equal("INC0010003", main.Incidents.Number);
    }

    [Fact]
    public async Task PracticeConnectionLoadsTheDeskWithoutANetwork()
    {
        var store = new MemorySettingsStore();
        var main = new MainViewModel(store, new RecordingDesktopServices());
        main.Connection.UseSampleData = true;
        await main.ConnectCommand.ExecuteAsync(null);

        Assert.True(main.IsConnected);
        Assert.True(main.IsSample);
        Assert.Equal("Alex Rivera", main.ConnectedUser);
        Assert.Equal(DeskSection.Incidents, main.SelectedSection);
        Assert.NotEmpty(main.Incidents.Items);
        Assert.True(main.Requests.HasLoaded);
        Assert.True(main.WalkUps.HasLoaded);
        Assert.False(main.Startup.ShowScreen);
        Assert.False(main.Startup.ShowBar);
        Assert.False(main.Startup.IsRunning);
        Assert.Equal(9, main.Startup.Lines.Count);
        Assert.Contains(main.Startup.Lines, line => line.Name == "Knowledge" && line.Percent == 100);
        Assert.Contains(main.Startup.Lines, line => line.Name == "Service offerings");
        Assert.Contains(main.Startup.Lines, line => line.Name == "Configuration items");
        Assert.All(main.Startup.Lines, line => Assert.Equal(100, line.Percent));
        Assert.Contains(main.Startup.Lines, line => line.Name == "Walk-ups");
        Assert.Contains(main.Incidents.Assignment.Groups, group => group.Value == "group-aus" && group.Label == "Aus DT - Client Services");
        Assert.True(store.Current.UseSampleData);
        Assert.NotEmpty(main.Activity);
    }

    [Fact]
    public async Task AusClientServicesMembersFollowAnyCasingOfTheGroupName()
    {
        using var client = new SampleServiceNowClient();
        var workspace = new IncidentWorkspaceViewModel(new RecordingDesktopServices());
        workspace.Attach(client);
        await workspace.EnsureChoicesAsync();
        workspace.NewRecordCommand.Execute(null);

        workspace.Assignment.GroupId = "aus dt - client services";
        await workspace.Assignment.WhenReady;

        Assert.Equal("group-aus", workspace.Assignment.GroupId);
        AssertAusMembers(workspace);

        workspace.Assignment.GroupId = "AUS DT - CLIENT SERVICES";
        await workspace.Assignment.WhenReady;

        Assert.Equal("group-aus", workspace.Assignment.GroupId);
        AssertAusMembers(workspace);
        await workspace.Assignment.LoadGroupsAsync();
        Assert.Equal("group-aus", workspace.Assignment.GroupId);
        AssertAusMembers(workspace);
    }

    [Fact]
    public async Task SaveAcceptsACallerTypedAsTheirExactEmail()
    {
        using var client = new SampleServiceNowClient();
        var workspace = await OpenIncidentsAsync(client);
        workspace.NewRecordCommand.Execute(null);
        workspace.ShortDescription = "Badge printer";
        workspace.Caller.Text = "  Jordan.Lee@Example.com ";

        await workspace.SaveCommand.ExecuteAsync(null);

        Assert.Equal("", workspace.ErrorMessage);
        Assert.Equal("user-jordan", workspace.Caller.SysId);
        Assert.False(workspace.IsNew);
        Assert.StartsWith("INC", workspace.Number);
    }

    [Fact]
    public async Task ServiceOfferingIsOptionalOnCreateAndRequiredOnUpdate()
    {
        using var client = new SampleServiceNowClient();
        var workspace = await OpenIncidentsAsync(client);
        workspace.NewRecordCommand.Execute(null);
        workspace.ShortDescription = "Headset failed";
        workspace.Caller.Set("user-jordan", "Jordan Lee");

        await workspace.SaveCommand.ExecuteAsync(null);

        Assert.Equal("", workspace.ErrorMessage);
        Assert.False(workspace.IsNew);
        Assert.Equal("", workspace.ServiceOffering.Id);
        var created = await client.GetIncidentAsync(workspace.Items.First(row => row.Number == workspace.Number).SysId, CancellationToken.None);
        Assert.True(created.ServiceOffering.IsEmpty);

        workspace.ShortDescription = "Headset failed again";
        await workspace.SaveCommand.ExecuteAsync(null);

        Assert.Contains("service offering", workspace.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Headset failed", created.ShortDescription);

        workspace.ServiceOffering.Show("offering-print", "Printing");
        workspace.ConfigurationItem.Show("ci-printer", "HQ-PRINTER-01");
        await workspace.SaveCommand.ExecuteAsync(null);

        Assert.Equal("", workspace.ErrorMessage);
        var updated = await client.GetIncidentAsync(workspace.Items.First(row => row.Number == workspace.Number).SysId, CancellationToken.None);
        Assert.Equal("Headset failed again", updated.ShortDescription);
        Assert.Equal("offering-print", updated.ServiceOffering.SysId);
        Assert.Equal("Printing", updated.ServiceOffering.Display);
        Assert.Equal("HQ-PRINTER-01", updated.ConfigurationItem.Display);
        Assert.Equal("Printing", workspace.ServiceOffering.SelectedLabel);
        Assert.DoesNotContain("offering-print", workspace.ServiceOffering.SelectedLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveAcceptsACallerTypedAsTheirDisplayNameInAnotherCasing()
    {
        using var client = new SampleServiceNowClient();
        var workspace = await OpenIncidentsAsync(client);
        workspace.NewRecordCommand.Execute(null);
        workspace.ShortDescription = "Badge printer";
        workspace.Caller.Text = "jordan lee";

        await workspace.SaveCommand.ExecuteAsync(null);

        Assert.Equal("", workspace.ErrorMessage);
        Assert.Equal("user-jordan", workspace.Caller.SysId);
        Assert.False(workspace.IsNew);
    }

    [Fact]
    public async Task AmbiguousCallerNameStillAsksForTheList()
    {
        using var client = new SampleServiceNowClient();
        var workspace = await OpenIncidentsAsync(client);
        workspace.NewRecordCommand.Execute(null);
        workspace.ShortDescription = "Badge printer";
        workspace.Caller.Text = "Casey Ng";

        await workspace.SaveCommand.ExecuteAsync(null);

        Assert.Contains("Choose the caller from the list", workspace.ErrorMessage);
        Assert.Equal("", workspace.Caller.SysId);
        Assert.True(workspace.IsNew);
    }

    [Fact]
    public async Task ChoosingACallerRowCountsAsChosen()
    {
        using var client = new SampleServiceNowClient();
        var workspace = await OpenIncidentsAsync(client);
        workspace.NewRecordCommand.Execute(null);
        workspace.ShortDescription = "Badge printer";
        workspace.Caller.Choose(new ReferenceSuggestion("user-sam", "Sam Patel", "sam.patel · sam.patel@example.com")
        {
            UserName = "sam.patel",
            Email = "sam.patel@example.com"
        });

        await workspace.SaveCommand.ExecuteAsync(null);

        Assert.Equal("", workspace.ErrorMessage);
        Assert.Equal("user-sam", workspace.Caller.SysId);
        Assert.False(workspace.IsNew);
    }

    [Fact]
    public async Task EmptyCallerStaysInvalidOnANewIncident()
    {
        using var client = new SampleServiceNowClient();
        var workspace = await OpenIncidentsAsync(client);
        workspace.NewRecordCommand.Execute(null);
        workspace.ShortDescription = "Badge printer";
        workspace.Caller.Text = "   ";

        await workspace.SaveCommand.ExecuteAsync(null);

        Assert.Contains("Choose the caller from the list", workspace.ErrorMessage);
        Assert.True(workspace.IsNew);
    }

    [Fact]
    public void SplashKeepsEachFinishedLine()
    {
        var splash = new StartupDownloadModel();
        splash.Begin(6);
        Assert.Equal("Downloading data 0/6", splash.Title);
        Assert.True(splash.ShowScreen);
        Assert.False(splash.ShowBar);

        splash.Start("Choices");
        Assert.Equal("Downloading data 0/6 — Choices", splash.Title);
        splash.Report(0);
        splash.Report(40);
        Assert.Equal("Choices    40%", splash.Lines[0].Text);

        splash.Complete();
        splash.Start("Assignment groups");
        splash.Report(15);

        Assert.Equal(2, splash.Lines.Count);
        Assert.Equal(100, splash.Lines[0].Percent);
        Assert.Equal("Choices    100%", splash.Lines[0].Text);
        Assert.Equal(15, splash.Lines[1].Percent);
        Assert.Equal("Downloading data 1/6 — Assignment groups", splash.Title);

        splash.CompleteCached();
        Assert.Equal("Assignment groups    cached", splash.Lines[1].Text);
        Assert.Equal(100, splash.Lines[1].Percent);

        splash.Start("Assignment group members");
        splash.Report(20);
        splash.Fail("Could not reach the ServiceNow instance.");
        Assert.Equal("Assignment group members    Could not reach the ServiceNow instance.", splash.Lines[2].Text);
        Assert.Equal("Choices    100%", splash.Lines[0].Text);
        splash.Close();
        Assert.False(splash.ShowScreen);
        Assert.True(splash.ShowBar);
        Assert.Equal("3/6", splash.CountText);
        Assert.Equal("50%", splash.PercentText);
        Assert.Equal(20, splash.Lines[2].Percent);
        Assert.True(splash.IsRunning);
    }

    [Fact]
    public void StartupScreenClosesWhenTheDownloadFinishes()
    {
        var splash = new StartupDownloadModel();
        Assert.False(splash.ShowScreen);
        Assert.False(splash.ShowBar);
        Assert.False(splash.IsRunning);

        splash.Begin(2);
        Assert.True(splash.ShowScreen);
        Assert.False(splash.ShowBar);
        splash.Start("Choices");
        splash.Report(10);
        splash.Complete();
        Assert.True(splash.ShowScreen);
        Assert.Equal("Downloading data 1/2", splash.Title);

        splash.Start("Incidents");
        splash.Report(40);
        splash.Complete();

        Assert.False(splash.ShowScreen);
        Assert.False(splash.ShowBar);
        Assert.False(splash.IsRunning);
        Assert.Equal("2/2", splash.CountText);
        Assert.Equal("100%", splash.PercentText);
        Assert.Equal(100, splash.Lines[0].Percent);
        Assert.Equal("Incidents    100%", splash.Lines[1].Text);
    }

    [Fact]
    public void ClosingTheStartupScreenEarlyLeavesTheCompactBar()
    {
        var splash = new StartupDownloadModel();
        splash.Begin(2);
        splash.Start("Choices");
        splash.Report(10);
        splash.Close();

        Assert.False(splash.ShowScreen);
        Assert.True(splash.ShowBar);
        Assert.True(splash.IsRunning);
        Assert.Equal("0/2", splash.CountText);
        Assert.Equal("0%", splash.PercentText);
        Assert.Equal("Downloading data 0/2 — Choices", splash.Title);

        splash.Report(55);
        Assert.Equal(55, splash.Lines[0].Percent);
        splash.Complete();
        Assert.Equal("1/2", splash.CountText);
        Assert.Equal("50%", splash.PercentText);
        Assert.True(splash.ShowBar);
        Assert.False(splash.ShowScreen);

        splash.Start("Assignment groups");
        splash.Report(5);
        splash.Complete();

        Assert.False(splash.ShowScreen);
        Assert.False(splash.ShowBar);
        Assert.False(splash.IsRunning);
        Assert.Equal(100, splash.Lines[0].Percent);
        Assert.Equal("Assignment groups    100%", splash.Lines[1].Text);
    }

    [Fact]
    public async Task RefreshingAListDoesNotOpenTheStartupScreen()
    {
        var main = new MainViewModel(new MemorySettingsStore(), new RecordingDesktopServices());
        main.Connection.UseSampleData = true;
        await main.ConnectCommand.ExecuteAsync(null);

        Assert.False(main.Startup.ShowScreen);
        Assert.False(main.Startup.ShowBar);
        Assert.False(main.Startup.IsRunning);
        Assert.True(main.IsConnected);
        var before = main.Incidents.Items.Count;

        await main.RefreshActiveCommand.ExecuteAsync(null);

        Assert.True(main.IsConnected);
        Assert.False(main.Startup.ShowScreen);
        Assert.False(main.Startup.ShowBar);
        Assert.False(main.Startup.IsRunning);
        Assert.Equal(before, main.Incidents.Items.Count);
        Assert.NotEmpty(main.Incidents.Items);
    }

    [Fact]
    public async Task ReferenceFieldChoosesAMatchAndClearsItWhenEdited()
    {
        var field = new ReferenceFieldModel(async (text, _) =>
        {
            await Task.Yield();
            return new List<ReferenceSuggestion> { new("user-jordan", "Jordan Lee", "jordan.lee") };
        }, TimeSpan.Zero);

        field.Text = "jor";
        await FlushAsync();
        Assert.True(field.HasSuggestions);
        Assert.False(field.IsMatched);
        field.Choose(field.Highlighted!);
        Assert.Equal("user-jordan", field.SysId);
        Assert.Equal("Jordan Lee", field.Text);
        Assert.True(field.IsMatched);

        field.Text = "Jordan Leigh";
        Assert.Equal("", field.SysId);
        Assert.False(field.IsMatched);
    }

    private static void AssertAusMembers(IncidentWorkspaceViewModel workspace)
    {
        Assert.Contains(workspace.Assignment.Members, member => member.Value == "user-jordan" && member.Label == "Jordan Lee");
        Assert.Contains(workspace.Assignment.Members, member => member.Value == "user-sam" && member.Label == "Sam Patel");
        Assert.Contains(workspace.Assignment.Members, member => member.Value == "" && member.Label == "Unassigned");
    }

    private static void MimicComboClearingMissingValue(AssignmentFields fields)
    {
        fields.PropertyChanged += (_, args) => ClearSelectionWhenMissing(fields, args.PropertyName);
        fields.Groups.CollectionChanged += (_, _) => ClearSelectionWhenMissing(fields, nameof(AssignmentFields.GroupId));
        fields.Members.CollectionChanged += (_, _) => ClearSelectionWhenMissing(fields, nameof(AssignmentFields.MemberId));
    }

    private static void ClearSelectionWhenMissing(AssignmentFields fields, string? propertyName)
    {
        if (propertyName == nameof(AssignmentFields.GroupId)
            && fields.Groups.All(choice => choice.Value != fields.GroupId))
            fields.GroupId = null!;

        if (propertyName == nameof(AssignmentFields.MemberId)
            && fields.Members.All(choice => choice.Value != fields.MemberId))
            fields.MemberId = null!;
    }

    private static async Task<IncidentWorkspaceViewModel> OpenIncidentsAsync(IServiceNowClient client, IDesktopServices? desktop = null)
    {
        var workspace = new IncidentWorkspaceViewModel(desktop ?? new RecordingDesktopServices());
        workspace.Attach(client);
        await workspace.EnsureChoicesAsync();
        await workspace.RefreshAsync();
        return workspace;
    }

    private static async Task FlushAsync()
    {
        await Task.Delay(50);
    }

    private static async Task WaitUntilAsync(Func<bool> ready)
    {
        for (var attempt = 0; attempt < 40 && !ready(); attempt++)
            await Task.Delay(50);
    }
}
