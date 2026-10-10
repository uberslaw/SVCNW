using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class SearchFilterTests
{
    [Fact]
    public async Task SampleSearchHonorsAndWildcardsJournalAndDescription()
    {
        using var client = new SampleServiceNowClient();
        var search = new SearchWorkspaceViewModel();

        await search.RunAsync(client, "drops + tunnel");
        var vpn = Assert.Single(search.Results);
        Assert.Equal("INC0010002", vpn.Number);

        await search.RunAsync(client, "vpn + printer");
        Assert.Empty(search.Results);

        await search.RunAsync(client, "jammed*");
        Assert.Contains(search.Results, hit => hit.Number == "INC0010001");

        await search.RunAsync(client, "reprint*");
        var note = Assert.Single(search.Results);
        Assert.Equal("INC0010001", note.Number);

        await search.RunAsync(client, "printer*");
        Assert.Contains(search.Results, hit => hit.Number == "INC0010001");
        Assert.Contains(search.Results, hit => hit.Number == "KB0001002");
    }

    [Fact]
    public async Task AssignmentOpenedDateFiltersNarrowResultsAndHideKnowledge()
    {
        using var client = new SampleServiceNowClient();
        var search = new SearchWorkspaceViewModel();

        await search.RunAsync(client, "printer");
        Assert.Contains(search.Results, hit => hit.Number == "KB0001002");

        search.AssignmentGroup.Set("group-aus", "Aus DT - Client Services");
        await search.RunAsync(client, "Brisbane");
        Assert.Contains(search.Results, hit => hit.Number == "INC0010007");
        Assert.Contains(search.Results, hit => hit.Number == "INC0010009");
        Assert.DoesNotContain(search.Results, hit => hit.Section == DeskSection.Knowledge);
        Assert.Contains(SearchWorkspaceViewModel.KnowledgeHiddenSummary, search.Summary);

        search.Assignee.Set("user-jordan", "Jordan Lee");
        search.OpenedFrom = new DateTime(2026, 9, 30);
        search.OpenedTo = new DateTime(2026, 9, 30);
        await search.RunAsync(client, "Brisbane");
        var day = Assert.Single(search.Results);
        Assert.Equal("INC0010007", day.Number);

        search.AssignmentGroup.Clear();
        search.Assignee.Clear();
        search.OpenedFrom = new DateTime(2026, 10, 2);
        search.OpenedTo = new DateTime(2026, 10, 1);
        var calls = client.RecentActivity.Count;
        await search.RunAsync(client, "Brisbane");
        Assert.Equal("Opened from is after opened to.", search.FilterMessage);
        Assert.Empty(search.Results);
        Assert.Equal(calls, client.RecentActivity.Count);
    }

    [Fact]
    public async Task AFilterWithoutTextStillSearches()
    {
        using var client = new SampleServiceNowClient();
        var search = new SearchWorkspaceViewModel();
        search.AssignmentGroup.Set("group-aus", "Aus DT - Client Services");
        search.IncludeKnowledge = false;

        await search.RunAsync(client, "");

        Assert.NotEmpty(search.Results);
        Assert.All(search.Results, hit => Assert.NotEqual(DeskSection.Knowledge, hit.Section));
        Assert.Contains(search.Results, hit => hit.Number == "INC0010007");
        Assert.DoesNotContain(search.Results, hit => hit.Number == "INC0010001");
    }

    [Fact]
    public async Task AFailedTableDoesNotBlankTheOtherSearchHits()
    {
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/api/now/table/incident", StringComparison.Ordinal))
                return Api.Json(Api.IncidentList());
            if (path.Contains("/api/now/table/kb_knowledge", StringComparison.Ordinal))
                return Api.Json("""{"result":{"not":"a list"}}""");
            if (path.Contains("/api/now/table/interaction", StringComparison.Ordinal))
                return Api.Json("");
            return Api.Json("""{"result":[]}""");
        });
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);
        var search = new SearchWorkspaceViewModel();

        await search.RunAsync(client, "revit 2026");

        Assert.Equal("INC0010001", Assert.Single(search.Results).Number);
        Assert.DoesNotContain(search.Results, hit => hit.Section == DeskSection.Knowledge);
        Assert.Contains("Knowledge", search.ErrorMessage);
        Assert.Contains("did not include a list", search.ErrorMessage);
        Assert.DoesNotContain("Walk-ups", search.ErrorMessage);
        Assert.Equal("1 match", search.Summary);
    }

    [Fact]
    public async Task SearchKeepsHitsUntilReplaceAndDoesNotAutoApplyFirstState()
    {
        using var client = new SampleServiceNowClient();
        var search = new SearchWorkspaceViewModel();

        await search.RunAsync(client, "printer");
        Assert.True(search.Results.Count > 0, search.Summary);
        Assert.Empty(search.SelectedStates);
        Assert.Equal("All states", search.StateFilterSummary);
        Assert.Contains("match", search.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" of ", search.Summary, StringComparison.Ordinal);

        // Spurious empty selection (ItemsSource rebuild) must not clear real hits.
        search.SetSelectedStates([]);
        Assert.True(search.Results.Count > 0, search.Summary);
        Assert.Empty(search.SelectedStates);

        // Applying a catalog state that matches none of these hits must report 0 of N.
        Assert.Contains("Closed Abandoned", search.StateOptions);
        search.SetSelectedStates(["Closed Abandoned"]);
        Assert.Empty(search.Results);
        Assert.Contains(" of ", search.Summary, StringComparison.Ordinal);
        search.ClearStateFilter();
        Assert.True(search.Results.Count > 0, search.Summary);
    }

    [Fact]
    public async Task StateFilterUsesCatalogUnionAndMultiSelectNarrowsResults()
    {
        using var client = new SampleServiceNowClient();
        var search = new SearchWorkspaceViewModel();

        await search.RunAsync(client, "printer");
        Assert.True(search.Results.Count > 1);
        Assert.Contains("New", search.StateOptions);
        Assert.Contains("In Progress", search.StateOptions);
        Assert.Contains("Requested", search.StateOptions);
        Assert.Contains("Open", search.StateOptions);
        Assert.Contains("Published", search.StateOptions);
        Assert.Equal("All states", search.StateFilterSummary);

        search.SetSelectedStates(["In Progress", "Published"]);
        Assert.Equal(2, search.SelectedStates.Count);
        Assert.All(search.Results, hit => Assert.True(
            hit.StateLabel is "In Progress" or "Published"));
        Assert.Contains(search.Results, hit => hit.Number == "INC0010001");
        Assert.Contains(search.Results, hit => hit.Section == DeskSection.Knowledge);
        Assert.DoesNotContain(search.Results, hit => hit.StateLabel == "New");
        Assert.Contains(" of ", search.Summary);

        search.ClearStateFilter();
        Assert.Empty(search.SelectedStates);
        Assert.Equal("All states", search.StateFilterSummary);
        Assert.Contains(search.Results, hit => hit.StateLabel == "New" || hit.Number == "INC0010001" || hit.Section == DeskSection.Knowledge);
    }

    [Fact]
    public async Task SortAndGroupReorderCurrentResultsWithoutResearch()
    {
        using var client = new SampleServiceNowClient();
        var search = new SearchWorkspaceViewModel();
        search.IncludeKnowledge = false;
        search.IncludeRequests = false;
        search.IncludeItems = false;
        search.IncludeWalkUps = false;

        await search.RunAsync(client, "INC");
        Assert.True(search.Results.Count >= 2);
        var before = search.Results.Select(hit => hit.Number).ToArray();

        search.ToggleSort(SearchWorkspaceViewModel.SortNumber);
        Assert.Equal(SearchWorkspaceViewModel.SortNumber, search.SortColumn);
        Assert.True(search.SortAscending);
        var ascending = search.Results.Select(hit => hit.Number).ToArray();
        Assert.Equal(ascending.OrderBy(number => number, StringComparer.OrdinalIgnoreCase), ascending);
        Assert.NotEqual(before, ascending);

        search.ToggleSort(SearchWorkspaceViewModel.SortNumber);
        Assert.False(search.SortAscending);
        var descending = search.Results.Select(hit => hit.Number).ToArray();
        Assert.Equal(descending.OrderByDescending(number => number, StringComparer.OrdinalIgnoreCase), descending);

        search.GroupBy(SearchWorkspaceViewModel.SortState);
        Assert.True(search.HasGrouping);
        Assert.Equal(SearchWorkspaceViewModel.SortState, search.GroupColumn);
        var grouped = search.Results.Select(hit => hit.StateLabel).ToArray();
        Assert.Equal(grouped.Distinct(StringComparer.OrdinalIgnoreCase).Count(), CountGroupRuns(grouped));

        search.ClearGroupingCommand.Execute(null);
        Assert.False(search.HasGrouping);
        Assert.Equal("", search.GroupColumn);
    }

    private static int CountGroupRuns(IReadOnlyList<string> labels)
    {
        if (labels.Count == 0)
            return 0;
        var runs = 1;
        for (var i = 1; i < labels.Count; i++)
        {
            if (!string.Equals(labels[i - 1], labels[i], StringComparison.OrdinalIgnoreCase))
                runs++;
        }

        return runs;
    }
}
