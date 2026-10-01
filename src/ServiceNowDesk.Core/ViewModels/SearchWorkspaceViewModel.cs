using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Query;

namespace ServiceNowDesk.ViewModels;

public sealed partial class SearchWorkspaceViewModel : ObservableObject
{
    [ObservableProperty] private SearchHit? selected;
    [ObservableProperty] private bool includeIncidents = true;
    [ObservableProperty] private bool includeRequests = true;
    [ObservableProperty] private bool includeItems = true;
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private string summary = "Search open and closed incidents, requests, and items.";
    [ObservableProperty] private string errorMessage = "";

    public ObservableCollection<SearchHit> Results { get; } = [];

    public event EventHandler<SearchHit>? OpenRequested;

    public async Task RunAsync(IServiceNowClient? client, string? text)
    {
        if (client is null)
        {
            Summary = "Connect to ServiceNow to search.";
            return;
        }

        var trimmed = (text ?? "").Trim();
        if (trimmed.Length < 2 && !EncodedQuery.IsNumberQuery(trimmed))
        {
            Results.Clear();
            Summary = "Type at least 2 characters. Numbers such as INC0010001 can be shorter.";
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

            await Task.WhenAll(incidents, requests, items);
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

            Results.Clear();
            foreach (var hit in hits.OrderByDescending(hit => hit.SortKey, StringComparer.Ordinal))
                Results.Add(hit);
            Summary = Results.Count == 1 ? "1 match" : Results.Count + " matches";
        }
        catch (Exception ex)
        {
            ErrorMessage = WorkspaceMessages.Describe(ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void OpenSelected()
    {
        if (Selected is not null)
            OpenRequested?.Invoke(this, Selected);
    }
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
