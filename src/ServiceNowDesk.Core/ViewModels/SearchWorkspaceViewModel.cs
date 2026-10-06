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

    private IServiceNowClient? _client;
    private int _runVersion;
    private bool _resultsCurrent;
    private bool _suppressFilter;
    private string _signature = "";

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

    public ObservableCollection<SearchHit> Results { get; } = [];
    public ReferenceFieldModel AssignmentGroup { get; }
    public ReferenceFieldModel Assignee { get; }

    public event EventHandler? SearchFiltersChanged;

    public SearchWorkspaceViewModel()
    {
        AssignmentGroup = new ReferenceFieldModel(SearchGroupsAsync);
        Assignee = new ReferenceFieldModel(SearchUsersAsync, match: MatchUsersAsync);
        AssignmentGroup.PropertyChanged += OnReferenceChanged;
        Assignee.PropertyChanged += OnReferenceChanged;
    }

    public Action<SearchHit>? PrepareHit { get; set; }

    public string Query { get; private set; } = "";

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
        Results.Clear();
        Selected = null;
        Summary = "Search incidents, requests, items, walk-ups, and knowledge articles.";
        ErrorMessage = "";
        IsLoading = false;
        _suppressFilter = false;
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
            Results.Clear();
            Selected = null;
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
            Results.Clear();
            Selected = null;
            Summary = "Type at least 2 characters. Numbers such as INC0010001, IMS0010001, or KB0001001 can be shorter.";
            Remember(trimmed);
            return;
        }

        if (!IncludeIncidents && !IncludeRequests && !IncludeItems && !IncludeKnowledge && !IncludeWalkUps)
        {
            if (version != _runVersion)
                return;
            Query = trimmed;
            Results.Clear();
            Selected = null;
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

            var incidents = (kind is null or DeskSection.Incidents) && IncludeIncidents
                ? client.SearchIncidentsAsync(query, CancellationToken.None)
                : Task.FromResult(new PagedResult<IncidentRecord>([], 0));
            var requests = (kind is null or DeskSection.Requests) && IncludeRequests
                ? client.SearchRequestsAsync(query, CancellationToken.None)
                : Task.FromResult(new PagedResult<RequestRecord>([], 0));
            var items = (kind is null or DeskSection.RequestedItems) && IncludeItems
                ? client.SearchRequestedItemsAsync(query, CancellationToken.None)
                : Task.FromResult(new PagedResult<RequestedItemRecord>([], 0));
            var hideKnowledge = filtersActive && IncludeKnowledge && kind is null or DeskSection.Knowledge;
            var includeArticles = (kind is null or DeskSection.Knowledge) && IncludeKnowledge && !hideKnowledge;
            if (includeArticles)
                KnowledgeSearchRequested?.Invoke(this, EventArgs.Empty);
            var articles = includeArticles
                ? client.SearchKnowledgeAsync(query, CancellationToken.None)
                : Task.FromResult(new PagedResult<KnowledgeArticle>([], 0));
            var walkUps = (kind is null or DeskSection.WalkUps) && IncludeWalkUps
                ? client.SearchInteractionsAsync(query, CancellationToken.None)
                : Task.FromResult(new PagedResult<InteractionRecord>([], 0));

            await Task.WhenAll(incidents, requests, items, articles, walkUps);
            if (version != _runVersion)
                return;

            var hits = new List<SearchHit>();
            hits.AddRange(incidents.Result.Items.Select(record => new SearchHit
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
            hits.AddRange(requests.Result.Items.Select(record => new SearchHit
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
            hits.AddRange(items.Result.Items.Select(record => new SearchHit
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
            hits.AddRange(walkUps.Result.Items.Select(record => new SearchHit
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
            hits.AddRange(articles.Result.Items.Select(record => new SearchHit
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

            var previous = Selected?.SysId;
            Results.Clear();
            foreach (var hit in hits.OrderByDescending(hit => hit.SortKey, StringComparer.Ordinal))
            {
                PrepareHit?.Invoke(hit);
                Results.Add(hit);
            }
            Selected = previous is null ? null : Results.FirstOrDefault(hit => hit.SysId == previous);
            Query = trimmed;
            Summary = Results.Count == 1 ? "1 match" : Results.Count + " matches";
            if (hideKnowledge)
                Summary += ". " + KnowledgeHiddenSummary;
            Remember(trimmed);
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
