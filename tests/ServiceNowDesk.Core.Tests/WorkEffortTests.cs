using System.Net;
using ServiceNowDesk.Alerts;
using ServiceNowDesk.Client;
using ServiceNowDesk.ViewModels;
using ServiceNowDesk.WorkEffort;

namespace ServiceNowDesk.Tests;

public class WorkEffortTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 15, 0, 0);
    private static readonly WorkEffortPerson Alex = new("sample-user", "Alex Rivera", "alex.rivera");

    [Fact]
    public void WeightingCountsOpenAndResolveSeparatelyAndDoesNotAlsoCountAnUpdate()
    {
        var window = WorkEffortWindow.For(WorkEffortScale.Today, Now);
        var rows = WorkEffortScore.Build(
            [Alex],
            [
                new("inc-both", WorkEffortKind.Incident, "sample-user", Now, "sample-user", Now, null, null, "alex.rivera", Now),
                new("ritm-note", WorkEffortKind.RequestedItem, null, null, null, null, null, null, "alex.rivera", Now)
            ],
            window);

        var row = Assert.Single(rows);
        Assert.Equal(1, row.IncOpened);
        Assert.Equal(1, row.IncResolved);
        Assert.Equal(0, row.IncUpdated);
        Assert.Equal(0, row.RitmOpened);
        Assert.Equal(0, row.RitmResolved);
        Assert.Equal(1, row.RitmUpdated);
        Assert.Equal(2.3m, row.Weighted);
        Assert.Equal("2.3", row.WeightedText);
    }

    [Fact]
    public void CloseWithoutResolveCountsOnceAndResolvePlusCloseDoesNotDouble()
    {
        var window = WorkEffortWindow.For(WorkEffortScale.Today, Now);
        var closedOnly = WorkEffortScore.Build(
            [Alex],
            [new("ims-close", WorkEffortKind.Interaction, null, null, null, null, "sample-user", Now, "alex.rivera", Now)],
            window);
        var closed = Assert.Single(closedOnly);
        Assert.Equal(0, closed.ImsOpened);
        Assert.Equal(1, closed.ImsResolved);
        Assert.Equal(0, closed.ImsUpdated);
        Assert.Equal(0.6m, closed.Weighted);

        var both = WorkEffortScore.Build(
            [Alex],
            [new("inc-done", WorkEffortKind.Incident, null, null, "sample-user", Now, "sample-user", Now, "alex.rivera", Now)],
            window);
        var resolved = Assert.Single(both);
        Assert.Equal(1, resolved.IncResolved);
        Assert.Equal(0, resolved.IncUpdated);
        Assert.Equal(1.0m, resolved.Weighted);
        Assert.Equal("1.0", resolved.WeightedText);
    }

    [Fact]
    public void TodayKeepsOnlyRecordsInsideTheWindowAndALongerWindowKeepsMore()
    {
        var person = Alex;
        var touches = new WorkEffortTouch[]
        {
            new("today", WorkEffortKind.Incident, "sample-user", Now, null, null, null, null, null, null),
            new("ten", WorkEffortKind.Incident, "sample-user", Now.AddDays(-10), null, null, null, null, null, null),
            new("forty", WorkEffortKind.Incident, "sample-user", Now.AddDays(-40), null, null, null, null, null, null)
        };

        var today = WorkEffortScore.Build([person], touches, WorkEffortWindow.For(WorkEffortScale.Today, Now));
        var week = WorkEffortScore.Build([person], touches, WorkEffortWindow.For(WorkEffortScale.ThisWeek, Now));
        var month = WorkEffortScore.Build([person], touches, WorkEffortWindow.For(WorkEffortScale.ThisMonth, Now));
        var four = WorkEffortScore.Build([person], touches, WorkEffortWindow.For(WorkEffortScale.Last4Weeks, Now));
        var six = WorkEffortScore.Build([person], touches, WorkEffortWindow.For(WorkEffortScale.SixMonths, Now));

        Assert.Equal(1, Assert.Single(today).IncOpened);
        Assert.Equal(1, Assert.Single(week).IncOpened);
        Assert.Equal(1, Assert.Single(month).IncOpened);
        Assert.Equal(2, Assert.Single(four).IncOpened);
        Assert.Equal(3, Assert.Single(six).IncOpened);

        var rolling = WorkEffortWindow.For(WorkEffortScale.Last4Weeks, Now);
        Assert.Equal(Now.AddDays(-28), rolling.Start);
        Assert.Equal(Now, rolling.End);
        var edges = WorkEffortScore.Build(
            [person],
            [
                new("in", WorkEffortKind.Incident, "sample-user", rolling.Start, null, null, null, null, null, null),
                new("out", WorkEffortKind.Incident, "sample-user", rolling.Start.AddSeconds(-1), null, null, null, null, null, null)
            ],
            rolling);
        Assert.Equal(1, Assert.Single(edges).IncOpened);

        Assert.Equal(new DateTime(2026, 10, 5), WorkEffortWindow.For(WorkEffortScale.ThisWeek, Now).Start);
        Assert.Equal(new DateTime(2026, 10, 7, 23, 59, 59), WorkEffortWindow.For(WorkEffortScale.ThisWeek, Now).End);
        Assert.Equal(new DateTime(2026, 10, 5), WorkEffortWindow.For(WorkEffortScale.ThisWeek, new DateTime(2026, 10, 11, 9, 0, 0)).Start);
        Assert.Equal(new DateTime(2026, 10, 1), WorkEffortWindow.For(WorkEffortScale.ThisMonth, Now).Start);
        Assert.Equal(Now.AddMonths(-6), WorkEffortWindow.For(WorkEffortScale.SixMonths, Now).Start);
        Assert.Equal(Now.AddMonths(-12), WorkEffortWindow.For(WorkEffortScale.TwelveMonths, Now).Start);
    }

    [Fact]
    public void APersonOutsideTheUsersGroupsIsExcluded()
    {
        var people = WorkEffortRoster.Collect(SampleWorkEffort.Memberships(), SampleWorkEffort.SignedIn);
        Assert.DoesNotContain(people, person => person.SysId == SampleWorkEffort.Sam.SysId);
        Assert.Empty(WorkEffortRoster.Collect([new WorkEffortMembership("group-net", SampleWorkEffort.Sam)], SampleWorkEffort.SignedIn));

        var rows = WorkEffortScore.Build(people, SampleWorkEffort.Touches(Now), WorkEffortWindow.For(WorkEffortScale.Today, Now));
        Assert.DoesNotContain(rows, row => row.Name == "Sam Patel");
        Assert.Equal(2.9m, rows.Single(row => row.Name == "Alex Rivera").Weighted);
    }

    [Fact]
    public void DuplicateGroupMembershipDoesNotDuplicateAPerson()
    {
        var people = WorkEffortRoster.Collect(SampleWorkEffort.Memberships(), SampleWorkEffort.SignedIn);
        Assert.Single(people, person => person.SysId == "user-jordan");
        Assert.Contains(people, person => person.SysId == "sample-user");
        Assert.Contains(people, person => person.SysId == "user-riley");
        Assert.Equal(3, people.Count);

        var rows = WorkEffortScore.Build(people, SampleWorkEffort.Touches(Now), WorkEffortWindow.For(WorkEffortScale.Today, Now));
        var jordan = Assert.Single(rows, row => row.Name == "Jordan Lee");
        Assert.Equal(1, jordan.IncUpdated);
        Assert.Equal(0.3m, jordan.Weighted);
    }

    [Fact]
    public async Task SampleModeReturnsDeterministicRowsForTheTeam()
    {
        using var client = new SampleServiceNowClient();
        var today = await client.GetWorkEffortAsync(WorkEffortScale.Today, Now, CancellationToken.None);
        var again = await client.GetWorkEffortAsync(WorkEffortScale.Today, Now, CancellationToken.None);
        var six = await client.GetWorkEffortAsync(WorkEffortScale.SixMonths, Now, CancellationToken.None);

        Assert.Equal(today.Rows.Select(row => row.WeightedText), again.Rows.Select(row => row.WeightedText));
        Assert.Equal(["Alex Rivera", "Jordan Lee", "Riley Chen"], today.Rows.Select(row => row.Name).ToArray());
        Assert.DoesNotContain(today.Rows, row => row.Name == "Sam Patel");

        var alex = today.Rows[0];
        Assert.Equal(1, alex.IncOpened);
        Assert.Equal(1, alex.IncResolved);
        Assert.Equal(0, alex.IncUpdated);
        Assert.Equal(1, alex.RitmUpdated);
        Assert.Equal(1, alex.ImsResolved);
        Assert.Equal(0, alex.ImsUpdated);
        Assert.Equal(2.9m, alex.Weighted);
        Assert.Equal("2.9", alex.WeightedText);
        Assert.Equal(0.0m, today.Rows.Single(row => row.Name == "Riley Chen").Weighted);

        var alexLater = six.Rows.Single(row => row.Name == "Alex Rivera");
        Assert.Equal(2, alexLater.IncOpened);
        Assert.Equal(1, alexLater.IncUpdated);
        Assert.Equal(4.2m, alexLater.Weighted);
        Assert.Equal("Counts for today.", today.Status);
        Assert.Equal("", today.EmptyMessage);
    }

    [Fact]
    public void WorkEffortStaysUnderLeadsAndDefaultsToToday()
    {
        var leads = new LeadsViewModel();
        Assert.Equal(LeadArea.Team, leads.Area);
        Assert.False(leads.ShowWorkEffort);
        Assert.True(leads.ShowQueues);
        Assert.Equal(WorkEffortScale.Today, leads.WorkEffort.Scale);

        leads.Area = LeadArea.WorkEffort;
        Assert.True(leads.ShowWorkEffort);
        Assert.False(leads.ShowQueues);
        Assert.Equal("interaction", WorkEffortTablePlan.InteractionAttempts[0].Table);
        Assert.Contains("opened_for", WorkEffortTablePlan.InteractionAttempts[^1].Fields);
        Assert.DoesNotContain("short_description", WorkEffortTablePlan.Incident.Fields);
    }

    [Fact]
    public void TheCountQueryUsesNarrowFieldsAndDropsCarets()
    {
        var people = new[]
        {
            Alex,
            new WorkEffortPerson("user-jordan", "Jordan Lee", "jordan^lee")
        };
        var clause = WorkEffortQuery.Clause(WorkEffortTablePlan.Incident, people, WorkEffortWindow.For(WorkEffortScale.Today, Now));
        Assert.Contains("opened_byINsample-user,user-jordan", clause);
        Assert.Contains("resolved_byINsample-user,user-jordan", clause);
        Assert.Contains("closed_byINsample-user,user-jordan", clause);
        Assert.Contains("sys_updated_byINalex.rivera,jordan lee", clause);
        Assert.Contains("opened_at>=2026-10-07@00:00:00", clause);
        Assert.Contains("opened_at<=2026-10-07@23:59:59", clause);
        Assert.DoesNotContain("jordan^lee", clause);
        Assert.DoesNotContain("opened_at<2026", clause);
        Assert.DoesNotContain("short_description", WorkEffortTablePlan.Incident.Fields);

        var rolling = WorkEffortQuery.Clause(
            WorkEffortTablePlan.Incident,
            [Alex],
            WorkEffortWindow.For(WorkEffortScale.Last4Weeks, new DateTime(2026, 10, 7, 15, 4, 5)));
        Assert.Contains("sys_updated_on>=2026-09-09@15:04:05", rolling);
        Assert.Contains("sys_updated_on<=2026-10-07@15:04:05", rolling);
    }

    [Fact]
    public async Task LiveCountPagesNarrowRowsAndSkipsWhenTheUserHasNoGroups()
    {
        var emptyHandler = new StubHandler(GroupResponder(includeMembers: false, incidentTotal: null, rejectInteractionResolve: false));
        using var emptyClient = ServiceNowClient.Create(Api.BasicSession(), emptyHandler);
        var empty = await emptyClient.GetWorkEffortAsync(WorkEffortScale.Today, Now, CancellationToken.None);
        Assert.Equal(WorkEffortReport.EmptyGroupsMessage, empty.EmptyMessage);
        Assert.Empty(empty.Rows);
        Assert.DoesNotContain(emptyHandler.Calls, call => call.PathAndQuery.Contains("table/incident", StringComparison.Ordinal));

        var liveHandler = new StubHandler(GroupResponder(includeMembers: true, incidentTotal: null, rejectInteractionResolve: true));
        using var live = ServiceNowClient.Create(Api.BasicSession(), liveHandler);
        var report = await live.GetWorkEffortAsync(WorkEffortScale.Today, Now, CancellationToken.None);
        Assert.Equal(["Alex Rivera", "Jordan Lee"], report.Rows.Select(row => row.Name).ToArray());
        Assert.Equal(1, report.Rows[0].IncOpened);
        Assert.Equal(1, report.Rows[0].IncResolved);
        Assert.Equal(0, report.Rows[0].IncUpdated);
        Assert.Equal(2.0m, report.Rows[0].Weighted);
        Assert.Equal(0, report.Rows[1].IncUpdated);

        var incidentCall = liveHandler.Calls.Single(call => call.PathAndQuery.Contains("table/incident", StringComparison.Ordinal));
        var incident = Uri.UnescapeDataString(incidentCall.PathAndQuery);
        Assert.Contains("sysparm_display_value=all", incident);
        Assert.Contains("sysparm_exclude_reference_link=true", incident);
        Assert.Contains("sys_id,opened_by,opened_at,resolved_by,resolved_at,closed_by,closed_at,sys_updated_by,sys_updated_on", incident);
        Assert.DoesNotContain("short_description", incident);
        Assert.DoesNotContain("user-sam", incident);
        Assert.Contains("opened_at>=2026-10-07@00:00:00", incident);
        Assert.Contains("opened_at<=2026-10-07@23:59:59", incident);
        Assert.DoesNotContain("<", incidentCall.PathAndQuery);

        var interactions = liveHandler.Calls.Where(call => call.PathAndQuery.Contains("table/interaction", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, interactions.Length);
        Assert.Contains("resolved_by", Uri.UnescapeDataString(interactions[0].PathAndQuery));
        var fallback = Uri.UnescapeDataString(interactions[1].PathAndQuery);
        Assert.DoesNotContain("resolved_by", fallback);
        Assert.Contains("opened_for", fallback);
        Assert.Contains("opened_by", fallback);
        Assert.Contains("closed_by", fallback);
    }

    [Fact]
    public async Task HittingTheSafetyCapSaysTheFiguresArePartial()
    {
        var handler = new StubHandler(GroupResponder(includeMembers: true, incidentTotal: 9, rejectInteractionResolve: false));
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);
        var report = await client.GetWorkEffortAsync(WorkEffortScale.Today, Now, safetyCap: 1, CancellationToken.None);
        Assert.Contains("safety cap", report.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(WorkEffortQuery.CapNotice, report.Status, StringComparison.Ordinal);
    }

    private static Func<HttpRequestMessage, string, HttpResponseMessage> GroupResponder(bool includeMembers, int? incidentTotal, bool rejectInteractionResolve) =>
        (request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            if (path.Contains("sys_user_grmember", StringComparison.Ordinal))
            {
                var query = Uri.UnescapeDataString(path);
                if (!includeMembers || !query.Contains("groupIN", StringComparison.Ordinal))
                    return Api.Json(includeMembers ? GroupsJson : """{"result":[]}""");
                return Api.Json(MembersJson);
            }

            if (path.Contains("table/sys_user", StringComparison.Ordinal))
                return Api.Json(UserJson);
            if (path.Contains("table/interaction", StringComparison.Ordinal))
            {
                if (rejectInteractionResolve && Uri.UnescapeDataString(path).Contains("resolved_by", StringComparison.Ordinal))
                    return Api.Json("""{"error":{"message":"Invalid query","detail":"Invalid field resolved_by"}}""", HttpStatusCode.BadRequest);
                return Api.Json("""{"result":[]}""");
            }

            if (path.Contains("table/sc_req_item", StringComparison.Ordinal))
                return Api.Json("""{"result":[]}""");
            if (path.Contains("table/incident", StringComparison.Ordinal))
                return Api.Json(IncidentTouchJson, total: incidentTotal);
            return Api.Json("""{"result":[]}""");
        };

    private const string UserJson = """
        {"result":[{"sys_id":{"value":"sample-user","display_value":"sample-user"},"name":{"value":"Alex Rivera","display_value":"Alex Rivera"},"user_name":{"value":"alex.rivera","display_value":"alex.rivera"},"email":{"value":"alex.rivera@example.com","display_value":"alex.rivera@example.com"}}]}
        """;

    private const string GroupsJson = """
        {"result":[{"group":{"value":"group-cs","display_value":"Client Services"},"user":{"value":"sample-user","display_value":"Alex Rivera"}}]}
        """;

    private const string MembersJson = """
        {"result":[
          {"group":{"value":"group-cs","display_value":"Client Services"},"user":{"value":"sample-user","display_value":"Alex Rivera"},"user.name":{"value":"Alex Rivera","display_value":"Alex Rivera"},"user.user_name":{"value":"alex.rivera","display_value":"alex.rivera"}},
          {"group":{"value":"group-cs","display_value":"Client Services"},"user":{"value":"user-jordan","display_value":"Jordan Lee"},"user.name":{"value":"Jordan Lee","display_value":"Jordan Lee"},"user.user_name":{"value":"jordan.lee","display_value":"jordan.lee"}},
          {"group":{"value":"group-cs","display_value":"Client Services"},"user":{"value":"user-jordan","display_value":"Jordan Lee"},"user.name":{"value":"Jordan Lee","display_value":"Jordan Lee"},"user.user_name":{"value":"jordan.lee","display_value":"jordan.lee"}}
        ]}
        """;

    private const string IncidentTouchJson = """
        {"result":[{
          "sys_id":{"value":"inc-1","display_value":"inc-1"},
          "opened_by":{"value":"sample-user","display_value":"Alex Rivera"},
          "opened_at":{"value":"2026-10-06 23:00:00","display_value":"2026-10-07 15:00:00"},
          "resolved_by":{"value":"sample-user","display_value":"Alex Rivera"},
          "resolved_at":{"value":"2026-10-06 23:00:00","display_value":"2026-10-07 15:00:00"},
          "closed_by":{"value":"","display_value":""},
          "closed_at":{"value":"","display_value":""},
          "sys_updated_by":{"value":"alex.rivera","display_value":"alex.rivera"},
          "sys_updated_on":{"value":"2026-10-06 23:00:00","display_value":"2026-10-07 15:00:00"}
        }]}
        """;
}
