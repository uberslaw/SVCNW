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
    private readonly HashSet<string> _selectedStates = new(StringComparer.OrdinalIgnoreCase);

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
        OnPropertyChanged(nameof(HasGrouping));
        OnPropertyChanged(nameof(SelectedStates));
        PresentationChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RunAsync(IServiceNowClient? client, string? text)
    {
        var version = ++_runVersion;
        _client = client;
        if (client is null)
        {
            Summary = "Connect to ServiceNow to search.";
            return;
        }

        var trimmed = (text ?? "").Trim();
        if (!TryOpenedRange(out var openedFrom, out var openedTo))
        {
            if (version != _runVersion)
                return;
            Query = trimmed;
            ReplaceHits([]);
            ErrorMessage = "";
            Summary = FilterMessage;
            Remember(trimmed);
            return;
        }

        var groupId = AssignmentGroup.SysId;
        var assigneeId = Assignee.SysId;
        var filtersActive = EncodedQuery.HasRecordFilter(groupId, assigneeId, openedFrom, openedTo);
        if (trimmed.Length < 2 && !EncodedQuery.IsNumberQuery(trimmed) && !filtersActive)
        {
            if (version != _runVersion)
                return;
            Query = trimmed;
            ReplaceHits([]);
            Summary = "Type at least 2 characters. Numbers such as INC0010001, IMS0010001, or KB0001001 can be shorter.";
            Remember(trimmed);
            return;
        }

        if (!IncludeIncidents && !IncludeRequests && !IncludeItems && !IncludeKnowledge && !IncludeWalkUps)
        {
            if (version != _runVersion)
                return;
            Query = trimmed;
            ReplaceHits([]);
            Summary = "Choose at least one record type.";
            Remember(trimmed);
            return;
        }

        try
        {
            IsLoading = true;
            ErrorMessage = "";
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
            var statesTask = LoadStateOptionsAsync(client, CancellationToken.None);

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

            MergeHitStates(hits);
            Query = trimmed;
            _resultSummary = hits.Count == 1 ? "1 match" : hits.Count + " matches";
            if (hideKnowledge)
                _resultSummary += ". " + KnowledgeHiddenSummary;
            ErrorMessage = string.Join(Environment.NewLine, errors);
            ReplaceHits(hits);
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

    private void OnReferenceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReferenceFieldModel.SysId))
            NotifyFilters();
    }

    private void NotifyFilters()
    {
        if (_suppressFilter)
            return;
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

    private async Task LoadStateOptionsAsync(IServiceNowClient client, CancellationToken cancellationToken)
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

        StateOptions.Clear();
        foreach (var label in ordered)
            StateOptions.Add(label);

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

    private void ReplaceHits(IReadOnlyList<SearchHit> hits)
    {
        _hits.Clear();
        _hits.AddRange(hits);
        ApplyPresentation();
    }

    private void ApplyPresentation()
    {
        IEnumerable<SearchHit> rows = _hits;
        if (_selectedStates.Count > 0)
            rows = rows.Where(hit => _selectedStates.Contains(hit.StateLabel));

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
        PresentationChanged?.Invoke(this, EventArgs.Empty);
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
