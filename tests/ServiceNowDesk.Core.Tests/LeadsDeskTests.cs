using ServiceNowDesk.Alerts;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class LeadsDeskTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 15, 0, 0);

    [Fact]
    public async Task SelectingAnIncidentExposesTheIncidentEditor()
    {
        var desktop = new RecordingDesktopServices();
        var leads = new LeadsViewModel();
        var incidents = new IncidentWorkspaceViewModel(desktop);
        var items = new RequestedItemWorkspaceViewModel(desktop);
        leads.UseEditors(incidents, new RequestWorkspaceViewModel(desktop), items, new InteractionWorkspaceViewModel(desktop));
        Assert.Equal("Click a ticket.", leads.EditorPrompt);

        await leads.OpenTicketAsync(Row(DeskSection.Incidents, "inc-1", "INC0002", "Printer"));

        Assert.Equal("incident", leads.EditorIdentity);
        Assert.Same(incidents, leads.ActiveEditor);
        Assert.True(leads.ShowIncidentEditor);
        Assert.False(leads.ShowRequestedItemEditor);
        Assert.Equal("", leads.EditorPrompt);
    }

    [Fact]
    public async Task SelectingARequestItemExposesThatEditor()
    {
        var desktop = new RecordingDesktopServices();
        var leads = new LeadsViewModel();
        var items = new RequestedItemWorkspaceViewModel(desktop);
        leads.UseEditors(
            new IncidentWorkspaceViewModel(desktop),
            new RequestWorkspaceViewModel(desktop),
            items,
            new InteractionWorkspaceViewModel(desktop));

        await leads.OpenTicketAsync(Row(DeskSection.RequestedItems, "ritm-1", "RITM0004", "Dock"));

        Assert.Equal("sc_req_item", leads.EditorIdentity);
        Assert.Same(items, leads.ActiveEditor);
        Assert.True(leads.ShowRequestedItemEditor);
        Assert.False(leads.ShowIncidentEditor);
    }

    [Fact]
    public void TheSameTwoPeopleInOneRegionMatchCallerAndUnattendedCounts()
    {
        const string region = "Aus DT - Client Services";
        var ada = Person("user-ada", "Ada", region, "Brisbane", "inc-ada", "INC0002");
        var bea = Person("user-bea", "Bea", region, "Brisbane", "inc-bea", "INC0010");
        var duplicate = ada with { };
        var outside = Person("user-ada", "Ada", "Network", "Brisbane", "inc-out", "INC0008");
        var otherCity = Person("user-bea", "Bea", region, "Sydney", "inc-syd", "INC0003");
        var records = new[] { ada, duplicate, bea, outside, otherCity };

        var board = LeadBoard.Build(records, Now, ["user-ada", "user-bea"], region, ["Brisbane"]);
        var teamCaller = board.For(LeadArea.Team).Bucket(AlertKind.UpdatedByCaller);
        var regionalCaller = board.For(LeadArea.Regional).Bucket(AlertKind.UpdatedByCaller);
        var teamQuiet = board.For(LeadArea.Team).Bucket(AlertKind.Unattended);
        var regionalQuiet = board.For(LeadArea.Regional).Bucket(AlertKind.Unattended);

        Assert.Equal(regionalCaller.TotalCount, teamCaller.TotalCount);
        Assert.Equal(regionalQuiet.TotalCount, teamQuiet.TotalCount);
        Assert.Equal(2, teamCaller.TotalCount);
        Assert.Equal(3, teamQuiet.TotalCount);
        Assert.Contains(teamQuiet.Rows, row => row.Number == "INC0003");
        Assert.Equal(teamCaller.Rows.Count, teamCaller.TotalCount);
        Assert.DoesNotContain(teamCaller.Rows, row => row.Number == "INC0008");
        Assert.DoesNotContain(regionalCaller.Rows, row => row.Number == "INC0008");
        Assert.DoesNotContain(teamQuiet.Rows, row => row.Number == "INC0008");
        Assert.DoesNotContain(teamCaller.Rows, row => row.Number == "INC0003");
        Assert.Single(teamCaller.Rows, row => row.SysId == "inc-ada");
    }

    [Fact]
    public void FirstClickSortsAscendingAndSecondClickSortsDescending()
    {
        var board = new NotificationWorkspaceViewModel([AlertKind.Unattended], alwaysShowAssignee: true);
        board.Show(new AlertSnapshot(new Dictionary<AlertKind, AlertBucket>
        {
            [AlertKind.Unattended] = new(
            [
                RowRecord("inc-10", "INC0010", "Zebra"),
                RowRecord("inc-2", "INC0002", "Alpha")
            ], 2)
        }));

        board.SortByCommand.Execute("Number");
        Assert.Equal("Number ▲", board.NumberHeader);
        Assert.Equal(["INC0002", "INC0010"], board.DashboardRows.Select(row => row.Number).ToArray());

        board.SortByCommand.Execute("Number");
        Assert.Equal("Number ▼", board.NumberHeader);
        Assert.Equal(["INC0010", "INC0002"], board.DashboardRows.Select(row => row.Number).ToArray());

        board.SortByCommand.Execute("Title");
        Assert.Equal("Title ▲", board.TitleHeader);
        Assert.Equal("Number", board.NumberHeader);
        Assert.Equal(["Alpha", "Zebra"], board.DashboardRows.Select(row => row.Title).ToArray());
    }

    [Fact]
    public async Task SortingDoesNotClearTheOpenTicket()
    {
        var desktop = new RecordingDesktopServices();
        var leads = new LeadsViewModel();
        leads.UseEditors(
            new IncidentWorkspaceViewModel(desktop),
            new RequestWorkspaceViewModel(desktop),
            new RequestedItemWorkspaceViewModel(desktop),
            new InteractionWorkspaceViewModel(desktop));
        leads.Board.SelectedQueue = AlertKind.Unattended;
        leads.Board.Show(new AlertSnapshot(new Dictionary<AlertKind, AlertBucket>
        {
            [AlertKind.Unattended] = new(
            [
                RowRecord("inc-10", "INC0010", "Zebra"),
                RowRecord("inc-2", "INC0002", "Alpha")
            ], 2)
        }));
        await leads.OpenTicketAsync(leads.Board.DashboardRows[0]);
        var identity = leads.EditorIdentity;
        var sysId = leads.ActiveEditor is null ? "" : "open";

        leads.Board.SortByCommand.Execute("Number");
        leads.Board.SortByCommand.Execute("Number");

        Assert.Equal(identity, leads.EditorIdentity);
        Assert.Equal(sysId, leads.ActiveEditor is null ? "" : "open");
        Assert.True(leads.ShowIncidentEditor);
    }

    private static AlertRow Row(DeskSection section, string sysId, string number, string title) => new()
    {
        Kind = AlertKind.Unattended,
        Section = section,
        SysId = sysId,
        Number = number,
        Title = title,
        State = "In Progress",
        Group = "Aus DT - Client Services",
        Location = "Brisbane",
        Updated = "2026-09-01 09:00"
    };

    private static AlertRecord RowRecord(string sysId, string number, string title) =>
        new(AlertKind.Unattended, DeskSection.Incidents, sysId, number, title, "In Progress", "Aus DT - Client Services", "Brisbane", "2026-09-01");

    private static WatchedRecord Person(string userId, string name, string group, string city, string sysId, string number) => new()
    {
        Section = DeskSection.Incidents,
        SysId = sysId,
        Number = number,
        Title = name + " ticket",
        State = "In Progress",
        StateValue = "2",
        Group = group,
        Location = city,
        Updated = "2026-09-01 09:00",
        UpdatedAt = new DateTime(2026, 9, 1, 9, 0, 0),
        UpdatedBy = name.ToLowerInvariant(),
        CallerUserName = name.ToLowerInvariant(),
        AssigneeDisplay = name,
        AssignedToSysId = userId
    };
}
