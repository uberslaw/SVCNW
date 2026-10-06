using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;

namespace ServiceNowDesk.ViewModels;

public partial class RequestWorkspaceViewModel : RecordWorkspaceViewModel
{
    private RequestRecord? _loaded;
    private bool _choicesReady;

    public RequestWorkspaceViewModel(IDesktopServices desktop)
        : base(desktop, "sc_request", "request", true, PresetCatalog.Requests)
    {
        RequestedFor = new ReferenceFieldModel(SearchUsersAsync, match: MatchUsersAsync);
        RequestedFor.Changed += (_, _) => Touch();
        ResolveChoiceLabel = "Outcome";
    }

    public ReferenceFieldModel RequestedFor { get; }
    public ObservableCollection<Choice> StateChoices { get; } = [];
    public ObservableCollection<Choice> PriorityChoices { get; } = [];
    public ObservableCollection<TicketRow> RelatedItems { get; } = [];

    [ObservableProperty] private string requestState = "requested";
    [ObservableProperty] private string priority = "4";
    [ObservableProperty] private string specialInstructions = "";
    [ObservableProperty] private string dueDate = "";
    [ObservableProperty] private string approvalLabel = "";
    [ObservableProperty] private string stageLabel = "";

    public event EventHandler<string>? RelatedItemRequested;

    public override Task EnsureChoicesAsync() => LoadChoiceListsAsync();

    public async Task LoadChoiceListsAsync()
    {
        if (_choicesReady || Client is null)
            return;
        _choicesReady = true;
        await FillChoicesAsync(StateChoices, "sc_request", "request_state", DefaultChoices.RequestStates);
        await FillChoicesAsync(PriorityChoices, "sc_request", "priority", DefaultChoices.Priorities);
        await FillChoicesAsync(ResolveChoices, "sc_request", "request_state", DefaultChoices.RequestOutcomes);
        ResolveChoices.Clear();
        foreach (var choice in DefaultChoices.RequestOutcomes)
            ResolveChoices.Add(choice);
        if (StateChoices.Count > 0)
        {
            var outcomes = StateChoices.Where(choice => choice.Value.StartsWith("closed", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (outcomes.Length > 0)
            {
                ResolveChoices.Clear();
                foreach (var choice in outcomes)
                    ResolveChoices.Add(choice);
            }
        }
    }

    protected override Task ResolveReferencesAsync(CancellationToken cancellationToken) =>
        RequestedFor.AcceptExactUserAsync();

    protected override async Task<PagedResult<TicketRow>> FetchPageAsync(TicketQuery query, CancellationToken cancellationToken)
    {
        var page = await Client!.SearchRequestsAsync(query, cancellationToken);
        return new PagedResult<TicketRow>(page.Items.Select(TicketRow.FromRequest).ToArray(), page.TotalCount);
    }

    protected override async Task LoadRecordAsync(string sysId, CancellationToken cancellationToken)
    {
        var record = await Client!.GetRequestAsync(sysId, cancellationToken);
        Apply(record);
        UpsertRow(TicketRow.FromRequest(record));
        await LoadRelatedAsync(record.SysId);
    }

    protected override bool ComputeDirty()
    {
        if (!string.IsNullOrWhiteSpace(JournalText))
            return true;
        if (_loaded is null)
            return IsNew && HasNewInput();

        var record = _loaded;
        return ShortDescription != record.ShortDescription
            || Description != record.Description
            || SpecialInstructions != record.SpecialInstructions
            || RequestState != record.RequestState
            || Priority != record.Priority
            || DueDate != record.DueDate
            || RequestedFor.SysId != record.RequestedFor.SysId;
    }

    protected override void OnStartNew()
    {
        _loaded = null;
        ShortDescription = "";
        Description = "";
        SpecialInstructions = "";
        RequestState = "requested";
        Priority = "4";
        DueDate = "";
        ApprovalLabel = "";
        StageLabel = "";
        RequestedFor.Clear();
        RelatedItems.Clear();
    }

    protected override void Restore()
    {
        if (_loaded is null)
            OnStartNew();
        else
            Apply(_loaded);
    }

    protected override bool TryValidate(out string message)
    {
        if (string.IsNullOrWhiteSpace(ShortDescription))
        {
            message = "Enter a short description.";
            return false;
        }

        if (IsNew && string.IsNullOrEmpty(RequestedFor.SysId))
        {
            message = "Choose who the request is for.";
            return false;
        }

        if (!ReferenceIsChosen(RequestedFor))
        {
            message = "Choose a person from the list, or clear the field.";
            return false;
        }

        message = "";
        return true;
    }

    protected override async Task SaveNewAsync(CancellationToken cancellationToken)
    {
        var created = await Client!.CreateRequestAsync(new RequestChanges
        {
            ShortDescription = ShortDescription.Trim(),
            Description = Description.Trim(),
            SpecialInstructions = SpecialInstructions.Trim(),
            RequestedForId = RequestedFor.SysId,
            RequestState = RequestState,
            Priority = Priority,
            DueDate = FieldDiff.NullIfEmpty(DueDate)
        }, cancellationToken);
        Apply(created);
        UpsertRow(TicketRow.FromRequest(created));
        EditorSysId = created.SysId;
    }

    protected override async Task SaveExistingAsync(CancellationToken cancellationToken)
    {
        var changes = BuildChanges();
        if (!changes.HasChanges || string.IsNullOrEmpty(EditorSysId))
            return;

        var updated = await Client!.UpdateRequestAsync(EditorSysId, changes, cancellationToken);
        Apply(updated);
        UpsertRow(TicketRow.FromRequest(updated));
    }

    protected override async Task ResolveRecordAsync(CancellationToken cancellationToken)
    {
        var updated = await Client!.ResolveRequestAsync(EditorSysId!, ResolveCode, ResolveNotes.Trim(), cancellationToken);
        Apply(updated);
        UpsertRow(TicketRow.FromRequest(updated));
    }

    protected override void OnDetached()
    {
        _choicesReady = false;
        _loaded = null;
        RequestedFor.Clear();
        RelatedItems.Clear();
        StateChoices.Clear();
        PriorityChoices.Clear();
        ResolveChoices.Clear();
    }

    [RelayCommand]
    private void OpenRelated(string? sysId)
    {
        if (!string.IsNullOrWhiteSpace(sysId))
            RelatedItemRequested?.Invoke(this, sysId);
    }

    partial void OnRequestStateChanged(string value)
    {
        if (!Applying && value.StartsWith("closed", StringComparison.OrdinalIgnoreCase))
        {
            BeginResolve();
            Applying = true;
            RequestState = _loaded?.RequestState ?? "in_process";
            Applying = false;
            return;
        }

        if (!Applying)
            Touch();
    }

    partial void OnPriorityChanged(string value) => Touch();
    partial void OnSpecialInstructionsChanged(string value) => Touch();
    partial void OnDueDateChanged(string value) => Touch();

    private async Task LoadRelatedAsync(string requestSysId)
    {
        try
        {
            var page = await Client!.SearchRequestedItemsAsync(new TicketQuery
            {
                ParentRequestId = requestSysId,
                Activity = ActivityFilter.Any,
                Assignment = AssignmentScope.Any,
                Limit = 20
            }, CancellationToken.None);
            RelatedItems.Clear();
            foreach (var item in page.Items)
                RelatedItems.Add(TicketRow.FromItem(item));
        }
        catch (Exception ex)
        {
            ErrorMessage = WorkspaceMessages.Describe(ex);
        }
    }

    private void Apply(RequestRecord record)
    {
        _loaded = record;
        EnsureChoice(StateChoices, record.RequestState, record.RequestStateLabel);
        Number = record.Number;
        StateLabel = record.RequestStateLabel;
        PriorityLabel = record.PriorityLabel;
        OpenedAt = record.OpenedAtDisplay;
        ShortDescription = record.ShortDescription;
        Description = record.Description;
        SpecialInstructions = record.SpecialInstructions;
        RequestState = string.IsNullOrEmpty(record.RequestState) ? "requested" : record.RequestState;
        Priority = string.IsNullOrEmpty(record.Priority) ? "4" : record.Priority;
        DueDate = record.DueDate;
        ApprovalLabel = record.ApprovalLabel;
        StageLabel = record.StageLabel;
        RequestedFor.Set(record.RequestedFor.SysId, record.RequestedFor.Display);
        HasEditor = true;
    }

    private RequestChanges BuildChanges()
    {
        var record = _loaded ?? throw new InvalidOperationException("Open a request before saving.");
        return new RequestChanges
        {
            ShortDescription = FieldDiff.Changed(ShortDescription, record.ShortDescription),
            Description = FieldDiff.Changed(Description, record.Description),
            SpecialInstructions = FieldDiff.Changed(SpecialInstructions, record.SpecialInstructions),
            RequestState = FieldDiff.Changed(RequestState, record.RequestState),
            Priority = FieldDiff.Changed(Priority, record.Priority),
            DueDate = FieldDiff.Changed(DueDate, record.DueDate),
            RequestedForId = RequestedFor.SysId != record.RequestedFor.SysId ? RequestedFor.SysId : null
        };
    }

    private bool HasNewInput() =>
        !string.IsNullOrWhiteSpace(ShortDescription)
        || !string.IsNullOrWhiteSpace(Description)
        || !string.IsNullOrEmpty(RequestedFor.SysId)
        || !string.IsNullOrWhiteSpace(JournalText);

    private Task<IReadOnlyList<ReferenceSuggestion>> SearchUsersAsync(string text, CancellationToken cancellationToken) =>
        Client is null
            ? Task.FromResult<IReadOnlyList<ReferenceSuggestion>>([])
            : Client.SearchUsersAsync(text, cancellationToken);

    private Task<IReadOnlyList<ReferenceSuggestion>> MatchUsersAsync(string text, CancellationToken cancellationToken) =>
        Client is null
            ? Task.FromResult<IReadOnlyList<ReferenceSuggestion>>([])
            : Client.MatchUsersAsync(text, cancellationToken);
}
