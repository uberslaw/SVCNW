using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;

namespace ServiceNowDesk.ViewModels;

public partial class IncidentWorkspaceViewModel : RecordWorkspaceViewModel
{
    private IncidentRecord? _loaded;
    private bool _choicesReady;

    public IncidentWorkspaceViewModel(IDesktopServices desktop)
        : base(desktop, "incident", "incident", true, PresetCatalog.Incidents)
    {
        Caller = new ReferenceFieldModel(SearchUsersAsync);
        Assignee = new ReferenceFieldModel(SearchUsersAsync);
        Group = new ReferenceFieldModel(SearchGroupsAsync);
        Caller.Changed += (_, _) => Touch();
        Assignee.Changed += (_, _) => Touch();
        Group.Changed += (_, _) => Touch();
        ResolveChoiceLabel = "Close code";
    }

    public ReferenceFieldModel Caller { get; }
    public ReferenceFieldModel Assignee { get; }
    public ReferenceFieldModel Group { get; }
    public ObservableCollection<Choice> StateChoices { get; } = [];
    public ObservableCollection<Choice> ImpactChoices { get; } = [];
    public ObservableCollection<Choice> UrgencyChoices { get; } = [];
    public ObservableCollection<Choice> PriorityChoices { get; } = [];
    public ObservableCollection<Choice> CategoryChoices { get; } = [];
    public ObservableCollection<Choice> SubcategoryChoices { get; } = [];
    public ObservableCollection<Choice> ContactChoices { get; } = [];
    public ObservableCollection<Choice> HoldReasonChoices { get; } = [];

    [ObservableProperty] private string state = "1";
    [ObservableProperty] private string impact = "3";
    [ObservableProperty] private string urgency = "3";
    [ObservableProperty] private string priority = "";
    [ObservableProperty] private string category = "";
    [ObservableProperty] private string subcategory = "";
    [ObservableProperty] private string subcategoryLabel = "";
    [ObservableProperty] private string contactType = "phone";
    [ObservableProperty] private string holdReason = "";

    public bool ShowHoldReason => State == "3";

    public override async Task EnsureChoicesAsync()
    {
        if (_choicesReady || Client is null)
            return;
        _choicesReady = true;
        await FillChoicesAsync(StateChoices, "incident", "state", DefaultChoices.IncidentStates);
        await FillChoicesAsync(ImpactChoices, "incident", "impact", DefaultChoices.Impacts);
        await FillChoicesAsync(UrgencyChoices, "incident", "urgency", DefaultChoices.Urgencies);
        await FillChoicesAsync(PriorityChoices, "incident", "priority", DefaultChoices.Priorities, includeBlank: true, blankLabel: "Let ServiceNow set this");
        await FillChoicesAsync(CategoryChoices, "incident", "category", DefaultChoices.Categories, includeBlank: true, blankLabel: "None");
        await FillChoicesAsync(ContactChoices, "incident", "contact_type", DefaultChoices.ContactTypes);
        await FillChoicesAsync(HoldReasonChoices, "incident", "hold_reason", DefaultChoices.HoldReasons, includeBlank: true, blankLabel: "None");
        await FillChoicesAsync(ResolveChoices, "incident", "close_code", DefaultChoices.CloseCodes);
    }

    protected override async Task<PagedResult<TicketRow>> FetchPageAsync(TicketQuery query, CancellationToken cancellationToken)
    {
        var page = await Client!.SearchIncidentsAsync(query, cancellationToken);
        return new PagedResult<TicketRow>(page.Items.Select(TicketRow.FromIncident).ToArray(), page.TotalCount);
    }

    protected override async Task LoadRecordAsync(string sysId, CancellationToken cancellationToken)
    {
        var record = await Client!.GetIncidentAsync(sysId, cancellationToken);
        Apply(record);
        UpsertRow(TicketRow.FromIncident(record));
        await LoadSubcategoriesAsync(record.Category, record.Subcategory, record.SubcategoryLabel);
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
            || Impact != record.Impact
            || Urgency != record.Urgency
            || Priority != record.Priority
            || Category != record.Category
            || Subcategory != record.Subcategory
            || ContactType != record.ContactType
            || HoldReason != record.HoldReason
            || Caller.SysId != record.Caller.SysId
            || Assignee.SysId != record.AssignedTo.SysId
            || Group.SysId != record.AssignmentGroup.SysId;
    }

    protected override void OnStartNew()
    {
        _loaded = null;
        ShortDescription = "";
        Description = "";
        State = "1";
        Impact = "3";
        Urgency = "3";
        Priority = "";
        Category = "";
        Subcategory = "";
        ContactType = "phone";
        HoldReason = "";
        Caller.Clear();
        Assignee.Clear();
        Group.Clear();
        SubcategoryChoices.Clear();
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

        if (IsNew && Caller.SysId.Length == 0)
        {
            message = "Choose the caller from the list.";
            return false;
        }

        if (!ReferenceIsChosen(Caller) || !ReferenceIsChosen(Assignee) || !ReferenceIsChosen(Group))
        {
            message = "Choose a name from the list, or clear the field.";
            return false;
        }

        message = "";
        return true;
    }

    protected override async Task SaveNewAsync(CancellationToken cancellationToken)
    {
        var created = await Client!.CreateIncidentAsync(new IncidentChanges
        {
            ShortDescription = ShortDescription.Trim(),
            Description = Description.Trim(),
            CallerId = Caller.SysId,
            AssignedToId = FieldDiff.NullIfEmpty(Assignee.SysId),
            AssignmentGroupId = FieldDiff.NullIfEmpty(Group.SysId),
            State = State,
            Impact = Impact,
            Urgency = Urgency,
            Priority = FieldDiff.NullIfEmpty(Priority),
            Category = FieldDiff.NullIfEmpty(Category),
            Subcategory = FieldDiff.NullIfEmpty(Subcategory),
            ContactType = ContactType,
            HoldReason = State == "3" ? FieldDiff.NullIfEmpty(HoldReason) : null
        }, cancellationToken);
        Apply(created);
        UpsertRow(TicketRow.FromIncident(created));
        EditorSysId = created.SysId;
    }

    protected override async Task SaveExistingAsync(CancellationToken cancellationToken)
    {
        var changes = BuildChanges();
        if (!changes.HasChanges || string.IsNullOrEmpty(EditorSysId))
            return;

        var updated = await Client!.UpdateIncidentAsync(EditorSysId, changes, cancellationToken);
        Apply(updated);
        UpsertRow(TicketRow.FromIncident(updated));
    }

    protected override async Task ResolveRecordAsync(CancellationToken cancellationToken)
    {
        var state = StateChoices.FirstOrDefault(choice => choice.Label.Equals("Resolved", StringComparison.OrdinalIgnoreCase))?.Value ?? "6";
        var updated = await Client!.ResolveIncidentAsync(EditorSysId!, ResolveCode, ResolveNotes.Trim(), state, cancellationToken);
        Apply(updated);
        UpsertRow(TicketRow.FromIncident(updated));
    }

    protected override void OnDetached()
    {
        _choicesReady = false;
        _loaded = null;
        Caller.Clear();
        Assignee.Clear();
        Group.Clear();
        StateChoices.Clear();
        ImpactChoices.Clear();
        UrgencyChoices.Clear();
        PriorityChoices.Clear();
        CategoryChoices.Clear();
        SubcategoryChoices.Clear();
        ContactChoices.Clear();
        HoldReasonChoices.Clear();
        ResolveChoices.Clear();
    }

    partial void OnStateChanged(string value)
    {
        if (!Applying && value is "6" or "7")
        {
            BeginResolve();
            Applying = true;
            State = _loaded?.State ?? "2";
            Applying = false;
            return;
        }

        if (!Applying)
            Touch();
        OnPropertyChanged(nameof(ShowHoldReason));
    }

    partial void OnImpactChanged(string value) => Touch();
    partial void OnUrgencyChanged(string value) => Touch();
    partial void OnPriorityChanged(string value) => Touch();
    partial void OnContactTypeChanged(string value) => Touch();
    partial void OnHoldReasonChanged(string value) => Touch();
    partial void OnSubcategoryChanged(string value) => Touch();

    partial void OnCategoryChanged(string value)
    {
        if (!Applying)
            Touch();
        if (!Applying)
            _ = LoadSubcategoriesAsync(value, "", "");
    }

    private async Task LoadSubcategoriesAsync(string category, string selectedValue, string selectedLabel)
    {
        await FillChoicesAsync(SubcategoryChoices, "incident", "subcategory", [], category, includeBlank: true, blankLabel: "None");
        EnsureChoice(SubcategoryChoices, selectedValue, selectedLabel);
        Subcategory = selectedValue;
    }

    private void Apply(IncidentRecord record)
    {
        _loaded = record;
        EnsureChoice(StateChoices, record.State, record.StateLabel);
        EnsureChoice(CategoryChoices, record.Category, record.CategoryLabel);
        EnsureChoice(SubcategoryChoices, record.Subcategory, record.SubcategoryLabel);
        Number = record.Number;
        StateLabel = record.StateLabel;
        PriorityLabel = record.PriorityLabel;
        OpenedAt = record.OpenedAtDisplay;
        ShortDescription = record.ShortDescription;
        Description = record.Description;
        State = string.IsNullOrEmpty(record.State) ? "1" : record.State;
        Impact = string.IsNullOrEmpty(record.Impact) ? "3" : record.Impact;
        Urgency = string.IsNullOrEmpty(record.Urgency) ? "3" : record.Urgency;
        Priority = record.Priority;
        Category = record.Category;
        SubcategoryLabel = record.SubcategoryLabel;
        Subcategory = record.Subcategory;
        ContactType = string.IsNullOrEmpty(record.ContactType) ? "phone" : record.ContactType;
        HoldReason = record.HoldReason;
        Caller.Set(record.Caller.SysId, record.Caller.Display);
        Assignee.Set(record.AssignedTo.SysId, record.AssignedTo.Display);
        Group.Set(record.AssignmentGroup.SysId, record.AssignmentGroup.Display);
        HasEditor = true;
    }

    private IncidentChanges BuildChanges()
    {
        var record = _loaded ?? throw new InvalidOperationException("Open an incident before saving.");
        return new IncidentChanges
        {
            ShortDescription = FieldDiff.Changed(ShortDescription, record.ShortDescription),
            Description = FieldDiff.Changed(Description, record.Description),
            State = FieldDiff.Changed(State, record.State),
            Impact = FieldDiff.Changed(Impact, record.Impact),
            Urgency = FieldDiff.Changed(Urgency, record.Urgency),
            Priority = FieldDiff.Changed(Priority, record.Priority),
            Category = FieldDiff.Changed(Category, record.Category),
            Subcategory = FieldDiff.Changed(Subcategory, record.Subcategory),
            ContactType = FieldDiff.Changed(ContactType, record.ContactType),
            HoldReason = FieldDiff.Changed(HoldReason, record.HoldReason),
            CallerId = Caller.SysId != record.Caller.SysId ? Caller.SysId : null,
            AssignedToId = Assignee.SysId.Length > 0 && Assignee.SysId != record.AssignedTo.SysId ? Assignee.SysId : null,
            ClearAssignedTo = Assignee.SysId.Length == 0 && !record.AssignedTo.IsEmpty,
            AssignmentGroupId = Group.SysId.Length > 0 && Group.SysId != record.AssignmentGroup.SysId ? Group.SysId : null,
            ClearAssignmentGroup = Group.SysId.Length == 0 && !record.AssignmentGroup.IsEmpty
        };
    }

    private bool HasNewInput() =>
        !string.IsNullOrWhiteSpace(ShortDescription)
        || !string.IsNullOrWhiteSpace(Description)
        || Caller.SysId.Length > 0
        || Assignee.SysId.Length > 0
        || Group.SysId.Length > 0
        || !string.IsNullOrWhiteSpace(JournalText);

    private Task<IReadOnlyList<ReferenceSuggestion>> SearchUsersAsync(string text, CancellationToken cancellationToken) =>
        Client is null ? Empty() : Client.SearchUsersAsync(text, cancellationToken);

    private Task<IReadOnlyList<ReferenceSuggestion>> SearchGroupsAsync(string text, CancellationToken cancellationToken) =>
        Client is null ? Empty() : Client.SearchGroupsAsync(text, cancellationToken);

    private static Task<IReadOnlyList<ReferenceSuggestion>> Empty() =>
        Task.FromResult<IReadOnlyList<ReferenceSuggestion>>([]);
}
