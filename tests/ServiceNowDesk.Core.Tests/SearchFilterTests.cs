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
}
