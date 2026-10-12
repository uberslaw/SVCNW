using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class SilentTicketSwitchTests
{
    [Fact]
    public async Task ViewingAnIncidentThenSelectingAnotherOpensItWithoutDiscard()
    {
        using var client = new SampleServiceNowClient();
        var workspace = await OpenIncidentsAsync(client);
        var first = Row(workspace, "inc-printer");
        var second = Row(workspace, "inc-vpn");

        await SelectAndWait(workspace, first);

        Assert.False(workspace.IsDirty);
        Assert.False(workspace.ShowUnsavedBanner);
        Assert.Equal("INC0010001", workspace.Number);

        await SelectAndWait(workspace, second);

        Assert.Equal("INC0010002", workspace.Number);
        Assert.Equal(second.SysId, workspace.Selected?.SysId);
        Assert.False(workspace.IsNew);
        Assert.False(workspace.IsDirty);
        Assert.False(workspace.ShowUnsavedBanner);
        Assert.Equal("VPN drops every few minutes", workspace.ShortDescription);
    }

    [Fact]
    public async Task UntouchedBlankIncidentFormDoesNotBlockSelectingATicket()
    {
        using var client = new SampleServiceNowClient();
        var workspace = await OpenIncidentsAsync(client);

        Assert.True(workspace.IsNew);
        Assert.Equal("New", workspace.StateLabel);
        Assert.Contains("Save the record", workspace.AttachmentNote);
        Assert.False(workspace.IsDirty);
        Assert.False(workspace.ShowUnsavedBanner);

        // Choice lists and the blank form's defaults are not a hand edit.
        await workspace.EnsureChoicesAsync();
        Assert.False(workspace.IsDirty);

        var row = Row(workspace, "inc-printer");
        await SelectAndWait(workspace, row);

        Assert.Equal("INC0010001", workspace.Number);
        Assert.Equal(row.SysId, workspace.Selected?.SysId);
        Assert.False(workspace.IsNew);
        Assert.False(workspace.ShowUnsavedBanner);
        Assert.False(workspace.IsDirty);
    }

    [Fact]
    public async Task ComboBindingClearOnTheBlankFormDoesNotBlockSelectingATicket()
    {
        using var client = new SampleServiceNowClient();
        var workspace = await OpenIncidentsAsync(client);
        await workspace.EnsureChoicesAsync();

        // WPF writes null when the current value is missing from ItemsSource.
        MimicComboClear(workspace.StateChoices, () => workspace.State, value => workspace.State = value);
        MimicComboClear(workspace.ImpactChoices, () => workspace.Impact, value => workspace.Impact = value);
        MimicComboClear(workspace.UrgencyChoices, () => workspace.Urgency, value => workspace.Urgency = value);
        MimicComboClear(workspace.ContactChoices, () => workspace.ContactType, value => workspace.ContactType = value);
        MimicComboClear(workspace.Assignment.Groups, () => workspace.Assignment.GroupId, value => workspace.Assignment.GroupId = value);
        MimicComboClear(workspace.Assignment.Members, () => workspace.Assignment.MemberId, value => workspace.Assignment.MemberId = value);

        Assert.Equal("1", workspace.State);
        Assert.Equal("3", workspace.Impact);
        Assert.Equal("3", workspace.Urgency);
        Assert.False(workspace.IsDirty);
        Assert.False(workspace.ShowUnsavedBanner);

        var row = Row(workspace, "inc-printer");
        await SelectAndWait(workspace, row);

        Assert.Equal(row.Number, workspace.Number);
        Assert.Equal(row.SysId, workspace.Selected?.SysId);
        Assert.False(workspace.ShowUnsavedBanner);
    }

    [Fact]
    public async Task ComboBindingClearAfterViewingAnIncidentDoesNotBlockTheNextTicket()
    {
        using var client = new SampleServiceNowClient();
        var workspace = await OpenIncidentsAsync(client);
        var first = Row(workspace, "inc-printer");
        var second = Row(workspace, "inc-vpn");
        await SelectAndWait(workspace, first);
        Assert.False(workspace.IsDirty);

        MimicComboClear(workspace.StateChoices, () => workspace.State, value => workspace.State = value);
        MimicComboClear(workspace.Assignment.Groups, () => workspace.Assignment.GroupId, value => workspace.Assignment.GroupId = value);
        MimicComboClear(workspace.Assignment.Members, () => workspace.Assignment.MemberId, value => workspace.Assignment.MemberId = value);

        Assert.Equal("2", workspace.State);
        Assert.False(workspace.IsDirty);

        await SelectAndWait(workspace, second);

        Assert.Equal(second.Number, workspace.Number);
        Assert.Equal(second.SysId, workspace.Selected?.SysId);
        Assert.False(workspace.ShowUnsavedBanner);
        Assert.False(workspace.IsDirty);
    }

    [Fact]
    public async Task AHandEditOnAnIncidentStillBlocksNavigation()
    {
        using var client = new SampleServiceNowClient();
        var workspace = await OpenIncidentsAsync(client);
        var first = Row(workspace, "inc-printer");
        var second = Row(workspace, "inc-vpn");
        await SelectAndWait(workspace, first);

        workspace.ShortDescription = "Changed by hand";

        Assert.True(workspace.IsDirty);
        workspace.Selected = second;

        Assert.True(workspace.ShowUnsavedBanner);
        Assert.Contains("Save or discard", workspace.EditorMessage);
        Assert.Equal(first.SysId, workspace.Selected?.SysId);
        Assert.Equal("Changed by hand", workspace.ShortDescription);
        Assert.Equal("INC0010001", workspace.Number);
    }

    [Fact]
    public async Task UntouchedBlankWalkUpDoesNotBlockSelectingAWalkUp()
    {
        using var client = new SampleServiceNowClient();
        var workspace = await OpenWalkUpsAsync(client);

        Assert.True(workspace.IsNew);
        Assert.False(workspace.IsDirty);

        MimicComboClear(workspace.StateChoices, () => workspace.State, value => workspace.State = value);
        MimicComboClear(workspace.TypeChoices, () => workspace.Type, value => workspace.Type = value);
        Assert.False(workspace.IsDirty);

        var row = Row(workspace, "ims-password");
        await SelectAndWait(workspace, row);

        Assert.Equal("IMS0010001", workspace.Number);
        Assert.Equal(row.SysId, workspace.Selected?.SysId);
        Assert.False(workspace.IsNew);
        Assert.False(workspace.ShowUnsavedBanner);
        Assert.False(workspace.IsDirty);
    }

    [Fact]
    public async Task ViewingAWalkUpThenSelectingAnotherOpensItWithoutDiscard()
    {
        using var client = new SampleServiceNowClient();
        var workspace = await OpenWalkUpsAsync(client);
        var first = Row(workspace, "ims-password");
        var second = OtherRow(workspace, first);
        await SelectAndWait(workspace, first);
        Assert.False(workspace.IsDirty);

        await SelectAndWait(workspace, second);

        Assert.Equal(second.Number, workspace.Number);
        Assert.Equal(second.SysId, workspace.Selected?.SysId);
        Assert.False(workspace.ShowUnsavedBanner);
        Assert.False(workspace.IsDirty);
    }

    [Fact]
    public async Task AHandEditOnAWalkUpStillBlocksNavigation()
    {
        using var client = new SampleServiceNowClient();
        var workspace = await OpenWalkUpsAsync(client);
        var first = Row(workspace, "ims-password");
        var second = OtherRow(workspace, first);
        await SelectAndWait(workspace, first);

        workspace.ShortDescription = "Changed by hand";
        workspace.Selected = second;

        Assert.True(workspace.IsDirty);
        Assert.True(workspace.ShowUnsavedBanner);
        Assert.Equal(first.SysId, workspace.Selected?.SysId);
        Assert.Equal("Changed by hand", workspace.ShortDescription);
    }

    [Fact]
    public async Task ViewingARequestThenSelectingAnotherOpensItWithoutDiscard()
    {
        using var client = new SampleServiceNowClient();
        var workspace = new RequestWorkspaceViewModel(new RecordingDesktopServices());
        workspace.Attach(client);
        await workspace.EnsureChoicesAsync();
        await workspace.RefreshAsync();
        var first = Row(workspace, "req-laptop");
        var second = OtherRow(workspace, first);

        await SelectAndWait(workspace, first);
        Assert.False(workspace.IsDirty);
        Assert.False(workspace.ShowUnsavedBanner);

        MimicComboClear(workspace.StateChoices, () => workspace.RequestState, value => workspace.RequestState = value);
        MimicComboClear(workspace.PriorityChoices, () => workspace.Priority, value => workspace.Priority = value);
        Assert.False(workspace.IsDirty);

        await SelectAndWait(workspace, second);

        Assert.Equal(second.Number, workspace.Number);
        Assert.Equal(second.SysId, workspace.Selected?.SysId);
        Assert.False(workspace.ShowUnsavedBanner);
        Assert.False(workspace.IsDirty);
    }

    [Fact]
    public async Task AHandEditOnARequestStillBlocksNavigation()
    {
        using var client = new SampleServiceNowClient();
        var workspace = new RequestWorkspaceViewModel(new RecordingDesktopServices());
        workspace.Attach(client);
        await workspace.EnsureChoicesAsync();
        await workspace.RefreshAsync();
        var first = Row(workspace, "req-laptop");
        var second = OtherRow(workspace, first);
        await SelectAndWait(workspace, first);

        workspace.SpecialInstructions = "Changed by hand";
        workspace.Selected = second;

        Assert.True(workspace.IsDirty);
        Assert.True(workspace.ShowUnsavedBanner);
        Assert.Equal(first.SysId, workspace.Selected?.SysId);
        Assert.Equal("Changed by hand", workspace.SpecialInstructions);
    }

    [Fact]
    public async Task ViewingARequestItemThenSelectingAnotherOpensItWithoutDiscard()
    {
        using var client = new SampleServiceNowClient();
        var workspace = new RequestedItemWorkspaceViewModel(new RecordingDesktopServices());
        workspace.Attach(client);
        await workspace.EnsureChoicesAsync();
        await workspace.RefreshAsync();
        var first = Row(workspace, "ritm-laptop");
        var second = OtherRow(workspace, first);

        await SelectAndWait(workspace, first);
        await workspace.Assignment.WhenReady;
        Assert.False(workspace.IsDirty);
        Assert.False(workspace.ShowUnsavedBanner);

        MimicComboClear(workspace.StateChoices, () => workspace.State, value => workspace.State = value);
        MimicComboClear(workspace.Assignment.Groups, () => workspace.Assignment.GroupId, value => workspace.Assignment.GroupId = value);
        MimicComboClear(workspace.Assignment.Members, () => workspace.Assignment.MemberId, value => workspace.Assignment.MemberId = value);
        Assert.False(workspace.IsDirty);

        await SelectAndWait(workspace, second);

        Assert.Equal(second.Number, workspace.Number);
        Assert.Equal(second.SysId, workspace.Selected?.SysId);
        Assert.False(workspace.ShowUnsavedBanner);
        Assert.False(workspace.IsDirty);
    }

    [Fact]
    public async Task AHandEditOnARequestItemStillBlocksNavigation()
    {
        using var client = new SampleServiceNowClient();
        var workspace = new RequestedItemWorkspaceViewModel(new RecordingDesktopServices());
        workspace.Attach(client);
        await workspace.EnsureChoicesAsync();
        await workspace.RefreshAsync();
        var first = Row(workspace, "ritm-laptop");
        var second = OtherRow(workspace, first);
        await SelectAndWait(workspace, first);

        workspace.ShortDescription = "Changed by hand";
        workspace.Selected = second;

        Assert.True(workspace.IsDirty);
        Assert.True(workspace.ShowUnsavedBanner);
        Assert.Equal(first.SysId, workspace.Selected?.SysId);
        Assert.Equal("Changed by hand", workspace.ShortDescription);
    }

    private static void MimicComboClear(System.Collections.ObjectModel.ObservableCollection<Choice> choices, Func<string> read, Action<string> write)
    {
        var held = read();
        choices.Clear();
        write("");
        Assert.Equal(held, read());
    }

    private static TicketRow Row(RecordWorkspaceViewModel workspace, string sysId) =>
        Assert.Single(workspace.Items, item => item.SysId == sysId);

    private static TicketRow OtherRow(RecordWorkspaceViewModel workspace, TicketRow current)
    {
        var other = workspace.Items.FirstOrDefault(item => item.SysId != current.SysId);
        Assert.NotNull(other);
        return other;
    }

    private static async Task SelectAndWait(RecordWorkspaceViewModel workspace, TicketRow row)
    {
        workspace.Selected = row;
        await WaitUntilAsync(() => !workspace.IsEditorBusy && !workspace.IsNew && workspace.Number == row.Number);
        Assert.Equal(row.Number, workspace.Number);
    }

    private static async Task<IncidentWorkspaceViewModel> OpenIncidentsAsync(IServiceNowClient client)
    {
        var workspace = new IncidentWorkspaceViewModel(new RecordingDesktopServices());
        workspace.Attach(client);
        await workspace.EnsureChoicesAsync();
        await workspace.RefreshAsync();
        return workspace;
    }

    private static async Task<InteractionWorkspaceViewModel> OpenWalkUpsAsync(IServiceNowClient client)
    {
        var workspace = new InteractionWorkspaceViewModel(new RecordingDesktopServices());
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
}
