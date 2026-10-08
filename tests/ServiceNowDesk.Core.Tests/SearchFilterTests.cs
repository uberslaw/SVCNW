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
}
