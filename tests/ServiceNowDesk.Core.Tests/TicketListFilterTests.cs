using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Query;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class TicketListFilterTests
{
    [Fact]
    public void AssignedToMePrefersTheSignedInSysId()
    {
        Assert.Equal("assigned_to=sample-user", TicketListFilter.AssignedToMeClause("sample-user"));
        Assert.Equal(
            "assigned_to=" + TicketListFilter.CurrentUserScript,
            TicketListFilter.AssignedToMeClause(null));
        Assert.Equal(
            "assigned_to=" + TicketListFilter.CurrentUserScript,
            TicketListFilter.AssignedToMeClause(""));
    }

    [Fact]
    public void BindCurrentUserRewritesRequestPresets()
    {
        var bound = TicketListFilter.BindCurrentUser(
            "requested_for=" + TicketListFilter.CurrentUserScript,
            "sample-user");
        Assert.Equal("requested_for=sample-user", bound);
        Assert.Equal(
            "opened_by=" + TicketListFilter.CurrentUserScript,
            TicketListFilter.BindCurrentUser("opened_by=" + TicketListFilter.CurrentUserScript, null));
    }

    [Fact]
    public void DescribeActiveShowsMyTicketsWithoutOfficeFilter()
    {
        var text = TicketListFilter.DescribeActive(
            PresetCatalog.Incidents[0],
            officeCities: ["Brisbane"],
            teamMemberIds: null);
        Assert.Contains("My Tickets", text, StringComparison.Ordinal);
        Assert.Contains("assigned_to=signed-in user", text, StringComparison.Ordinal);
        Assert.Contains("no office filter", text, StringComparison.Ordinal);
        Assert.DoesNotContain("location.name", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeActiveShowsOfficeNqForMyTeam()
    {
        var text = TicketListFilter.DescribeActive(
            PresetCatalog.Incidents.Single(preset => preset.Label == "My Team"),
            officeCities: ["Brisbane"],
            teamMemberIds: null);
        Assert.Contains("My Team", text, StringComparison.Ordinal);
        Assert.Contains("assignment_groupIN my groups", text, StringComparison.Ordinal);
        Assert.Contains("location.name NQ", text, StringComparison.Ordinal);
        Assert.Contains("\"Brisbane\"", text, StringComparison.Ordinal);
        Assert.Contains("\"Brisbane Office\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LiveMineQueryUsesSysIdAfterConnectAndExposesLastEncodedQuery()
    {
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/sys_user", StringComparison.Ordinal)
                && !path.Contains("sys_user_grmember", StringComparison.Ordinal))
            {
                return Api.Json("""{"result":[{"sys_id":{"value":"user-alex","display_value":"user-alex"},"name":{"value":"Alex","display_value":"Alex"},"user_name":{"value":"alex","display_value":"alex"},"email":{"value":"a@b.c","display_value":"a@b.c"},"location":{"value":"","display_value":""}}]}""");
            }

            return Api.Json("""{"result":[]}""");
        });
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);
        await client.GetCurrentUserAsync(CancellationToken.None);
        await client.SearchIncidentsAsync(
            new TicketQuery { Assignment = AssignmentScope.Mine, Activity = ActivityFilter.Open },
            CancellationToken.None);

        Assert.Contains("assigned_to=user-alex", client.LastTicketEncodedQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("javascript:gs.getUserID()", client.LastTicketEncodedQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("location.name", client.LastTicketEncodedQuery, StringComparison.Ordinal);
        var wire = QueryOf(handler.Calls.Last(call => call.PathAndQuery.Contains("/incident", StringComparison.Ordinal)).PathAndQuery);
        Assert.Equal(client.LastTicketEncodedQuery, wire);
    }

    [Fact]
    public async Task PracticeListChromeSurfacesFilterAndEncodedQuery()
    {
        var client = new SampleServiceNowClient();
        var workspace = new IncidentWorkspaceViewModel(new RecordingDesktopServices());
        workspace.UseOfficeCities(["Brisbane"]);
        workspace.Attach(client);
        await workspace.ReloadAsync();

        Assert.Contains("My Tickets", workspace.FilterSummary, StringComparison.Ordinal);
        Assert.Contains("no office filter", workspace.FilterSummary, StringComparison.Ordinal);
        Assert.Contains("assigned_to=sample-user", workspace.LastEncodedQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("javascript:gs.getUserID()", workspace.LastEncodedQuery, StringComparison.Ordinal);
        Assert.True(workspace.TotalCount > 0, workspace.LastEncodedQuery);
        Assert.Equal(client.LastTicketEncodedQuery, workspace.LastEncodedQuery);
    }

    private static string QueryOf(string pathAndQuery)
    {
        var marker = "sysparm_query=";
        var start = pathAndQuery.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, pathAndQuery);
        start += marker.Length;
        var end = pathAndQuery.IndexOf('&', start);
        var encoded = end < 0 ? pathAndQuery[start..] : pathAndQuery[start..end];
        return Uri.UnescapeDataString(encoded);
    }
}
