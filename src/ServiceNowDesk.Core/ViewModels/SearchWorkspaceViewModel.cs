using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceNowDesk.Alerts;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Query;

namespace ServiceNowDesk.ViewModels;

public sealed partial class SearchWorkspaceViewModel : ObservableObject
{
    public const string KnowledgeHiddenSummary = "Knowledge is hidden while assignment or opened-date filters are set.";
    public const string SortType = "Type";
    public const string SortNumber = "Number";
    public const string SortTitle = "Title";
    public const string SortMeta = "Meta";
    public const string SortState = "State";
    public const string SortWhen = "When";

    private IServiceNowClient? _client;
    private int _runVersion;
    private bool _resultsCurrent;
    private bool _suppressFilter;
    private string _signature = "";
    private string _resultSummary = "";
    private readonly List<SearchHit> _hits = [];
    private readonly List<SearchCacheEntry> _local = [];
    private readonly List<SearchHit> _remoteOnly = [];
    private readonly HashSet<string> _selectedStates = new(StringComparer.OrdinalIgnoreCase);
    private string _filterText = "";

    [ObservableProperty] private SearchHit? selected;
    [ObservableProperty] private bool includeIncidents = true;
    [ObservableProperty] private bool includeRequests = true;
    [ObservableProperty] private bool includeItems = true;
    [ObservableProperty] private bool includeKnowledge = true;
    [ObservableProperty] private bool includeWalkUps = true;
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private string summary = "Search incidents, requests, items, walk-ups, and knowledge articles.";
    [ObservableProperty] private string errorMessage = "";
    [ObservableProperty] private string filterMessage = "";
    [ObservableProperty] private DateTime? openedFrom;
    [ObservableProperty] private DateTime? openedTo;
    [ObservableProperty] private string sortColumn = SortWhen;
    [ObservableProperty] private bool sortAscending;
    [ObservableProperty] private string groupColumn = "";
    [ObservableProperty] private string stateFilterSummary = "All states";

    public int CachedCount { get; private set; }

    public int DisplayedCount { get; private set; }

    public string CacheStatus { get; private set; } = "0 cached · 0 shown";

    /// <summary>
    /// Test hook that runs after cache rows are on screen and before the ServiceNow calls.
    /// </summary>
    internal Func<Task>? WebSearchGate { get; set; }

    public ObservableCollection<SearchHit> Results { get; } = [];
    public ObservableCollection<string> StateOptions { get; } = [];
    public ReferenceFieldModel AssignmentGroup { get; }
    public ReferenceFieldModel Assignee { get; }

    public event EventHandler? SearchFiltersChanged;
    public event EventHandler? PresentationChanged;

    public SearchWorkspaceViewModel()
    {
        AssignmentGroup = new ReferenceFieldModel(SearchGroupsAsync);
        Assignee = new ReferenceFieldModel(SearchUsersAsync, match: MatchUsersAsync);
        AssignmentGroup.PropertyChanged += OnReferenceChanged;
        Assignee.PropertyChanged += OnReferenceChanged;
    }

    public Action<SearchHit>? PrepareHit { get; set; }

    public string Query { get; private set; } = "";

    public IReadOnlyCollection<string> SelectedStates => _selectedStates;

    public bool IsStateSelected(string? label) =>
        !string.IsNullOrWhiteSpace(label) && _selectedStates.Contains(label.Trim());

    public bool HasGrouping => !string.IsNullOrWhiteSpace(GroupColumn);

    public void Attach(IServiceNowClient? client) => _client = client;

    public event EventHandler<SearchHit>? OpenRequested;

    /// <summary>Raised when a search includes knowledge, so the desk can refresh the saved article list.</summary>
    public event EventHandler? KnowledgeSearchRequested;

    public bool HasCurrentResultsFor(string? text) =>
        _resultsCurrent && string.Equals(_signature, Signature((text ?? "").Trim()), StringComparison.Ordinal);

    public void MarkStale() => _resultsCurrent = false;

    public void Reset()
    {
        _runVersion++;
        _suppressFilter = true;
        _resultsCurrent = false;
        _signature = "";
        Query = "";
        AssignmentGroup.Clear();
        Assignee.Clear();
        OpenedFrom = null;
        OpenedTo = null;
        FilterMessage = "";
        _hits.Clear();
        _local.Clear();
        _remoteOnly.Clear();
        _filterText = "";
        _selectedStates.Clear();
        StateOptions.Clear();
        Results.Clear();
        Selected = null;
        SortColumn = SortWhen;
        SortAscending = false;
        GroupColumn = "";
        StateFilterSummary = "All states";
        _resultSummary = "";
        Summary = "Search incidents, requests, items, walk-ups, and knowledge articles.";
        ErrorMessage = "";
        IsLoading = false;
        _suppressFilter = false;
        PublishCounts();
        OnPropertyChanged(nameof(HasGrouping));
        OnPropertyChanged(nameof(SelectedStates));
        PresentationChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Replaces the ticket and knowledge population Search filters locally.
    /// Does not start a ServiceNow query.
    /// </summary>
    public void SetLocalCache(IEnumerable<SearchCacheEntry>? entries)
    {
        _local.Clear();
        if (entries is not null)
        {
            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.SysId))
                    continue;
                _local.Add(entry);
            }
        }

        SeedStateOptionsFromCache();
        ApplyCachedFilter(_filterText);
    }

    /// <summary>
    /// Filters the local cache immediately and drops any in-flight web search.
    /// </summary>
    public void ShowCached(string? text)
    {
        _runVersion++;
        _resultsCurrent = false;
        _remoteOnly.Clear();
        IsLoading = false;
        ApplyCachedFilter(text);
    }

    public async Task RunAsync(IServiceNowClient? client, string? text)
    {
        var version = ++_runVersion;
        _client = client;
        var trimmed = (text ?? "").Trim();
        _remoteOnly.Clear();
        if (client is null)
        {
            ApplyCachedFilter(trimmed);
            if (Results.Count == 0)
                Summary = "Connect to ServiceNow to search.";
            IsLoading = false;
            return;
        }

        if (!TryOpenedRange(out var openedFrom, out var openedTo))
        {
            if (version != _runVersion)
                return;
            _resultSummary = "";
            ReplaceHits([]);
            Query = trimmed;
            ErrorMessage = "";
            Summary = FilterMessage;
            Remember(trimmed);
            IsLoading = false;
            return;
        }

        var groupId = AssignmentGroup.SysId;
        var assigneeId = Assignee.SysId;
        var filtersActive = EncodedQuery.HasRecordFilter(groupId, assigneeId, openedFrom, openedTo);
        var willWeb = WillSearchWeb(trimmed, filtersActive);
        if (willWeb)
            IsLoading = true;

        // Cache rows land before any await so the grid is not blank while ServiceNow runs.
        ApplyCachedFilter(trimmed);
        if (version != _runVersion)
            return;

        if (!AnyTypeSelected())
        {
            _resultSummary = "Choose at least one record type.";
            Summary = _resultSummary;
            Remember(trimmed);
            IsLoading = false;
            return;
        }

        if (!willWeb)
        {
            if (_hits.Count == 0
                && _local.Count == 0
                && trimmed.Length < 2
                && !EncodedQuery.IsNumberQuery(trimmed)
                && !filtersActive)
            {
                _resultSummary = "Type at least 2 characters. Numbers such as INC0010001, IMS0010001, or KB0001001 can be shorter.";
                Summary = _resultSummary;
            }

            Remember(trimmed);
            IsLoading = false;
            return;
        }

        try
        {
            ErrorMessage = "";
            if (WebSearchGate is not null)
                await WebSearchGate().ConfigureAwait(true);
            if (version != _runVersion)
                return;

            var kind = EncodedQuery.SectionForNumber(trimmed);
            var query = new TicketQuery
            {
                Text = trimmed,
                Activity = ActivityFilter.Any,
                Assignment = AssignmentScope.Any,
                AssignmentGroupId = groupId,
                AssignedToId = assigneeId,
                OpenedFrom = openedFrom,
                OpenedTo = openedTo,
                Limit = 25
            };

            var hideKnowledge = filtersActive && IncludeKnowledge && kind is null or DeskSection.Knowledge;
            var includeArticles = (kind is null or DeskSection.Knowledge) && IncludeKnowledge && !hideKnowledge;
            if (includeArticles)
                KnowledgeSearchRequested?.Invoke(this, EventArgs.Empty);

            var errors = new List<string>();
            // Each table is on its own so one bad ServiceNow payload does not blank the page.
            var incidentTask = (kind is null or DeskSection.Incidents) && IncludeIncidents
                ? LoadTableAsync(version, "Incidents", errors, () => client.SearchIncidentsAsync(query, CancellationToken.None))
                : Task.FromResult(new PagedResult<IncidentRecord>([], 0));
            var requestTask = (kind is null or DeskSection.Requests) && IncludeRequests
                ? LoadTableAsync(version, "Requests", errors, () => client.SearchRequestsAsync(query, CancellationToken.None))
                : Task.FromResult(new PagedResult<RequestRecord>([], 0));
            var itemTask = (kind is null or DeskSection.RequestedItems) && IncludeItems
                ? LoadTableAsync(version, "Request items", errors, () => client.SearchRequestedItemsAsync(query, CancellationToken.None))
                : Task.FromResult(new PagedResult<RequestedItemRecord>([], 0));
            var articleTask = includeArticles
                ? LoadTableAsync(version, "Knowledge", errors, () => client.SearchKnowledgeAsync(query, CancellationToken.None))
                : Task.FromResult(new PagedResult<KnowledgeArticle>([], 0));
            var walkTask = (kind is null or DeskSection.WalkUps) && IncludeWalkUps
                ? LoadTableAsync(version, "Walk-ups", errors, () => client.SearchInteractionsAsync(query, CancellationToken.None))
                : Task.FromResult(new PagedResult<InteractionRecord>([], 0));
            var statesTask = LoadStateOptionsAsync(version, client, CancellationToken.None);

            await Task.WhenAll(incidentTask, requestTask, itemTask, articleTask, walkTask, statesTask);
            if (version != _runVersion)
                return;

            var hits = new List<SearchHit>();
            hits.AddRange(incidentTask.Result.Items.Select(record => new SearchHit
            {
                Section = DeskSection.Incidents,
                TableLabel = "Incident",
                SysId = record.SysId,
                Number = record.Number,
                Title = record.ShortDescription,
                StateLabel = record.StateLabel,
                Tone = StateTone.ForIncident(record.State),
                Meta = record.Caller.Display,
                When = record.UpdatedAtDisplay,
                SortKey = record.UpdatedAtValue,
                Unassigned = record.AssignedTo.IsEmpty
            }));
            hits.AddRange(requestTask.Result.Items.Select(record => new SearchHit
            {
                Section = DeskSection.Requests,
                TableLabel = "Request",
                SysId = record.SysId,
                Number = record.Number,
                Title = record.ShortDescription,
                StateLabel = record.RequestStateLabel,
                Tone = StateTone.ForRequest(record.RequestState),
                Meta = record.RequestedFor.Display,
                When = record.UpdatedAtDisplay,
                SortKey = record.UpdatedAtValue
            }));
            hits.AddRange(itemTask.Result.Items.Select(record => new SearchHit
            {
                Section = DeskSection.RequestedItems,
                TableLabel = "Request item",
                SysId = record.SysId,
                Number = record.Number,
                Title = record.ShortDescription,
                StateLabel = record.StateLabel,
                Tone = StateTone.ForItem(record.State),
                Meta = record.CatalogItem.Display,
                When = record.UpdatedAtDisplay,
                SortKey = record.UpdatedAtValue,
                Unassigned = record.AssignedTo.IsEmpty
            }));
            hits.AddRange(walkTask.Result.Items.Select(record => new SearchHit
            {
                Section = DeskSection.WalkUps,
                TableLabel = "Walk-up",
                SysId = record.SysId,
                Number = record.Number,
                Title = record.ShortDescription,
                StateLabel = record.StateLabel,
                Tone = StateTone.ForInteraction(record.State),
                Meta = record.OpenedFor.Display,
                When = record.UpdatedAtDisplay,
                SortKey = record.UpdatedAtValue,
                Unassigned = record.AssignedTo.IsEmpty
            }));
            hits.AddRange(articleTask.Result.Items.Select(record => new SearchHit
            {
                Section = DeskSection.Knowledge,
                TableLabel = "Knowledge",
                SysId = record.SysId,
                Number = record.Number,
                Title = record.ShortDescription,
                StateLabel = string.IsNullOrWhiteSpace(record.WorkflowStateLabel) ? record.WorkflowState : record.WorkflowStateLabel,
                Tone = StateTone.ForKnowledge(record.WorkflowState),
                Meta = JoinMeta(record.Topic, record.KnowledgeBase, record.Author.Display),
                When = record.UpdatedAtDisplay,
                SortKey = record.UpdatedAtValue
            }));

            if (version != _runVersion)
                return;

            MergeHitStates(hits);
            AbsorbRemote(hits);
            ErrorMessage = string.Join(Environment.NewLine, errors);
            // Publish hits before clearing IsLoading so a ListBox SelectionChanged from
            // StateOptions rebuild cannot filter an empty _hits list down to 0 rows.
            Remember(trimmed);
            if (errors.Count > 0)
                _resultsCurrent = false;
        }
        catch (Exception ex)
        {
            if (version == _runVersion)
            {
                ErrorMessage = WorkspaceMessages.Describe(ex);
                _resultsCurrent = false;
            }
        }
        finally
        {
            if (version == _runVersion)
                IsLoading = false;
        }
    }

    private bool WillSearchWeb(string trimmed, bool filtersActive) =>
        AnyTypeSelected()
        && (trimmed.Length >= 2 || EncodedQuery.IsNumberQuery(trimmed) || filtersActive);

    private bool AnyTypeSelected() =>
        IncludeIncidents || IncludeRequests || IncludeItems || IncludeKnowledge || IncludeWalkUps;

    private void ApplyCachedFilter(string? text)
    {
        _filterText = (text ?? "").Trim();
        Query = _filterText;
        if (!TryOpenedRange(out var openedFrom, out var openedTo) || !AnyTypeSelected())
        {
            _resultSummary = "";
            ReplaceHits([]);
            return;
        }

        var hits = new List<SearchHit>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _local)
        {
            if (!CacheEntryMatches(entry, _filterText, openedFrom, openedTo))
                continue;
            var hit = ToHit(entry);
            if (!seen.Add(HitKey(hit)))
                continue;
            hits.Add(hit);
        }

        foreach (var remote in _remoteOnly)
        {
            if (!RemoteStillVisible(remote, _filterText))
                continue;
            if (!seen.Add(HitKey(remote)))
                continue;
            hits.Add(remote);
        }

        _resultSummary = hits.Count == 1 ? "1 match" : hits.Count + " matches";
        if (KnowledgeHidden(_filterText))
            _resultSummary += ". " + KnowledgeHiddenSummary;
        ReplaceHits(hits);
    }

    private void AbsorbRemote(IReadOnlyList<SearchHit> remote)
    {
        if (!TryOpenedRange(out var openedFrom, out var openedTo))
            return;

        var localKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _local)
        {
            if (CacheEntryMatches(entry, _filterText, openedFrom, openedTo))
                localKeys.Add(HitKey(entry.Section, entry.SysId, entry.Number));
        }

        foreach (var hit in remote)
        {
            var key = HitKey(hit);
            if (localKeys.Contains(key))
                continue;
            var existing = -1;
            for (var i = 0; i < _remoteOnly.Count; i++)
            {
                if (string.Equals(HitKey(_remoteOnly[i]), key, StringComparison.OrdinalIgnoreCase))
                {
                    existing = i;
                    break;
                }
            }

            if (existing >= 0)
                _remoteOnly[existing] = hit;
            else
                _remoteOnly.Add(hit);
        }

        ApplyCachedFilter(_filterText);
    }

    private bool CacheEntryMatches(SearchCacheEntry entry, string text, string? openedFrom, string? openedTo)
    {
        if (!SectionIncluded(entry.Section, text))
            return false;
        if (!EncodedQuery.Matches(text, entry.Number, CacheFields(entry)))
            return false;
        if (!PassesGroup(entry))
            return false;
        if (!PassesAssignee(entry))
            return false;
        return EncodedQuery.OpenedInRange(openedFrom, openedTo, entry.OpenedAtDisplay);
    }

    private bool RemoteStillVisible(SearchHit hit, string text) =>
        SectionIncluded(hit.Section, text);

    private bool SectionIncluded(DeskSection section, string text)
    {
        var kind = EncodedQuery.SectionForNumber(text);
        if (kind is not null && section != kind)
            return false;
        return section switch
        {
            DeskSection.Incidents => IncludeIncidents,
            DeskSection.Requests => IncludeRequests,
            DeskSection.RequestedItems => IncludeItems,
            DeskSection.WalkUps => IncludeWalkUps,
            DeskSection.Knowledge => IncludeKnowledge && !KnowledgeHidden(text),
            _ => false
        };
    }

    private bool KnowledgeHidden(string text)
    {
        if (!IncludeKnowledge || !TryOpenedRange(out var openedFrom, out var openedTo))
            return false;
        if (!EncodedQuery.HasRecordFilter(AssignmentGroup.SysId, Assignee.SysId, openedFrom, openedTo))
            return false;
        var kind = EncodedQuery.SectionForNumber(text);
        return kind is null or DeskSection.Knowledge;
    }

    private bool PassesGroup(SearchCacheEntry entry)
    {
        var wanted = AssignmentGroup.SysId;
        if (string.IsNullOrWhiteSpace(wanted))
            return true;
        if (!string.IsNullOrWhiteSpace(entry.AssignmentGroupId))
            return EncodedQuery.SameReference(wanted, entry.AssignmentGroupId);

        var label = AssignmentGroup.Text.Trim();
        if (label.Length == 0)
            return false;
        if (string.Equals(entry.AssignmentGroupDisplay.Trim(), label, StringComparison.OrdinalIgnoreCase))
            return true;
        return MetaHasLabel(entry.Meta, label);
    }

    private bool PassesAssignee(SearchCacheEntry entry)
    {
        var wanted = Assignee.SysId;
        if (string.IsNullOrWhiteSpace(wanted))
            return true;
        if (!string.IsNullOrWhiteSpace(entry.AssigneeId))
            return EncodedQuery.SameReference(wanted, entry.AssigneeId);
        if (entry.Unassigned)
            return false;

        var label = Assignee.Text.Trim();
        if (label.Length == 0)
            return false;
        return string.Equals(entry.AssigneeDisplay.Trim(), label, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MetaHasLabel(string? meta, string label)
    {
        if (string.IsNullOrWhiteSpace(meta))
            return false;
        foreach (var part in meta.Split('·'))
        {
            if (string.Equals(part.Trim(), label, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static IEnumerable<string> CacheFields(SearchCacheEntry entry)
    {
        yield return entry.Title;
        yield return entry.Meta;
        yield return entry.StateLabel;
        yield return entry.AssignmentGroupDisplay;
        yield return entry.AssigneeDisplay;
    }

    private static SearchHit ToHit(SearchCacheEntry entry) => new()
    {
        Section = entry.Section,
        TableLabel = TableLabelFor(entry.Section),
        SysId = entry.SysId,
        Number = entry.Number ?? "",
        Title = entry.Title ?? "",
        StateLabel = entry.StateLabel ?? "",
        Tone = string.IsNullOrWhiteSpace(entry.Tone) ? "open" : entry.Tone,
        Meta = entry.Meta ?? "",
        When = entry.When ?? "",
        SortKey = string.IsNullOrWhiteSpace(entry.SortKey) ? entry.When ?? "" : entry.SortKey,
        Unassigned = entry.Unassigned
    };

    private static string TableLabelFor(DeskSection section) => section switch
    {
        DeskSection.Incidents => "Incident",
        DeskSection.Requests => "Request",
        DeskSection.RequestedItems => "Request item",
        DeskSection.WalkUps => "Walk-up",
        DeskSection.Knowledge => "Knowledge",
        _ => "Record"
    };

    private static string HitKey(SearchHit hit) => HitKey(hit.Section, hit.SysId, hit.Number);

    private static string HitKey(DeskSection section, string? sysId, string? number)
    {
        var id = string.IsNullOrWhiteSpace(sysId) ? (number ?? "").Trim() : sysId.Trim();
        return section.ToString() + ":" + id;
    }

    private void SeedStateOptionsFromCache()
    {
        var labels = new HashSet<string>(StateOptions, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _local)
        {
            if (!string.IsNullOrWhiteSpace(entry.StateLabel))
                labels.Add(entry.StateLabel.Trim());
        }

        ReplaceStateOptions(labels);
    }

    public void SetSelectedStates(IEnumerable<string>? labels)
    {
        _selectedStates.Clear();
        if (labels is not null)
        {
            foreach (var label in labels)
            {
                if (!string.IsNullOrWhiteSpace(label))
                    _selectedStates.Add(label.Trim());
            }
        }

        PruneSelectedStates();
        UpdateStateFilterSummary();
        OnPropertyChanged(nameof(SelectedStates));
        ApplyPresentation();
    }

    public void ClearStateFilter() => SetSelectedStates(null);

    public void ToggleSort(string column)
    {
        var key = NormalizeColumn(column);
        if (string.IsNullOrEmpty(key))
            return;

        if (string.Equals(SortColumn, key, StringComparison.Ordinal))
            SortAscending = !SortAscending;
        else
        {
            SortColumn = key;
            SortAscending = string.Equals(key, SortNumber, StringComparison.Ordinal)
                || string.Equals(key, SortTitle, StringComparison.Ordinal)
                || string.Equals(key, SortType, StringComparison.Ordinal)
                || string.Equals(key, SortMeta, StringComparison.Ordinal)
                || string.Equals(key, SortState, StringComparison.Ordinal);
        }

        ApplyPresentation();
    }

    public void GroupBy(string column)
    {
        var key = NormalizeColumn(column);
        if (string.IsNullOrEmpty(key))
            return;

        GroupColumn = string.Equals(GroupColumn, key, StringComparison.Ordinal) ? "" : key;
        OnPropertyChanged(nameof(HasGrouping));
        ApplyPresentation();
        PresentationChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ClearGrouping()
    {
        if (string.IsNullOrEmpty(GroupColumn))
            return;
        GroupColumn = "";
        OnPropertyChanged(nameof(HasGrouping));
        ApplyPresentation();
        PresentationChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void OpenSelected()
    {
        if (Selected is not null)
            OpenRequested?.Invoke(this, Selected);
    }

    partial void OnOpenedFromChanged(DateTime? value) => NotifyFilters();

    partial void OnOpenedToChanged(DateTime? value) => NotifyFilters();

    partial void OnIncludeIncidentsChanged(bool value) => ShowTypeFilter();

    partial void OnIncludeRequestsChanged(bool value) => ShowTypeFilter();

    partial void OnIncludeItemsChanged(bool value) => ShowTypeFilter();

    partial void OnIncludeKnowledgeChanged(bool value) => ShowTypeFilter();

    partial void OnIncludeWalkUpsChanged(bool value) => ShowTypeFilter();

    private void ShowTypeFilter()
    {
        if (_suppressFilter)
            return;
        ShowCached(_filterText);
    }

    private void OnReferenceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReferenceFieldModel.SysId))
            NotifyFilters();
    }

    private void NotifyFilters()
    {
        if (_suppressFilter)
            return;
        ShowCached(_filterText);
        SearchFiltersChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool TryOpenedRange(out string? from, out string? to)
    {
        from = OpenedFrom is DateTime start ? start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
        to = OpenedTo is DateTime end ? end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
        if (OpenedFrom is DateTime openedFrom && OpenedTo is DateTime openedTo && openedFrom.Date > openedTo.Date)
        {
            FilterMessage = "Opened from is after opened to.";
            return false;
        }

        FilterMessage = "";
        return true;
    }

    private void Remember(string text)
    {
        _signature = Signature(text);
        _resultsCurrent = true;
    }

    private async Task<PagedResult<T>> LoadTableAsync<T>(
        int version,
        string label,
        List<string> errors,
        Func<Task<PagedResult<T>>> load)
    {
        try
        {
            return await load().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (version == _runVersion)
                errors.Add(label + ": " + WorkspaceMessages.Describe(ex));
            return new PagedResult<T>([], 0);
        }
    }

    private async Task LoadStateOptionsAsync(int version, IServiceNowClient client, CancellationToken cancellationToken)
    {
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (IncludeIncidents)
            AddChoiceLabels(labels, await SafeChoicesAsync(client, "incident", "state", cancellationToken).ConfigureAwait(true));
        if (IncludeRequests)
            AddChoiceLabels(labels, await SafeChoicesAsync(client, "sc_request", "request_state", cancellationToken).ConfigureAwait(true));
        if (IncludeItems)
            AddChoiceLabels(labels, await SafeChoicesAsync(client, "sc_req_item", "state", cancellationToken).ConfigureAwait(true));
        if (IncludeWalkUps)
            AddChoiceLabels(labels, await SafeChoicesAsync(client, "interaction", "state", cancellationToken).ConfigureAwait(true));
        if (IncludeKnowledge)
            AddChoiceLabels(labels, await SafeChoicesAsync(client, "kb_knowledge", "workflow_state", cancellationToken).ConfigureAwait(true));

        // Stale searches must not wipe StateOptions / selection while a newer run is in flight.
        if (version != _runVersion)
            return;
        ReplaceStateOptions(labels);
    }

    private static async Task<IReadOnlyList<Choice>> SafeChoicesAsync(
        IServiceNowClient client,
        string table,
        string element,
        CancellationToken cancellationToken)
    {
        try
        {
            var choices = await client.GetChoicesAsync(table, element, null, cancellationToken).ConfigureAwait(true);
            if (choices.Count > 0)
                return choices;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Choice catalog is presentation-only; search hits still load.
        }

        return DefaultChoices.For(table, element);
    }

    private void MergeHitStates(IEnumerable<SearchHit> hits)
    {
        var labels = new HashSet<string>(StateOptions, StringComparer.OrdinalIgnoreCase);
        foreach (var hit in hits)
        {
            if (!string.IsNullOrWhiteSpace(hit.StateLabel))
                labels.Add(hit.StateLabel.Trim());
        }

        ReplaceStateOptions(labels);
    }

    private void ReplaceStateOptions(IEnumerable<string> labels)
    {
        var ordered = labels
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Select(label => label.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(label => label, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (SameStateOptions(ordered))
            return;

        for (var i = StateOptions.Count - 1; i >= 0; i--)
        {
            if (!ordered.Contains(StateOptions[i], StringComparer.OrdinalIgnoreCase))
                StateOptions.RemoveAt(i);
        }

        for (var i = 0; i < ordered.Length; i++)
        {
            var label = ordered[i];
            var existing = -1;
            for (var j = 0; j < StateOptions.Count; j++)
            {
                if (string.Equals(StateOptions[j], label, StringComparison.OrdinalIgnoreCase))
                {
                    existing = j;
                    break;
                }
            }

            if (existing < 0)
                StateOptions.Insert(Math.Min(i, StateOptions.Count), label);
            else if (existing != i)
                StateOptions.Move(existing, Math.Min(i, StateOptions.Count - 1));
        }

        PruneSelectedStates();
        UpdateStateFilterSummary();
        OnPropertyChanged(nameof(SelectedStates));
    }

    private void PruneSelectedStates()
    {
        if (_selectedStates.Count == 0 || StateOptions.Count == 0)
            return;

        var allowed = new HashSet<string>(StateOptions, StringComparer.OrdinalIgnoreCase);
        _selectedStates.RemoveWhere(label => !allowed.Contains(label));
    }

    private void UpdateStateFilterSummary()
    {
        StateFilterSummary = _selectedStates.Count == 0
            ? "All states"
            : _selectedStates.Count == 1
                ? _selectedStates.First()
                : _selectedStates.Count + " states";
    }

    private bool SameStateOptions(IReadOnlyList<string> ordered)
    {
        if (ordered.Count != StateOptions.Count)
            return false;
        for (var i = 0; i < ordered.Count; i++)
        {
            if (!string.Equals(ordered[i], StateOptions[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private void ReplaceHits(IReadOnlyList<SearchHit> hits)
    {
        var previous = new Dictionary<string, SearchHit>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in _hits)
            previous.TryAdd(HitKey(hit), hit);

        _hits.Clear();
        foreach (var hit in hits)
        {
            var key = HitKey(hit);
            if (previous.TryGetValue(key, out var existing) && SameHit(existing, hit))
                _hits.Add(existing);
            else
                _hits.Add(hit);
        }

        ApplyPresentation();
    }

    private static bool SameHit(SearchHit left, SearchHit right) =>
        left.Section == right.Section
        && string.Equals(left.TableLabel, right.TableLabel, StringComparison.Ordinal)
        && string.Equals(left.Number, right.Number, StringComparison.Ordinal)
        && string.Equals(left.Title, right.Title, StringComparison.Ordinal)
        && string.Equals(left.StateLabel, right.StateLabel, StringComparison.Ordinal)
        && string.Equals(left.Tone, right.Tone, StringComparison.Ordinal)
        && string.Equals(left.Meta, right.Meta, StringComparison.Ordinal)
        && string.Equals(left.When, right.When, StringComparison.Ordinal)
        && string.Equals(left.SortKey, right.SortKey, StringComparison.Ordinal)
        && left.Unassigned == right.Unassigned;

    private void ApplyPresentation()
    {
        IEnumerable<SearchHit> rows = _hits;
        if (_selectedStates.Count > 0)
        {
            rows = rows.Where(hit =>
                !string.IsNullOrWhiteSpace(hit.StateLabel)
                && _selectedStates.Contains(hit.StateLabel.Trim()));
        }

        rows = OrderHits(rows);

        var previous = Selected?.SysId;
        Results.Clear();
        foreach (var hit in rows)
        {
            PrepareHit?.Invoke(hit);
            Results.Add(hit);
        }

        Selected = previous is null ? null : Results.FirstOrDefault(hit => hit.SysId == previous);
        UpdateSummary();
        PublishCounts();
        PresentationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void PublishCounts()
    {
        var cached = _local.Count;
        var shown = Results.Count;
        var status = cached.ToString(CultureInfo.InvariantCulture)
            + " cached · "
            + shown.ToString(CultureInfo.InvariantCulture)
            + " shown";
        if (CachedCount != cached)
        {
            CachedCount = cached;
            OnPropertyChanged(nameof(CachedCount));
        }

        if (DisplayedCount != shown)
        {
            DisplayedCount = shown;
            OnPropertyChanged(nameof(DisplayedCount));
        }

        if (!string.Equals(CacheStatus, status, StringComparison.Ordinal))
        {
            CacheStatus = status;
            OnPropertyChanged(nameof(CacheStatus));
        }
    }

    private IEnumerable<SearchHit> OrderHits(IEnumerable<SearchHit> rows)
    {
        var ordered = rows.ToList();
        if (ordered.Count <= 1)
            return ordered;

        var comparer = StringComparer.OrdinalIgnoreCase;
        Func<SearchHit, string> primary = SortKeyFor(SortColumn);
        Func<SearchHit, string> group = string.IsNullOrEmpty(GroupColumn)
            ? _ => ""
            : SortKeyFor(GroupColumn);

        IOrderedEnumerable<SearchHit> sorted = SortAscending
            ? ordered.OrderBy(group, comparer).ThenBy(primary, comparer).ThenBy(hit => hit.Number, comparer)
            : ordered.OrderBy(group, comparer).ThenByDescending(primary, comparer).ThenBy(hit => hit.Number, comparer);

        // Default "When" uses SortKey (sys_updated_on value) so order matches ServiceNow timestamps.
        if (string.Equals(SortColumn, SortWhen, StringComparison.Ordinal))
        {
            sorted = SortAscending
                ? ordered.OrderBy(group, comparer).ThenBy(hit => hit.SortKey, StringComparer.Ordinal).ThenBy(hit => hit.Number, comparer)
                : ordered.OrderBy(group, comparer).ThenByDescending(hit => hit.SortKey, StringComparer.Ordinal).ThenBy(hit => hit.Number, comparer);
        }

        return sorted;
    }

    private static Func<SearchHit, string> SortKeyFor(string column) => column switch
    {
        SortType => hit => hit.TableLabel,
        SortNumber => hit => hit.Number,
        SortTitle => hit => hit.Title,
        SortMeta => hit => hit.Meta,
        SortState => hit => hit.StateLabel,
        SortWhen => hit => hit.When,
        _ => hit => hit.SortKey
    };

    private static string NormalizeColumn(string? column) => (column ?? "").Trim() switch
    {
        SortType or "TableLabel" or "Type" => SortType,
        SortNumber or "Number" => SortNumber,
        SortTitle or "Title" => SortTitle,
        SortMeta or "Meta" => SortMeta,
        SortState or "StateLabel" or "State" => SortState,
        SortWhen or "When" or "Updated" => SortWhen,
        _ => ""
    };

    private void UpdateSummary()
    {
        if (_hits.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(_resultSummary))
                Summary = _resultSummary;
            return;
        }

        if (_selectedStates.Count == 0 || Results.Count == _hits.Count)
        {
            Summary = _resultSummary;
            return;
        }

        var shown = Results.Count == 1 ? "1" : Results.Count.ToString(CultureInfo.InvariantCulture);
        Summary = shown + " of " + _hits.Count.ToString(CultureInfo.InvariantCulture) + " matches";
        if (_resultSummary.Contains(KnowledgeHiddenSummary, StringComparison.Ordinal))
            Summary += ". " + KnowledgeHiddenSummary;
    }

    private string Signature(string text) =>
        text
        + "\n" + (AssignmentGroup.SysId ?? "")
        + "\n" + (Assignee.SysId ?? "")
        + "\n" + (OpenedFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "")
        + "\n" + (OpenedTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "")
        + "\n" + IncludeIncidents + IncludeRequests + IncludeItems + IncludeKnowledge + IncludeWalkUps;

    private Task<IReadOnlyList<ReferenceSuggestion>> SearchGroupsAsync(string text, CancellationToken cancellationToken) =>
        _client is null
            ? Task.FromResult<IReadOnlyList<ReferenceSuggestion>>([])
            : _client.SearchGroupsAsync(text, cancellationToken);

    private Task<IReadOnlyList<ReferenceSuggestion>> SearchUsersAsync(string text, CancellationToken cancellationToken) =>
        _client is null
            ? Task.FromResult<IReadOnlyList<ReferenceSuggestion>>([])
            : _client.SearchUsersAsync(text, cancellationToken);

    private Task<IReadOnlyList<ReferenceSuggestion>> MatchUsersAsync(string text, CancellationToken cancellationToken) =>
        _client is null
            ? Task.FromResult<IReadOnlyList<ReferenceSuggestion>>([])
            : _client.MatchUsersAsync(text, cancellationToken);

    private static void AddChoiceLabels(HashSet<string> labels, IReadOnlyList<Choice> choices)
    {
        foreach (var choice in choices)
        {
            if (!string.IsNullOrWhiteSpace(choice.Label))
                labels.Add(choice.Label.Trim());
            else if (!string.IsNullOrWhiteSpace(choice.Value))
                labels.Add(choice.Value.Trim());
        }
    }

    private static string JoinMeta(params string[] parts) =>
        string.Join(" · ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
}

public sealed class SearchHit : IHighlightRow
{
    private string _highlightHex = "";

    public required DeskSection Section { get; init; }
    public required string TableLabel { get; init; }
    public required string SysId { get; init; }
    public required string Number { get; init; }
    public required string Title { get; init; }
    public required string StateLabel { get; init; }
    public required string Tone { get; init; }
    public required string Meta { get; init; }
    public required string When { get; init; }
    public required string SortKey { get; init; }
    public bool Unassigned { get; init; }

    public string HighlightHex
    {
        get => _highlightHex;
        set
        {
            var next = value ?? "";
            if (string.Equals(_highlightHex, next, StringComparison.Ordinal))
                return;
            _highlightHex = next;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HighlightHex)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// One locally cached ticket or knowledge article that Search can filter without a network call.
/// </summary>
public sealed class SearchCacheEntry
{
    public required DeskSection Section { get; init; }
    public required string SysId { get; init; }
    public string Number { get; init; } = "";
    public string Title { get; init; } = "";
    public string StateLabel { get; init; } = "";
    public string Tone { get; init; } = "";
    public string Meta { get; init; } = "";
    public string When { get; init; } = "";
    public string SortKey { get; init; } = "";
    public bool Unassigned { get; init; }
    public string AssignmentGroupId { get; init; } = "";
    public string AssignmentGroupDisplay { get; init; } = "";
    public string AssigneeId { get; init; } = "";
    public string AssigneeDisplay { get; init; } = "";
    public string OpenedAtDisplay { get; init; } = "";

    public static SearchCacheEntry FromCached(CachedTicketRow row, DeskSection section) => new()
    {
        Section = section,
        SysId = row.SysId,
        Number = row.Number ?? "",
        Title = row.Title ?? "",
        StateLabel = row.StateLabel ?? "",
        Tone = row.Tone ?? "",
        Meta = row.Meta ?? "",
        When = row.When ?? "",
        SortKey = string.IsNullOrWhiteSpace(row.SortKey) ? row.When ?? "" : row.SortKey,
        Unassigned = row.Unassigned,
        AssignmentGroupId = row.AssignmentGroupId ?? "",
        AssignmentGroupDisplay = row.AssignmentGroupDisplay ?? "",
        AssigneeId = row.AssigneeId ?? "",
        AssigneeDisplay = row.AssigneeDisplay ?? "",
        OpenedAtDisplay = row.OpenedAtDisplay ?? ""
    };
}
