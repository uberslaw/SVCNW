using ServiceNowDesk.Alerts;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class DailyWorkTests
{
    private static readonly DateTime Now = new(2026, 10, 6, 12, 0, 0);

    [Fact]
    public void UnattendedMeansOpenAndQuietForADay()
    {
        var quiet = Sample("quiet") with { UpdatedAt = Now.AddHours(-24) };
        var recent = Sample("recent") with { UpdatedAt = Now.AddHours(-23) };
        var onHold = Sample("hold") with
        {
            State = "On Hold",
            StateValue = "3",
            UpdatedAt = Now.AddDays(-2),
            FollowUp = Now.AddHours(-1)
        };
        var resolved = Sample("resolved") with { State = "Resolved", StateValue = "6", UpdatedAt = Now.AddDays(-5) };
        var unknown = Sample("unknown") with { Updated = "", UpdatedAt = null };

        Assert.True(AlertClassifier.IsUnattended(quiet, Now));
        Assert.False(AlertClassifier.IsUnattended(recent, Now));
        Assert.True(AlertClassifier.IsUnattended(quiet with { UpdatedBy = "alex.rivera" }, Now));
        Assert.False(AlertClassifier.IsUnattended(quiet with { UpdatedBy = "alex.rivera", UpdatedAt = Now.AddHours(-1) }, Now));
        Assert.True(AlertClassifier.IsStillOpen(onHold));
        Assert.True(AlertClassifier.IsUnattended(onHold, Now));
        Assert.False(AlertClassifier.IsStillOpen(resolved));
        Assert.False(AlertClassifier.IsUnattended(resolved, Now));
        Assert.False(AlertClassifier.IsUnattended(unknown, Now));

        var mine = new AssigneeScope("sample-user");
        var bucket = AlertClassifier.Bucket(
            AlertKind.Unattended,
            [quiet with { AssignedToSysId = "sample-user" }, quiet with { SysId = "other", Number = "INC2", AssignedToSysId = "user-jordan" }],
            Now,
            mine);
        Assert.Equal("INC-quiet", Assert.Single(bucket.Rows).Number);

        var resolvedToday = Sample("today") with { State = "Resolved", StateValue = "6", SlaHasBreached = true, UpdatedAt = Now };
        Assert.False(AlertClassifier.IsStillOpen(resolvedToday));
        Assert.False(AlertClassifier.Matches(AlertKind.SlaBreaching, resolvedToday, Now));
        Assert.Empty(AlertClassifier.Bucket(AlertKind.SlaBreaching, [resolvedToday], Now).Rows);
        Assert.Empty(DailyWorkRanker.Rank([resolvedToday], Now));
        Assert.True(AlertClassifier.IsStillOpen(DeskSection.Incidents, "3", "On Hold"));
    }

    [Fact]
    public void DailyWorkOrdersPriorityThenSlaThenCallerThenFollowUp()
    {
        var slaHigh = Sample("sla", "2", "2 - High") with { SlaHasBreached = true, UpdatedAt = Now.AddHours(-1) };
        var callerHigh = Sample("caller", "2", "2 - High") with { UpdatedBy = "jordan.lee", UpdatedAt = Now.AddHours(-1) };
        var followHigh = Sample("follow", "2", "2 - High") with
        {
            State = "On Hold",
            StateValue = "3",
            FollowUp = Now.AddHours(-2),
            UpdatedAt = Now.AddHours(-1)
        };
        var quietHigh = Sample("quiet", "2", "2 - High") with { UpdatedAt = Now.AddDays(-3) };
        var slaLow = Sample("low", "4", "4 - Low") with { SlaHasBreached = true, UpdatedAt = Now.AddDays(-3) };
        var resolved = Sample("done", "1", "1 - Critical") with { State = "Resolved", StateValue = "6", UpdatedAt = Now.AddDays(-4), SlaHasBreached = true };

        var ranked = DailyWorkRanker.Rank([quietHigh, slaLow, followHigh, resolved, callerHigh, slaHigh], Now);
        Assert.Equal(["INC-sla", "INC-caller", "INC-follow", "INC-quiet", "INC-low"], ranked.Select(item => item.Number).ToArray());
        Assert.Equal("SLA", ranked[0].Reasons);
        Assert.Contains("Caller updated", ranked[1].Reasons);
        Assert.Equal(2, ranked[0].PriorityRank);
        Assert.Equal(4, ranked[4].PriorityRank);
        Assert.DoesNotContain(ranked, item => item.Number == "INC-done");
    }

    [Fact]
    public void TheFirstCheckOfTheLocalDayFreezesTheReport()
    {
        var store = new MemoryDailyWorkStore();
        var morning = new DateTime(2026, 10, 6, 8, 0, 0);
        var first = Item("inc-a", "INC-A");
        var second = Item("inc-b", "INC-B");
        var third = Item("inc-c", "INC-C");
        var opened = DailyWorkReportBuilder.Build("user:sample-user", morning, [first, second], store);
        Assert.True(opened.CreatedNow);
        Assert.Empty(opened.Cleared);
        Assert.Empty(opened.Arrived);

        var later = DailyWorkReportBuilder.Build("user:sample-user", morning.AddHours(4), [second, third], store);
        Assert.False(later.CreatedNow);
        Assert.Equal("INC-A", Assert.Single(later.Cleared).Number);
        Assert.Equal("INC-C", Assert.Single(later.Arrived).Number);
        Assert.Equal(2, later.MorningCount);

        var nextDay = DailyWorkReportBuilder.Build("user:sample-user", morning.AddDays(1), [third], store);
        Assert.True(nextDay.CreatedNow);
        Assert.Empty(nextDay.Cleared);
        Assert.Equal(morning.AddDays(1), store.Find("user:sample-user", new DateOnly(2026, 10, 7))!.GeneratedAt);
    }

    [Fact]
    public void ASavedReportSurvivesANewStoreOnTheSameFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "svcnw-daily-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var day = new DateTime(2026, 10, 6, 9, 0, 0);
            var store = new FileDailyWorkStore(path);
            DailyWorkReportBuilder.Build("user:sample-user", day, [Item("inc-a", "INC-A")], store);
            var reloaded = new FileDailyWorkStore(path);
            var again = DailyWorkReportBuilder.Build("user:sample-user", day.AddHours(2), [], reloaded);
            Assert.False(again.CreatedNow);
            Assert.Equal("INC-A", Assert.Single(again.Cleared).Number);
            Assert.Null(reloaded.Find("user:sample-user", new DateOnly(2026, 10, 7)));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void TheDailyPageSwitchesBetweenTheUserAndTheTickedTeam()
    {
        var store = new MemoryDailyWorkStore();
        var now = new DateTime(2026, 10, 6, 9, 0, 0);
        var mine = Item("inc-mine", "INC-MINE");
        var team = Item("inc-team", "INC-TEAM");
        var page = new DailyWorkViewModel(store);
        page.Show(new DailyWorkBoard([mine], [team]), "sample-user", [], now);
        Assert.Equal("INC-MINE", Assert.Single(page.Attend).Number);
        Assert.Contains("matches the list", page.ReportNote, StringComparison.OrdinalIgnoreCase);

        page.Area = DailyWorkArea.Team;
        Assert.Contains("Leads", page.TeamPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(page.Attend);

        page.Show(new DailyWorkBoard([mine], [team]), "sample-user", ["user-jordan"], now.AddHours(1));
        Assert.Equal("INC-TEAM", Assert.Single(page.Attend).Number);
        Assert.Equal("", page.TeamPrompt);
        page.Area = DailyWorkArea.Mine;
        Assert.Equal("INC-MINE", Assert.Single(page.Attend).Number);
        Assert.Equal(DailyWorkRanker.AfterThoseHex, page.Attend[0].HighlightHex);
        Assert.Equal(DailyWorkRanker.ActFirstHex, page.ActFirstHex);
        Assert.Equal(DailyWorkRanker.NextHex, page.NextHex);
        Assert.Equal(DailyWorkRanker.AfterThoseHex, page.AfterThoseHex);
    }

    [Fact]
    public void IntroTextExplainsThePriorityColoursBriefly()
    {
        Assert.True(DailyWorkViewModel.IntroText.Length < 320);
        Assert.Contains("act first", DailyWorkViewModel.IntroText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Yellow", DailyWorkViewModel.IntroText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Green", DailyWorkViewModel.IntroText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SLA", DailyWorkViewModel.IntroText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Every 5 minutes", DailyWorkViewModel.IntroText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FirstClickSortsAscendingAndSecondClickSortsDescending()
    {
        var store = new MemoryDailyWorkStore();
        var now = new DateTime(2026, 10, 6, 9, 0, 0);
        var zebra = Item("inc-z", "INC0010") with { Title = "Zebra" };
        var alpha = Item("inc-a", "INC0002") with { Title = "Alpha" };
        var page = new DailyWorkViewModel(store);
        page.Show(new DailyWorkBoard([zebra, alpha], []), "sample-user", [], now);

        page.SortByCommand.Execute("Number");
        Assert.Equal("Number ▲", page.NumberHeader);
        Assert.Equal(["INC0002", "INC0010"], page.Attend.Select(row => row.Number).ToArray());

        page.SortByCommand.Execute("Number");
        Assert.Equal("Number ▼", page.NumberHeader);
        Assert.Equal(["INC0010", "INC0002"], page.Attend.Select(row => row.Number).ToArray());

        page.SortByCommand.Execute("Title");
        Assert.Equal("Title ▲", page.TitleHeader);
        Assert.Equal("Number", page.NumberHeader);
        Assert.Equal(["Alpha", "Zebra"], page.Attend.Select(row => row.Title).ToArray());
    }

    [Fact]
    public void ColourSortOrdersRedBeforeYellowBeforeGreen()
    {
        var store = new MemoryDailyWorkStore();
        var now = new DateTime(2026, 10, 6, 9, 0, 0);
        var green = Item("inc-green", "INC-GREEN");
        var yellow = Item("inc-yellow", "INC-YELLOW") with { UpdatedByCaller = true, Unattended = false };
        var red = Item("inc-red", "INC-RED") with { PriorityValue = "1", PriorityLabel = "1 - Critical" };
        var page = new DailyWorkViewModel(store);
        page.Show(new DailyWorkBoard([green, yellow, red], []), "sample-user", [], now);
        Assert.Equal(DailyWorkRanker.ActFirstHex, page.Attend.Single(row => row.Number == "INC-RED").HighlightHex);
        Assert.Equal(DailyWorkRanker.NextHex, page.Attend.Single(row => row.Number == "INC-YELLOW").HighlightHex);
        Assert.Equal(DailyWorkRanker.AfterThoseHex, page.Attend.Single(row => row.Number == "INC-GREEN").HighlightHex);

        page.SortByCommand.Execute("Colour");
        Assert.Equal("Colour ▲", page.ColourHeader);
        Assert.Equal(["INC-RED", "INC-YELLOW", "INC-GREEN"], page.Attend.Select(row => row.Number).ToArray());

        page.SortByCommand.Execute("Colour");
        Assert.Equal("Colour ▼", page.ColourHeader);
        Assert.Equal(["INC-GREEN", "INC-YELLOW", "INC-RED"], page.Attend.Select(row => row.Number).ToArray());
    }

    [Fact]
    public void SwitchingMineAndTeamRestoresTheDefaultOrder()
    {
        var store = new MemoryDailyWorkStore();
        var now = new DateTime(2026, 10, 6, 9, 0, 0);
        var first = Item("inc-a", "INC-A") with { PriorityValue = "2", PriorityLabel = "2 - High" };
        var second = Item("inc-b", "INC-B") with { PriorityValue = "4", PriorityLabel = "4 - Low" };
        var teamOnly = Item("inc-team", "INC-TEAM");
        var page = new DailyWorkViewModel(store);
        page.Show(new DailyWorkBoard([first, second], [teamOnly]), "sample-user", ["user-jordan"], now);
        Assert.Equal(["INC-A", "INC-B"], page.Attend.Select(row => row.Number).ToArray());

        page.SortByCommand.Execute("Number");
        page.SortByCommand.Execute("Number");
        Assert.Equal("Number ▼", page.NumberHeader);
        Assert.Equal(["INC-B", "INC-A"], page.Attend.Select(row => row.Number).ToArray());

        page.Area = DailyWorkArea.Team;
        Assert.Equal("Number", page.NumberHeader);
        Assert.Equal("INC-TEAM", Assert.Single(page.Attend).Number);

        page.Area = DailyWorkArea.Mine;
        Assert.Equal("Number", page.NumberHeader);
        Assert.Equal(["INC-A", "INC-B"], page.Attend.Select(row => row.Number).ToArray());
    }

    [Fact]
    public void DailyWorkRowsUseTrafficLightHues()
    {
        var quiet = Item("inc-quiet", "INC-QUIET");
        var critical = quiet with { SysId = "inc-p1", Number = "INC-P1", PriorityValue = "1", PriorityLabel = "1 - Critical" };
        var criticalLabel = quiet with { SysId = "inc-label", Number = "INC-LABEL", PriorityValue = "", PriorityLabel = "1 - Critical" };
        var missing = quiet with { SysId = "inc-blank", Number = "INC-BLANK", PriorityValue = "", PriorityLabel = "" };
        var sla = quiet with { SysId = "inc-sla", Number = "INC-SLA", SlaBreaching = true, PriorityValue = "4", PriorityLabel = "4 - Low" };
        var slaBlank = missing with { SysId = "inc-sla-blank", Number = "INC-SLA-BLANK", SlaBreaching = true };
        var caller = quiet with { SysId = "inc-caller", Number = "INC-CALLER", UpdatedByCaller = true, Unattended = true };
        var follow = quiet with { SysId = "inc-follow", Number = "INC-FOLLOW", FollowUpPassed = true, Unattended = false };
        var returned = quiet with { SysId = "inc-back", Number = "INC-BACK", Unattended = false, ReturnedWithNotes = true };
        Assert.Equal("Returned by DT", returned.Reasons);
        var both = critical with { UpdatedByCaller = true, FollowUpPassed = true, SlaBreaching = true };

        Assert.Equal(DailyWorkRanker.ActFirstHex, DailyWorkRanker.HighlightHex(critical));
        Assert.Equal(DailyWorkRanker.ActFirstHex, DailyWorkRanker.HighlightHex(criticalLabel));
        Assert.Equal(DailyWorkRanker.AfterThoseHex, DailyWorkRanker.HighlightHex(missing));
        Assert.Equal(99, missing.PriorityRank);
        Assert.Equal(DailyWorkRanker.ActFirstHex, DailyWorkRanker.HighlightHex(sla));
        Assert.Equal(DailyWorkRanker.ActFirstHex, DailyWorkRanker.HighlightHex(slaBlank));
        Assert.Equal(DailyWorkRanker.NextHex, DailyWorkRanker.HighlightHex(caller));
        Assert.Equal(DailyWorkRanker.NextHex, DailyWorkRanker.HighlightHex(follow));
        Assert.Equal(DailyWorkRanker.AfterThoseHex, DailyWorkRanker.HighlightHex(returned));
        Assert.Equal(DailyWorkRanker.AfterThoseHex, DailyWorkRanker.HighlightHex(quiet));
        Assert.Equal(DailyWorkRanker.ActFirstHex, DailyWorkRanker.HighlightHex(both));
        Assert.Equal(DailyWorkRanker.ActFirstHex, DailyWorkRow.From(both).HighlightHex);

        var store = new MemoryDailyWorkStore();
        var now = new DateTime(2026, 10, 6, 9, 0, 0);
        var page = new DailyWorkViewModel(store);
        page.Show(new DailyWorkBoard([quiet], []), "sample-user", [], now);
        Assert.Equal(DailyWorkRanker.AfterThoseHex, Assert.Single(page.Attend).HighlightHex);

        page.Show(new DailyWorkBoard([critical], []), "sample-user", [], now.AddHours(2));
        Assert.Equal(DailyWorkRanker.ActFirstHex, Assert.Single(page.Attend).HighlightHex);
        Assert.Equal(DailyWorkRanker.ActFirstHex, Assert.Single(page.Arrived).HighlightHex);
        var cleared = Assert.Single(page.Cleared);
        Assert.Equal("INC-QUIET", cleared.Number);
        Assert.Equal("", cleared.HighlightHex);
    }

    [Fact]
    public async Task PracticeDailyWorkAndLeadQueuesFollowTheSignedInUserAndTheTickedTeam()
    {
        using var client = new SampleServiceNowClient();
        var user = await client.GetCurrentUserAsync(CancellationToken.None);
        var report = await client.GetAlertReportAsync(
            new AlertSearch(user.SysId, "Aus DT - Client Services", NotificationPreferences.DefaultLocations, ["user-jordan"]),
            CancellationToken.None);

        Assert.Empty(report.Personal.Bucket(AlertKind.OnHoldPastFollowUp).Rows);
        var personal = report.Daily.Personal;
        Assert.Contains(personal, item => item.Number == "INC0010010" && item.SlaBreaching);
        Assert.Contains(personal, item => item.Number == "INC0010001");
        Assert.Contains(personal, item => item.Number == "INC0010002");
        Assert.Contains(personal, item => item.Number == "INC0010006");
        Assert.Contains(personal, item => item.Number == "RITM0010001");
        Assert.Contains(personal, item => item.Number == "IMS0010001");
        Assert.Contains(personal, item => item.Number == "IMS0010003");
        // Melbourne mine fixtures sit in 2099, so Daily Work does not rank them as needing attention.
        Assert.Equal("INC0010010", personal[0].Number);
        Assert.Equal("SLA, Unattended", personal[0].Reasons);
        Assert.Equal(DailyWorkRanker.ActFirstHex, DailyWorkRow.From(personal[0]).HighlightHex);
        Assert.DoesNotContain(personal, item => item.Number == "INC0010017");
        Assert.DoesNotContain(personal, item => item.Number == "INC0010018");
        Assert.Contains(personal, item => DailyWorkRanker.HighlightHex(item) == DailyWorkRanker.ActFirstHex);
        Assert.Contains(personal, item => DailyWorkRanker.HighlightHex(item) == DailyWorkRanker.AfterThoseHex);

        var team = report.Daily.Team;
        Assert.Equal("INC0010016", team[0].Number);
        Assert.True(team[0].SlaBreaching);
        Assert.Equal("Jordan Lee", team[0].Assignee);
        Assert.Equal("INC0010005", team[1].Number);
        Assert.DoesNotContain(team, item => item.Number == "INC0010008");
        Assert.DoesNotContain(team, item => item.Number == "INC0010010");
        var moderate = team.Where(item => item.PriorityRank == 3).Select(item => item.Number).ToArray();
        Assert.Equal(["INC0010007", "INC0010011", "RITM0010003"], moderate);
        Assert.Equal(DailyWorkRanker.ActFirstHex, DailyWorkRow.From(team[0]).HighlightHex);
        Assert.Equal(DailyWorkRanker.AfterThoseHex, DailyWorkRow.From(team[1]).HighlightHex);
        Assert.Equal(DailyWorkRanker.NextHex, DailyWorkRow.From(team.Single(item => item.Number == "INC0010007")).HighlightHex);
        Assert.Equal(DailyWorkRanker.NextHex, DailyWorkRow.From(team.Single(item => item.Number == "INC0010011")).HighlightHex);
        Assert.Equal(DailyWorkRanker.AfterThoseHex, DailyWorkRow.From(team.Single(item => item.Number == "RITM0010003")).HighlightHex);
        var teamHues = team.Select(DailyWorkRanker.HighlightHex).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains(DailyWorkRanker.ActFirstHex, teamHues);
        Assert.Contains(DailyWorkRanker.NextHex, teamHues);
        Assert.Contains(DailyWorkRanker.AfterThoseHex, teamHues);

        var teamHold = report.Leads.For(LeadArea.Team).Bucket(AlertKind.OnHoldPastFollowUp).Rows;
        Assert.Contains(teamHold, row => row.Number == "INC0010011");
        Assert.DoesNotContain(report.Leads.For(LeadArea.Regional).Bucket(AlertKind.OnHoldPastFollowUp).Rows, row => row.Number == "INC0010011");
        Assert.Contains(report.Leads.For(LeadArea.Regional).Bucket(AlertKind.UpdatedByCaller).Rows, row => row.Number == "INC0010007");
        var regionalQuiet = report.Leads.For(LeadArea.Regional).Bucket(AlertKind.Unattended).Rows;
        Assert.Contains(regionalQuiet, row => row.Number == "INC0010008");
        Assert.DoesNotContain(regionalQuiet, row => row.Number == "INC0010011");
    }

    [Fact]
    public void LeadTeamNamesRoundTripAndARosterTickRaisesOnce()
    {
        var connection = new ConnectionViewModel();
        connection.RememberLeadTeam(["user-jordan", " user-jordan ", ""]);
        Assert.Equal(["user-jordan"], connection.LeadTeamMemberIds);
        var again = new ConnectionViewModel();
        again.Load(connection.BuildSettings());
        Assert.Equal(["user-jordan"], again.LeadTeamMemberIds);
        again.Load(new DeskSettings());
        Assert.Empty(again.LeadTeamMemberIds);

        var leads = new LeadsViewModel();
        var fired = 0;
        leads.TeamChanged += (_, _) => fired++;
        leads.SetRoster(
            [new Choice("user-jordan", "Jordan Lee"), new Choice("user-sam", "Sam Patel")],
            ["user-jordan"]);
        Assert.Equal(0, fired);
        Assert.Equal("user-jordan", Assert.Single(leads.SelectedMemberIds));
        leads.Members.Single(member => member.SysId == "user-sam").IsSelected = true;
        Assert.Equal(1, fired);
        Assert.Equal(2, leads.SelectedMemberIds.Count);
    }

    [Fact]
    public void ALaterPollReportsOnlyIdsThatWereNotAlreadySeen()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), GroupQueueTracker.Interval);
        var seen = new[] { "inc-email", "inc-returned" };
        var first = GroupQueueTracker.NewlyAppeared(seen, ["inc-returned", "inc-queue-new", "inc-queue-new"]);
        Assert.Equal(["inc-queue-new"], first);

        var remembered = seen.Concat(first).ToArray();
        var second = GroupQueueTracker.NewlyAppeared(remembered, ["inc-email", "inc-returned", "inc-queue-new"]);
        Assert.Empty(second);

        var baseline = GroupQueueTracker.Compare(UnassignedSeen.None, ["inc-email", "inc-returned"]);
        Assert.Empty(baseline.NewIds);
        Assert.True(baseline.State.BaselineTaken);
        var arrived = GroupQueueTracker.Compare(baseline.State, ["inc-email", "inc-returned", "inc-queue-new"]);
        Assert.Equal(["inc-queue-new"], arrived.NewIds);
        var again = GroupQueueTracker.Compare(arrived.State, ["inc-email", "inc-returned", "inc-queue-new"]);
        Assert.Empty(again.NewIds);
        Assert.Contains("inc-queue-new", again.State.Announced);
        Assert.Equal("1 new unassigned in the group queue.", GroupQueueTracker.StatusText(1));
        Assert.Null(GroupQueueTracker.StatusText(0));
    }

    [Fact]
    public void NewUnassignedStayOnDailyWorkAndTheMorningReportStaysPut()
    {
        var store = new MemoryDailyWorkStore();
        var now = new DateTime(2026, 10, 7, 8, 0, 0);
        var page = new DailyWorkViewModel(store);
        page.Show(new DailyWorkBoard([Item("inc-a", "INC-A")], []), "sample-user", [], now);
        var morning = store.Find("user:sample-user", new DateOnly(2026, 10, 7))!;
        Assert.Equal("INC-A", Assert.Single(morning.Lines).Number);

        var fresh = QueueRecord("inc-fresh", "INC-FRESH", "1", "1 - Critical", now);
        var quiet = QueueRecord("inc-quiet-new", "INC-QUIET-NEW", "3", "3 - Moderate", now.AddDays(-3));
        var high = QueueRecord("inc-high", "INC-HIGH", "2", "2 - High", now);
        var baseline = GroupQueueTracker.Compare(store.FindUnassigned("user:sample-user", morning.LocalDate), ["inc-old"]);
        store.SaveUnassigned("user:sample-user", morning.LocalDate, baseline.State);
        var step = GroupQueueTracker.Compare(baseline.State, ["inc-old", fresh.SysId, quiet.SysId, high.SysId]);
        page.ShowGroupQueue([fresh, quiet, high], step.State, now.AddHours(1));

        Assert.Equal(["INC-FRESH", "INC-HIGH", "INC-QUIET-NEW"], page.NewUnassigned.Select(row => row.Number).ToArray());
        Assert.Equal("P1", page.NewUnassigned[0].PriorityBadge);
        Assert.Equal(DailyWorkRanker.ActFirstHex, page.NewUnassigned[0].HighlightHex);
        Assert.True(page.NewUnassigned[0].EmphasizePriority);
        Assert.Equal("P2", page.NewUnassigned[1].PriorityBadge);
        Assert.Equal(DailyWorkRanker.NextHex, page.NewUnassigned[1].HighlightHex);
        Assert.True(page.NewUnassigned[1].EmphasizePriority);
        Assert.Equal("", page.NewUnassigned[2].PriorityBadge);
        Assert.False(page.NewUnassigned[2].EmphasizePriority);
        Assert.Equal("Client Services", page.NewUnassigned[0].Group);
        Assert.Equal("2026-10-07 08:00", page.NewUnassigned[0].When);
        Assert.Contains(page.Attend, row => row.Number == "INC-QUIET-NEW");
        Assert.Equal("INC-QUIET-NEW", Assert.Single(page.Arrived).Number);
        Assert.DoesNotContain(page.Arrived, row => row.Number == "INC-FRESH");
        Assert.DoesNotContain(page.Arrived, row => row.Number == "INC-HIGH");
        Assert.Equal(["INC-A"], store.Find("user:sample-user", morning.LocalDate)!.Lines.Select(line => line.Number).ToArray());

        DailyWorkRow? opened = null;
        page.OpenRequested += (_, row) => opened = row;
        page.OpenCommand.Execute(page.NewUnassigned[0]);
        Assert.Equal("inc-fresh", opened!.SysId);
        Assert.Equal(DeskSection.Incidents, opened.Section);

        var repeat = GroupQueueTracker.Compare(step.State, ["inc-old", fresh.SysId, quiet.SysId, high.SysId]);
        Assert.Empty(repeat.NewIds);
        page.ShowGroupQueue([fresh, quiet, high], repeat.State, now.AddHours(2));
        Assert.Equal(3, page.NewUnassigned.Count);
        Assert.Equal(["INC-A"], store.Find("user:sample-user", morning.LocalDate)!.Lines.Select(line => line.Number).ToArray());
    }

    [Fact]
    public void SeenUnassignedIdsSurviveARestartAndDoNotReplaceTheSnapshot()
    {
        var path = Path.Combine(Path.GetTempPath(), "svcnw-queue-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var day = new DateTime(2026, 10, 7, 9, 0, 0);
            var store = new FileDailyWorkStore(path);
            DailyWorkReportBuilder.Build("user:sample-user", day, [Item("inc-a", "INC-A")], store);
            var baseline = GroupQueueTracker.Compare(UnassignedSeen.None, ["inc-email"]);
            store.SaveUnassigned("user:sample-user", DateOnly.FromDateTime(day), baseline.State);
            var arrived = GroupQueueTracker.Compare(store.FindUnassigned("user:sample-user", DateOnly.FromDateTime(day)), ["inc-email", "inc-queue-new"]);
            store.SaveUnassigned("user:sample-user", DateOnly.FromDateTime(day), arrived.State);

            var reloaded = new FileDailyWorkStore(path);
            var again = GroupQueueTracker.Compare(
                reloaded.FindUnassigned("user:sample-user", DateOnly.FromDateTime(day)),
                ["inc-email", "inc-queue-new"]);
            Assert.Empty(again.NewIds);
            Assert.Contains("inc-queue-new", again.State.Announced);
            var report = DailyWorkReportBuilder.Build("user:sample-user", day.AddHours(2), [Item("inc-a", "INC-A")], reloaded);
            Assert.False(report.CreatedNow);
            Assert.Empty(report.Arrived);
            Assert.Equal("INC-A", Assert.Single(reloaded.Find("user:sample-user", DateOnly.FromDateTime(day))!.Lines).Number);
            Assert.Contains("inc-queue-new", reloaded.FindUnassigned("user:sample-user", DateOnly.FromDateTime(day)).Announced);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task PracticeDataShowsAGroupQueueArrivalAfterTheFirstLook()
    {
        using var client = new SampleServiceNowClient();
        var watched = "Aus DT - Client Services";
        var first = await client.ListUnassignedGroupQueueAsync(watched, null, CancellationToken.None);
        Assert.Contains(first, row => row.Number == "INC0010004");
        Assert.Contains(first, row => row.Number == "INC0010013");
        Assert.Contains(first, row => row.Number == "INC0010018" && row.Location == "Melbourne");
        Assert.DoesNotContain(first, row => row.Number == "INC0010017");
        Assert.DoesNotContain(first, row => row.Number == "INC0010005");
        Assert.DoesNotContain(first, row => row.Number == "INC0010007");
        Assert.All(first, row => Assert.True(string.IsNullOrWhiteSpace(row.AssignedToSysId)));

        using var mineOnly = new SampleServiceNowClient();
        var withoutWatched = await mineOnly.ListUnassignedGroupQueueAsync(null, null, CancellationToken.None);
        Assert.DoesNotContain(withoutWatched, row => row.Number == "INC0010018");
        Assert.Contains(withoutWatched, row => row.Number == "INC0010004");

        var store = new MemoryDailyWorkStore();
        var now = new DateTime(2026, 10, 7, 9, 30, 0);
        var day = DateOnly.FromDateTime(now);
        var step = GroupQueueTracker.Compare(UnassignedSeen.None, first.Select(row => row.SysId));
        Assert.Empty(step.NewIds);
        store.SaveUnassigned("user:sample-user", day, step.State);

        var page = new DailyWorkViewModel(store);
        var report = await client.GetAlertReportAsync(
            new AlertSearch("sample-user", watched, NotificationPreferences.DefaultLocations),
            CancellationToken.None);
        page.Show(report.Daily, "sample-user", [], now);
        var morning = store.Find("user:sample-user", day)!.Lines.Select(line => line.SysId).ToArray();

        var second = await client.ListUnassignedGroupQueueAsync(watched, null, CancellationToken.None);
        var arrived = GroupQueueTracker.Compare(store.FindUnassigned("user:sample-user", day), second.Select(row => row.SysId));
        Assert.Equal(["inc-queue-new"], arrived.NewIds);
        store.SaveUnassigned("user:sample-user", day, arrived.State);
        page.ShowGroupQueue(second, arrived.State, now.AddMinutes(5));

        var row = Assert.Single(page.NewUnassigned);
        Assert.Equal("INC0010017", row.Number);
        Assert.Equal("P1", row.PriorityBadge);
        Assert.True(row.EmphasizePriority);
        Assert.Equal(DailyWorkRanker.ActFirstHex, row.HighlightHex);
        Assert.Equal("Client Services", row.Group);
        Assert.Equal("Brisbane Office", row.Office);
        Assert.False(string.IsNullOrWhiteSpace(row.When));
        Assert.DoesNotContain(page.Arrived, item => item.Number == "INC0010017");
        Assert.DoesNotContain(page.Attend, item => item.Number == "INC0010017");
        Assert.Equal(morning, store.Find("user:sample-user", day)!.Lines.Select(line => line.SysId).ToArray());

        var third = await client.ListUnassignedGroupQueueAsync(watched, null, CancellationToken.None);
        var repeat = GroupQueueTracker.Compare(store.FindUnassigned("user:sample-user", day), third.Select(item => item.SysId));
        Assert.Empty(repeat.NewIds);
        page.ShowGroupQueue(third, repeat.State, now.AddMinutes(10));
        Assert.Equal("INC0010017", Assert.Single(page.NewUnassigned).Number);
    }

    [Fact]
    public async Task OfficeScopedGroupQueueExcludesMelbourneAndKeepsBrisbane()
    {
        using var client = new SampleServiceNowClient();
        var offices = NotificationPreferences.DefaultLocations;
        var watched = "Aus DT - Client Services";
        var rows = await client.ListUnassignedGroupQueueAsync(watched, offices, CancellationToken.None);

        Assert.Contains(rows, row => row.Number == "INC0010004" && row.Location == "Brisbane Office");
        Assert.Contains(rows, row => row.Number == "INC0010013" && row.Location == "Brisbane Office");
        Assert.DoesNotContain(rows, row => row.Number == "INC0010018");
        Assert.DoesNotContain(rows, row => string.Equals(row.Location, "Melbourne", StringComparison.OrdinalIgnoreCase));
        Assert.All(rows, row => Assert.True(OfficeQueue.Matches(row.Location, offices)));

        var query = AlertQueryBuilder.UnassignedInGroups(["group-cs"], watched, offices);
        Assert.NotNull(query);
        Assert.Contains("location.name=", query, StringComparison.Ordinal);
        Assert.Contains("Brisbane", query, StringComparison.Ordinal);

        var store = new MemoryDailyWorkStore();
        var now = new DateTime(2026, 10, 7, 9, 30, 0);
        var day = DateOnly.FromDateTime(now);
        var baseline = GroupQueueTracker.Compare(UnassignedSeen.None, rows.Select(row => row.SysId));
        store.SaveUnassigned("user:sample-user", day, baseline.State);

        var page = new DailyWorkViewModel(store);
        page.ShowGroupQueue(rows, baseline.State, now, offices);
        Assert.Empty(page.NewUnassigned);

        var second = await client.ListUnassignedGroupQueueAsync(watched, offices, CancellationToken.None);
        var arrived = GroupQueueTracker.Compare(store.FindUnassigned("user:sample-user", day), second.Select(row => row.SysId));
        store.SaveUnassigned("user:sample-user", day, arrived.State);
        page.ShowGroupQueue(second, arrived.State, now.AddMinutes(5), offices);

        var fresh = Assert.Single(page.NewUnassigned);
        Assert.Equal("INC0010017", fresh.Number);
        Assert.Equal("Brisbane Office", fresh.Office);
    }

    [Fact]
    public void NotPartOfMyQueueRemovesRowAndPersistsLocalRecord()
    {
        var dismissals = new MemoryQueueDismissalStore();
        var store = new MemoryDailyWorkStore();
        var page = new DailyWorkViewModel(store, queueDismissals: dismissals);
        var now = new DateTime(2026, 10, 7, 10, 0, 0);
        var brisbane = QueueRecord("inc-bne", "INC-BNE", "3", "3 - Moderate", now) with { Location = "Brisbane Office" };
        var melbourne = QueueRecord("inc-mel", "INC-MEL", "2", "2 - High", now) with { Location = "Melbourne" };
        var seen = new UnassignedSeen(true, [brisbane.SysId, melbourne.SysId], [brisbane.SysId, melbourne.SysId]);

        page.ShowGroupQueue([brisbane, melbourne], seen, now, NotificationPreferences.DefaultLocations);
        Assert.Equal(["INC-BNE"], page.NewUnassigned.Select(row => row.Number).ToArray());
        Assert.Equal("Brisbane Office", page.NewUnassigned[0].Office);

        Assert.True(page.NotPartOfMyQueue(page.NewUnassigned[0], "Wrong region desk", DailyWorkViewModel.UnassignedListName, now));
        Assert.Empty(page.NewUnassigned);
        Assert.True(page.HasDismissed);
        var recorded = Assert.Single(dismissals.Load());
        Assert.Equal("INC-BNE", recorded.Number);
        Assert.Equal("Brisbane Office", recorded.Office);
        Assert.Equal("Wrong region desk", recorded.Reason);
        Assert.Equal(DailyWorkViewModel.UnassignedListName, recorded.List);
        Assert.Equal(DateOnly.FromDateTime(now), recorded.LocalDay);
        Assert.Equal(now, recorded.DismissedAtLocal);
        Assert.Equal("inc-bne", recorded.SysId);

        page.ShowGroupQueue([brisbane, melbourne], seen, now.AddHours(1), NotificationPreferences.DefaultLocations);
        Assert.Empty(page.NewUnassigned);

        page.ClearDismissalsCommand.Execute(null);
        page.ShowGroupQueue([brisbane, melbourne], seen, now.AddHours(2), NotificationPreferences.DefaultLocations);
        Assert.Equal("INC-BNE", Assert.Single(page.NewUnassigned).Number);
        Assert.Empty(dismissals.Load().Where(item => item.LocalDay == DateOnly.FromDateTime(now)));
    }

    [Fact]
    public async Task TheGroupQueueCheckStartsWithTheConnectionAndStopsOnDisconnect()
    {
        var main = new MainViewModel(new MemorySettingsStore(), new RecordingDesktopServices(), dailyWork: new MemoryDailyWorkStore());
        main.Connection.UseSampleData = true;
        await main.ConnectCommand.ExecuteAsync(null);
        Assert.True(main.IsSample);
        Assert.True(main.GroupQueueActive);

        main.DisconnectCommand.Execute(null);
        Assert.False(main.GroupQueueActive);
        Assert.False(main.IsConnected);
        Assert.Empty(main.DailyWork.NewUnassigned);
    }

    private static WatchedRecord QueueRecord(string sysId, string number, string priority, string label, DateTime updated) => new()
    {
        Section = DeskSection.Incidents,
        SysId = sysId,
        Number = number,
        Title = number,
        State = "New",
        StateValue = "1",
        Group = "Client Services",
        AssignmentGroupSysId = "group-cs",
        CallerUserName = "jordan.lee",
        AssignedToSysId = "",
        PriorityValue = priority,
        PriorityLabel = label,
        Location = "Brisbane Office",
        Opened = "2026-10-07 08:00",
        Updated = "2026-10-07 08:00",
        UpdatedAt = updated
    };

    private static WorkItem Item(string sysId, string number) => new(
        sysId,
        DeskSection.Incidents,
        number,
        number,
        "In Progress",
        "Alex Rivera",
        "3",
        "3 - Moderate",
        false,
        false,
        false,
        true,
        false);

    private static WatchedRecord Sample(string name, string priority = "3", string label = "3 - Moderate") => new()
    {
        Section = DeskSection.Incidents,
        SysId = "inc-" + name,
        Number = "INC-" + name,
        Title = name,
        State = "In Progress",
        StateValue = "2",
        CallerUserName = "jordan.lee",
        AssigneeUserName = "alex.rivera",
        AssignedToSysId = "sample-user",
        PriorityValue = priority,
        PriorityLabel = label,
        Updated = "2026-10-01 09:00",
        UpdatedAt = new DateTime(2026, 10, 1, 9, 0, 0)
    };
}
