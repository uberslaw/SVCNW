using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using ServiceNowDesk.Alerts;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;
using ServiceNowDesk.WorkEffort;

namespace ServiceNowDesk.Tests;

public class WorkEffortTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 15, 0, 0);
    private static readonly WorkEffortPerson Alex = new("sample-user", "Alex Rivera", "alex.rivera");

    private static readonly IReadOnlyList<WorkEffortPerson> SampleTeam =
    [
        SampleWorkEffort.SignedIn,
        SampleWorkEffort.Jordan,
        SampleWorkEffort.Riley
    ];

    private static readonly IReadOnlyList<WorkEffortPerson> LiveTeam =
    [
        new("sample-user", "Alex Rivera", "alex.rivera"),
        new("user-jordan", "Jordan Lee", "jordan.lee")
    ];

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
        var today = await client.GetWorkEffortAsync(WorkEffortScale.Today, Now, SampleTeam, CancellationToken.None);
        var again = await client.GetWorkEffortAsync(WorkEffortScale.Today, Now, SampleTeam, CancellationToken.None);
        var six = await client.GetWorkEffortAsync(WorkEffortScale.SixMonths, Now, SampleTeam, CancellationToken.None);

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
        Assert.Equal(2, alexLater.IncUpdated);
        Assert.Equal(2, alexLater.RitmUpdated);
        Assert.Equal(4.8m, alexLater.Weighted);
        Assert.Equal("Counts for today.", today.Status);
        Assert.Equal("", today.EmptyMessage);

        var todayMultiple = WorkEffortScore.Present(today, WorkEffortUpdateMode.Multiple);
        Assert.Equal(2, todayMultiple.Rows[0].RitmUpdated);
        Assert.Equal(3.2m, todayMultiple.Rows[0].Weighted);
        Assert.Equal(3, todayMultiple.Rows.Single(row => row.Name == "Jordan Lee").IncUpdated);
        Assert.Equal(0.9m, todayMultiple.Rows.Single(row => row.Name == "Jordan Lee").Weighted);
        Assert.Equal("Allow multiple updates adds 3 extra update credits versus daily updates.", todayMultiple.Shift);

        var week = await client.GetWorkEffortAsync(WorkEffortScale.ThisWeek, Now, SampleTeam, CancellationToken.None);
        var alexWeek = week.Rows.Single(row => row.Name == "Alex Rivera");
        Assert.Equal(2, alexWeek.RitmUpdated);
        Assert.Equal(3.2m, alexWeek.Weighted);
        var weekMultiple = WorkEffortScore.Present(week, WorkEffortUpdateMode.Multiple);
        Assert.Equal(3, weekMultiple.Rows.Single(row => row.Name == "Alex Rivera").RitmUpdated);

        var four = await client.GetWorkEffortAsync(WorkEffortScale.Last4Weeks, Now, SampleTeam, CancellationToken.None);
        var alexFour = four.Rows.Single(row => row.Name == "Alex Rivera");
        Assert.Equal(2, alexFour.IncOpened);
        Assert.Equal(1, alexFour.IncUpdated);
    }

    [Fact]
    public void WorkEffortStaysUnderLeadsAndDefaultsToToday()
    {
        var leads = new LeadsViewModel();
        Assert.Equal(LeadArea.Team, leads.Area);
        Assert.False(leads.ShowWorkEffort);
        Assert.True(leads.ShowQueues);
        Assert.Equal(WorkEffortScale.Today, leads.WorkEffort.Scale);
        Assert.Equal(WorkEffortUpdateMode.Daily, leads.WorkEffort.UpdateMode);

        leads.Area = LeadArea.WorkEffort;
        Assert.True(leads.ShowWorkEffort);
        Assert.False(leads.ShowQueues);
        Assert.Equal("interaction", WorkEffortTablePlan.InteractionAttempts[0].Table);
        Assert.Contains("opened_for", WorkEffortTablePlan.InteractionAttempts[^1].Fields);
        Assert.Contains("number", WorkEffortTablePlan.Incident.Fields);
        Assert.Contains("short_description", WorkEffortTablePlan.Incident.Fields);
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
        Assert.Contains("number", WorkEffortTablePlan.Incident.Fields);
        Assert.Contains("short_description", WorkEffortTablePlan.Incident.Fields);

        var rolling = WorkEffortQuery.Clause(
            WorkEffortTablePlan.Incident,
            [Alex],
            WorkEffortWindow.For(WorkEffortScale.Last4Weeks, new DateTime(2026, 10, 7, 15, 4, 5)));
        Assert.Contains("sys_updated_on>=2026-09-09@15:04:05", rolling);
        Assert.Contains("sys_updated_on<=2026-10-07@15:04:05", rolling);

        var journal = WorkEffortQuery.JournalClause(people, WorkEffortWindow.For(WorkEffortScale.Today, Now));
        Assert.Contains("elementINcomments,additional_comments,work_notes", journal);
        Assert.Contains("sys_created_byINalex.rivera,sample-user", journal);
        Assert.Contains("sys_created_on>=2026-10-07@00:00:00", journal);
        Assert.DoesNotContain("jordan^lee", journal);
        var audit = WorkEffortQuery.AuditClause([Alex], WorkEffortWindow.For(WorkEffortScale.Today, Now));
        Assert.Contains("tablenameINincident,sc_req_item,interaction", audit);
        Assert.Contains("userINalex.rivera,sample-user", audit);
    }

    [Fact]
    public async Task LiveCountPagesNarrowRowsAndSkipsWhenTheTeamIsEmpty()
    {
        var emptyHandler = new StubHandler(GroupResponder(includeMembers: false, incidentTotal: null, rejectInteractionResolve: false));
        using var emptyClient = ServiceNowClient.Create(Api.BasicSession(), emptyHandler);
        var empty = await emptyClient.GetWorkEffortAsync(WorkEffortScale.Today, Now, [], CancellationToken.None);
        Assert.Equal(WorkEffortReport.DefineTeamMessage, empty.EmptyMessage);
        Assert.Empty(empty.Rows);
        Assert.Empty(emptyHandler.Calls);

        var liveHandler = new StubHandler(GroupResponder(includeMembers: true, incidentTotal: null, rejectInteractionResolve: true));
        using var live = ServiceNowClient.Create(Api.BasicSession(), liveHandler);
        var report = await live.GetWorkEffortAsync(WorkEffortScale.Today, Now, LiveTeam, CancellationToken.None);
        Assert.Equal(["Alex Rivera", "Jordan Lee"], report.Rows.Select(row => row.Name).ToArray());
        Assert.Equal(1, report.Rows[0].IncOpened);
        Assert.Equal(1, report.Rows[0].IncResolved);
        Assert.Equal(0, report.Rows[0].IncUpdated);
        Assert.Equal(2.0m, report.Rows[0].Weighted);
        Assert.Equal(0, report.Rows[1].IncUpdated);

        var incidentCall = liveHandler.Calls.Single(call => call.PathAndQuery.Contains("table/incident", StringComparison.Ordinal));
        var incident = Uri.UnescapeDataString(incidentCall.PathAndQuery);
        Assert.Contains("sysparm_display_value=all", incident);
        Assert.Contains("sysparm_limit=" + WorkEffortQuery.PageSize, incident);
        Assert.Contains("sysparm_exclude_reference_link=true", incident);
        Assert.DoesNotContain(liveHandler.Calls, call => call.PathAndQuery.Contains("sys_user_grmember", StringComparison.Ordinal));
        Assert.Contains("sys_id,number,short_description,opened_by,opened_at,resolved_by,resolved_at,closed_by,closed_at,sys_updated_by,sys_updated_on", incident);
        Assert.Contains(liveHandler.Calls, call => call.PathAndQuery.Contains("table/sys_journal_field", StringComparison.Ordinal));
        Assert.Contains(liveHandler.Calls, call => call.PathAndQuery.Contains("table/sys_audit", StringComparison.Ordinal));
        Assert.Contains("short_description", incident);
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
    public async Task FirstOpenLoadsAndASecondViewTheSameDayDoesNotQuery()
    {
        using var client = new SampleServiceNowClient();
        var page = new WorkEffortViewModel();
        var queries = new Dictionary<WorkEffortScale, int>();

        await OpenWorkEffortAsync(page, client, Now, force: false, queries);
        Assert.Equal(1, queries[WorkEffortScale.Today]);
        Assert.Equal("As of 15:00", page.AsOf);
        Assert.True(page.HasRows);
        Assert.Equal("Alex Rivera", page.Rows[0].Name);

        await OpenWorkEffortAsync(page, client, Now.AddHours(1), force: false, queries);
        Assert.Equal(1, queries[WorkEffortScale.Today]);
        Assert.Equal("As of 15:00", page.AsOf);
        Assert.Equal("Alex Rivera", page.Rows[0].Name);
    }

    [Fact]
    public async Task ANewLocalDayQueriesAgain()
    {
        using var client = new SampleServiceNowClient();
        var page = new WorkEffortViewModel();
        var queries = new Dictionary<WorkEffortScale, int>();

        await OpenWorkEffortAsync(page, client, Now, force: false, queries);
        await OpenWorkEffortAsync(page, client, Now.AddDays(1), force: false, queries);

        Assert.Equal(2, queries[WorkEffortScale.Today]);
        Assert.Equal("As of 15:00", page.AsOf);
    }

    [Fact]
    public async Task ManualRefreshQueriesOnlyTheSelectedScaleAgain()
    {
        using var client = new SampleServiceNowClient();
        var page = new WorkEffortViewModel();
        var queries = new Dictionary<WorkEffortScale, int>();
        var refreshRequests = 0;
        page.RefreshRequested += (_, _) => refreshRequests++;

        await OpenWorkEffortAsync(page, client, Now, force: false, queries);
        page.Scale = WorkEffortScale.ThisWeek;
        await OpenWorkEffortAsync(page, client, Now, force: false, queries);

        page.RefreshCommand.Execute(null);
        Assert.Equal(1, refreshRequests);

        page.Scale = WorkEffortScale.Today;
        await OpenWorkEffortAsync(page, client, Now.AddMinutes(30), force: true, queries);

        Assert.Equal(2, queries[WorkEffortScale.Today]);
        Assert.Equal(1, queries[WorkEffortScale.ThisWeek]);
        Assert.Equal("As of 15:30", page.AsOf);
    }

    [Fact]
    public async Task SwitchingToAnUncachedTimeScaleLoadsThatScale()
    {
        using var client = new SampleServiceNowClient();
        var page = new WorkEffortViewModel();
        var queries = new Dictionary<WorkEffortScale, int>();

        await OpenWorkEffortAsync(page, client, Now, force: false, queries);
        page.Scale = WorkEffortScale.SixMonths;
        await OpenWorkEffortAsync(page, client, Now, force: false, queries);
        page.Scale = WorkEffortScale.Today;
        await OpenWorkEffortAsync(page, client, Now.AddHours(2), force: false, queries);

        Assert.Equal(1, queries[WorkEffortScale.Today]);
        Assert.Equal(1, queries[WorkEffortScale.SixMonths]);
        Assert.Equal("As of 15:00", page.AsOf);
        Assert.Equal(2.9m, page.Rows[0].Weighted);
    }

    [Fact]
    public async Task LeavingWorkEffortDoesNotCancelTheLoadAndTheNextVisitUsesTheCache()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proxy = DispatchProxy.Create<IServiceNowClient, WorkEffortHold>();
        var hold = (WorkEffortHold)(object)proxy;
        hold.Inner = new SampleServiceNowClient();
        hold.Ready = ready;

        var main = new MainViewModel(
            new MemorySettingsStore(),
            new RecordingDesktopServices(),
            sampleClientFactory: () => proxy);
        main.Connection.UseSampleData = true;
        main.Connection.DownloadCacheOnLaunch = false;
        main.Connection.LeadsPassword = "iddqd";
        // Aus DT roster includes jordan and sam; keep the saved team inside that set so a
        // late roster load does not change the Work Effort team key mid-test.
        main.Connection.RememberLeadTeam(["user-jordan", "user-sam"]);
        main.Connection.RememberLeadTeamSaved(true);
        main.Connection.UnlockLeads();
        await main.ConnectCommand.ExecuteAsync(null);
        Assert.True(main.IsConnected);
        Assert.True(main.TrySelect(DeskSection.Leads));
        await WaitUntilAsync(() => main.Leads.Members.Count(member => member.IsSelected) >= 2);

        main.Leads.Area = LeadArea.WorkEffort;

        await WaitUntilAsync(() => hold.Queries >= 1 && main.Leads.WorkEffort.IsLoading);
        var queriesAfterOpen = hold.Queries;
        Assert.True(main.Leads.WorkEffort.IsLoading);
        Assert.Equal(WorkEffortEstimate.BarMaximum, main.Leads.WorkEffort.ProgressMaximum);
        Assert.Equal(WorkEffortEstimate.Text(1), main.Leads.WorkEffort.Status);
        Assert.False(main.IsBusy);
        var token = hold.Tokens[^1];
        Assert.False(token.IsCancellationRequested);

        main.Leads.Area = LeadArea.Team;
        main.SelectedSection = DeskSection.Incidents;
        Assert.Equal(DeskSection.Incidents, main.SelectedSection);
        Assert.False(main.IsBusy);
        Assert.False(token.IsCancellationRequested);
        Assert.True(main.Leads.WorkEffort.IsLoading);

        ready.SetResult();
        await WaitUntilAsync(() =>
            !main.Leads.WorkEffort.IsLoading
            && main.Leads.WorkEffort.HasRows
            && main.Leads.WorkEffort.AsOf.StartsWith("As of ", StringComparison.Ordinal));

        Assert.False(main.Leads.WorkEffort.IsLoading);
        Assert.Contains(main.Leads.WorkEffort.Rows, row => row.Name == "Jordan Lee");
        Assert.StartsWith("As of ", main.Leads.WorkEffort.AsOf, StringComparison.Ordinal);
        Assert.Contains("Counts for today", main.Leads.WorkEffort.Status, StringComparison.Ordinal);

        // Re-enter Leads first and wait for the Aus DT ticks so a late SetRoster cannot
        // retarget the team key between BeginLoad and the waiter.
        Assert.True(main.TrySelect(DeskSection.Leads));
        await WaitUntilAsync(() => main.Leads.Members.Count(member => member.IsSelected) >= 2);
        var queriesBeforeReturn = hold.Queries;
        main.Leads.Area = LeadArea.WorkEffort;
        // Ready already completed: any refresh HoldAsync must finish without another SetResult.
        await WaitUntilAsync(() =>
            !main.Leads.WorkEffort.IsLoading
            && main.Leads.WorkEffort.HasRows
            && main.Leads.WorkEffort.AsOf.StartsWith("As of ", StringComparison.Ordinal));
        Assert.False(main.Leads.WorkEffort.IsLoading);
        Assert.True(main.Leads.WorkEffort.HasRows);
        Assert.Contains(main.Leads.WorkEffort.Rows, row => row.Name == "Jordan Lee");
        // Usually the cache answers without another query. A late roster tick may refresh once.
        Assert.True(hold.Queries >= queriesAfterOpen);
        Assert.True(hold.Queries <= queriesBeforeReturn + 1);

        main.DisconnectCommand.Execute(null);
    }

    [Fact]
    public async Task ADifferentTimeScaleCancelsOnlyThePreviousWorkEffortLoad()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proxy = DispatchProxy.Create<IServiceNowClient, WorkEffortHold>();
        var hold = (WorkEffortHold)(object)proxy;
        hold.Inner = new SampleServiceNowClient();
        hold.Ready = ready;

        var main = new MainViewModel(
            new MemorySettingsStore(),
            new RecordingDesktopServices(),
            sampleClientFactory: () => proxy);
        main.Connection.UseSampleData = true;
        main.Connection.DownloadCacheOnLaunch = false;
        main.Connection.LeadsPassword = "iddqd";
        main.Connection.RememberLeadTeam(["user-jordan", "user-sam"]);
        main.Connection.RememberLeadTeamSaved(true);
        main.Connection.UnlockLeads();
        await main.ConnectCommand.ExecuteAsync(null);

        Assert.True(main.TrySelect(DeskSection.Leads));
        await WaitUntilAsync(() => main.Leads.Members.Count(member => member.IsSelected) >= 2);
        main.Leads.Area = LeadArea.WorkEffort;
        await WaitUntilAsync(() => hold.Queries >= 1 && main.Leads.WorkEffort.IsLoading);
        var first = hold.Tokens[^1];
        var queriesAfterOpen = hold.Queries;

        main.Leads.WorkEffort.Scale = WorkEffortScale.ThisWeek;
        Assert.True(first.IsCancellationRequested);
        await WaitUntilAsync(() => hold.Queries > queriesAfterOpen);
        Assert.False(hold.Tokens[^1].IsCancellationRequested);
        Assert.True(main.Leads.WorkEffort.IsLoading);
        Assert.Equal(WorkEffortEstimate.Text(1), main.Leads.WorkEffort.Status);

        ready.SetResult();
        await WaitUntilAsync(() => !main.Leads.WorkEffort.IsLoading && main.Leads.WorkEffort.HasRows);
        Assert.Contains("this week", main.Leads.WorkEffort.Status, StringComparison.Ordinal);
        var afterWeek = hold.Queries;
        Assert.True(afterWeek > queriesAfterOpen);

        main.Leads.WorkEffort.Scale = WorkEffortScale.Today;
        await WaitUntilAsync(() => !main.Leads.WorkEffort.IsLoading && main.Leads.WorkEffort.HasRows && hold.Queries > afterWeek);
        Assert.Contains("today", main.Leads.WorkEffort.Status, StringComparison.Ordinal);
        var afterToday = hold.Queries;

        main.Leads.WorkEffort.Scale = WorkEffortScale.ThisWeek;
        Assert.Equal(afterToday, hold.Queries);
        Assert.False(main.Leads.WorkEffort.IsLoading);
        Assert.Contains("this week", main.Leads.WorkEffort.Status, StringComparison.Ordinal);

        main.Leads.WorkEffort.RefreshCommand.Execute(null);
        await WaitUntilAsync(() => !main.Leads.WorkEffort.IsLoading && hold.Queries > afterToday);
        Assert.True(main.Leads.WorkEffort.HasRows);

        main.DisconnectCommand.Execute(null);
    }

    [Fact]
    public void ARunningLoadStillFinishesAfterAnotherBeginAndTheSameDayDoesNotQueryAgain()
    {
        var page = new WorkEffortViewModel();
        Assert.True(page.BeginLoad(Now, force: false));
        Assert.True(page.IsLoading);
        Assert.Equal(0, page.ProgressValue);
        Assert.Equal(WorkEffortEstimate.Text(1), page.Status);

        Assert.False(page.BeginLoad(Now.AddMinutes(10), force: false));
        Assert.True(page.IsLoading);
        Assert.Equal(0, page.ProgressValue);

        page.Apply(WorkEffortScale.Today, WorkEffortProgress.Loading(WorkEffortScale.Today, 1));
        var afterIncidents = page.ProgressValue;
        Assert.True(afterIncidents > 0);
        Assert.Equal(WorkEffortEstimate.Text(1), page.Status);
        page.Apply(WorkEffortScale.Today, WorkEffortProgress.Loading(WorkEffortScale.Today, 2));
        Assert.True(page.ProgressValue > afterIncidents);
        Assert.Equal(WorkEffortEstimate.Text(1), page.Status);

        page.Remember(WorkEffortScale.Today, Now, SampleWorkEffort.Report(WorkEffortScale.Today, Now));
        Assert.False(page.IsLoading);
        Assert.True(page.HasRows);
        Assert.Equal("As of 15:00", page.AsOf);
        Assert.Contains("Counts for today", page.Status, StringComparison.Ordinal);

        Assert.False(page.BeginLoad(Now.AddHours(2), force: false));
        Assert.False(page.IsLoading);
        Assert.Equal("Alex Rivera", page.Rows[0].Name);
    }

    [Fact]
    public async Task LiveLoadReportsIncidentsThenRequestItemsThenInteractions()
    {
        var handler = new StubHandler(GroupResponder(includeMembers: true, incidentTotal: null, rejectInteractionResolve: true));
        using var live = ServiceNowClient.Create(Api.BasicSession(), handler);
        var ticks = new ProgressList();
        var report = await live.GetWorkEffortAsync(WorkEffortScale.Today, Now, LiveTeam, ticks, CancellationToken.None);

        Assert.NotEmpty(ticks.Ticks);
        Assert.All(ticks.Ticks, tick =>
        {
            Assert.Equal(WorkEffortEstimate.BarMaximum, tick.Total);
            Assert.StartsWith("Generating report. check back in: ", tick.Status, StringComparison.Ordinal);
            Assert.EndsWith(" minutes", tick.Status, StringComparison.Ordinal);
        });
        Assert.Contains(ticks.Ticks, tick => tick.Status == WorkEffortEstimate.Text(1));
        Assert.True(ticks.Ticks[^1].Completed >= ticks.Ticks[0].Completed);
        Assert.Contains(handler.Calls, call => call.PathAndQuery.Contains("table/incident", StringComparison.Ordinal));
        Assert.Contains(handler.Calls, call => call.PathAndQuery.Contains("table/sc_req_item", StringComparison.Ordinal));
        Assert.Contains(handler.Calls, call => call.PathAndQuery.Contains("table/interaction", StringComparison.Ordinal));
        Assert.Equal("Alex Rivera", report.Rows[0].Name);
        Assert.False(string.IsNullOrWhiteSpace(report.Status));
    }

    [Fact]
    public async Task HittingTheSafetyCapSaysTheFiguresArePartial()
    {
        var handler = new StubHandler(GroupResponder(includeMembers: true, incidentTotal: 9, rejectInteractionResolve: false));
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);
        var report = await client.GetWorkEffortAsync(WorkEffortScale.Today, Now, safetyCap: 1, CancellationToken.None, team: LiveTeam);
        Assert.Contains("safety cap", report.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(WorkEffortQuery.CapNotice, report.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUpdateOnALaterDayStillCountsAfterAnOpenInTheSameWindow()
    {
        var monday = new DateTime(2026, 10, 5, 9, 0, 0);
        var wednesday = new DateTime(2026, 10, 7, 11, 30, 0);
        var window = WorkEffortWindow.For(WorkEffortScale.ThisWeek, Now);
        var touch = new WorkEffortTouch(
            "inc-return",
            WorkEffortKind.Incident,
            "sample-user",
            monday,
            null,
            null,
            null,
            null,
            "alex.rivera",
            monday,
            new WorkEffortUpdate[] { new("alex.rivera", monday), new("alex.rivera", wednesday) });

        var daily = Assert.Single(WorkEffortScore.Build([Alex], [touch], window, WorkEffortUpdateMode.Daily));
        var multiple = Assert.Single(WorkEffortScore.Build([Alex], [touch], window, WorkEffortUpdateMode.Multiple));
        Assert.Equal(1, daily.IncOpened);
        Assert.Equal(1, daily.IncUpdated);
        Assert.Equal(1, multiple.IncUpdated);
        Assert.Equal(1.3m, daily.Weighted);
        Assert.Equal(WorkEffortWindow.LocalDay(wednesday), WorkEffortWindow.LocalDay(Now));
        Assert.NotEqual(WorkEffortWindow.LocalDay(monday), WorkEffortWindow.LocalDay(wednesday));
    }

    [Fact]
    public void SameDaySavesCollapseAndTheSameMomentIsCountedOnce()
    {
        var morning = new DateTime(2026, 10, 7, 9, 0, 0);
        var noon = new DateTime(2026, 10, 7, 12, 0, 0);
        var window = WorkEffortWindow.For(WorkEffortScale.Today, Now);
        var jordan = new WorkEffortPerson("user-jordan", "Jordan Lee", "jordan.lee");
        var touch = new WorkEffortTouch(
            "inc-chase",
            WorkEffortKind.Incident,
            null,
            null,
            null,
            null,
            null,
            null,
            "jordan.lee",
            Now,
            new WorkEffortUpdate[]
            {
                new("jordan.lee", morning),
                new("jordan.lee", noon),
                new("jordan.lee", Now)
            });

        var daily = Assert.Single(WorkEffortScore.Build([jordan], [touch], window, WorkEffortUpdateMode.Daily));
        var multiple = Assert.Single(WorkEffortScore.Build([jordan], [touch], window, WorkEffortUpdateMode.Multiple));
        Assert.Equal(1, daily.IncUpdated);
        Assert.Equal(3, multiple.IncUpdated);
        Assert.Equal(0.3m, daily.Weighted);
        Assert.Equal(0.9m, multiple.Weighted);
        Assert.Equal(
            WorkEffortWindow.LocalStamp(Now),
            WorkEffortWindow.LocalStamp(new DateTime(2026, 10, 7, 15, 0, 0, 400)));
    }

    [Fact]
    public void TwoDaysOnOneTicketAreTwoDailyCredits()
    {
        var monday = new DateTime(2026, 10, 5, 10, 0, 0);
        var wednesdayMorning = new DateTime(2026, 10, 7, 9, 0, 0);
        var touch = new WorkEffortTouch(
            "ritm-chase",
            WorkEffortKind.RequestedItem,
            null,
            null,
            null,
            null,
            null,
            null,
            "alex.rivera",
            Now,
            new WorkEffortUpdate[] { new("alex.rivera", monday), new("alex.rivera", wednesdayMorning) });
        var week = WorkEffortWindow.For(WorkEffortScale.ThisWeek, Now);
        var daily = Assert.Single(WorkEffortScore.Build([Alex], [touch], week, WorkEffortUpdateMode.Daily));
        var multiple = Assert.Single(WorkEffortScore.Build([Alex], [touch], week, WorkEffortUpdateMode.Multiple));
        Assert.Equal(2, daily.RitmUpdated);
        Assert.Equal(3, multiple.RitmUpdated);
        Assert.Equal("Allow multiple updates adds 1 extra update credit versus daily updates.", WorkEffortScore.ShiftLine([daily], [multiple]));
    }

    [Fact]
    public void ResolveDayDoesNotAlsoCountAsAnUpdateAndTheNextDayDoes()
    {
        var tuesday = new DateTime(2026, 10, 6, 16, 0, 0);
        var touch = new WorkEffortTouch(
            "ims-done",
            WorkEffortKind.Interaction,
            null,
            null,
            null,
            null,
            "sample-user",
            tuesday,
            "alex.rivera",
            tuesday,
            new WorkEffortUpdate[] { new("alex.rivera", tuesday.AddHours(1)), new("alex.rivera", Now) });
        var week = WorkEffortWindow.For(WorkEffortScale.ThisWeek, Now);
        var row = Assert.Single(WorkEffortScore.Build([Alex], [touch], week, WorkEffortUpdateMode.Multiple));
        Assert.Equal(1, row.ImsResolved);
        Assert.Equal(1, row.ImsUpdated);
        Assert.Equal(0.9m, row.Weighted);
    }

    [Fact]
    public async Task SwitchingUpdateModeRescoresCachedEventsWithoutAnotherQuery()
    {
        using var client = new SampleServiceNowClient();
        var page = new WorkEffortViewModel();
        var queries = new Dictionary<WorkEffortScale, int>();

        await OpenWorkEffortAsync(page, client, Now, force: false, queries);
        Assert.Equal(WorkEffortUpdateMode.Daily, page.UpdateMode);
        Assert.Equal(1, page.Rows.Single(row => row.Name == "Jordan Lee").IncUpdated);
        Assert.Equal(1, page.Rows[0].RitmUpdated);
        Assert.Equal(2.9m, page.Rows[0].Weighted);
        Assert.Equal("Allow multiple updates adds 3 extra update credits versus daily updates.", page.Shift);

        page.UpdateMode = WorkEffortUpdateMode.Multiple;
        Assert.Equal(1, queries[WorkEffortScale.Today]);
        Assert.False(page.IsLoading);
        Assert.Equal(3, page.Rows.Single(row => row.Name == "Jordan Lee").IncUpdated);
        Assert.Equal(2, page.Rows[0].RitmUpdated);
        Assert.Equal(3.2m, page.Rows[0].Weighted);
        Assert.Equal(0.9m, page.Rows.Single(row => row.Name == "Jordan Lee").Weighted);
        Assert.Equal("As of 15:00", page.AsOf);
        Assert.Equal("Allow multiple updates adds 3 extra update credits versus daily updates.", page.Shift);

        page.UpdateMode = WorkEffortUpdateMode.Daily;
        Assert.Equal(1, queries[WorkEffortScale.Today]);
        Assert.Equal(1, page.Rows.Single(row => row.Name == "Jordan Lee").IncUpdated);
        Assert.Equal(2.9m, page.Rows[0].Weighted);
    }

    [Fact]
    public async Task JournalAndAuditHistoryCountEarlierDaysWithoutWipingTheWindow()
    {
        var handler = new StubHandler(HistoryResponder());
        using var live = ServiceNowClient.Create(Api.BasicSession(), handler);
        var report = await live.GetWorkEffortAsync(WorkEffortScale.ThisWeek, Now, [Alex], CancellationToken.None);
        var daily = Assert.Single(report.Rows);
        Assert.Equal("Alex Rivera", daily.Name);
        Assert.Equal(1, daily.IncOpened);
        Assert.Equal(0, daily.IncResolved);
        Assert.Equal(1, daily.IncUpdated);
        Assert.Equal(1, daily.RitmUpdated);
        Assert.Equal(1, daily.ImsUpdated);
        Assert.Equal(1.9m, daily.Weighted);
        Assert.DoesNotContain(WorkEffortQuery.HistoryNotice, report.Status, StringComparison.Ordinal);

        var multiple = WorkEffortScore.Present(report, WorkEffortUpdateMode.Multiple);
        var row = Assert.Single(multiple.Rows);
        Assert.Equal(3, row.IncUpdated);
        Assert.Equal(2, row.RitmUpdated);
        Assert.Equal(1, row.ImsUpdated);
        Assert.Equal(2.8m, row.Weighted);
        Assert.Equal("Allow multiple updates adds 3 extra update credits versus daily updates.", multiple.Shift);

        var journalCall = handler.Calls.Single(call => call.PathAndQuery.Contains("table/sys_journal_field", StringComparison.Ordinal));
        var journal = Uri.UnescapeDataString(journalCall.PathAndQuery);
        Assert.Contains("elementINcomments,additional_comments,work_notes", journal);
        Assert.Contains("sys_created_byINalex.rivera,sample-user", journal);
        Assert.Contains(WorkEffortQuery.JournalFields, journal);
        var audit = Uri.UnescapeDataString(handler.Calls.Single(call => call.PathAndQuery.Contains("table/sys_audit", StringComparison.Ordinal)).PathAndQuery);
        Assert.Contains("tablenameINincident,sc_req_item,interaction", audit);
        Assert.Contains("userINalex.rivera,sample-user", audit);
        Assert.Contains(
            handler.Calls,
            call => call.PathAndQuery.Contains("table/sc_req_item", StringComparison.Ordinal)
                && Uri.UnescapeDataString(call.PathAndQuery).Contains("sys_idINritm-9", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedJournalReadKeepsTheHeaderUpdateOnALaterDay()
    {
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            if (path.Contains("table/sys_journal_field", StringComparison.Ordinal))
                return Api.Json("""{"error":{"message":"ACL","detail":"not allowed"}}""", HttpStatusCode.Forbidden);
            if (path.Contains("table/sys_audit", StringComparison.Ordinal))
                return Api.Json("""{"error":{"message":"Invalid query","detail":"no such table"}}""", HttpStatusCode.BadRequest);
            if (path.Contains("table/incident", StringComparison.Ordinal))
                return Api.Json(HistoryIncidentJson);
            return Api.Json("""{"result":[]}""");
        });
        using var live = ServiceNowClient.Create(Api.BasicSession(), handler);
        var report = await live.GetWorkEffortAsync(WorkEffortScale.ThisWeek, Now, [Alex], CancellationToken.None);
        var row = Assert.Single(report.Rows);
        Assert.Equal(1, row.IncOpened);
        Assert.Equal(1, row.IncUpdated);
        Assert.Contains(WorkEffortQuery.HistoryNotice, report.Status, StringComparison.Ordinal);
        var multiple = WorkEffortScore.Present(report, WorkEffortUpdateMode.Multiple);
        Assert.Equal(1, Assert.Single(multiple.Rows).IncUpdated);
        Assert.Contains("no extra update credits", multiple.Shift, StringComparison.Ordinal);
    }

    [Fact]
    public void CellDetailMembershipMatchesTheScoreAndPersonDetailIsTheUnion()
    {
        var window = WorkEffortWindow.For(WorkEffortScale.Today, Now);
        var people = SampleWorkEffort.WithLogins(SampleTeam);
        var touches = SampleWorkEffort.Touches(Now);
        var rows = WorkEffortScore.Build(people, touches, window, WorkEffortUpdateMode.Daily);
        var credits = WorkEffortDetail.Build(people, touches, window, WorkEffortUpdateMode.Daily);
        var alex = rows.Single(row => row.Name == "Alex Rivera");
        var jordan = rows.Single(row => row.Name == "Jordan Lee");

        Assert.Equal(alex.IncOpened, WorkEffortDetail.ForCell(credits, alex.PersonSysId, WorkEffortColumn.IncOpened).Count);
        Assert.Equal(alex.IncResolved, WorkEffortDetail.ForCell(credits, alex.PersonSysId, WorkEffortColumn.IncResolved).Count);
        Assert.Equal(alex.RitmUpdated, WorkEffortDetail.ForCell(credits, alex.PersonSysId, WorkEffortColumn.RitmUpdated).Count);
        Assert.Equal(alex.ImsResolved, WorkEffortDetail.ForCell(credits, alex.PersonSysId, WorkEffortColumn.ImsResolved).Count);
        Assert.Equal(jordan.IncUpdated, WorkEffortDetail.ForCell(credits, jordan.PersonSysId, WorkEffortColumn.IncUpdated).Count);

        var alexAll = WorkEffortDetail.ForPerson(credits, alex.PersonSysId);
        Assert.Equal(
            alex.IncOpened + alex.IncResolved + alex.IncUpdated
            + alex.RitmOpened + alex.RitmResolved + alex.RitmUpdated
            + alex.ImsOpened + alex.ImsResolved + alex.ImsUpdated,
            alexAll.Count);
        Assert.Contains(alexAll, line => line.DisplayNumber == "INC0010001" && line.MetricLabel == "opened");
        Assert.Contains(alexAll, line => line.DisplayNumber == "IMS0010003" && line.MetricLabel == "closed");
        Assert.Contains(alexAll, line => line.DisplayNumber == "RITM0010002" && line.MetricLabel == "updated");
        Assert.DoesNotContain(alexAll, line => line.PersonSysId == jordan.PersonSysId);
    }

    [Fact]
    public void DailyVersusMultipleChangesUpdatedDetailLines()
    {
        var window = WorkEffortWindow.For(WorkEffortScale.Today, Now);
        var people = SampleWorkEffort.WithLogins(SampleTeam);
        var touches = SampleWorkEffort.Touches(Now);
        var daily = WorkEffortDetail.Build(people, touches, window, WorkEffortUpdateMode.Daily);
        var multiple = WorkEffortDetail.Build(people, touches, window, WorkEffortUpdateMode.Multiple);

        var jordanDaily = WorkEffortDetail.ForCell(daily, SampleWorkEffort.Jordan.SysId, WorkEffortColumn.IncUpdated);
        var jordanMultiple = WorkEffortDetail.ForCell(multiple, SampleWorkEffort.Jordan.SysId, WorkEffortColumn.IncUpdated);
        Assert.Single(jordanDaily);
        Assert.Equal(3, jordanMultiple.Count);
        Assert.All(jordanDaily, line => Assert.False(string.IsNullOrWhiteSpace(line.Day)));
        Assert.All(jordanMultiple, line => Assert.Equal("", line.Day));

        var alexDaily = WorkEffortDetail.ForCell(daily, SampleWorkEffort.SignedIn.SysId, WorkEffortColumn.RitmUpdated);
        var alexMultiple = WorkEffortDetail.ForCell(multiple, SampleWorkEffort.SignedIn.SysId, WorkEffortColumn.RitmUpdated);
        Assert.Single(alexDaily);
        Assert.Equal(2, alexMultiple.Count);
    }

    [Fact]
    public async Task DetailAndExportUseLoadedTouchesForAlexAndJordan()
    {
        using var client = new SampleServiceNowClient();
        var desktop = new RecordingDesktopServices();
        var page = new WorkEffortViewModel();
        page.UseDesktop(desktop);
        var queries = new Dictionary<WorkEffortScale, int>();
        await OpenWorkEffortAsync(page, client, Now, force: false, queries);

        var alex = page.Rows.Single(row => row.Name == "Alex Rivera");
        page.ShowCellDetail(alex, WorkEffortColumn.RitmUpdated);
        Assert.True(page.ShowDetail);
        Assert.True(page.DetailHasRows);
        Assert.Equal(alex.RitmUpdated, page.DetailLines.Count);
        Assert.Contains(page.DetailLines, line => line.DisplayNumber == "RITM0010002");

        page.ExportDetailCommand.Execute(null);
        var detailPath = Assert.Single(desktop.SavedFiles);
        var detailCsv = File.ReadAllText(detailPath);
        Assert.Contains("RITM0010002", detailCsv, StringComparison.Ordinal);
        Assert.Contains("updated", detailCsv, StringComparison.Ordinal);
        Assert.Contains("RITM", detailCsv, StringComparison.Ordinal);

        var jordan = page.Rows.Single(row => row.Name == "Jordan Lee");
        page.ShowPersonDetail(jordan);
        Assert.Equal(jordan.IncUpdated, page.DetailLines.Count);
        Assert.Contains(page.DetailLines, line => line.DisplayNumber == "INC0010006");

        page.UpdateMode = WorkEffortUpdateMode.Multiple;
        Assert.Equal(3, page.DetailLines.Count);

        page.ShowCellDetail(alex, WorkEffortColumn.IncOpened);
        Assert.Equal(alex.IncOpened, page.Rows.Single(row => row.Name == "Alex Rivera").IncOpened);
        Assert.Single(page.DetailLines);

        page.ShowCellDetail(page.Rows.Single(row => row.Name == "Riley Chen"), WorkEffortColumn.IncOpened);
        Assert.True(page.ShowDetail);
        Assert.False(page.DetailHasRows);
        Assert.Equal(WorkEffortDetail.EmptyCellMessage, page.DetailEmptyMessage);

        desktop.SavedFiles.Clear();
        page.ExportBoardCommand.Execute(null);
        var boardPath = Assert.Single(desktop.SavedFiles);
        var boardCsv = File.ReadAllText(boardPath);
        Assert.Contains("INC0010001", boardCsv, StringComparison.Ordinal);
        Assert.Contains("INC0010006", boardCsv, StringComparison.Ordinal);
        Assert.Contains("opened", boardCsv, StringComparison.Ordinal);
        Assert.Contains("Alex Rivera", boardCsv, StringComparison.Ordinal);
        Assert.Contains("Jordan Lee", boardCsv, StringComparison.Ordinal);

        var unloaded = new WorkEffortViewModel();
        unloaded.UseDesktop(desktop);
        unloaded.ShowPersonDetail(alex);
        Assert.Equal(WorkEffortDetail.NotLoadedMessage, unloaded.DetailEmptyMessage);
    }

    private static Func<HttpRequestMessage, string, HttpResponseMessage> HistoryResponder() =>
        (request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery ?? "";
            var query = Uri.UnescapeDataString(path);
            if (path.Contains("table/sys_journal_field", StringComparison.Ordinal))
                return Api.Json(JournalHistoryJson);
            if (path.Contains("table/sys_audit", StringComparison.Ordinal))
                return Api.Json(AuditHistoryJson);
            if (path.Contains("table/sc_req_item", StringComparison.Ordinal))
            {
                if (query.Contains("sys_idIN", StringComparison.Ordinal))
                    return Api.Json(HistoryRitmLookupJson);
                return Api.Json("""{"result":[]}""");
            }

            if (path.Contains("table/incident", StringComparison.Ordinal))
            {
                if (query.Contains("sys_idIN", StringComparison.Ordinal))
                    return Api.Json("""{"result":[]}""");
                return Api.Json(HistoryIncidentJson);
            }

            return Api.Json("""{"result":[]}""");
        };

    private const string HistoryIncidentJson = """
        {"result":[{
          "sys_id":{"value":"inc-1","display_value":"inc-1"},
          "opened_by":{"value":"sample-user","display_value":"Alex Rivera"},
          "opened_at":{"value":"2026-10-05 09:00:00","display_value":"2026-10-05 09:00:00"},
          "resolved_by":{"value":"","display_value":""},
          "resolved_at":{"value":"","display_value":""},
          "closed_by":{"value":"","display_value":""},
          "closed_at":{"value":"","display_value":""},
          "sys_updated_by":{"value":"alex.rivera","display_value":"alex.rivera"},
          "sys_updated_on":{"value":"2026-10-07 15:00:00","display_value":"2026-10-07 15:00:00"}
        }]}
        """;

    private const string HistoryRitmLookupJson = """
        {"result":[{"sys_id":{"value":"ritm-9","display_value":"ritm-9"}}]}
        """;

    private const string JournalHistoryJson = """
        {"result":[
          {"sys_id":{"value":"j1","display_value":"j1"},"element_id":{"value":"inc-1","display_value":"inc-1"},"element":{"value":"work_notes","display_value":"Work notes"},"name":{"value":"incident","display_value":"incident"},"sys_created_by":{"value":"alex.rivera","display_value":"alex.rivera"},"sys_created_on":{"value":"2026-10-05 09:00:00","display_value":"2026-10-05 09:00:00"}},
          {"sys_id":{"value":"j2","display_value":"j2"},"element_id":{"value":"inc-1","display_value":"inc-1"},"element":{"value":"comments","display_value":"Comments"},"name":{"value":"incident","display_value":"incident"},"sys_created_by":{"value":"alex.rivera","display_value":"alex.rivera"},"sys_created_on":{"value":"2026-10-07 10:00:00","display_value":"2026-10-07 10:00:00"}},
          {"sys_id":{"value":"j3","display_value":"j3"},"element_id":{"value":"inc-1","display_value":"inc-1"},"element":{"value":"work_notes","display_value":"Work notes"},"name":{"value":"incident","display_value":"incident"},"sys_created_by":{"value":"alex.rivera","display_value":"alex.rivera"},"sys_created_on":{"value":"2026-10-07 14:00:00","display_value":"2026-10-07 14:00:00"}},
          {"sys_id":{"value":"j4","display_value":"j4"},"element_id":{"value":"inc-1","display_value":"inc-1"},"element":{"value":"work_notes","display_value":"Work notes"},"name":{"value":"incident","display_value":"incident"},"sys_created_by":{"value":"alex.rivera","display_value":"alex.rivera"},"sys_created_on":{"value":"2026-10-07 15:00:00","display_value":"2026-10-07 15:00:00"}},
          {"sys_id":{"value":"j5","display_value":"j5"},"element_id":{"value":"ritm-9","display_value":"ritm-9"},"element":{"value":"work_notes","display_value":"Work notes"},"name":{"value":"task","display_value":"task"},"sys_created_by":{"value":"alex.rivera","display_value":"alex.rivera"},"sys_created_on":{"value":"2026-10-06 09:00:00","display_value":"2026-10-06 09:00:00"}},
          {"sys_id":{"value":"j6","display_value":"j6"},"element_id":{"value":"ritm-9","display_value":"ritm-9"},"element":{"value":"comments","display_value":"Comments"},"name":{"value":"task","display_value":"task"},"sys_created_by":{"value":"alex.rivera","display_value":"alex.rivera"},"sys_created_on":{"value":"2026-10-06 15:00:00","display_value":"2026-10-06 15:00:00"}}
        ]}
        """;

    private const string AuditHistoryJson = """
        {"result":[
          {"sys_id":{"value":"a1","display_value":"a1"},"documentkey":{"value":"ims-3","display_value":"ims-3"},"tablename":{"value":"interaction","display_value":"interaction"},"user":{"value":"alex.rivera","display_value":"alex.rivera"},"sys_created_on":{"value":"2026-10-07 16:00:00","display_value":"2026-10-07 16:00:00"}},
          {"sys_id":{"value":"a2","display_value":"a2"},"documentkey":{"value":"ims-3","display_value":"ims-3"},"tablename":{"value":"interaction","display_value":"interaction"},"user":{"value":"alex.rivera","display_value":"alex.rivera"},"sys_created_on":{"value":"2026-10-07 16:00:00","display_value":"2026-10-07 16:00:00"}}
        ]}
        """;

    private static async Task WaitUntilAsync(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!ready())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Timed out waiting for work effort.");
            await Task.Delay(15);
        }
    }

    private static async Task OpenWorkEffortAsync(
        WorkEffortViewModel page,
        SampleServiceNowClient client,
        DateTime localNow,
        bool force,
        Dictionary<WorkEffortScale, int> queries)
    {
        var scale = page.Scale;
        if (!page.BeginLoad(localNow, force))
            return;

        queries[scale] = queries.GetValueOrDefault(scale) + 1;
        var report = await client.GetWorkEffortAsync(scale, localNow, SampleTeam, CancellationToken.None);
        page.Remember(scale, localNow, report);
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

    private sealed class ProgressList : IProgress<WorkEffortProgress>
    {
        public List<WorkEffortProgress> Ticks { get; } = [];

        public void Report(WorkEffortProgress value) => Ticks.Add(value);
    }

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

public class WorkEffortHold : DispatchProxy
{
    public SampleServiceNowClient Inner { get; set; } = null!;

    public TaskCompletionSource Ready { get; set; } = null!;

    public int Queries;

    public List<CancellationToken> Tokens { get; } = [];

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null)
            throw new InvalidOperationException("Missing ServiceNow method.");
        if (targetMethod.Name == nameof(IServiceNowClient.GetWorkEffortAsync) && targetMethod.GetParameters().Length == 5)
            return HoldAsync(args ?? []);

        try
        {
            return targetMethod.Invoke(Inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private async Task<WorkEffortReport> HoldAsync(object?[] args)
    {
        var scale = (WorkEffortScale)args[0]!;
        var localNow = (DateTime)args[1]!;
        var progress = args[3] as IProgress<WorkEffortProgress>;
        var token = args[4] is CancellationToken cancellation ? cancellation : CancellationToken.None;
        Interlocked.Increment(ref Queries);
        Tokens.Add(token);
        progress?.Report(WorkEffortProgress.Loading(scale, 0));
        // Only block while the test gate is open. After ready.SetResult(), a return-visit
        // refresh must finish immediately — never wait on a gate that will not be signaled again.
        if (!Ready.Task.IsCompleted)
            await Ready.Task.WaitAsync(token).ConfigureAwait(false);
        progress?.Report(WorkEffortProgress.Loading(scale, 1));
        progress?.Report(WorkEffortProgress.Loading(scale, 2));
        return SampleWorkEffort.Report(scale, localNow);
    }
}
