using System.Globalization;
using System.Net;
using ServiceNowDesk.Alerts;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class AssignedDaysTests
{
    [Fact]
    public void WholeDaysUseTheLocalCalendarDate()
    {
        var today = new DateTime(2026, 10, 7, 0, 10, 0);
        Assert.Equal(0, AssignmentAge.WholeDays("2026-10-07 00:05:00", today));
        Assert.Equal(0, AssignmentAge.WholeDays("2026-10-07 23:59:00", today));
        Assert.Equal(1, AssignmentAge.WholeDays("2026-10-06 23:30:00", today));
        Assert.Equal(12, AssignmentAge.WholeDays("2026-09-25 09:00:00", today));
        Assert.Equal(0, AssignmentAge.WholeDays("2026-10-08 09:00:00", today));
        Assert.Null(AssignmentAge.WholeDays("", today));
        Assert.Null(AssignmentAge.WholeDays("   ", today));
        Assert.Null(AssignmentAge.WholeDays("not-a-date", today));
        Assert.Equal("0", AssignmentAge.Format("2026-10-07 23:00:00", today));
        Assert.Equal("", AssignmentAge.Format("", today));
    }

    [Fact]
    public void AssignmentAuditQueryIsOnlyTheAssignedRecords()
    {
        var queries = AlertQueryBuilder.AssignmentAuditQueries("sample-user", ["inc-old", "inc-old", "ritm-new", "bad^id", ""]);
        var query = Assert.Single(queries);
        Assert.Contains("tablenameINincident,sc_request,sc_req_item", query);
        Assert.Contains("fieldname=assigned_to", query);
        Assert.Contains("newvalue=sample-user", query);
        Assert.Contains("documentkeyINinc-old,ritm-new", query);
        Assert.Contains("ORDERBYDESCsys_created_on", query);
        Assert.DoesNotContain("sys_updated_on", query);
        Assert.DoesNotContain("opened_at", query);
        Assert.DoesNotContain("bad", query);

        Assert.Empty(AlertQueryBuilder.AssignmentAuditQueries("sample-user", []));
        Assert.Empty(AlertQueryBuilder.AssignmentAuditQueries("sample-user", null));
        Assert.Empty(AlertQueryBuilder.AssignmentAuditQueries("user^id", ["inc-old"]));

        var many = Enumerable.Range(0, 41).Select(index => "task" + index.ToString("00", CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(2, AlertQueryBuilder.AssignmentAuditQueries("sample-user", many).Count);
    }

    [Fact]
    public void AssignmentJournalQueryBatchesElementIds()
    {
        var queries = AlertQueryBuilder.AssignmentJournalQueries(["inc-old", "inc-old", "ritm-new", "bad^id", ""]);
        var query = Assert.Single(queries);
        Assert.Contains("element_idINinc-old,ritm-new", query);
        Assert.Contains("elementINcomments,additional_comments,work_notes", query);
        Assert.Contains("ORDERBYDESCsys_created_on", query);
        Assert.DoesNotContain("bad", query);
        Assert.Empty(AlertQueryBuilder.AssignmentJournalQueries([]));
        Assert.Empty(AlertQueryBuilder.AssignmentJournalQueries(null));

        var many = Enumerable.Range(0, 41).Select(index => "task" + index.ToString("00", CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(2, AlertQueryBuilder.AssignmentJournalQueries(many).Count);
    }

    [Fact]
    public void JournalNotesSupplyAssignedOnDaysAndIgnoreUnrelatedNotes()
    {
        var today = DateTime.Today;
        var assignedStamp = today.AddDays(-7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 09:15:00";
        var laterWork = today.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 11:00:00";
        var olderOther = today.AddDays(-20).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 08:00:00";
        var notes = new[]
        {
            new JournalEntry("j1", "work_notes", "Work note", "Assigned to changed from Jordan Lee to Alex Rivera", "system", assignedStamp),
            new JournalEntry("j2", "work_notes", "Work note", "Replaced the tray.", "alex.rivera", laterWork),
            new JournalEntry("j3", "work_notes", "Work note", "Assigned to changed from  to Casey Ng", "system", olderOther)
        };

        var when = AssignmentNoteReader.FindAssignedOn(notes, AssignmentNoteReader.TokensFor("Alex Rivera", "alex.rivera"));
        Assert.Equal(assignedStamp, when);
        Assert.Equal(7, AssignmentAge.WholeDays(when, today));
        Assert.Equal("7", AssignmentAge.Format(when, today));

        Assert.Null(AssignmentNoteReader.FindAssignedOn(notes, AssignmentNoteReader.TokensFor("Nobody")));
        Assert.Null(AssignmentNoteReader.FindAssignedOn([], AssignmentNoteReader.TokensFor("Alex Rivera")));
        Assert.True(AssignmentNoteReader.IndicatesAssignmentTo("Assigned to me.", ["me", "Alex Rivera"]));
        Assert.True(AssignmentNoteReader.IndicatesAssignmentTo("Assigned to: Alex Rivera", ["Alex Rivera"]));
        Assert.False(AssignmentNoteReader.IndicatesAssignmentTo("Assigned to Melanie", ["me"]));
    }

    [Fact]
    public void AssignedToMeOpensLongestFirstAndHeadersToggle()
    {
        var notifications = new NotificationWorkspaceViewModel();
        Assert.True(notifications.ShowDaysAssigned);
        Assert.False(notifications.ShowAssigneeColumn);
        Assert.False(notifications.ShowStandardColumns);
        Assert.Equal("Days assigned ▼", notifications.DaysAssignedHeader);

        notifications.Show(Snapshot(
            Row("inc-12", "INC0012", "Oldest", daysAgo: 12),
            Row("inc-2", "INC0002", "Recent", daysAgo: 2),
            Row("inc-0", "INC0000", "Today", daysAgo: 0),
            Row("inc-blank", "INC0009", "Unknown", daysAgo: null),
            Row("inc-group", "INC0008", "Group", daysAgo: 40, AlertKind.WatchedGroup)));

        Assert.Equal(["INC0012", "INC0002", "INC0000", "INC0009"], notifications.DashboardRows.Select(row => row.Number).ToArray());
        Assert.Equal(["12", "2", "0", ""], notifications.DashboardRows.Select(row => row.DaysAssigned).ToArray());
        Assert.Equal("", notifications.DashboardRows.Single(row => row.Number == "INC0009").AssignedOn);

        notifications.SortByCommand.Execute("DaysAssigned");
        Assert.Equal("Days assigned ▲", notifications.DaysAssignedHeader);
        Assert.Equal("Number", notifications.NumberHeader);
        Assert.Equal(["INC0000", "INC0002", "INC0012", "INC0009"], notifications.DashboardRows.Select(row => row.Number).ToArray());

        notifications.SortByCommand.Execute("DaysAssigned");
        Assert.Equal("Days assigned ▼", notifications.DaysAssignedHeader);
        Assert.Equal(["INC0012", "INC0002", "INC0000", "INC0009"], notifications.DashboardRows.Select(row => row.Number).ToArray());

        notifications.SortByCommand.Execute("Title");
        Assert.Equal("Title ▲", notifications.TitleHeader);
        Assert.Equal("Days assigned", notifications.DaysAssignedHeader);
        Assert.Equal(["Oldest", "Recent", "Today", "Unknown"], notifications.DashboardRows.Select(row => row.Title).ToArray());

        notifications.SortByCommand.Execute("Number");
        Assert.Equal("Number ▲", notifications.NumberHeader);
        notifications.SortByCommand.Execute("Number");
        Assert.Equal("Number ▼", notifications.NumberHeader);
        Assert.Equal(["INC0012", "INC0009", "INC0002", "INC0000"], notifications.DashboardRows.Select(row => row.Number).ToArray());

        notifications.SelectQueueCommand.Execute(AlertKind.WatchedGroup);
        Assert.False(notifications.ShowDaysAssigned);
        Assert.True(notifications.ShowStandardColumns);
        Assert.Equal("Number", notifications.NumberHeader);
        Assert.Equal("Days assigned", notifications.DaysAssignedHeader);
        Assert.Equal(["INC0008"], notifications.DashboardRows.Select(row => row.Number).ToArray());

        notifications.SortByCommand.Execute("State");
        Assert.Equal("State ▲", notifications.StateHeader);
        notifications.SortByCommand.Execute("State");
        Assert.Equal("State ▼", notifications.StateHeader);

        notifications.SelectQueueCommand.Execute(AlertKind.SlaBreaching);
        Assert.True(notifications.ShowAssigneeColumn);
        Assert.False(notifications.ShowDaysAssigned);
        Assert.Equal("Assigned to", notifications.AssigneeHeader);
        notifications.SortByCommand.Execute("Assignee");
        Assert.Equal("Assigned to ▲", notifications.AssigneeHeader);
        notifications.SortByCommand.Execute("Group");
        Assert.Equal("Group ▲", notifications.GroupHeader);
        notifications.SortByCommand.Execute("Queue");
        Assert.Equal("Queue ▲", notifications.QueueHeader);

        notifications.SelectQueueCommand.Execute(AlertKind.AssignedToMe);
        Assert.True(notifications.ShowDaysAssigned);
        Assert.Equal("Days assigned ▼", notifications.DaysAssignedHeader);
        Assert.Equal(["INC0012", "INC0002", "INC0000", "INC0009"], notifications.DashboardRows.Select(row => row.Number).ToArray());
    }

    [Fact]
    public void LeadsKeepsItsOwnSortAndDoesNotShowDaysAssigned()
    {
        var board = new NotificationWorkspaceViewModel([AlertKind.Unattended], alwaysShowAssignee: true);
        Assert.False(board.ShowDaysAssigned);
        Assert.True(board.ShowAssigneeColumn);
        Assert.Equal("Number", board.NumberHeader);
        Assert.Equal("Days assigned", board.DaysAssignedHeader);
    }

    [Fact]
    public async Task PracticeTicketsUseDifferentAssignmentDaysAndNotTheLastUpdate()
    {
        using var client = new SampleServiceNowClient();
        var user = await client.GetCurrentUserAsync(CancellationToken.None);
        var snapshot = await client.GetOpenAlertsAsync(
            new AlertSearch(user.SysId, "Aus DT - Client Services", NotificationPreferences.DefaultLocations),
            CancellationToken.None);

        var rows = snapshot.Bucket(AlertKind.AssignedToMe).Rows;
        Assert.Equal(8, rows.Count);
        Assert.DoesNotContain(rows, row => row.Number is "INC0010003" or "INC0010015" or "RITM0010004");
        Assert.Contains(rows, row => row.Number == "INC0010024");
        Assert.Contains(rows, row => row.Number == "RITM0010008");

        var printer = rows.Single(row => row.Number == "INC0010001");
        var vpn = rows.Single(row => row.Number == "INC0010002");
        var blue = rows.Single(row => row.Number == "INC0010006");
        var request = rows.Single(row => row.Number == "REQ0010001");
        var item = rows.Single(row => row.Number == "RITM0010001");
        var missing = rows.Single(row => row.Number == "INC0010010");

        Assert.Equal(12, AssignmentAge.WholeDays(printer.AssignedOn, DateTime.Today));
        Assert.Equal(1, AssignmentAge.WholeDays(vpn.AssignedOn, DateTime.Today));
        Assert.Equal(0, AssignmentAge.WholeDays(blue.AssignedOn, DateTime.Today));
        Assert.Equal(5, AssignmentAge.WholeDays(request.AssignedOn, DateTime.Today));
        Assert.Equal(3, AssignmentAge.WholeDays(item.AssignedOn, DateTime.Today));
        Assert.Equal("", missing.AssignedOn);
        Assert.NotEqual("", missing.Updated);
        Assert.All(new[] { printer, vpn, blue, request, item }, row =>
        {
            Assert.NotEqual(row.Updated, row.AssignedOn);
            Assert.False(string.IsNullOrWhiteSpace(row.AssignedOn));
        });

        var notifications = new NotificationWorkspaceViewModel();
        notifications.Show(snapshot);
        Assert.Contains(notifications.DashboardRows, row => row.Number == "INC0010001");
        Assert.Contains(notifications.DashboardRows, row => row.Number == "INC0010024");
        Assert.Contains(notifications.DashboardRows, row => row.Number == "RITM0010008");
        Assert.Equal(8, notifications.DashboardRows.Count);

        notifications.SortByCommand.Execute("DaysAssigned");
        Assert.Equal("Days assigned ▲", notifications.DaysAssignedHeader);
        Assert.Equal(8, notifications.DashboardRows.Count);
        Assert.Contains(notifications.DashboardRows, row => row.Number == "INC0010006");
        Assert.Contains(notifications.DashboardRows, row => row.Number == "INC0010024");
    }

    [Fact]
    public async Task AuditSuppliesTheAssignmentTimeAndAFailureLeavesTheQueueUp()
    {
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            var query = Uri.UnescapeDataString(request.RequestUri?.Query ?? "");
            if (path.Contains("/sys_audit", StringComparison.Ordinal))
            {
                return Api.Json("""
                    {"result":[
                      {"documentkey":{"value":"inc-old","display_value":"inc-old"},"fieldname":{"value":"assigned_to","display_value":"assigned_to"},"newvalue":{"value":"sample-user","display_value":"sample-user"},"sys_created_on":{"value":"2026-08-01 08:00:00","display_value":"2026-08-01 08:00:00"}},
                      {"documentkey":{"value":"inc-old","display_value":"inc-old"},"fieldname":{"value":"assigned_to","display_value":"assigned_to"},"newvalue":{"value":"other-user","display_value":"other-user"},"sys_created_on":{"value":"2026-10-01 08:00:00","display_value":"2026-10-01 08:00:00"}},
                      {"documentkey":{"value":"inc-old","display_value":"inc-old"},"fieldname":{"value":"assigned_to","display_value":"assigned_to"},"newvalue":{"value":"sample-user","display_value":"sample-user"},"sys_created_on":{"value":"2026-09-01 08:00:00","display_value":"2026-09-01 08:00:00"}},
                      {"documentkey":{"value":"inc-hold","display_value":"inc-hold"},"fieldname":{"value":"state","display_value":"state"},"newvalue":{"value":"3","display_value":"3"},"sys_created_on":{"value":"2026-10-06 08:00:00","display_value":"2026-10-06 08:00:00"}},
                      {"documentkey":{"value":"inc-hold","display_value":"inc-hold"},"fieldname":{"value":"assigned_to","display_value":"assigned_to"},"newvalue":{"value":"sample-user","display_value":"sample-user"},"sys_created_on":{"value":"2026-10-06 09:15:00","display_value":"2026-10-06 09:15"}}
                    ]}
                    """);
            }

            if (path.Contains("/incident", StringComparison.Ordinal) && query.Contains("sysparm_fields=sys_id,number,short_description,state,assigned_to,assignment_group,location,sys_updated_on,active", StringComparison.Ordinal))
            {
                if (query.Contains("assignment_group.name", StringComparison.Ordinal))
                    return Api.Json("{\"result\":[" + IncidentJson("inc-group", "INC0090008", "1", "New") + "]}");
                if (query.Contains("assigned_to=sample-user", StringComparison.Ordinal))
                {
                    return Api.Json("{\"result\":[" + string.Join(",",
                        IncidentJson("inc-old", "INC0090001", "2", "In Progress"),
                        IncidentJson("inc-hold", "INC0090002", "3", "On Hold"),
                        IncidentJson("inc-resolved", "INC0090003", "6", "Resolved")) + "]}");
                }
            }

            return Api.Json("""{"result":[]}""");
        });

        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);
        var snapshot = await client.GetOpenAlertsAsync(
            new AlertSearch("sample-user", "Aus DT - Client Services", ["Brisbane"]),
            CancellationToken.None);

        var assigned = snapshot.Bucket(AlertKind.AssignedToMe).Rows;
        Assert.Equal(2, assigned.Count);
        Assert.Contains(assigned, row => row.Number == "INC0090002");
        Assert.DoesNotContain(assigned, row => row.Number == "INC0090003");
        Assert.Equal("2026-09-01 08:00:00", assigned.Single(row => row.Number == "INC0090001").AssignedOn);
        Assert.Equal("2026-10-06 09:15", assigned.Single(row => row.Number == "INC0090002").AssignedOn);
        Assert.All(assigned, row => Assert.NotEqual(row.Updated, row.AssignedOn));
        Assert.Equal("INC0090008", Assert.Single(snapshot.Bucket(AlertKind.WatchedGroup).Rows).Number);
        Assert.Equal("", snapshot.Bucket(AlertKind.WatchedGroup).Status);
        Assert.Equal("", snapshot.Bucket(AlertKind.SlaBreaching).Status);

        var audit = Uri.UnescapeDataString(Assert.Single(handler.Calls, call => call.PathAndQuery.Contains("/sys_audit", StringComparison.Ordinal)).PathAndQuery);
        Assert.Contains("fieldname=assigned_to", audit);
        Assert.Contains("newvalue=sample-user", audit);
        Assert.Contains("documentkeyIN", audit);
        Assert.Contains("inc-old", audit);
        Assert.Contains("inc-hold", audit);
        Assert.DoesNotContain("inc-resolved", audit);
        Assert.DoesNotContain("inc-group", audit);
        Assert.DoesNotContain("sys_updated_on", audit);
    }

    [Fact]
    public async Task ABlockedAuditReadsAssignmentDaysFromJournalNotes()
    {
        var assignedStamp = DateTime.Today.AddDays(-4).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 09:15:00";
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            var query = Uri.UnescapeDataString(request.RequestUri?.Query ?? "");
            if (path.Contains("/sys_audit", StringComparison.Ordinal))
                return Api.Json("""{"error":{"message":"ACL","detail":"sys_audit denied"}}""", HttpStatusCode.Forbidden);
            if (path.Contains("/sys_journal_field", StringComparison.Ordinal))
            {
                return Api.Json(
                    "{"result":["
                    + "{"sys_id":{"value":"j-assign","display_value":"j-assign"},"element_id":{"value":"inc-old","display_value":"inc-old"},"element":{"value":"work_notes","display_value":"work_notes"},"name":{"value":"task","display_value":"task"},"value":{"value":"Assigned to changed from Jordan Lee to Alex Rivera","display_value":"Assigned to changed from Jordan Lee to Alex Rivera"},"sys_created_on":{"value":""
                    + assignedStamp
                    + "","display_value":""
                    + assignedStamp
                    + ""},"sys_created_by":{"value":"system","display_value":"system"}},"
                    + "{"sys_id":{"value":"j-work","display_value":"j-work"},"element_id":{"value":"inc-old","display_value":"inc-old"},"element":{"value":"work_notes","display_value":"work_notes"},"name":{"value":"task","display_value":"task"},"value":{"value":"Printer tray replaced.","display_value":"Printer tray replaced."},"sys_created_on":{"value":"2026-09-28 10:40:00","display_value":"2026-09-28 10:40"},"sys_created_by":{"value":"alex.rivera","display_value":"alex.rivera"}}"
                    + "]}");
            }

            if (path.Contains("/incident", StringComparison.Ordinal)
                && query.Contains("assigned_to=sample-user", StringComparison.Ordinal)
                && query.Contains("sysparm_fields=sys_id,number,short_description,state,assigned_to,assignment_group,location,sys_updated_on,active", StringComparison.Ordinal)
                && !query.Contains("assignment_group.name", StringComparison.Ordinal))
                return Api.Json("{\"result\":[" + IncidentJson("inc-old", "INC0090001", "2", "In Progress") + "]}");
            if (path.Contains("/incident", StringComparison.Ordinal)
                && query.Contains("assignment_group.name", StringComparison.Ordinal)
                && query.Contains("sysparm_fields=sys_id,number,short_description,state,assigned_to,assignment_group,location,sys_updated_on,active", StringComparison.Ordinal))
                return Api.Json("{\"result\":[" + IncidentJson("inc-group", "INC0090008", "1", "New") + "]}");
            return Api.Json("""{"result":[]}""");
        });

        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);
        var snapshot = await client.GetOpenAlertsAsync(
            new AlertSearch("sample-user", "Aus DT - Client Services", ["Brisbane"]),
            CancellationToken.None);

        var assigned = Assert.Single(snapshot.Bucket(AlertKind.AssignedToMe).Rows);
        Assert.Equal("INC0090001", assigned.Number);
        Assert.Equal(assignedStamp, assigned.AssignedOn);
        Assert.Equal(4, AssignmentAge.WholeDays(assigned.AssignedOn, DateTime.Today));
        Assert.Equal("2026-09-28 10:40", assigned.Updated);
        Assert.NotEqual(assigned.Updated, assigned.AssignedOn);
        Assert.Equal("INC0090008", Assert.Single(snapshot.Bucket(AlertKind.WatchedGroup).Rows).Number);
        Assert.Equal("", snapshot.Bucket(AlertKind.SlaBreaching).Status);
        Assert.Contains(handler.Calls, call => call.PathAndQuery.Contains("/sys_audit", StringComparison.Ordinal));
        Assert.Contains(handler.Calls, call => call.PathAndQuery.Contains("/sys_journal_field", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABlockedAuditAndNoAssignmentNotesLeavesAssignedDaysBlank()
    {
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            var query = Uri.UnescapeDataString(request.RequestUri?.Query ?? "");
            if (path.Contains("/sys_audit", StringComparison.Ordinal))
                return Api.Json("""{"error":{"message":"ACL","detail":"sys_audit denied"}}""", HttpStatusCode.Forbidden);
            if (path.Contains("/sys_journal_field", StringComparison.Ordinal))
                return Api.Json("""{"result":[{"sys_id":{"value":"j-work","display_value":"j-work"},"element_id":{"value":"inc-old","display_value":"inc-old"},"element":{"value":"work_notes","display_value":"work_notes"},"name":{"value":"task","display_value":"task"},"value":{"value":"Checked the printer.","display_value":"Checked the printer."},"sys_created_on":{"value":"2026-09-28 10:40:00","display_value":"2026-09-28 10:40"},"sys_created_by":{"value":"alex.rivera","display_value":"alex.rivera"}}]}""");
            if (path.Contains("/incident", StringComparison.Ordinal)
                && query.Contains("assigned_to=sample-user", StringComparison.Ordinal)
                && query.Contains("sysparm_fields=sys_id,number,short_description,state,assigned_to,assignment_group,location,sys_updated_on,active", StringComparison.Ordinal)
                && !query.Contains("assignment_group.name", StringComparison.Ordinal))
                return Api.Json("{\"result\":[" + IncidentJson("inc-old", "INC0090001", "2", "In Progress") + "]}");
            if (path.Contains("/incident", StringComparison.Ordinal)
                && query.Contains("assignment_group.name", StringComparison.Ordinal)
                && query.Contains("sysparm_fields=sys_id,number,short_description,state,assigned_to,assignment_group,location,sys_updated_on,active", StringComparison.Ordinal))
                return Api.Json("{\"result\":[" + IncidentJson("inc-group", "INC0090008", "1", "New") + "]}");
            return Api.Json("""{"result":[]}""");
        });

        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);
        var snapshot = await client.GetOpenAlertsAsync(
            new AlertSearch("sample-user", "Aus DT - Client Services", ["Brisbane"]),
            CancellationToken.None);

        var assigned = Assert.Single(snapshot.Bucket(AlertKind.AssignedToMe).Rows);
        Assert.Equal("INC0090001", assigned.Number);
        Assert.Equal("", assigned.AssignedOn);
        Assert.Equal("2026-09-28 10:40", assigned.Updated);
        Assert.Equal("INC0090008", Assert.Single(snapshot.Bucket(AlertKind.WatchedGroup).Rows).Number);
        Assert.Contains(handler.Calls, call => call.PathAndQuery.Contains("/sys_journal_field", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnEmptyAssignedQueueDoesNotAskForAuditOrJournalHistory()
    {
        var handler = new StubHandler((_, _) => Api.Json("""{"result":[]}"""));
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);
        var snapshot = await client.GetOpenAlertsAsync(new AlertSearch("sample-user", "", []), CancellationToken.None);

        Assert.Equal(0, snapshot.Count(AlertKind.AssignedToMe));
        Assert.DoesNotContain(handler.Calls, call => call.PathAndQuery.Contains("/sys_audit", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Calls, call => call.PathAndQuery.Contains("/sys_journal_field", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PracticeTicketsExposeAssignmentDaysFromJournalNotes()
    {
        using var client = new SampleServiceNowClient();
        var notes = await client.GetJournalAsync("incident", "inc-printer", CancellationToken.None);
        Assert.Contains(notes, note => note.Text.Contains("Assigned to changed from", StringComparison.OrdinalIgnoreCase));

        var user = await client.GetCurrentUserAsync(CancellationToken.None);
        var snapshot = await client.GetOpenAlertsAsync(
            new AlertSearch(user.SysId, "Aus DT - Client Services", NotificationPreferences.DefaultLocations),
            CancellationToken.None);
        var printer = snapshot.Bucket(AlertKind.AssignedToMe).Rows.Single(row => row.Number == "INC0010001");
        Assert.Equal(12, AssignmentAge.WholeDays(printer.AssignedOn, DateTime.Today));
        Assert.Equal(
            AssignmentNoteReader.FindAssignedOn(notes, AssignmentNoteReader.TokensFor(user)),
            printer.AssignedOn);
    }

    private static AlertSnapshot Snapshot(params AlertRecord[] rows)
    {
        var assigned = rows.Where(row => row.Kind == AlertKind.AssignedToMe).ToArray();
        var group = rows.Where(row => row.Kind == AlertKind.WatchedGroup).ToArray();
        return new AlertSnapshot(new Dictionary<AlertKind, AlertBucket>
        {
            [AlertKind.AssignedToMe] = new(assigned, assigned.Length),
            [AlertKind.WatchedGroup] = new(group, group.Length)
        });
    }

    private static AlertRecord Row(string id, string number, string title, int? daysAgo, AlertKind kind = AlertKind.AssignedToMe) => new(
        kind,
        DeskSection.Incidents,
        id,
        number,
        title,
        "New",
        "Aus DT - Client Services",
        "Brisbane",
        "2026-09-28 10:40",
        "Alex Rivera",
        "sample-user",
        daysAgo is int days
            ? DateTime.Today.AddDays(-days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 22:30:00"
            : "");

    private static string IncidentJson(string sysId, string number, string state, string label) =>
        $$"""
        {
          "sys_id": {"value": "{{sysId}}", "display_value": "{{sysId}}"},
          "number": {"value": "{{number}}", "display_value": "{{number}}"},
          "short_description": {"value": "Printer", "display_value": "Printer"},
          "state": {"value": "{{state}}", "display_value": "{{label}}"},
          "assigned_to": {"value": "sample-user", "display_value": "Alex Rivera"},
          "assignment_group": {"value": "group-cs", "display_value": "Client Services"},
          "location": {"value": "loc", "display_value": "Brisbane"},
          "sys_updated_on": {"value": "2026-09-28 10:40:00", "display_value": "2026-09-28 10:40"},
          "active": {"value": "true", "display_value": "true"}
        }
        """;
}
