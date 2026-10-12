using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceNowDesk.Alerts;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Query;
using ServiceNowDesk.Services;
using ServiceNowDesk.WorkEffort;

namespace ServiceNowDesk.ViewModels;

public abstract partial class RecordWorkspaceViewModel : ObservableObject
{
    private const string LiveInstanceMessage = "Connect to a live instance to open this record in the browser.";
    private CancellationTokenSource? _openCts;
    private TicketRow? _boundRow;
    private bool _suppressSelection;
    private int _loadVersion;
    private int _openVersion;
    private bool _limitOffices;
    private IReadOnlyList<string> _officeCities = [];
    private IReadOnlyList<string> _regionOffices = [];
    private IReadOnlyList<string> _teamMemberIds = [];
    private bool _useLeadTeam;
    private string _watchedGroupName = "";
    private bool _queryFilteredByRegion;
    private readonly Dictionary<string, string> _teamNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _comboValue = new(StringComparer.Ordinal);
    private string[]? _cleanFields;
    private int _applyingDepth;

    protected RecordWorkspaceViewModel(IDesktopServices desktop, string tableName, string recordLabel, bool allowCreate, IReadOnlyList<PresetOption> presets, DeskSection section, bool attachments = false)
    {
        Desktop = desktop;
        TableName = tableName;
        RecordLabel = recordLabel;
        AllowCreate = allowCreate;
        Presets = presets;
        Preset = presets[0];
        Section = section;
        ResolveChoiceLabel = "Outcome";
        SupportsAttachments = attachments;
        Journal.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasJournal));
    }

    protected IDesktopServices Desktop { get; }
    protected IServiceNowClient? Client { get; private set; }

    /// <summary>
    /// Nested loads and choice rebuilds each take a level. Programmatic sets inside
    /// a level are not user edits.
    /// </summary>
    protected bool Applying
    {
        get => _applyingDepth > 0;
        set
        {
            if (value)
                _applyingDepth++;
            else if (_applyingDepth > 0)
                _applyingDepth--;
        }
    }

    protected string? EditorSysId { get; set; }

    public string TableName { get; }
    public string RecordLabel { get; }
    public DeskSection Section { get; }
    public bool AllowCreate { get; }
    public bool SupportsAttachments { get; }
    public ObservableCollection<AttachmentSummary> Attachments { get; } = [];
    public IReadOnlyList<PresetOption> Presets { get; }
    public ObservableCollection<TicketRow> Items { get; } = [];

    public Action<TicketRow>? PrepareRow { get; set; }
    public event EventHandler<SavedTicketFields>? RecordSaved;
    public ObservableCollection<JournalEntry> Journal { get; } = [];
    public ObservableCollection<Choice> ResolveChoices { get; } = [];

    [ObservableProperty] private TicketRow? selected;
    [ObservableProperty] private PresetOption? preset;
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string searchHint = "";
    [ObservableProperty] private bool isDirty;
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private bool isEditorBusy;
    [ObservableProperty] private bool isNew;
    [ObservableProperty] private bool hasEditor;
    [ObservableProperty] private bool hasLoaded;
    [ObservableProperty] private bool isReady;
    [ObservableProperty] private bool showUnsavedBanner;
    [ObservableProperty] private bool showResolvePanel;
    [ObservableProperty] private string number = "";
    [ObservableProperty] private string stateLabel = "";
    [ObservableProperty] private string priorityLabel = "";
    [ObservableProperty] private string openedAt = "";
    [ObservableProperty] private string shortDescription = "";
    [ObservableProperty] private string description = "";
    [ObservableProperty] private string journalText = "";
    [ObservableProperty] private JournalKind journalKind = JournalKind.WorkNotes;
    [ObservableProperty] private bool journalIsCustomerVisible;
    [ObservableProperty] private string editorMessage = "";
    [ObservableProperty] private string errorMessage = "";
    [ObservableProperty] private int totalCount;
    [ObservableProperty] private string resolveChoiceLabel = "";
    [ObservableProperty] private string resolveCode = "";
    [ObservableProperty] private string resolveNotes = "";
    [ObservableProperty] private string attachmentNote = "";
    [ObservableProperty] private string filterSummary = "";
    [ObservableProperty] private string lastEncodedQuery = "";
    [ObservableProperty] private string listRefreshedText = "";

    /// <summary>When the visible list was last loaded (live or from a saved copy).</summary>
    public DateTimeOffset? ListRefreshedAt { get; private set; }

    /// <summary>Clock for list "As of" stamps. Tests may replace this.</summary>
    internal Func<DateTimeOffset> ListClock { get; set; } = static () => DateTimeOffset.Now;

    public bool HasJournalText => !string.IsNullOrWhiteSpace(JournalText);

    public bool HasJournal => Journal.Count > 0;

    public IReadOnlyList<string> OfficeCities => _officeCities;

    /// <summary>Office labels saved as "my region" on Settings. Empty when none are saved.</summary>
    public IReadOnlyList<string> RegionOffices => _regionOffices;

    public IReadOnlyList<string> TeamMemberIds => _teamMemberIds;

    /// <summary>True after <see cref="UseTeamMembers"/> so My Team is the Leads roster, not every group.</summary>
    public bool UsesLeadTeam => _useLeadTeam;

    /// <summary>True when the last query for this list included a Settings region location clause.</summary>
    public bool QueryFilteredByRegion => _queryFilteredByRegion;

    /// <summary>Why the last refresh did not call ServiceNow. Empty when a query was sent or a cached query is shown.</summary>
    public string QuerySkipReason { get; private set; } = "";

    /// <summary>Cache population painted onto this list, or <c>none</c> when the rows did not come from one.</summary>
    public string CachePopulationName { get; private set; } = "none";

    public int CacheRowsStored { get; private set; }

    /// <summary>Incidents, request items, and walk-ups can copy a troubleshooting block.</summary>
    public bool ShowCopyDetails =>
        Section is DeskSection.Incidents or DeskSection.RequestedItems or DeskSection.WalkUps;

    /// <summary>
    /// Incidents and walk-ups open on the new-ticket form. Request items are created from the
    /// catalog, so they keep the select prompt instead of an empty form that cannot be saved.
    /// </summary>
    public virtual bool OpensOnBlankForm => false;

    /// <summary>Right pane is the new-ticket form with empty fields and no list row selected.</summary>
    public bool ShowsBlankTicketForm => OpensOnBlankForm && IsNew && HasEditor && Selected is null;

    /// <summary>Right pane is the "Select a …" blurb. Blank-form lists never use it.</summary>
    public bool ShowsSelectPrompt => !OpensOnBlankForm && !HasEditor;

    /// <summary>
    /// Paints a saved My Tickets or My Team population before any network call.
    /// Returns true when that fully handled the preset change.
    /// </summary>
    public Func<PresetOption, bool>? TryPaintCachedPreset { get; set; }

    /// <summary>Saves the visible My Tickets or My Team rows after a successful list refresh.</summary>
    public Action? PopulationReady { get; set; }

    /// <summary>
    /// Queue cities for Unassigned. My Tickets ignores the list (assignee-only).
    /// All Open ignores it too. People My Team ignores it too.
    /// </summary>
    public void UseOfficeCities(IReadOnlyList<string>? cities)
    {
        _limitOffices = true;
        _officeCities = cities ?? [];
    }

    /// <summary>
    /// Settings region offices. Reported on Copy details. People My Team does not add them
    /// to the query. Hardware is what filters by this list.
    /// </summary>
    public void UseRegionOffices(IEnumerable<string>? offices) =>
        _regionOffices = RegionOfficeMatch.NormalizeOffices(offices);

    /// <summary>
    /// Leads people for My Team. The same ids are used for walk-ups, incidents, and request items.
    /// An empty list keeps those My Team lists empty.
    /// </summary>
    public void UseTeamMembers(IReadOnlyList<string>? memberIds)
    {
        _useLeadTeam = true;
        _teamMemberIds = (memberIds ?? [])
            .Select(id => id?.Trim() ?? "")
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Display names already on the Leads roster. A missing or id-only name stays a sys_id.</summary>
    public void UseTeamNames(IEnumerable<WorkEffortPerson>? people)
    {
        _teamNames.Clear();
        if (people is null)
            return;
        foreach (var person in people)
        {
            var id = person.SysId?.Trim() ?? "";
            if (id.Length == 0)
                continue;
            _teamNames[id] = person.DisplayName ?? "";
        }
    }

    public IReadOnlyList<string> TeamLabels()
    {
        var labels = new List<string>(_teamMemberIds.Count);
        foreach (var id in _teamMemberIds)
        {
            if (_teamNames.TryGetValue(id, out var name)
                && name.Length > 0
                && !name.Equals(id, StringComparison.OrdinalIgnoreCase))
                labels.Add(name);
            else
                labels.Add(id);
        }

        return labels;
    }

    public void NoteCacheRead(string? population, int stored)
    {
        CachePopulationName = string.IsNullOrWhiteSpace(population) ? "none" : population.Trim();
        CacheRowsStored = Math.Max(0, stored);
    }

    /// <summary>
    /// Group name used only when My Team has no selected people. People My Team ignores it.
    /// </summary>
    public void UseWatchedGroup(string? name) =>
        _watchedGroupName = name?.Trim() ?? "";

    public void Attach(IServiceNowClient client)
    {
        Client = client;
        IsReady = true;
        // A blank form prepared before connect stays. This only fills the gap if nothing is open yet.
        if (OpensOnBlankForm && !HasEditor && Selected is null && !IsDirty)
            ShowBlankTicket();
    }

    public void Detach()
    {
        _openCts?.Cancel();
        Client = null;
        IsReady = false;
        HasLoaded = false;
        Applying = true;
        _suppressSelection = true;
        Items.Clear();
        Journal.Clear();
        Selected = null;
        _boundRow = null;
        EditorSysId = null;
        Number = "";
        StateLabel = "";
        PriorityLabel = "";
        OpenedAt = "";
        ShortDescription = "";
        Description = "";
        JournalText = "";
        IsNew = false;
        HasEditor = false;
        IsDirty = false;
        // The session is gone. Do not compare the cleared fields with the ticket that was open.
        _cleanFields = null;
        ShowResolvePanel = false;
        ShowUnsavedBanner = false;
        ErrorMessage = "";
        EditorMessage = "";
        TotalCount = 0;
        Attachments.Clear();
        AttachmentNote = "";
        ListRefreshedText = "";
        ListRefreshedAt = null;
        OnDetached();
        _suppressSelection = false;
        Applying = false;
        if (OpensOnBlankForm)
            ShowBlankTicket();
    }

    public async Task OpenFromSearchAsync(string sysId)
    {
        if (HasUnsavedEdits("Save or discard unsaved changes before opening another record."))
            return;

        await LoadEditorAsync(sysId, CancellationToken.None);
    }

    public virtual Task EnsureChoicesAsync() => Task.CompletedTask;

    public void ShowCachedRows(
        IReadOnlyList<TicketRow> rows,
        int totalCount,
        DateTimeOffset? capturedAt = null,
        string? encodedQuery = null,
        string? filterSummary = null,
        string? cachePopulation = null,
        int? cacheStored = null,
        bool? filteredByRegion = null)
    {
        var visible = rows.Where(ShowsOnThisList).ToArray();
        _suppressSelection = true;
        Items.Clear();
        foreach (var row in visible)
        {
            PrepareRow?.Invoke(row);
            Items.Add(row);
        }
        TotalCount = visible.Length == rows.Count ? totalCount : visible.Length;
        Selected = null;
        _boundRow = null;
        _suppressSelection = false;
        ErrorMessage = "";
        QuerySkipReason = "";
        HasLoaded = true;
        var query = BuildQuery();
        if (filteredByRegion is bool filtered)
            _queryFilteredByRegion = filtered;
        if (!string.IsNullOrWhiteSpace(filterSummary))
            FilterSummary = filterSummary.Trim();
        else
            RefreshFilterChrome(query, encodedQuery ?? Client?.LastTicketEncodedQuery ?? LastEncodedQuery);
        if (!string.IsNullOrWhiteSpace(encodedQuery))
            LastEncodedQuery = encodedQuery.Trim();
        else if (string.IsNullOrWhiteSpace(filterSummary))
            LastEncodedQuery = Client?.LastTicketEncodedQuery ?? LastEncodedQuery;
        if (cachePopulation is not null)
            NoteCacheRead(cachePopulation, cacheStored ?? visible.Length);
        MarkListRefreshed(capturedAt);
    }

    /// <summary>
    /// Remembers the last download encoded query for logging. List chrome stays human-readable.
    /// </summary>
    public void RememberDownloadQuery(string? encodedQuery)
    {
        LastEncodedQuery = encodedQuery?.Trim() ?? "";
        RefreshFilterChrome(BuildQuery(), LastEncodedQuery);
    }

    public async Task<bool> ReloadAsync()
    {
        ErrorMessage = "";
        await RefreshAsync();
        return HasLoaded && string.IsNullOrEmpty(ErrorMessage);
    }

    [RelayCommand]
    public Task RefreshAsync() => RefreshListedAsync(keepRowsWhenEmpty: false);

    /// <summary>
    /// Reloads from ServiceNow without blanking rows already painted from the cache
    /// when the server returns nothing.
    /// </summary>
    internal Task RefreshKeepingCachedRowsAsync() => RefreshListedAsync(keepRowsWhenEmpty: true);

    private async Task RefreshListedAsync(bool keepRowsWhenEmpty)
    {
        if (Client is null)
            return;

        var version = ++_loadVersion;
        try
        {
            IsLoading = true;
            QuerySkipReason = "";
            var trimmed = SearchText.Trim();
            var searchHint = trimmed.Length is > 0 and < 2 && !EncodedQuery.IsNumberQuery(trimmed)
                ? "Type at least 2 characters to search."
                : "";
            var query = BuildQuery();
            if (SkipListQuery(query, out var emptyHint))
            {
                SearchHint = emptyHint;
                QuerySkipReason = emptyHint;
                NoteCacheRead("none", 0);
                _suppressSelection = true;
                Items.Clear();
                TotalCount = 0;
                _suppressSelection = false;
                HasLoaded = true;
                LastEncodedQuery = "";
                RefreshFilterChrome(query, "");
                MarkListRefreshed();
                return;
            }

            SearchHint = searchHint;
            var openAtStart = _openVersion;
            var editorAtStart = EditorSysId;
            var painted = Items.Count;
            var page = await FetchPageAsync(query, CancellationToken.None);
            if (version != _loadVersion)
                return;

            var openedDuringRefresh = openAtStart != _openVersion;
            var openStillApplying = openedDuringRefresh && EditorSysId == editorAtStart;
            var keep = openStillApplying ? null : EditorSysId;
            var visible = page.Items.Where(ShowsOnThisList).ToArray();
            if (keepRowsWhenEmpty && visible.Length == 0 && painted > 0)
            {
                HasLoaded = true;
                return;
            }

            _suppressSelection = true;
            Items.Clear();
            foreach (var row in visible)
            {
                PrepareRow?.Invoke(row);
                Items.Add(row);
            }
            TotalCount = visible.Length == page.Items.Count
                ? page.TotalCount ?? visible.Length
                : visible.Length;
            var match = keep is null ? null : Items.FirstOrDefault(row => row.SysId == keep);
            if (match is not null)
            {
                Selected = match;
                _boundRow = match;
            }

            _suppressSelection = false;
            HasLoaded = true;
            ErrorMessage = "";
            RefreshFilterChrome(query, Client?.LastTicketEncodedQuery ?? "");
            MarkListRefreshed();
            if (string.IsNullOrWhiteSpace(SearchText))
                PopulationReady?.Invoke();
        }
        catch (Exception ex)
        {
            if (version == _loadVersion)
                ErrorMessage = WorkspaceMessages.Describe(ex);
        }
        finally
        {
            if (version == _loadVersion)
                IsLoading = false;
        }
    }

    protected void MarkListRefreshed(DateTimeOffset? when = null)
    {
        var stamp = when is { } at && at != default ? at : ListClock();
        ListRefreshedAt = stamp;
        ListRefreshedText = "As of " + stamp.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    }

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private void NewRecord()
    {
        if (!CanCreate())
            return;
        if (HasUnsavedEdits("Save or discard unsaved changes first."))
            return;

        BeginNew();
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (Client is null || IsEditorBusy)
            return;
        await ResolveReferencesAsync(CancellationToken.None);
        if (!TryValidate(out var message))
        {
            ErrorMessage = message;
            return;
        }

        var saved = false;
        try
        {
            IsEditorBusy = true;
            ErrorMessage = "";
            Applying = true;
            if (IsNew)
                await SaveNewAsync(CancellationToken.None);
            else
                await SaveExistingAsync(CancellationToken.None);
            await PostPendingJournalAsync(CancellationToken.None);
            IsNew = false;
            ShowUnsavedBanner = false;
            EditorMessage = "Saved " + Number + ".";
            var assignee = SavedAssignee();
            RecordSaved?.Invoke(this, new SavedTicketFields(
                EditorSysId ?? "",
                ShortDescription ?? "",
                StateLabel ?? "",
                assignee?.Name,
                assignee?.Id));
            await RefreshAttachmentsAsync(CancellationToken.None);
            saved = true;
        }
        catch (Exception ex)
        {
            ErrorMessage = WorkspaceMessages.Describe(ex);
        }
        finally
        {
            Applying = false;
            IsEditorBusy = false;
            if (saved)
                AcceptCleanState();
            else
                RecalculateDirty();
        }
    }

    [RelayCommand]
    private void Discard()
    {
        Applying = true;
        try
        {
            JournalText = "";
            ShowResolvePanel = false;
            ShowUnsavedBanner = false;
            ErrorMessage = "";
            Restore();
            EditorMessage = "Changes discarded.";
        }
        finally
        {
            Applying = false;
            AcceptCleanState();
        }
    }

    [RelayCommand(CanExecute = nameof(CanPostJournal))]
    private async Task PostJournalAsync()
    {
        if (Client is null || IsEditorBusy)
            return;

        try
        {
            IsEditorBusy = true;
            ErrorMessage = "";
            await PostPendingJournalAsync(CancellationToken.None);
            EditorMessage = JournalKind == JournalKind.Comments
                ? "Customer comment posted on " + Number + "."
                : "Work note posted on " + Number + ".";
            ShowUnsavedBanner = false;
        }
        catch (Exception ex)
        {
            ErrorMessage = WorkspaceMessages.Describe(ex);
        }
        finally
        {
            IsEditorBusy = false;
            RecalculateDirty();
        }
    }

    [RelayCommand]
    protected void BeginResolve()
    {
        if (IsNew || string.IsNullOrEmpty(EditorSysId))
        {
            EditorMessage = "Save the record before resolving it.";
            return;
        }

        ShowResolvePanel = true;
        if (string.IsNullOrWhiteSpace(ResolveCode) && ResolveChoices.Count > 0)
            ResolveCode = ResolveChoices[0].Value;
        EditorMessage = "";
    }

    [RelayCommand]
    private void CancelResolve()
    {
        // Hide resolve UI and leave draft outcome/notes so Resolve again keeps them.
        ShowResolvePanel = false;
    }

    [RelayCommand(CanExecute = nameof(CanConfirmResolve))]
    private async Task ConfirmResolveAsync()
    {
        if (Client is null || string.IsNullOrEmpty(EditorSysId))
            return;
        if (string.IsNullOrWhiteSpace(ResolveCode) || string.IsNullOrWhiteSpace(ResolveNotes))
        {
            ErrorMessage = "Choose an outcome and enter notes.";
            return;
        }

        var resolved = false;
        try
        {
            IsEditorBusy = true;
            ErrorMessage = "";
            Applying = true;
            if (IsDirty)
            {
                if (!TryValidate(out var message))
                {
                    ErrorMessage = message;
                    return;
                }

                await SaveExistingAsync(CancellationToken.None);
                await PostPendingJournalAsync(CancellationToken.None);
            }

            await ResolveRecordAsync(CancellationToken.None);
            ShowResolvePanel = false;
            ResolveNotes = "";
            ShowUnsavedBanner = false;
            EditorMessage = "Updated " + Number + ".";
            await RefreshAsync();
            resolved = true;
        }
        catch (Exception ex)
        {
            ErrorMessage = WorkspaceMessages.Describe(ex);
        }
        finally
        {
            Applying = false;
            IsEditorBusy = false;
            if (resolved)
                AcceptCleanState();
            else
                RecalculateDirty();
        }
    }

    [RelayCommand]
    private void CopyNumber()
    {
        if (Number.Length == 0)
            return;
        Desktop.CopyText(Number);
        EditorMessage = "Copied " + Number + ".";
    }

    [RelayCommand]
    private void CopyDetails()
    {
        var snapshot = new ListDiagnosticSnapshot(
            SectionLabel(),
            Preset?.Label ?? "",
            _teamMemberIds.Count > 0,
            _teamMemberIds.Count,
            TeamLabels(),
            _regionOffices.Count,
            _queryFilteredByRegion,
            CachePopulationName,
            CacheRowsStored,
            Items.Count,
            string.IsNullOrWhiteSpace(QuerySkipReason) ? LastEncodedQuery : null,
            QuerySkipReason,
            ErrorMessage);
        Desktop.CopyText(ListDiagnostics.Format(snapshot));
        EditorMessage = "Copied list details.";
    }

    private string SectionLabel() => Section switch
    {
        DeskSection.Incidents => "Incidents",
        DeskSection.RequestedItems => "Request items",
        DeskSection.WalkUps => "Walk-ups",
        DeskSection.Requests => "Requests",
        _ => Section.ToString()
    };

    [RelayCommand]
    private void OpenInBrowser()
    {
        if (Client?.InstanceUri is null || string.IsNullOrEmpty(EditorSysId))
        {
            EditorMessage = LiveInstanceMessage;
            return;
        }

        Desktop.OpenUrl(ServiceNowLinks.Record(Client.InstanceUri, TableName, EditorSysId));
    }

    [RelayCommand]
    private void OpenAttachment(AttachmentSummary? attachment)
    {
        if (attachment is null || Client is null)
            return;
        if (IsNew || string.IsNullOrEmpty(EditorSysId))
        {
            AttachmentNote = "Save the record before it can have attachments.";
            return;
        }

        var url = ResolveAttachmentUrl(attachment);
        if (url is null)
        {
            EditorMessage = LiveInstanceMessage;
            AttachmentNote = LiveInstanceMessage;
            return;
        }

        Desktop.OpenUrl(url);
    }

    private string? ResolveAttachmentUrl(AttachmentSummary attachment)
    {
        var given = attachment.Url?.Trim() ?? "";
        if (given.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || given.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return given;

        if (Client?.InstanceUri is null)
            return null;

        if (given.Length > 0)
        {
            var authority = Client.InstanceUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
            return authority + (given.StartsWith('/') ? given : "/" + given);
        }

        return ServiceNowLinks.Attachment(Client.InstanceUri, attachment.SysId);
    }

    [RelayCommand]
    private void ApplyPreset(PresetOption? option)
    {
        if (option is null || option == Preset)
            return;
        Preset = option;
    }

    [RelayCommand]
    private void DismissError() => ErrorMessage = "";

    protected void Touch() => RecalculateDirty();

    /// <summary>
    /// Puts the right pane on the existing new-ticket form. A loaded row replaces it;
    /// clearing that row comes back here. Dirty edits stay put until Save or Discard.
    /// </summary>
    protected void ShowBlankTicket()
    {
        if (!OpensOnBlankForm)
            return;
        if (IsNew && HasEditor && Selected is null)
            return;
        if (HasUnsavedEdits("Save or discard unsaved changes first."))
        {
            if (_boundRow is not null && Selected != _boundRow)
            {
                _suppressSelection = true;
                Selected = _boundRow;
                _suppressSelection = false;
            }

            return;
        }

        BeginNew();
    }

    protected void BeginNew()
    {
        Applying = true;
        _suppressSelection = true;
        try
        {
            Selected = null;
            _boundRow = null;
            EditorSysId = null;
            Number = "";
            StateLabel = "New";
            PriorityLabel = "";
            OpenedAt = "";
            JournalText = "";
            Journal.Clear();
            ShowResolvePanel = false;
            ShowUnsavedBanner = false;
            ErrorMessage = "";
            EditorMessage = "";
            IsNew = true;
            HasEditor = true;
            OnStartNew();
            if (SupportsAttachments)
            {
                Attachments.Clear();
                AttachmentNote = "Save the record before it can have attachments.";
            }
        }
        finally
        {
            _suppressSelection = false;
            Applying = false;
            AcceptCleanState();
        }
    }

    protected async Task FillChoicesAsync(
        ObservableCollection<Choice> target,
        string table,
        string element,
        IReadOnlyList<Choice> fallback,
        string? dependent = null,
        bool includeBlank = false,
        string blankLabel = "")
    {
        if (Client is null)
            return;

        IReadOnlyList<Choice> choices;
        try
        {
            choices = await Client.GetChoicesAsync(table, element, dependent, CancellationToken.None);
        }
        catch
        {
            choices = fallback;
        }

        if (choices is null || choices.Count == 0)
            choices = fallback;

        target.Clear();
        if (includeBlank)
            target.Add(new Choice("", blankLabel));
        foreach (var choice in choices)
        {
            if (choice?.Value is null)
                continue;
            target.Add(choice);
        }
    }

    protected static void EnsureChoice(ObservableCollection<Choice> target, string value, string label)
    {
        if (string.IsNullOrEmpty(value) || target.Any(choice => choice.Value == value))
            return;
        target.Add(new Choice(value, string.IsNullOrWhiteSpace(label) ? value : label));
    }

    protected virtual Task ResolveReferencesAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    protected static bool ReferenceIsChosen(ReferenceFieldModel field)
    {
        var text = field.Text ?? "";
        var id = field.SysId ?? "";
        return string.IsNullOrWhiteSpace(text) == (id.Length == 0);
    }

    protected void UpsertRow(TicketRow row)
    {
        if (!ShowsOnThisList(row) || !MatchesOfficeScope(row))
        {
            RemoveFromList(row.SysId);
            return;
        }

        var index = -1;
        for (var i = 0; i < Items.Count; i++)
        {
            if (Items[i].SysId == row.SysId)
            {
                index = i;
                break;
            }
        }

        PrepareRow?.Invoke(row);
        _suppressSelection = true;
        if (index < 0)
            Items.Insert(0, row);
        else
            Items[index] = row;
        Selected = Items.First(item => item.SysId == row.SysId);
        _boundRow = Selected;
        _suppressSelection = false;
    }

    /// <summary>
    /// Open presets hide resolved, closed, and cancelled rows that still come back from ServiceNow.
    /// On hold stays. Closed and any-activity lists keep every row.
    /// </summary>
    protected bool ShowsOnThisList(TicketRow row)
    {
        if (Preset?.Activity != ActivityFilter.Open)
            return true;
        return AlertClassifier.IsStillOpen(Section, row.StateValue, row.StateLabel);
    }

    /// <summary>
    /// Unassigned drops out-of-office rows on Upsert so opening a Daily Work / search
    /// hit cannot inject another city's ticket into that queue.
    /// My Tickets and All Open stay unscoped. People My Team keeps a selected assignee
    /// in any location. The same scope as All Open, then the selected people.
    /// </summary>
    protected bool MatchesOfficeScope(TicketRow row)
    {
        if (_useLeadTeam && Preset?.Assignment == AssignmentScope.MyGroups)
            return TeamListScope.Includes(_teamMemberIds, row.AssigneeId);

        if (Preset?.Assignment is not (AssignmentScope.MyGroups or AssignmentScope.Unassigned))
            return true;

        if (!_limitOffices)
            return true;
        return OfficeQueue.Matches(row.Location, _officeCities);
    }

    private void RemoveFromList(string sysId)
    {
        var index = -1;
        for (var i = 0; i < Items.Count; i++)
        {
            if (Items[i].SysId == sysId)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
            return;

        _suppressSelection = true;
        Items.RemoveAt(index);
        if (Selected?.SysId == sysId)
            Selected = null;
        _suppressSelection = false;
    }

    protected virtual bool SkipListQuery(TicketQuery query, out string hint)
    {
        hint = "";
        if (!_useLeadTeam || query.Assignment != AssignmentScope.MyGroups)
            return false;
        if (_teamMemberIds.Count > 0)
            return false;
        hint = WalkUpTeam.EmptyPrompt;
        return true;
    }

    protected TicketQuery BuildQuery() => BuildQuery(Preset);

    protected TicketQuery BuildQuery(PresetOption? preset)
    {
        var trimmed = SearchText.Trim();
        string? text = null;
        if (trimmed.Length >= 2 || EncodedQuery.IsNumberQuery(trimmed))
            text = trimmed;

        var assignment = preset?.Assignment ?? AssignmentScope.Any;
        IReadOnlyList<string>? offices = null;
        IReadOnlyList<string>? team = null;
        string? watched = null;
        _queryFilteredByRegion = false;
        if (_useLeadTeam && assignment == AssignmentScope.MyGroups)
        {
            // Same Leads people on every My Team list. All Open does not filter by
            // location, and neither does this. A region clause dropped tickets that
            // were assigned to a selected person when the location name did not match
            // a Settings office, while My Tickets (assignee only) still showed them.
            team = _teamMemberIds;
        }
        else if (_limitOffices && assignment is AssignmentScope.MyGroups or AssignmentScope.Unassigned)
        {
            // Unassigned, and My Team before the Leads roster is in use: queue offices
            // (notification cities, or the signed-in user's city). My Tickets stays assignee-only.
            // Those queue cities are not the Settings region list.
            offices = _officeCities;
            if (assignment == AssignmentScope.MyGroups && _watchedGroupName.Length > 0)
                watched = _watchedGroupName;
        }
        else if (assignment == AssignmentScope.MyGroups && _watchedGroupName.Length > 0)
        {
            watched = _watchedGroupName;
        }

        return new TicketQuery
        {
            Text = text,
            Assignment = assignment,
            Activity = preset?.Activity ?? ActivityFilter.Open,
            AssignmentClause = preset?.AssignmentClause,
            OfficeLocations = offices,
            TeamMemberIds = team,
            LimitTeamToOffices = false,
            WatchedGroupName = watched,
            ListSection = Section,
            Limit = 50
        };
    }

    /// <summary>Downloads one preset without replacing the rows on screen.</summary>
    public async Task<PopulationFetch> FetchPopulationAsync(PresetOption preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var query = BuildQuery(preset);
        var filtered = _queryFilteredByRegion;
        var summary = TicketListFilter.DescribeActive(preset, query.OfficeLocations, query.TeamMemberIds);
        if (SkipListQuery(query, out var hint))
            return PopulationFetch.Skip(hint, summary, filtered);

        try
        {
            var page = await FetchPageAsync(query, CancellationToken.None);
            var visible = page.Items.Where(row => ShowsOnList(preset, row)).ToArray();
            var total = visible.Length == page.Items.Count
                ? page.TotalCount ?? visible.Length
                : visible.Length;
            return PopulationFetch.Loaded(visible, total, Client?.LastTicketEncodedQuery ?? "", summary, filtered);
        }
        catch (Exception ex)
        {
            return PopulationFetch.Fail(WorkspaceMessages.Describe(ex));
        }
    }

    private bool ShowsOnList(PresetOption preset, TicketRow row)
    {
        if (preset.Activity != ActivityFilter.Open)
            return true;
        return AlertClassifier.IsStillOpen(Section, row.StateValue, row.StateLabel);
    }

    private void RefreshFilterChrome(TicketQuery query, string encodedQuery)
    {
        FilterSummary = TicketListFilter.DescribeActive(
            Preset,
            query.OfficeLocations,
            query.TeamMemberIds);
        if (!string.IsNullOrWhiteSpace(encodedQuery))
            LastEncodedQuery = encodedQuery.Trim();
        else if (string.IsNullOrWhiteSpace(LastEncodedQuery))
            LastEncodedQuery = "";
    }

    protected abstract Task<PagedResult<TicketRow>> FetchPageAsync(TicketQuery query, CancellationToken cancellationToken);
    protected abstract Task LoadRecordAsync(string sysId, CancellationToken cancellationToken);

    /// <summary>
    /// Field values a person can change. The clean snapshot is taken after load, after the
    /// blank form is prepared, and after choice lists settle. A later difference is a hand edit.
    /// </summary>
    protected abstract IReadOnlyList<string> ReadEditableFields();

    protected abstract void OnStartNew();
    protected abstract void Restore();
    protected abstract Task SaveNewAsync(CancellationToken cancellationToken);
    protected abstract Task SaveExistingAsync(CancellationToken cancellationToken);

    /// <summary>Assignee name and id after a save. Null when this record has no assignee field.</summary>
    protected virtual (string Name, string Id)? SavedAssignee() => null;
    protected abstract Task ResolveRecordAsync(CancellationToken cancellationToken);
    protected virtual bool TryValidate(out string message)
    {
        message = "";
        return true;
    }

    protected virtual void OnDetached()
    {
    }

    private async Task RefreshAttachmentsAsync(CancellationToken cancellationToken)
    {
        if (!SupportsAttachments)
            return;

        if (IsNew || string.IsNullOrEmpty(EditorSysId) || Client is null)
        {
            Attachments.Clear();
            AttachmentNote = "Save the record before it can have attachments.";
            return;
        }

        var files = await Client.ListAttachmentsAsync(TableName, EditorSysId, cancellationToken);
        if (cancellationToken.IsCancellationRequested)
            return;

        Attachments.Clear();
        foreach (var file in files)
            Attachments.Add(file);
        if (Attachments.Count == 0)
            AttachmentNote = "No attachments.";
        else if (Client.InstanceUri is null)
            AttachmentNote = LiveInstanceMessage;
        else
            AttachmentNote = "";
    }

    protected void ReplaceJournal(IEnumerable<JournalEntry> notes)
    {
        Journal.Clear();
        foreach (var note in notes)
            Journal.Add(note);
    }

    protected async Task PostPendingJournalAsync(CancellationToken cancellationToken)
    {
        if (Client is null || string.IsNullOrWhiteSpace(JournalText) || string.IsNullOrEmpty(EditorSysId))
            return;

        var text = JournalText.Trim();
        var kind = JournalKind;
        await Client.AddJournalAsync(TableName, EditorSysId, kind, text, cancellationToken);
        JournalText = "";
        var notes = await Client.GetJournalAsync(TableName, EditorSysId, cancellationToken);
        ReplaceJournal(notes);
    }

    private async Task LoadEditorAsync(string sysId, CancellationToken cancellationToken)
    {
        if (Client is null || string.IsNullOrWhiteSpace(sysId))
            return;

        var version = ++_openVersion;
        var acceptLoaded = false;
        try
        {
            IsEditorBusy = true;
            ErrorMessage = "";
            Applying = true;
            // A search pop-out is a fresh editor. Choice lists and the assignment-group
            // member roster have to load before the record, or Channel, Impact, Urgency,
            // and Assigned to stay empty except for the person already on the ticket.
            await EnsureChoicesAsync();
            if (version != _openVersion)
                return;
            await LoadRecordAsync(sysId, cancellationToken);
            if (version != _openVersion)
                return;

            EditorSysId = sysId;
            IsNew = false;
            HasEditor = true;
            ShowResolvePanel = false;
            ShowUnsavedBanner = false;
            Applying = false;
            // Journal and attachments are secondary. An HTML/ACL failure on either must
            // not leave the editor busy or Save stuck — the record fields already loaded.
            try
            {
                var notes = await Client.GetJournalAsync(TableName, sysId, cancellationToken);
                if (version != _openVersion)
                    return;
                ReplaceJournal(notes);
            }
            catch (Exception ex)
            {
                if (version == _openVersion)
                {
                    ReplaceJournal([]);
                    ErrorMessage = ClarifySecondaryLookup(ex, "work notes");
                }
            }

            try
            {
                await RefreshAttachmentsAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                if (version == _openVersion)
                {
                    Attachments.Clear();
                    AttachmentNote = "Attachments could not be loaded.";
                    if (string.IsNullOrEmpty(ErrorMessage))
                        ErrorMessage = ClarifySecondaryLookup(ex, "attachments");
                }
            }

            if (version != _openVersion)
                return;
            _boundRow = Items.FirstOrDefault(row => row.SysId == sysId);
            _suppressSelection = true;
            if (_boundRow is not null)
                Selected = _boundRow;
            _suppressSelection = false;
            acceptLoaded = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (version == _openVersion)
                ErrorMessage = WorkspaceMessages.Describe(ex);
        }
        finally
        {
            if (version == _openVersion)
            {
                Applying = false;
                IsEditorBusy = false;
                // Opening or viewing a record is the new clean baseline, including values
                // the server and choice lists filled in. Only a later hand edit is dirty.
                if (acceptLoaded)
                    AcceptCleanState();
                else
                    RecalculateDirty();
            }
        }
    }

    private static string ClarifySecondaryLookup(Exception ex, string what)
    {
        var text = WorkspaceMessages.Describe(ex);
        if (text.Contains("web page instead of API data", StringComparison.OrdinalIgnoreCase))
            return $"Could not load {what}: the instance returned a web page instead of Table API data. The ticket is still open — you can edit and Save.";
        return $"Could not load {what}: {text}";
    }

    private void QueueOpen(string sysId)
    {
        _openCts?.Cancel();
        _openCts = new CancellationTokenSource();
        var token = _openCts.Token;
        _ = OpenAfterDelayAsync(sysId, token);
    }

    private async Task OpenAfterDelayAsync(string sysId, CancellationToken token)
    {
        try
        {
            await Task.Delay(80, token);
            await LoadEditorAsync(sysId, token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void RecalculateDirty()
    {
        if (Applying)
            return;
        if (_cleanFields is null)
        {
            IsDirty = false;
            return;
        }

        var current = ReadEditableFields();
        IsDirty = current.Count != _cleanFields.Length || !current.SequenceEqual(_cleanFields);
    }

    /// <summary>
    /// Remembers the form as the user sees it after a load or a blank open.
    /// Binding and choice-list fills that happen before this are not edits.
    /// </summary>
    protected void AcceptCleanState()
    {
        _cleanFields = ReadEditableFields().ToArray();
        RememberCombos();
        IsDirty = false;
        ShowUnsavedBanner = false;
    }

    /// <summary>
    /// Choice lists and group catalogs can rewrite defaults after the form is shown.
    /// Keep that as the clean snapshot unless the user has already edited by hand.
    /// </summary>
    protected void AdoptUntouchedForm()
    {
        if (!IsDirty)
            AcceptCleanState();
    }

    protected virtual void RememberCombos()
    {
    }

    protected void RememberCombo(string key, string? value) =>
        _comboValue[key] = value ?? "";

    /// <summary>
    /// A combo writes null when its current value is missing from ItemsSource (empty list,
    /// or the list was just cleared). That is binding, not a person clearing the field.
    /// A real clear still counts when the previous value is present in the list.
    /// </summary>
    protected bool RestoreComboIfCleared(string key, string? incoming, IEnumerable<Choice> choices, Action<string> writeBack)
    {
        var next = incoming ?? "";
        _comboValue.TryGetValue(key, out var held);
        held ??= "";
        if (!Applying
            && next.Length == 0
            && held.Length > 0
            && choices.All(choice => !string.Equals(choice.Value, held, StringComparison.Ordinal)))
        {
            writeBack(held);
            return true;
        }

        _comboValue[key] = next;
        return false;
    }

    /// <summary>
    /// True when a hand edit should block opening another record. An untouched form,
    /// including one that only looks dirty because it was opened, does not block.
    /// </summary>
    protected bool HasUnsavedEdits(string message)
    {
        RecalculateDirty();
        if (!IsDirty)
        {
            ShowUnsavedBanner = false;
            return false;
        }

        ShowUnsavedBanner = true;
        EditorMessage = message;
        return true;
    }

    private bool CanCreate() => AllowCreate && IsReady && Client is not null;

    private bool CanSave() => IsReady && !IsEditorBusy && IsDirty;

    private bool CanPostJournal() =>
        IsReady && !IsEditorBusy && !IsNew && !string.IsNullOrWhiteSpace(JournalText) && !string.IsNullOrEmpty(EditorSysId);

    private bool CanConfirmResolve() =>
        IsReady && !IsEditorBusy && HasEditor && !IsNew && !string.IsNullOrWhiteSpace(ResolveCode) && !string.IsNullOrWhiteSpace(ResolveNotes);

    private void NotifyCommands()
    {
        SaveCommand.NotifyCanExecuteChanged();
        PostJournalCommand.NotifyCanExecuteChanged();
        ConfirmResolveCommand.NotifyCanExecuteChanged();
        NewRecordCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedChanged(TicketRow? value)
    {
        OnPropertyChanged(nameof(ShowsBlankTicketForm));
        if (_suppressSelection)
            return;
        if (value is null)
        {
            if (OpensOnBlankForm)
                ShowBlankTicket();
            return;
        }
        if (!IsNew && value.SysId == EditorSysId)
            return;
        if (HasUnsavedEdits("Save or discard unsaved changes before opening another record."))
        {
            _suppressSelection = true;
            Selected = _boundRow;
            _suppressSelection = false;
            return;
        }

        QueueOpen(value.SysId);
    }

    partial void OnPresetChanged(PresetOption? value)
    {
        if (Client is null || Applying || value is null)
            return;
        if (TryPaintCachedPreset?.Invoke(value) == true)
            return;
        _ = RefreshAsync();
    }

    partial void OnShortDescriptionChanged(string value) => Touch();
    partial void OnDescriptionChanged(string value) => Touch();

    partial void OnJournalTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasJournalText));
        if (!Applying)
            RecalculateDirty();
        PostJournalCommand.NotifyCanExecuteChanged();
    }

    partial void OnJournalKindChanged(JournalKind value) => JournalIsCustomerVisible = value == JournalKind.Comments;

    partial void OnIsDirtyChanged(bool value) => NotifyCommands();
    partial void OnIsEditorBusyChanged(bool value) => NotifyCommands();
    partial void OnIsReadyChanged(bool value) => NotifyCommands();
    partial void OnIsNewChanged(bool value)
    {
        NotifyCommands();
        OnPropertyChanged(nameof(ShowsBlankTicketForm));
    }
    partial void OnResolveCodeChanged(string value) => ConfirmResolveCommand.NotifyCanExecuteChanged();
    partial void OnResolveNotesChanged(string value) => ConfirmResolveCommand.NotifyCanExecuteChanged();
    partial void OnHasEditorChanged(bool value)
    {
        NotifyCommands();
        OnPropertyChanged(nameof(ShowsBlankTicketForm));
        OnPropertyChanged(nameof(ShowsSelectPrompt));
    }
}

public sealed record SavedTicketFields(string SysId, string ShortDescription, string StateLabel, string? AssigneeName, string? AssigneeId);

/// <summary>One cache population fetched without touching the list on screen.</summary>
public readonly record struct PopulationFetch(
    bool Skipped,
    bool Failed,
    string SkipOrError,
    TicketRow[] Rows,
    int Total,
    string EncodedQuery,
    string FilterSummary,
    bool FilteredByRegion)
{
    public static PopulationFetch Skip(string reason, string summary, bool filteredByRegion) =>
        new(true, false, reason ?? "", [], 0, "", summary ?? "", filteredByRegion);

    public static PopulationFetch Fail(string error) =>
        new(false, true, error ?? "", [], 0, "", "", false);

    public static PopulationFetch Loaded(TicketRow[] rows, int total, string encodedQuery, string summary, bool filteredByRegion) =>
        new(false, false, "", rows ?? [], total, encodedQuery ?? "", summary ?? "", filteredByRegion);
}
