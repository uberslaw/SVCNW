using ServiceNowDesk.Client;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class BlankTicketFormTests
{
    [Fact]
    public void NoSelectionShowsTheNewIncidentForm()
    {
        var workspace = new IncidentWorkspaceViewModel(new RecordingDesktopServices());

        AssertBlank(workspace);
        Assert.Equal("", workspace.Number);
        Assert.Equal("", workspace.ShortDescription);
        Assert.Equal("New", workspace.StateLabel);
        Assert.Equal("1", workspace.State);
    }

    [Fact]
    public async Task SelectingAnIncidentShowsThatRowAndClearingReturnsToTheBlankForm()
    {
        using var client = new SampleServiceNowClient();
        var workspace = await OpenIncidentsAsync(client);
        AssertBlank(workspace);

        var row = Assert.Single(workspace.Items, item => item.SysId == "inc-printer");
        workspace.Selected = row;
        await WaitUntilAsync(() => !workspace.IsNew && workspace.Number == row.Number && !workspace.IsEditorBusy);

        Assert.False(workspace.ShowsBlankTicketForm);
        Assert.False(workspace.ShowsSelectPrompt);
        Assert.True(workspace.HasEditor);
        Assert.False(workspace.IsNew);
        Assert.Equal(row.Number, workspace.Number);
        Assert.Equal(row.SysId, workspace.Selected?.SysId);
        var loaded = workspace.Selected;

        workspace.ShortDescription = "Changed while open";
        Assert.True(workspace.IsDirty);
        workspace.Selected = null;
        Assert.Same(loaded, workspace.Selected);
        Assert.True(workspace.ShowUnsavedBanner);
        Assert.False(workspace.ShowsBlankTicketForm);
        Assert.Equal("Changed while open", workspace.ShortDescription);

        workspace.DiscardCommand.Execute(null);
        Assert.False(workspace.IsDirty);
        workspace.Selected = null;

        AssertBlank(workspace);
        Assert.Equal("", workspace.Number);
        Assert.Equal("", workspace.ShortDescription);
    }

    [Fact]
    public async Task CreateNewResetsALoadedIncidentToTheBlankForm()
    {
        using var client = new SampleServiceNowClient();
        var workspace = await OpenIncidentsAsync(client);
        await workspace.OpenFromSearchAsync("inc-printer");

        Assert.Equal("INC0010001", workspace.Number);
        Assert.False(workspace.IsNew);
        Assert.False(workspace.ShowsSelectPrompt);

        workspace.NewRecordCommand.Execute(null);

        AssertBlank(workspace);
        Assert.Equal("", workspace.Number);
    }

    [Fact]
    public async Task DisconnectReturnsIncidentsToTheBlankForm()
    {
        using var client = new SampleServiceNowClient();
        var workspace = await OpenIncidentsAsync(client);
        await workspace.OpenFromSearchAsync("inc-printer");

        workspace.Detach();

        AssertBlank(workspace);
        Assert.False(workspace.IsReady);
    }

    [Fact]
    public void NoSelectionShowsTheNewWalkUpForm()
    {
        var workspace = new InteractionWorkspaceViewModel(new RecordingDesktopServices());

        AssertBlank(workspace);
        Assert.Equal("", workspace.Number);
        Assert.Equal("", workspace.ShortDescription);
        Assert.Equal("New", workspace.StateLabel);
        Assert.Equal("new", workspace.State);
    }

    [Fact]
    public async Task SelectingAWalkUpShowsThatRowAndClearingReturnsToTheBlankForm()
    {
        using var client = new SampleServiceNowClient();
        var workspace = new InteractionWorkspaceViewModel(new RecordingDesktopServices());
        workspace.Attach(client);
        await workspace.EnsureChoicesAsync();
        await workspace.RefreshAsync();
        AssertBlank(workspace);

        var row = Assert.Single(workspace.Items, item => item.SysId == "ims-password");
        workspace.Selected = row;
        await WaitUntilAsync(() => !workspace.IsNew && workspace.Number == row.Number && !workspace.IsEditorBusy);

        Assert.False(workspace.ShowsBlankTicketForm);
        Assert.False(workspace.ShowsSelectPrompt);
        Assert.True(workspace.HasEditor);
        Assert.Equal("IMS0010001", workspace.Number);
        Assert.Contains("locked out", workspace.Description, StringComparison.OrdinalIgnoreCase);

        workspace.NewRecordCommand.Execute(null);
        AssertBlank(workspace);
        Assert.Equal("", workspace.Number);

        workspace.Selected = row;
        await WaitUntilAsync(() => workspace.Number == row.Number && !workspace.IsEditorBusy);
        workspace.Selected = null;
        AssertBlank(workspace);
    }

    [Fact]
    public void RequestItemsAndRequestsKeepTheSelectPrompt()
    {
        var items = new RequestedItemWorkspaceViewModel(new RecordingDesktopServices());
        Assert.False(items.OpensOnBlankForm);
        Assert.False(items.HasEditor);
        Assert.False(items.IsNew);
        Assert.True(items.ShowsSelectPrompt);
        Assert.False(items.ShowsBlankTicketForm);

        var requests = new RequestWorkspaceViewModel(new RecordingDesktopServices());
        Assert.False(requests.OpensOnBlankForm);
        Assert.False(requests.HasEditor);
        Assert.True(requests.ShowsSelectPrompt);
        Assert.False(requests.ShowsBlankTicketForm);
    }

    [Fact]
    public void TicketListsUseTheSharedButtonGridAndDropTheEmptyBlurb()
    {
        var incidents = RepoFile("src/ServiceNowDesk.App/Views/IncidentView.xaml");
        Assert.DoesNotContain("Select an incident", incidents, StringComparison.Ordinal);
        Assert.Contains("ListActionBar", incidents, StringComparison.Ordinal);
        Assert.Contains("CreateText=\"Create new\"", incidents, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding NewRecordCommand}\"", incidents, StringComparison.Ordinal);
        Assert.DoesNotContain("Create new\" Style=\"{StaticResource ChipButton}\" Command=\"{Binding NewRecordCommand}\" HorizontalAlignment=\"Center\"", incidents, StringComparison.Ordinal);

        var walkUps = RepoFile("src/ServiceNowDesk.App/Views/InteractionView.xaml");
        Assert.DoesNotContain("Select a walk-up", walkUps, StringComparison.Ordinal);
        Assert.Contains("ListActionBar", walkUps, StringComparison.Ordinal);
        Assert.Contains("CreateText=\"Create new\"", walkUps, StringComparison.Ordinal);
        Assert.DoesNotContain("HorizontalAlignment=\"Center\" Margin=\"0,16,0,0\"", walkUps, StringComparison.Ordinal);

        var items = RepoFile("src/ServiceNowDesk.App/Views/RequestedItemView.xaml");
        Assert.Contains("Select a request item.", items, StringComparison.Ordinal);
        Assert.Contains("ListActionBar", items, StringComparison.Ordinal);
        Assert.Contains("CreateText=\"Create New\"", items, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding CreateNewCommand}\"", items, StringComparison.Ordinal);

        var requests = RepoFile("src/ServiceNowDesk.App/Views/RequestView.xaml");
        Assert.Contains("Select a request", requests, StringComparison.Ordinal);
        Assert.DoesNotContain("ListActionBar", requests, StringComparison.Ordinal);

        var grid = RepoFile("src/ServiceNowDesk.App/Controls/EqualButtonGrid.cs");
        Assert.Contains("MinCellWidth = 128", grid, StringComparison.Ordinal);
        Assert.Contains("MinCellHeight = 32", grid, StringComparison.Ordinal);
        Assert.Contains("Gap = 8", grid, StringComparison.Ordinal);
        Assert.Contains("_sharedWidth", grid, StringComparison.Ordinal);
    }

    private static void AssertBlank(RecordWorkspaceViewModel workspace)
    {
        Assert.True(workspace.OpensOnBlankForm);
        Assert.Null(workspace.Selected);
        Assert.True(workspace.IsNew);
        Assert.True(workspace.HasEditor);
        Assert.True(workspace.ShowsBlankTicketForm);
        Assert.False(workspace.ShowsSelectPrompt);
        Assert.False(workspace.IsDirty);
    }

    private static async Task<IncidentWorkspaceViewModel> OpenIncidentsAsync(IServiceNowClient client)
    {
        var workspace = new IncidentWorkspaceViewModel(new RecordingDesktopServices());
        workspace.Attach(client);
        await workspace.EnsureChoicesAsync();
        await workspace.RefreshAsync();
        return workspace;
    }

    private static async Task WaitUntilAsync(Func<bool> ready)
    {
        for (var attempt = 0; attempt < 40 && !ready(); attempt++)
            await Task.Delay(50);
    }

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ServiceNowDesk.sln")))
                return File.ReadAllText(Path.Combine(dir.FullName, relative));
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not find ServiceNowDesk.sln from " + AppContext.BaseDirectory);
    }
}
