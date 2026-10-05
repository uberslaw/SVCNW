using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Query;

namespace ServiceNowDesk.ViewModels;

public sealed partial class SearchWorkspaceViewModel : ObservableObject
{
    private int _runVersion;
    private bool _resultsCurrent;

    [ObservableProperty] private SearchHit? selected;
    [ObservableProperty] private bool includeIncidents = true;
    [ObservableProperty] private bool includeRequests = true;
    [ObservableProperty] private bool includeItems = true;
    [ObservableProperty] private bool includeKnowledge = true;
    [ObservableProperty] private bool includeWalkUps = true;
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private string summary = "Search incidents, requests, items, walk-ups, and knowledge articles.";
    [ObservableProperty] private string errorMessage = "";

    public ObservableCollection<SearchHit> Results { get; } = [];

    public string Query { get; private set; } = "";

    public event EventHandler<SearchHit>? OpenRequested;

    public bool HasCurrentResultsFor(string? text) =>
        _resultsCurrent && string.Equals(Query, (text ?? "").Trim(), StringComparison.Ordinal);

    public void MarkStale() => _resultsCurrent = false;

    public void Reset()
    {
        _runVersion++;
        _resultsCurrent = false;
        Query = "";
        Results.Clear();
        Selected = null;
        Summary = "Search incidents, requests, items, walk-ups, and knowledge articles.";
        ErrorMessage = "";
        IsLoading = false;
    }

    public async Task RunAsync(IServiceNowClient? client, string? text)
    {
        var version = ++_runVersion;
        if (client is null)
        {
            Summary = "Connect to ServiceNow to search.";
            return;
        }

        var trimmed = (text ?? "").Trim();
        if (trimmed.Length < 2 && !EncodedQuery.IsNumberQuery(trimmed))
        {
            if (version != _runVersion)
                return;
            Query = trimmed;
            Results.Clear();
            Selected = null;
            Summary = "Type at least 2 characters. Numbers such as INC0010001, IMS0010001, or KB0001001 can be shorter.";
            _resultsCurrent = true;
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
            _resultsCurrent = true;
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
            var articles = (kind is null or DeskSection.Knowledge) && IncludeKnowledge
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
                SortKey = record.UpdatedAtValue
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
                SortKey = record.UpdatedAtValue
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
                SortKey = record.UpdatedAtValue
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
                Results.Add(hit);
            Selected = previous is null ? null : Results.FirstOrDefault(hit => hit.SysId == previous);
            Query = trimmed;
            Summary = Results.Count == 1 ? "1 match" : Results.Count + " matches";
            _resultsCurrent = true;
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

    private static string JoinMeta(params string[] parts) =>
        string.Join(" · ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
}

public sealed class SearchHit
{
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
}
