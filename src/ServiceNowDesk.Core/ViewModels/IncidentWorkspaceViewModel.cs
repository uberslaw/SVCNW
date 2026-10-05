using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;

namespace ServiceNowDesk.ViewModels;

public partial class IncidentWorkspaceViewModel : RecordWorkspaceViewModel
{
    private readonly IIncidentTemplateStore _templates;
    private IncidentRecord? _loaded;
    private bool _choicesReady;

    public IncidentWorkspaceViewModel(IDesktopServices desktop, IIncidentTemplateStore? templates = null)
        : base(desktop, "incident", "incident", true, PresetCatalog.Incidents)
    {
        _templates = templates ?? new MemoryIncidentTemplateStore();
        Caller = new ReferenceFieldModel(SearchUsersAsync);
        Caller.Changed += (_, _) => Touch();
        Assignment.Changed += (_, _) => Touch();
        Templates.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTemplates));
        ReloadTemplates();
        ResolveChoiceLabel = "Close code";
    }

    public ReferenceFieldModel Caller { get; }
    public AssignmentFields Assignment { get; } = new();
    public ObservableCollection<IncidentTemplate> Templates { get; } = [];
    public bool HasTemplates => Templates.Count > 0;

    [ObservableProperty] private string templateName = "";
    [ObservableProperty] private string templateMessage = "";
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
        await LoadChoiceListsAsync();
        if (Client is null || Assignment.GroupsLoaded)
            return;
        Assignment.Use(Client);
        await Assignment.LoadGroupsAsync();
    }

    public async Task LoadChoiceListsAsync()
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

    protected override Task ResolveReferencesAsync(CancellationToken cancellationToken) =>
        Caller.AcceptExactUserAsync();

    protected override async Task<PagedResult<TicketRow>> FetchPageAsync(TicketQuery query, CancellationToken cancellationToken)
    {
        var page = await Client!.SearchIncidentsAsync(query, cancellationToken);
        return new PagedResult<TicketRow>(page.Items.Select(TicketRow.FromIncident).ToArray(), page.TotalCount);
    }

    protected override async Task LoadRecordAsync(string sysId, CancellationToken cancellationToken)
    {
        var record = await Client!.GetIncidentAsync(sysId, cancellationToken);
        Apply(record);
        await Assignment.ShowAsync(record.AssignmentGroup.SysId, record.AssignmentGroup.Display, record.AssignedTo.SysId, record.AssignedTo.Display);
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
            || Assignment.MemberId != record.AssignedTo.SysId
            || Assignment.GroupId != record.AssignmentGroup.SysId;
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
        ContactType = "phone";
        HoldReason = "";
        Caller.Clear();
        Assignment.ClearSelection();
        SubcategoryChoices.Clear();
        SubcategoryChoices.Add(new Choice("", "None"));
        Subcategory = "";
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
            message = "Choose the caller from the list.";
            return false;
        }

        if (!ReferenceIsChosen(Caller))
        {
            message = "Choose the caller from the list, or clear the field.";
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
            AssignedToId = FieldDiff.NullIfEmpty(Assignment.MemberId),
            AssignmentGroupId = FieldDiff.NullIfEmpty(Assignment.GroupId),
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
        Assignment.Clear();
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
            AssignedToId = !string.IsNullOrEmpty(Assignment.MemberId) && Assignment.MemberId != record.AssignedTo.SysId ? Assignment.MemberId : null,
            ClearAssignedTo = string.IsNullOrEmpty(Assignment.MemberId) && !record.AssignedTo.IsEmpty,
            AssignmentGroupId = !string.IsNullOrEmpty(Assignment.GroupId) && Assignment.GroupId != record.AssignmentGroup.SysId ? Assignment.GroupId : null,
            ClearAssignmentGroup = string.IsNullOrEmpty(Assignment.GroupId) && !record.AssignmentGroup.IsEmpty
        };
    }

    [RelayCommand]
    private async Task ApplyTemplateAsync(IncidentTemplate? template)
    {
        if (template is null || !AllowCreate || !IsReady || Client is null)
            return;
        if (IsDirty)
        {
            ShowUnsavedBanner = true;
            EditorMessage = "Save or discard unsaved changes first.";
            return;
        }

        BeginNew();
        Applying = true;
        try
        {
            ShortDescription = template.ShortDescription ?? "";
            Description = template.Description ?? "";
            State = string.IsNullOrEmpty(template.State) ? "1" : template.State;
            Impact = string.IsNullOrEmpty(template.Impact) ? "3" : template.Impact;
            Urgency = string.IsNullOrEmpty(template.Urgency) ? "3" : template.Urgency;
            Priority = template.Priority ?? "";
            Category = template.Category ?? "";
            ContactType = string.IsNullOrEmpty(template.ContactType) ? "phone" : template.ContactType;
            HoldReason = template.HoldReason ?? "";
            Caller.Set(template.CallerId, template.CallerDisplay);
            await Assignment.ShowAsync(
                template.AssignmentGroupId,
                template.AssignmentGroupDisplay,
                template.AssignedToId,
                template.AssignedToDisplay);
            await LoadSubcategoriesAsync(template.Category ?? "", template.Subcategory ?? "", template.SubcategoryLabel ?? "");
            SubcategoryLabel = template.SubcategoryLabel ?? "";
            EditorMessage = "New incident from " + template.Name + ". Nothing is sent until you save.";
            TemplateMessage = "";
        }
        finally
        {
            Applying = false;
            Touch();
        }
    }

    [RelayCommand]
    private void SaveTemplate()
    {
        var name = TemplateName.Trim();
        if (name.Length == 0)
        {
            TemplateMessage = "Enter a template name.";
            return;
        }

        if (!HasEditor)
        {
            TemplateMessage = "Open or start an incident before saving a template.";
            return;
        }

        try
        {
            var replaced = _templates.List().Any(template => template.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var template = CaptureTemplate();
            template.Name = name;
            _templates.Save(template);
            ReloadTemplates();
            TemplateName = "";
            TemplateMessage = replaced
                ? "Replaced template " + name + "."
                : "Saved template " + name + ".";
        }
        catch (Exception ex)
        {
            TemplateMessage = WorkspaceMessages.Describe(ex);
        }
    }

    [RelayCommand]
    private void DeleteTemplate(IncidentTemplate? template)
    {
        if (template is null || string.IsNullOrWhiteSpace(template.Name))
            return;

        try
        {
            _templates.Delete(template.Name);
            ReloadTemplates();
            TemplateMessage = "Deleted template " + template.Name + ".";
        }
        catch (Exception ex)
        {
            TemplateMessage = WorkspaceMessages.Describe(ex);
        }
    }

    private void ReloadTemplates()
    {
        Templates.Clear();
        foreach (var template in _templates.List())
            Templates.Add(template);
    }

    private IncidentTemplate CaptureTemplate()
    {
        var groupLabel = string.IsNullOrEmpty(Assignment.GroupId)
            ? ""
            : Assignment.Groups.FirstOrDefault(choice => choice.Value == Assignment.GroupId)?.Label ?? "";
        var memberLabel = string.IsNullOrEmpty(Assignment.MemberId)
            ? ""
            : Assignment.Members.FirstOrDefault(choice => choice.Value == Assignment.MemberId)?.Label ?? "";
        var subcategoryLabel = string.IsNullOrEmpty(Subcategory)
            ? ""
            : SubcategoryChoices.FirstOrDefault(choice => choice.Value == Subcategory)?.Label ?? SubcategoryLabel;
        return new IncidentTemplate
        {
            ShortDescription = ShortDescription ?? "",
            Description = Description ?? "",
            State = State ?? "",
            Impact = Impact ?? "",
            Urgency = Urgency ?? "",
            Priority = Priority ?? "",
            Category = Category ?? "",
            Subcategory = Subcategory ?? "",
            SubcategoryLabel = subcategoryLabel,
            ContactType = ContactType ?? "",
            HoldReason = HoldReason ?? "",
            AssignmentGroupId = Assignment.GroupId,
            AssignmentGroupDisplay = groupLabel,
            AssignedToId = Assignment.MemberId,
            AssignedToDisplay = memberLabel,
            CallerId = Caller.SysId,
            CallerDisplay = string.IsNullOrEmpty(Caller.SysId) ? "" : Caller.Text
        };
    }

    private bool HasNewInput() =>
        !string.IsNullOrWhiteSpace(ShortDescription)
        || !string.IsNullOrWhiteSpace(Description)
        || !string.IsNullOrEmpty(Caller.SysId)
        || !string.IsNullOrEmpty(Assignment.MemberId)
        || !string.IsNullOrEmpty(Assignment.GroupId)
        || !string.IsNullOrWhiteSpace(JournalText)
        || State != "1"
        || Impact != "3"
        || Urgency != "3"
        || !string.IsNullOrEmpty(Priority)
        || !string.IsNullOrEmpty(Category)
        || !string.IsNullOrEmpty(Subcategory)
        || ContactType != "phone"
        || !string.IsNullOrEmpty(HoldReason);

    private Task<IReadOnlyList<ReferenceSuggestion>> SearchUsersAsync(string text, CancellationToken cancellationToken) =>
        Client is null
            ? Task.FromResult<IReadOnlyList<ReferenceSuggestion>>([])
            : Client.SearchUsersAsync(text, cancellationToken);
}
