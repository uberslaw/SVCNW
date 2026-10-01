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

        Assert.Equal(3, workspace.Items.Count);
        Assert.Contains(workspace.Items, row => row.Number == "INC0010001");
        Assert.Contains(workspace.Items, row => row.Number == "INC0010002");
        Assert.Contains(workspace.Items, row => row.Number == "INC0010006");
        Assert.DoesNotContain(workspace.Items, row => row.Number == "INC0010004");
    }

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

        items.Assignee.Set("sample-user", "Alex Rivera");
        await items.SaveCommand.ExecuteAsync(null);
        items.ResolveCode = "3";
        items.ResolveNotes = "Dock imaged and delivered.";
        await items.ConfirmResolveCommand.ExecuteAsync(null);

        var closed = await client.GetRequestedItemAsync("ritm-dock", CancellationToken.None);
        Assert.Equal("3", closed.State);
        Assert.Equal("sample-user", closed.AssignedTo.SysId);
        Assert.False(items.AllowCreate);
    }

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
        Assert.True(store.Current.UseSampleData);
        Assert.NotEmpty(main.Activity);
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
        field.Choose(field.Highlighted!);
        Assert.Equal("user-jordan", field.SysId);
        Assert.Equal("Jordan Lee", field.Text);

        field.Text = "Jordan Leigh";
        Assert.Equal("", field.SysId);
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
}
