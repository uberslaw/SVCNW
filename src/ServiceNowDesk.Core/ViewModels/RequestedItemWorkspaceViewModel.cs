using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;

namespace ServiceNowDesk.ViewModels;

public partial class RequestedItemWorkspaceViewModel : RecordWorkspaceViewModel
{
    private RequestedItemRecord? _loaded;
    private bool _choicesReady;

    public RequestedItemWorkspaceViewModel(IDesktopServices desktop, IRecentAssignmentGroupStore? recentGroups = null)
        : base(desktop, "sc_req_item", "request item", false, PresetCatalog.RequestedItems, attachments: true)
    {
        Assignment = new AssignmentFields(recentGroups);
        Assignment.Changed += (_, _) => Touch();
        ResolveChoiceLabel = "Outcome";
    }

    public AssignmentFields Assignment { get; }
    public ObservableCollection<Choice> StateChoices { get; } = [];
    public ObservableCollection<Choice> PriorityChoices { get; } = [];

    [ObservableProperty] private string state = "1";
    [ObservableProperty] private string priority = "";
    [ObservableProperty] private string closeNotes = "";
    [ObservableProperty] private string stageLabel = "";
    [ObservableProperty] private string quantity = "";
    [ObservableProperty] private string requestNumber = "";
    [ObservableProperty] private string catalogItem = "";

    public override async Task EnsureChoicesAsync()
    {
        await LoadChoiceListsAsync();
        if (Client is null || Assignment.GroupsLoaded)
            return;
        Assignment.Use(Client);
        await Assignment.LoadGroupsAsync();
    }

    public Task ReloadChoiceListsAsync()
    {
        _choicesReady = false;
        return LoadChoiceListsAsync();
    }

    public async Task LoadChoiceListsAsync()
    {
        if (_choicesReady || Client is null)
            return;
        _choicesReady = true;
        await FillChoicesAsync(StateChoices, "sc_req_item", "state", DefaultChoices.ItemStates);
        await FillChoicesAsync(PriorityChoices, "sc_req_item", "priority", DefaultChoices.Priorities, includeBlank: true, blankLabel: "Unchanged");
        ResolveChoices.Clear();
        foreach (var choice in DefaultChoices.ItemOutcomes)
            ResolveChoices.Add(choice);
    }

    protected override async Task<PagedResult<TicketRow>> FetchPageAsync(TicketQuery query, CancellationToken cancellationToken)
    {
        var page = await Client!.SearchRequestedItemsAsync(query, cancellationToken);
        return new PagedResult<TicketRow>(page.Items.Select(TicketRow.FromItem).ToArray(), page.TotalCount);
    }

    protected override async Task LoadRecordAsync(string sysId, CancellationToken cancellationToken)
    {
        var record = await Client!.GetRequestedItemAsync(sysId, cancellationToken);
        Apply(record);
        await Assignment.ShowAsync(record.AssignmentGroup.SysId, record.AssignmentGroup.Display, record.AssignedTo.SysId, record.AssignedTo.Display);
        UpsertRow(TicketRow.FromItem(record));
    }

    protected override bool ComputeDirty()
    {
        if (!string.IsNullOrWhiteSpace(JournalText))
            return true;
        if (_loaded is null)
            return false;

        var record = _loaded;
        return ShortDescription != record.ShortDescription
            || Description != record.Description
            || State != record.State
            || Priority != record.Priority
            || CloseNotes != record.CloseNotes
            || Assignment.MemberId != record.AssignedTo.SysId
            || Assignment.GroupId != record.AssignmentGroup.SysId;
    }

    protected override void OnStartNew()
    {
    }

    protected override void Restore()
    {
        if (_loaded is null)
            return;
        Apply(_loaded);
        _ = Assignment.ShowAsync(_loaded.AssignmentGroup.SysId, _loaded.AssignmentGroup.Display, _loaded.AssignedTo.SysId, _loaded.AssignedTo.Display);
    }

    protected override bool TryValidate(out string message)
    {
        if (string.IsNullOrWhiteSpace(ShortDescription))
        {
            message = "Enter a short description.";
            return false;
        }

        message = "";
        return true;
    }

    protected override Task SaveNewAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Request items are created from the catalog.");

    protected override async Task SaveExistingAsync(CancellationToken cancellationToken)
    {
        var record = _loaded ?? throw new InvalidOperationException("Open a request item before saving.");
        var changes = new RequestedItemChanges
        {
            ShortDescription = FieldDiff.Changed(ShortDescription, record.ShortDescription),
            Description = FieldDiff.Changed(Description, record.Description),
            State = FieldDiff.Changed(State, record.State),
            Priority = FieldDiff.Changed(Priority, record.Priority),
            CloseNotes = FieldDiff.Changed(CloseNotes, record.CloseNotes),
            AssignedToId = !string.IsNullOrEmpty(Assignment.MemberId) && Assignment.MemberId != record.AssignedTo.SysId ? Assignment.MemberId : null,
            ClearAssignedTo = string.IsNullOrEmpty(Assignment.MemberId) && !record.AssignedTo.IsEmpty,
            AssignmentGroupId = !string.IsNullOrEmpty(Assignment.GroupId) && Assignment.GroupId != record.AssignmentGroup.SysId ? Assignment.GroupId : null,
            ClearAssignmentGroup = string.IsNullOrEmpty(Assignment.GroupId) && !record.AssignmentGroup.IsEmpty
        };
        if (!changes.HasChanges || string.IsNullOrEmpty(EditorSysId))
            return;

        var updated = await Client!.UpdateRequestedItemAsync(EditorSysId, changes, cancellationToken);
        Apply(updated);
        UpsertRow(TicketRow.FromItem(updated));
    }

    protected override async Task ResolveRecordAsync(CancellationToken cancellationToken)
    {
        var updated = await Client!.ResolveRequestedItemAsync(EditorSysId!, ResolveCode, ResolveNotes.Trim(), cancellationToken);
        Apply(updated);
        UpsertRow(TicketRow.FromItem(updated));
    }

    protected override void OnDetached()
    {
        _choicesReady = false;
        _loaded = null;
        Assignment.Clear();
        StateChoices.Clear();
        PriorityChoices.Clear();
        ResolveChoices.Clear();
    }

    partial void OnStateChanged(string value)
    {
        if (!Applying && value is "3" or "4" or "7")
        {
            BeginResolve();
            Applying = true;
            State = _loaded?.State ?? "2";
            Applying = false;
            return;
        }

        if (!Applying)
            Touch();
    }

    partial void OnPriorityChanged(string value) => Touch();
    partial void OnCloseNotesChanged(string value) => Touch();

    private void Apply(RequestedItemRecord record)
    {
        _loaded = record;
        EnsureChoice(StateChoices, record.State, record.StateLabel);
        Number = record.Number;
        StateLabel = record.StateLabel;
        PriorityLabel = record.PriorityLabel;
        OpenedAt = record.OpenedAtDisplay;
        ShortDescription = record.ShortDescription;
        Description = record.Description;
        State = string.IsNullOrEmpty(record.State) ? "1" : record.State;
        Priority = record.Priority;
        CloseNotes = record.CloseNotes;
        StageLabel = record.StageLabel;
        Quantity = record.Quantity;
        RequestNumber = record.Request.Display;
        CatalogItem = record.CatalogItem.Display;
        HasEditor = true;
    }
}
