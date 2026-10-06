using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;

namespace ServiceNowDesk.ViewModels;

public partial class InteractionWorkspaceViewModel : RecordWorkspaceViewModel
{
    private InteractionRecord? _loaded;
    private bool _choicesReady;

    public InteractionWorkspaceViewModel(IDesktopServices desktop, IRecentAssignmentGroupStore? recentGroups = null)
        : base(desktop, "interaction", "walk-up", true, PresetCatalog.WalkUps, DeskSection.WalkUps)
    {
        Assignment = new AssignmentFields(recentGroups);
        Caller = new ReferenceFieldModel(SearchUsersAsync, match: MatchUsersAsync);
        Caller.Changed += (_, _) => Touch();
        Assignment.Changed += (_, _) => Touch();
        PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(HasEditor) or nameof(IsEditorBusy) or nameof(IsReady))
                ConvertToIncidentCommand.NotifyCanExecuteChanged();
        };
        ResolveChoiceLabel = "Outcome";
    }

    public ReferenceFieldModel Caller { get; }
    public AssignmentFields Assignment { get; }
    public ObservableCollection<Choice> StateChoices { get; } = [];
    public ObservableCollection<Choice> TypeChoices { get; } = [];

    public event EventHandler<InteractionConversion>? IncidentRequested;

    [ObservableProperty] private string state = "new";
    [ObservableProperty] private string type = DefaultChoices.WalkUpType;

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
        await FillChoicesAsync(StateChoices, "interaction", "state", DefaultChoices.InteractionStates);
        await FillChoicesAsync(TypeChoices, "interaction", "type", DefaultChoices.InteractionTypes);
        EnsureChoice(TypeChoices, DefaultChoices.WalkUpType, "Walk-up");
        ResolveChoices.Clear();
        var outcomes = StateChoices.Where(choice => choice.Value.Contains("closed", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (outcomes.Length == 0)
            outcomes = DefaultChoices.InteractionOutcomes.ToArray();
        foreach (var choice in outcomes)
            ResolveChoices.Add(choice);
    }

    protected override Task ResolveReferencesAsync(CancellationToken cancellationToken) =>
        Caller.AcceptExactUserAsync();

    protected override async Task<PagedResult<TicketRow>> FetchPageAsync(TicketQuery query, CancellationToken cancellationToken)
    {
        var page = await Client!.SearchInteractionsAsync(query, cancellationToken);
        return new PagedResult<TicketRow>(page.Items.Select(TicketRow.FromInteraction).ToArray(), page.TotalCount);
    }

    protected override async Task LoadRecordAsync(string sysId, CancellationToken cancellationToken)
    {
        var record = await Client!.GetInteractionAsync(sysId, cancellationToken);
        Apply(record);
        await Assignment.ShowAsync(record.AssignmentGroup.SysId, record.AssignmentGroup.Display, record.AssignedTo.SysId, record.AssignedTo.Display);
        UpsertRow(TicketRow.FromInteraction(record));
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
            || State != record.State
            || Type != record.Type
            || Id(Caller.SysId) != Id(record.OpenedFor.SysId)
            || Id(Assignment.MemberId) != Id(record.AssignedTo.SysId)
            || Id(Assignment.GroupId) != Id(record.AssignmentGroup.SysId);
    }

    protected override void OnStartNew()
    {
        _loaded = null;
        ShortDescription = "";
        Description = "";
        State = "new";
        Type = DefaultChoices.WalkUpType;
        Caller.Clear();
        Assignment.ClearSelection();
    }

    protected override void Restore()
    {
        if (_loaded is null)
            OnStartNew();
        else
        {
            Apply(_loaded);
            _ = Assignment.ShowAsync(_loaded.AssignmentGroup.SysId, _loaded.AssignmentGroup.Display, _loaded.AssignedTo.SysId, _loaded.AssignedTo.Display);
        }
    }

    protected override bool TryValidate(out string message)
    {
        if (string.IsNullOrWhiteSpace(ShortDescription))
        {
            message = "Enter a short description.";
            return false;
        }

        if (IsNew && string.IsNullOrEmpty(Caller.SysId))
        {
            message = "Choose who this walk-up is for from the list.";
            return false;
        }

        if (!ReferenceIsChosen(Caller))
        {
            message = "Choose who this walk-up is for from the list, or clear the field.";
            return false;
        }

        message = "";
        return true;
    }

    protected override async Task SaveNewAsync(CancellationToken cancellationToken)
    {
        Type = DefaultChoices.WalkUpType;
        var created = await Client!.CreateInteractionAsync(new InteractionChanges
        {
            ShortDescription = ShortDescription.Trim(),
            Description = Description.Trim(),
            OpenedForId = Caller.SysId,
            AssignedToId = FieldDiff.NullIfEmpty(Assignment.MemberId),
            AssignmentGroupId = FieldDiff.NullIfEmpty(Assignment.GroupId),
            State = State,
            Type = DefaultChoices.WalkUpType
        }, cancellationToken);
        Apply(created);
        await Assignment.ShowAsync(created.AssignmentGroup.SysId, created.AssignmentGroup.Display, created.AssignedTo.SysId, created.AssignedTo.Display);
        UpsertRow(TicketRow.FromInteraction(created));
        EditorSysId = created.SysId;
    }

    protected override async Task SaveExistingAsync(CancellationToken cancellationToken)
    {
        var changes = BuildChanges();
        if (!changes.HasChanges || string.IsNullOrEmpty(EditorSysId))
            return;

        var updated = await Client!.UpdateInteractionAsync(EditorSysId, changes, cancellationToken);
        Apply(updated);
        UpsertRow(TicketRow.FromInteraction(updated));
    }

    protected override async Task ResolveRecordAsync(CancellationToken cancellationToken)
    {
        var updated = await Client!.UpdateInteractionAsync(EditorSysId!, new InteractionChanges
        {
            State = ResolveCode
        }, cancellationToken);
        await Client.AddJournalAsync(TableName, EditorSysId!, JournalKind.WorkNotes, ResolveNotes.Trim(), cancellationToken);
        Apply(updated);
        UpsertRow(TicketRow.FromInteraction(updated));
        var notes = await Client.GetJournalAsync(EditorSysId!, cancellationToken);
        Journal.Clear();
        foreach (var note in notes)
            Journal.Add(note);
    }

    protected override void OnDetached()
    {
        _choicesReady = false;
        _loaded = null;
        Caller.Clear();
        Assignment.Clear();
        StateChoices.Clear();
        TypeChoices.Clear();
        ResolveChoices.Clear();
    }

    [RelayCommand(CanExecute = nameof(CanConvert))]
    private async Task ConvertToIncidentAsync()
    {
        if (Client is null || IsEditorBusy)
            return;
        if (IsNew || string.IsNullOrEmpty(EditorSysId) || IsDirty)
        {
            EditorMessage = "Save the walk-up before creating an incident.";
            return;
        }

        try
        {
            IsEditorBusy = true;
            ErrorMessage = "";
            var conversion = await Client.ConvertInteractionToIncidentAsync(EditorSysId, CancellationToken.None);
            EditorMessage = conversion.Created
                ? "Created " + conversion.Incident.Number + " from " + Number + "."
                : "Opened " + conversion.Incident.Number + ", already linked to " + Number + ".";
            if (!string.IsNullOrWhiteSpace(conversion.LinkError))
                ErrorMessage = conversion.LinkError;
            IncidentRequested?.Invoke(this, conversion);
        }
        catch (Exception ex)
        {
            ErrorMessage = WorkspaceMessages.Describe(ex);
        }
        finally
        {
            IsEditorBusy = false;
        }
    }

    private bool CanConvert() => IsReady && HasEditor && !IsEditorBusy;

    partial void OnStateChanged(string value)
    {
        if (!Applying && IsClosedState(value))
        {
            if (ResolveChoices.Any(choice => choice.Value == value))
                ResolveCode = value;
            BeginResolve();
            Applying = true;
            State = _loaded?.State ?? "new";
            Applying = false;
            return;
        }

        if (!Applying)
            Touch();
    }

    partial void OnTypeChanged(string value)
    {
        if (!Applying)
            Touch();
    }

    private void Apply(InteractionRecord record)
    {
        _loaded = record;
        EnsureChoice(StateChoices, record.State, record.StateLabel);
        EnsureChoice(TypeChoices, record.Type, record.TypeLabel);
        Number = record.Number;
        StateLabel = record.StateLabel;
        OpenedAt = record.OpenedAtDisplay;
        ShortDescription = record.ShortDescription;
        Description = record.Description;
        State = string.IsNullOrEmpty(record.State) ? "new" : record.State;
        Type = string.IsNullOrEmpty(record.Type) ? DefaultChoices.WalkUpType : record.Type;
        Caller.Set(record.OpenedFor.SysId, record.OpenedFor.Display);
        HasEditor = true;
    }

    private InteractionChanges BuildChanges()
    {
        var record = _loaded ?? throw new InvalidOperationException("Open a walk-up before saving.");
        return new InteractionChanges
        {
            ShortDescription = FieldDiff.Changed(ShortDescription, record.ShortDescription),
            Description = FieldDiff.Changed(Description, record.Description),
            State = FieldDiff.Changed(State, record.State),
            Type = FieldDiff.Changed(Type, record.Type),
            OpenedForId = Caller.SysId != record.OpenedFor.SysId ? Caller.SysId : null,
            AssignedToId = !string.IsNullOrEmpty(Assignment.MemberId) && Assignment.MemberId != record.AssignedTo.SysId ? Assignment.MemberId : null,
            ClearAssignedTo = string.IsNullOrEmpty(Assignment.MemberId) && !record.AssignedTo.IsEmpty,
            AssignmentGroupId = !string.IsNullOrEmpty(Assignment.GroupId) && Assignment.GroupId != record.AssignmentGroup.SysId ? Assignment.GroupId : null,
            ClearAssignmentGroup = string.IsNullOrEmpty(Assignment.GroupId) && !record.AssignmentGroup.IsEmpty
        };
    }

    private bool HasNewInput() =>
        !string.IsNullOrWhiteSpace(ShortDescription)
        || !string.IsNullOrWhiteSpace(Description)
        || !string.IsNullOrEmpty(Caller.SysId)
        || !string.IsNullOrEmpty(Assignment.MemberId)
        || !string.IsNullOrEmpty(Assignment.GroupId)
        || !string.IsNullOrWhiteSpace(JournalText)
        || State != "new"
        || Type != DefaultChoices.WalkUpType;

    private static bool IsClosedState(string? value) =>
        value?.Contains("closed", StringComparison.OrdinalIgnoreCase) == true;

    private static string Id(string? value) => value ?? "";

    private Task<IReadOnlyList<ReferenceSuggestion>> SearchUsersAsync(string text, CancellationToken cancellationToken) =>
        Client is null
            ? Task.FromResult<IReadOnlyList<ReferenceSuggestion>>([])
            : Client.SearchUsersAsync(text, cancellationToken);

    private Task<IReadOnlyList<ReferenceSuggestion>> MatchUsersAsync(string text, CancellationToken cancellationToken) =>
        Client is null
            ? Task.FromResult<IReadOnlyList<ReferenceSuggestion>>([])
            : Client.MatchUsersAsync(text, cancellationToken);
}
