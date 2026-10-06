using ServiceNowDesk.Alerts;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
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
        Assert.Equal(
            ["INC0010010", "INC0010002", "INC0010006", "INC0010001", "RITM0010001", "IMS0010003", "IMS0010001"],
            report.Daily.Personal.Select(item => item.Number).ToArray());
        Assert.Equal("SLA, Unattended", report.Daily.Personal[0].Reasons);
        Assert.All(report.Daily.Personal.Skip(1).Take(4), item => Assert.Equal("Unattended", item.Reasons));
        Assert.Equal("SLA, Unattended", report.Daily.Personal[5].Reasons);
        Assert.Contains(report.Daily.Personal, item => item.Number == "INC0010010" && item.SlaBreaching);
        Assert.Equal(DailyWorkRanker.ActFirstHex, DailyWorkRow.From(report.Daily.Personal[0]).HighlightHex);
        Assert.All(
            report.Daily.Personal.Skip(1).Take(4),
            item => Assert.Equal(DailyWorkRanker.AfterThoseHex, DailyWorkRow.From(item).HighlightHex));
        Assert.Equal(DailyWorkRanker.ActFirstHex, DailyWorkRow.From(report.Daily.Personal[5]).HighlightHex);
        Assert.Equal(DailyWorkRanker.AfterThoseHex, DailyWorkRow.From(report.Daily.Personal[6]).HighlightHex);
        Assert.Contains(report.Daily.Personal, item => DailyWorkRanker.HighlightHex(item) == DailyWorkRanker.ActFirstHex);
        Assert.Contains(report.Daily.Personal, item => DailyWorkRanker.HighlightHex(item) == DailyWorkRanker.AfterThoseHex);

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
