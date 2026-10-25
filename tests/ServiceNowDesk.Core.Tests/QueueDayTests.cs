using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.QueueDay;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;
using ServiceNowDesk.WorkEffort;

namespace ServiceNowDesk.Tests;

public class QueueDayTests
{
    private static readonly DateTime Morning = new(2026, 10, 25, 8, 0, 0, DateTimeKind.Local);
    private static readonly DateTime Afternoon = new(2026, 10, 25, 11, 30, 0, DateTimeKind.Local);
    private static readonly WorkEffortPerson Sam = new("user-sam", "Sam Patel", "sam.patel");

    [Fact]
    public async Task FirstScanSavesABaselineAndDoesNotLookUp()
    {
        var source = new ScriptSource
        {
            Queue = _ => [Ticket("INC1001", "inc-1", "incident", "Printer jam", updatedOn: "2026-10-25 07:00:00")]
        };
        var store = new CountingStore();
        var report = await QueueDayCheck.RunAsync(source, store, [Sam], Morning, CancellationToken.None);

        Assert.Equal(0, source.Lookups);
        Assert.Equal(1, source.Lists);
        Assert.Equal(1, store.Saves);
        Assert.True(report.CreatedBaseline);
        Assert.Equal(QueueDayText.BaselineSaved, report.Status);
        var saved = store.Find(DateOnly.FromDateTime(Morning));
        Assert.NotNull(saved);
        Assert.Equal(Morning, saved.TakenAt);
        var line = Assert.Single(report.Lines);
        Assert.Equal("INC1001", line.Number);
        Assert.Equal("Printer jam", line.ShortDescription);
        Assert.Equal(QueueDayKind.Unchanged, line.Kind);
        Assert.Equal(QueueDayText.Unchanged, line.Breakdown);
        Assert.Equal("INC1001", Assert.Single(Assert.Single(saved.People).Tickets).Number);
    }

    [Fact]
    public async Task SecondScanTheSameDayDoesNotReplaceTheBaseline()
    {
        var phase = 0;
        var source = new ScriptSource
        {
            Queue = _ => phase == 0
                ? [Ticket("INC1001", "inc-1", "incident", "Printer jam", updatedOn: "2026-10-25 07:00:00", updatedBy: "sam.patel")]
                :
                [
                    Ticket("INC1001", "inc-1", "incident", "Printer jam", updatedOn: "2026-10-25 09:00:00", updatedBy: "sam.patel"),
                    Ticket("INC1005", "inc-5", "incident", "New laptop", updatedOn: "2026-10-25 09:30:00", updatedBy: "sam.patel")
                ]
        };
        var store = new CountingStore();
        await QueueDayCheck.RunAsync(source, store, [Sam], Morning, CancellationToken.None);
        var taken = store.Find(DateOnly.FromDateTime(Morning))!.TakenAt;
        phase = 1;
        var report = await QueueDayCheck.RunAsync(source, store, [Sam], Afternoon, CancellationToken.None);

        Assert.Equal(1, store.Saves);
        var saved = store.Find(DateOnly.FromDateTime(Morning));
        Assert.NotNull(saved);
        Assert.Equal(taken, saved.TakenAt);
        Assert.Equal(Morning, report.BaselineAt);
        Assert.Equal("INC1001", Assert.Single(Assert.Single(saved.People).Tickets).Number);
        Assert.Contains(report.Lines, line => line.Number == "INC1005" && line.Kind == QueueDayKind.New);
        Assert.Equal(QueueDayText.Compared, report.Status);
    }

    [Fact]
    public async Task UnassignedGroupTicketsAreAbsent()
    {
        var source = new ScriptSource
        {
            Queue = _ =>
            [
                Ticket("INC1001", "inc-1", "incident", "Printer jam"),
                Ticket("INC-FREE", "inc-free", "incident", "Sitting in the group", assigneeId: "", assigneeName: "", assigneeUser: "")
            ]
        };
        var report = await QueueDayCheck.RunAsync(source, new MemoryQueueDayStore(), [Sam], Morning, CancellationToken.None);

        Assert.Contains(report.Lines, line => line.Number == "INC1001");
        Assert.DoesNotContain(report.Lines, line => line.Number == "INC-FREE");
        Assert.DoesNotContain(QueueDayQuery.OpenAssigned("user-sam", "incident"), "ISEMPTY", StringComparison.Ordinal);
        Assert.DoesNotContain(QueueDayQuery.OpenAssigned("user-sam", "incident"), "assignment_group", StringComparison.Ordinal);
        Assert.DoesNotContain(QueueDayQuery.OpenAssigned("user-sam", "incident"), "^NQ", StringComparison.Ordinal);
    }

    [Fact]
    public async Task IncidentsRequestItemsAndWalkUpsCanAllAppear()
    {
        var source = new ScriptSource
        {
            Queue = _ =>
            [
                Ticket("INC1001", "inc-1", "incident", "Printer jam"),
                Ticket("RITM1001", "ritm-1", "sc_req_item", "USB-C dock"),
                Ticket("IMS1001", "ims-1", "interaction", "Password reset")
            ]
        };
        var report = await QueueDayCheck.RunAsync(source, new MemoryQueueDayStore(), [Sam], Morning, CancellationToken.None);
        var lines = report.Lines.ToArray();

        Assert.Contains(lines, line => line.Number == "INC1001" && line.ShortDescription == "Printer jam" && line.Table == "incident");
        Assert.Contains(lines, line => line.Number == "RITM1001" && line.ShortDescription == "USB-C dock" && line.Table == "sc_req_item");
        Assert.Contains(lines, line => line.Number == "IMS1001" && line.ShortDescription == "Password reset" && line.Table == "interaction");
    }

    [Fact]
    public async Task ComparisonExplainsWhoChangedTheQueue()
    {
        var phase = 0;
        var source = new ScriptSource
        {
            Queue = _ => phase == 0 ? MorningQueue() : AfternoonQueue(),
            LookupRows =
            [
                Ticket("INC1006", "inc-6", "incident", "Old monitor", stateValue: "7", stateDisplay: "Closed"),
                Ticket("INC1007", "inc-7", "incident", "Moved desk", assigneeId: "user-jordan", assigneeName: "Jordan Lee", assigneeUser: "jordan.lee"),
                Ticket(
                    "INC1009",
                    "inc-9",
                    "incident",
                    "Closed and moved",
                    assigneeId: "user-jordan",
                    assigneeName: "Jordan Lee",
                    assigneeUser: "jordan.lee",
                    groupId: "group-net",
                    groupName: "Network",
                    stateValue: "7",
                    stateDisplay: "Closed")
            ]
        };
        var store = new CountingStore();
        await QueueDayCheck.RunAsync(source, store, [Sam], Morning, CancellationToken.None);
        Assert.Equal(0, source.Lookups);
        Assert.Equal(1, store.Saves);
        phase = 1;
        var report = await QueueDayCheck.RunAsync(source, store, [Sam], Afternoon, CancellationToken.None);

        Assert.Equal(1, store.Saves);
        Assert.Equal(1, source.Lookups);
        Assert.Equal(3, source.LastLookup.Count);
        Assert.DoesNotContain(source.LastLookup, ticket => ticket.SysId == "inc-1");
        var lines = report.People.Single(person => person.PersonName == "Sam Patel").Lines;
        AssertLine(lines, "INC1001", "Printer jam", QueueDayKind.Changed, QueueDayText.UpdatedByAssignee);
        AssertLine(lines, "INC1002", "Badge photo", QueueDayKind.Changed, QueueDayText.UpdatedByCaller);
        AssertLine(lines, "INC1003", "Vpn drop", QueueDayKind.Changed, "Updated by alex.rivera");
        AssertLine(lines, "INC1004", "Dock flicker", QueueDayKind.Changed, "Assignment group changed from Client Services to Network");
        AssertLine(lines, "INC1005", "New laptop", QueueDayKind.New, QueueDayText.New);
        AssertLine(lines, "INC1006", "Old monitor", QueueDayKind.Missing, "Closed, state Closed");
        AssertLine(lines, "INC1007", "Moved desk", QueueDayKind.Missing, "Assigned to Jordan Lee");
        AssertLine(lines, "INC1008", "Same all day", QueueDayKind.Unchanged, QueueDayText.Unchanged);
        AssertLine(
            lines,
            "INC1009",
            "Closed and moved",
            QueueDayKind.Missing,
            "Closed, state Closed. Assigned to Jordan Lee. Assignment group changed from Client Services to Network");
        Assert.DoesNotContain(lines, line => line.Number == "INC-FREE");
        Assert.Equal(Morning, report.BaselineAt);
        var groups = QueueCheckPersonRow.From(report.People.Single()).Groups.Select(group => group.Title).ToArray();
        Assert.Equal(["Changed", "New", "Missing", "Unchanged"], groups);
    }

    [Fact]
    public async Task ANewLocalDayStartsANewBaseline()
    {
        var store = new CountingStore();
        store.Save(new QueueDayBaseline(
            new DateOnly(2026, 10, 24),
            new DateTime(2026, 10, 24, 8, 0, 0, DateTimeKind.Local),
            [new QueueDayPersonBaseline("user-sam", "Sam Patel", [Ticket("INC-OLD", "inc-old", "incident", "Yesterday")])]));
        var source = new ScriptSource
        {
            Queue = _ => [Ticket("INC-NEW", "inc-new", "incident", "Fresh today")]
        };
        var report = await QueueDayCheck.RunAsync(source, store, [Sam], Morning, CancellationToken.None);

        Assert.Equal(0, source.Lookups);
        Assert.Equal(2, store.Saves);
        Assert.DoesNotContain(report.Lines, line => line.Number == "INC-OLD");
        var line = Assert.Single(report.Lines);
        Assert.Equal("INC-NEW", line.Number);
        Assert.Equal("Fresh today", line.ShortDescription);
        Assert.Equal(QueueDayKind.Unchanged, line.Kind);
        Assert.Equal("INC-NEW", Assert.Single(Assert.Single(store.Find(DateOnly.FromDateTime(Morning))!.People).Tickets).Number);
        Assert.Equal("INC-OLD", Assert.Single(Assert.Single(store.Find(new DateOnly(2026, 10, 24))!.People).Tickets).Number);
    }

    [Fact]
    public async Task AnEmptyTeamDoesNotCallTheSource()
    {
        var source = new ScriptSource();
        var report = await QueueDayCheck.RunAsync(source, new MemoryQueueDayStore(), [], Morning, CancellationToken.None);
        Assert.Equal(0, source.Lists);
        Assert.Equal(0, source.Lookups);
        Assert.Equal(QueueDayText.DefineTeamMessage, report.EmptyMessage);
    }

    [Fact]
    public async Task LeadsNotActivatedDoesNotScanUntilItIsUnlocked()
    {
        using var client = new SampleServiceNowClient();
        var store = new MemoryQueueDayStore();
        var main = new MainViewModel(
            new MemorySettingsStore(),
            new RecordingDesktopServices(),
            sampleClientFactory: () => client,
            queueDay: store);
        main.Connection.UseSampleData = true;
        main.Connection.DownloadCacheOnLaunch = false;
        main.Connection.RememberLeadTeam(["user-sam"]);
        main.Connection.RememberLeadTeamSaved(true);
        await main.ConnectCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => main.Leads.Members.Count > 0);
        await Task.Delay(200);
        Assert.Equal(0, client.QueueDayListCalls);
        Assert.Equal(0, client.QueueDayLookupCalls);
        Assert.Null(store.Find(DateOnly.FromDateTime(DateTime.Now)));

        main.Connection.LeadsPassword = "iddqd";
        main.Connection.UnlockLeadsCommand.Execute(null);
        await WaitUntilAsync(() =>
            client.QueueDayListCalls > 0 && store.Find(DateOnly.FromDateTime(DateTime.Now)) is not null);
        Assert.True(client.QueueDayListCalls > 0);
        Assert.Equal(0, client.QueueDayLookupCalls);
        var baseline = store.Find(DateOnly.FromDateTime(DateTime.Now));
        Assert.NotNull(baseline);
        Assert.Contains(baseline.People, person => person.PersonId == "user-sam");
        Assert.Equal(QueueDayText.BaselineSaved, main.Leads.QueueCheck.Status);
    }

    [Fact]
    public async Task OpeningWithLeadsAlreadyUnlockedComparesTodaysBaseline()
    {
        using var client = new SampleServiceNowClient();
        var store = new CountingStore();
        var today = DateOnly.FromDateTime(DateTime.Now);
        var taken = DateTime.Now.AddHours(-3);
        store.Save(new QueueDayBaseline(
            today,
            taken,
            [
                new QueueDayPersonBaseline(
                    "user-sam",
                    "Sam Patel",
                    [Ticket("KEEP", "keep-id", "incident", "Kept from this morning", updatedOn: "2026-10-25 01:00:00")])
            ]));
        var main = new MainViewModel(
            new MemorySettingsStore(),
            new RecordingDesktopServices(),
            sampleClientFactory: () => client,
            queueDay: store);
        main.Connection.UseSampleData = true;
        main.Connection.DownloadCacheOnLaunch = false;
        main.Connection.RememberLeadTeam(["user-sam"]);
        main.Connection.RememberLeadTeamSaved(true);
        main.Connection.LeadsPassword = "iddqd";
        main.Connection.UnlockLeadsCommand.Execute(null);
        await main.ConnectCommand.ExecuteAsync(null);

        await WaitUntilAsync(() => main.Leads.QueueCheck.Status == QueueDayText.Compared);
        Assert.Equal(1, store.Saves);
        Assert.Equal(taken, store.Find(today)!.TakenAt);
        Assert.Equal("KEEP", Assert.Single(Assert.Single(store.Find(today)!.People).Tickets).Number);
        Assert.True(client.QueueDayListCalls > 0);
        Assert.Contains(main.Leads.QueueCheck.People, person => person.Name == "Sam Patel");
    }

    [Fact]
    public async Task AnEmptyMyTeamDoesNotCallServiceNow()
    {
        using var client = new SampleServiceNowClient();
        var main = new MainViewModel(
            new MemorySettingsStore(),
            new RecordingDesktopServices(),
            sampleClientFactory: () => client,
            queueDay: new MemoryQueueDayStore());
        main.Connection.UseSampleData = true;
        main.Connection.DownloadCacheOnLaunch = false;
        main.Connection.RememberLeadTeamSaved(true);
        main.Connection.LeadsPassword = "iddqd";
        main.Connection.UnlockLeadsCommand.Execute(null);
        await main.ConnectCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => main.Leads.Members.Count > 0 && main.Leads.QueueCheck.EmptyMessage.Length > 0);
        await Task.Delay(200);

        Assert.Equal(0, client.QueueDayListCalls);
        Assert.Equal(0, client.QueueDayLookupCalls);
        Assert.Equal(QueueDayText.DefineTeamMessage, main.Leads.QueueCheck.EmptyMessage);
        Assert.DoesNotContain(client.RecentActivity, call => call.Path.Contains("assigned_to=", StringComparison.Ordinal));
    }

    [Fact]
    public void LookupUsesSmallIdChunksAndDoesNotUseNq()
    {
        var ids = Enumerable.Range(0, 45).Select(index => "id" + index.ToString("00")).ToArray();
        var queries = QueueDayQuery.LookupQueries("incident", ids);
        Assert.Equal(3, queries.Count);
        Assert.All(queries, query =>
        {
            Assert.StartsWith("sys_idIN", query, StringComparison.Ordinal);
            Assert.DoesNotContain("^NQ", query, StringComparison.Ordinal);
            Assert.DoesNotContain("active=", query, StringComparison.Ordinal);
            Assert.DoesNotContain("^", query, StringComparison.Ordinal);
        });
        Assert.Equal(20, queries[0]["sys_idIN".Length..].Split(',').Length);
        Assert.Equal(5, queries[2]["sys_idIN".Length..].Split(',').Length);

        var incident = QueueDayQuery.OpenAssigned("user-sam", "incident");
        var item = QueueDayQuery.OpenAssigned("user-sam", "sc_req_item");
        var walkUp = QueueDayQuery.OpenAssigned("user-sam", "interaction");
        Assert.Contains("assigned_to=user-sam^", incident, StringComparison.Ordinal);
        Assert.Contains("stateNOT IN6,7,8", incident, StringComparison.Ordinal);
        Assert.Contains("stateNOT IN3,4,7", item, StringComparison.Ordinal);
        Assert.Contains("type=walkup", walkUp, StringComparison.Ordinal);
        Assert.All(new[] { incident, item, walkUp }, query =>
        {
            Assert.DoesNotContain("^NQ", query, StringComparison.Ordinal);
            Assert.DoesNotContain("ISEMPTY", query, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task ClientDropsUnassignedTicketsAndLooksUpMissingOnesInChunks()
    {
        var handler = new StubHandler((request, _) =>
        {
            var path = Uri.UnescapeDataString(request.RequestUri?.PathAndQuery ?? "");
            if (path.Contains("/table/incident", StringComparison.Ordinal) && path.Contains("assigned_to=", StringComparison.Ordinal))
                return Api.Json(IncidentQueueJson);
            if (path.Contains("/table/sc_req_item", StringComparison.Ordinal))
                return Api.Json(ItemQueueJson);
            if (path.Contains("/table/interaction", StringComparison.Ordinal) && path.Contains("type=walkup", StringComparison.Ordinal))
                return Api.Json(WalkUpQueueJson);
            if (path.Contains("sys_idIN", StringComparison.Ordinal))
                return Api.Json("{\"result\":[]}");
            return Api.Json("{\"result\":[]}");
        });
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);
        var rows = await client.ListOpenAssignedQueueAsync("user-sam", CancellationToken.None);
        var calls = handler.Snapshot().Select(call => Uri.UnescapeDataString(call.PathAndQuery)).ToArray();

        Assert.Equal(3, calls.Length);
        Assert.Contains(calls, call => call.Contains("/table/incident?", StringComparison.Ordinal) && call.Contains("assigned_to=user-sam", StringComparison.Ordinal));
        Assert.Contains(calls, call => call.Contains("/table/sc_req_item?", StringComparison.Ordinal));
        Assert.Contains(calls, call => call.Contains("/table/interaction?", StringComparison.Ordinal) && call.Contains("type=walkup", StringComparison.Ordinal));
        Assert.All(calls, call =>
        {
            Assert.DoesNotContain("^NQ", call, StringComparison.Ordinal);
            Assert.DoesNotContain("ISEMPTY", call, StringComparison.Ordinal);
        });
        Assert.Contains(rows, row => row.Number == "INC1001" && row.CallerName == "Jordan Lee" && row.CallerUserName == "jordan.lee" && row.AssigneeUserName == "sam.patel");
        Assert.Contains(rows, row => row.Number == "RITM1001" && row.Table == "sc_req_item" && row.CallerUserName == "jordan.lee");
        Assert.Contains(rows, row => row.Number == "IMS1001" && row.Table == "interaction" && row.CallerName == "Sam Patel");
        Assert.DoesNotContain(rows, row => row.Number == "INC-FREE");
        Assert.DoesNotContain(rows, row => row.Number == "IMS-PHONE");

        handler.Calls.Clear();
        var refs = Enumerable.Range(0, 25).Select(index => new QueueDayTicketRef("incident", "rec" + index.ToString("00"))).ToArray();
        var lookedUp = await client.LookupQueueTicketsAsync(refs, CancellationToken.None);
        var lookups = handler.Snapshot().Select(call => Uri.UnescapeDataString(call.PathAndQuery)).ToArray();
        Assert.Empty(lookedUp);
        Assert.Equal(2, lookups.Length);
        Assert.All(lookups, call =>
        {
            Assert.Contains("sys_idIN", call, StringComparison.Ordinal);
            Assert.DoesNotContain("^NQ", call, StringComparison.Ordinal);
            Assert.DoesNotContain("active=true", call, StringComparison.Ordinal);
            Assert.Contains("/table/incident?", call, StringComparison.Ordinal);
        });
        Assert.Equal(20, CountIds(lookups[0]));
        Assert.Equal(5, CountIds(lookups[1]));
    }

    [Fact]
    public void BaselineFileRoundTripsAndStoresNoSecrets()
    {
        var taken = new DateTime(2026, 10, 25, 8, 1, 0, DateTimeKind.Local);
        var baseline = new QueueDayBaseline(
            new DateOnly(2026, 10, 25),
            taken,
            [
                new QueueDayPersonBaseline(
                    "user-sam",
                    "Sam Patel",
                    [Ticket("INC1001", "inc-1", "incident", "Printer jam", updatedOn: "2026-10-25 07:00:00", updatedBy: "sam.patel")])
            ]);
        var folder = Path.Combine(Path.GetTempPath(), "queue-day-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "queue-day.json");
        try
        {
            QueueDayFileCodec.WriteVerified(path, [baseline], baseline);
            var json = File.ReadAllText(path);
            var read = Assert.Single(QueueDayFileCodec.Deserialize(json));
            Assert.Equal(baseline.LocalDate, read.LocalDate);
            Assert.Equal(baseline.TakenAt, read.TakenAt);
            var ticket = Assert.Single(Assert.Single(read.People).Tickets);
            Assert.Equal("INC1001", ticket.Number);
            Assert.Equal("Printer jam", ticket.ShortDescription);
            Assert.Equal("jordan.lee", ticket.CallerUserName);
            Assert.Equal("Jordan Lee", ticket.CallerName);
            Assert.Equal("sam.patel", ticket.AssigneeUserName);
            Assert.Contains("\"sysUpdatedOn\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cookie", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
            Assert.Equal([], QueueDayFileCodec.Deserialize("{not json"));
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    private static void AssertLine(
        IReadOnlyList<QueueDayLine> lines,
        string number,
        string description,
        QueueDayKind kind,
        string breakdown)
    {
        var line = Assert.Single(lines, item => item.Number == number);
        Assert.Equal(description, line.ShortDescription);
        Assert.Equal(kind, line.Kind);
        Assert.Equal(breakdown, line.Breakdown);
        Assert.False(string.IsNullOrWhiteSpace(line.Number));
        Assert.False(string.IsNullOrWhiteSpace(line.ShortDescription));
        Assert.False(string.IsNullOrWhiteSpace(line.Breakdown));
    }

    private static IReadOnlyList<QueueDayTicket> MorningQueue() =>
    [
        Ticket("INC1001", "inc-1", "incident", "Printer jam", updatedOn: "2026-10-25 07:00:00", updatedBy: "sam.patel"),
        Ticket("INC1002", "inc-2", "incident", "Badge photo", updatedOn: "2026-10-25 07:10:00", updatedBy: "sam.patel"),
        Ticket("INC1003", "inc-3", "incident", "Vpn drop", updatedOn: "2026-10-25 07:20:00", updatedBy: "sam.patel"),
        Ticket("INC1004", "inc-4", "incident", "Dock flicker", updatedOn: "2026-10-25 07:30:00", updatedBy: "sam.patel"),
        Ticket("INC1006", "inc-6", "incident", "Old monitor", updatedOn: "2026-10-25 07:40:00", updatedBy: "sam.patel"),
        Ticket("INC1007", "inc-7", "incident", "Moved desk", updatedOn: "2026-10-25 07:45:00", updatedBy: "sam.patel"),
        Ticket("INC1008", "inc-8", "incident", "Same all day", updatedOn: "2026-10-25 07:50:00", updatedBy: "sam.patel"),
        Ticket("INC1009", "inc-9", "incident", "Closed and moved", updatedOn: "2026-10-25 07:55:00", updatedBy: "sam.patel"),
        Ticket("INC-FREE", "inc-free", "incident", "Sitting in the group", assigneeId: "", assigneeName: "", assigneeUser: "")
    ];

    private static IReadOnlyList<QueueDayTicket> AfternoonQueue() =>
    [
        Ticket("INC1001", "inc-1", "incident", "Printer jam", updatedOn: "2026-10-25 09:00:00", updatedBy: "sam.patel"),
        Ticket("INC1002", "inc-2", "incident", "Badge photo", updatedOn: "2026-10-25 09:05:00", updatedBy: "jordan.lee"),
        Ticket("INC1003", "inc-3", "incident", "Vpn drop", updatedOn: "2026-10-25 09:10:00", updatedBy: "alex.rivera"),
        Ticket("INC1004", "inc-4", "incident", "Dock flicker", updatedOn: "2026-10-25 07:30:00", updatedBy: "sam.patel", groupId: "group-net", groupName: "Network"),
        Ticket("INC1005", "inc-5", "incident", "New laptop", updatedOn: "2026-10-25 10:00:00", updatedBy: "sam.patel"),
        Ticket("INC1008", "inc-8", "incident", "Same all day", updatedOn: "2026-10-25 07:50:00", updatedBy: "sam.patel"),
        Ticket("INC-FREE", "inc-free", "incident", "Sitting in the group", assigneeId: "", assigneeName: "", assigneeUser: "")
    ];

    private static QueueDayTicket Ticket(
        string number,
        string sysId,
        string table,
        string description,
        string assigneeId = "user-sam",
        string assigneeName = "Sam Patel",
        string assigneeUser = "sam.patel",
        string groupId = "group-cs",
        string groupName = "Client Services",
        string stateValue = "2",
        string stateDisplay = "In Progress",
        string updatedOn = "2026-10-25 07:00:00",
        string updatedBy = "sam.patel",
        string callerId = "user-jordan",
        string callerName = "Jordan Lee",
        string callerUser = "jordan.lee")
    {
        DateTime? updatedAt = ServiceNowDesk.Alerts.AlertClassifier.TryParseInstant(updatedOn, out var parsed) ? parsed : null;
        return new QueueDayTicket(
            number,
            sysId,
            table,
            description,
            assigneeId,
            assigneeName,
            assigneeUser,
            groupId,
            groupName,
            stateValue,
            stateDisplay,
            updatedOn,
            updatedAt,
            updatedBy,
            callerId,
            callerName,
            callerUser);
    }

    private static int CountIds(string query)
    {
        var raw = Uri.UnescapeDataString(query);
        var marker = raw.IndexOf("sys_idIN", StringComparison.Ordinal);
        var ids = raw[(marker + "sys_idIN".Length)..];
        var end = ids.IndexOf('&');
        if (end >= 0)
            ids = ids[..end];
        return ids.Split(',', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static async Task WaitUntilAsync(Func<bool> ready)
    {
        for (var attempt = 0; attempt < 40 && !ready(); attempt++)
            await Task.Delay(50);
    }

    private const string IncidentQueueJson = """
        {"result":[
          {
            "sys_id":{"value":"inc-1","display_value":"inc-1"},
            "number":{"value":"INC1001","display_value":"INC1001"},
            "short_description":{"value":"Printer jam","display_value":"Printer jam"},
            "state":{"value":"2","display_value":"In Progress"},
            "assigned_to":{"value":"user-sam","display_value":"Sam Patel"},
            "assigned_to.user_name":{"value":"sam.patel","display_value":"sam.patel"},
            "assignment_group":{"value":"group-cs","display_value":"Client Services"},
            "sys_updated_on":{"value":"2026-10-25 07:00:00","display_value":"2026-10-25 07:00:00"},
            "sys_updated_by":{"value":"sam.patel","display_value":"sam.patel"},
            "caller_id":{"value":"user-jordan","display_value":"Jordan Lee"},
            "caller_id.user_name":{"value":"jordan.lee","display_value":"jordan.lee"},
            "active":{"value":"true","display_value":"true"}
          },
          {
            "sys_id":{"value":"inc-free","display_value":"inc-free"},
            "number":{"value":"INC-FREE","display_value":"INC-FREE"},
            "short_description":{"value":"Sitting in the group","display_value":"Sitting in the group"},
            "state":{"value":"1","display_value":"New"},
            "assigned_to":{"value":"","display_value":""},
            "assignment_group":{"value":"group-cs","display_value":"Client Services"},
            "sys_updated_on":{"value":"2026-10-25 07:00:00","display_value":"2026-10-25 07:00:00"},
            "sys_updated_by":{"value":"system","display_value":"system"},
            "caller_id":{"value":"user-jordan","display_value":"Jordan Lee"},
            "active":{"value":"true","display_value":"true"}
          }
        ]}
        """;

    private const string ItemQueueJson = """
        {"result":[{
          "sys_id":{"value":"ritm-1","display_value":"ritm-1"},
          "number":{"value":"RITM1001","display_value":"RITM1001"},
          "short_description":{"value":"USB-C dock","display_value":"USB-C dock"},
          "state":{"value":"2","display_value":"Work in Progress"},
          "assigned_to":{"value":"user-sam","display_value":"Sam Patel"},
          "assigned_to.user_name":{"value":"sam.patel","display_value":"sam.patel"},
          "assignment_group":{"value":"group-cs","display_value":"Client Services"},
          "sys_updated_on":{"value":"2026-10-25 07:00:00","display_value":"2026-10-25 07:00:00"},
          "sys_updated_by":{"value":"sam.patel","display_value":"sam.patel"},
          "requested_for":{"value":"user-jordan","display_value":"Jordan Lee"},
          "requested_for.user_name":{"value":"jordan.lee","display_value":"jordan.lee"},
          "active":{"value":"true","display_value":"true"}
        }]}
        """;

    private const string WalkUpQueueJson = """
        {"result":[
          {
            "sys_id":{"value":"ims-1","display_value":"ims-1"},
            "number":{"value":"IMS1001","display_value":"IMS1001"},
            "short_description":{"value":"Password reset","display_value":"Password reset"},
            "state":{"value":"new","display_value":"New"},
            "type":{"value":"walkup","display_value":"Walk-up"},
            "assigned_to":{"value":"user-sam","display_value":"Sam Patel"},
            "assigned_to.user_name":{"value":"sam.patel","display_value":"sam.patel"},
            "assignment_group":{"value":"group-cs","display_value":"Client Services"},
            "sys_updated_on":{"value":"2026-10-25 07:00:00","display_value":"2026-10-25 07:00:00"},
            "sys_updated_by":{"value":"sam.patel","display_value":"sam.patel"},
            "opened_for":{"value":"user-sam","display_value":"Sam Patel"},
            "opened_for.user_name":{"value":"sam.patel","display_value":"sam.patel"},
            "active":{"value":"true","display_value":"true"}
          },
          {
            "sys_id":{"value":"ims-phone","display_value":"ims-phone"},
            "number":{"value":"IMS-PHONE","display_value":"IMS-PHONE"},
            "short_description":{"value":"Phone call","display_value":"Phone call"},
            "state":{"value":"new","display_value":"New"},
            "type":{"value":"phone","display_value":"Phone"},
            "assigned_to":{"value":"user-sam","display_value":"Sam Patel"},
            "assignment_group":{"value":"group-cs","display_value":"Client Services"},
            "active":{"value":"true","display_value":"true"}
          }
        ]}
        """;

    private sealed class ScriptSource : IQueueDaySource
    {
        public Func<string, IReadOnlyList<QueueDayTicket>>? Queue { get; set; }

        public IReadOnlyList<QueueDayTicket> LookupRows { get; set; } = [];

        public int Lists { get; private set; }

        public int Lookups { get; private set; }

        public IReadOnlyList<QueueDayTicketRef> LastLookup { get; private set; } = [];

        public Task<IReadOnlyList<QueueDayTicket>> ListOpenAssignedAsync(string assigneeSysId, CancellationToken cancellationToken)
        {
            Lists++;
            return Task.FromResult(Queue?.Invoke(assigneeSysId) ?? []);
        }

        public Task<IReadOnlyList<QueueDayTicket>> LookupAsync(IReadOnlyList<QueueDayTicketRef> tickets, CancellationToken cancellationToken)
        {
            Lookups++;
            LastLookup = tickets;
            return Task.FromResult(LookupRows);
        }
    }

    private sealed class CountingStore : IQueueDayStore
    {
        private readonly MemoryQueueDayStore _inner = new();

        public int Saves { get; private set; }

        public QueueDayBaseline? Find(DateOnly localDate) => _inner.Find(localDate);

        public void Save(QueueDayBaseline baseline)
        {
            Saves++;
            _inner.Save(baseline);
        }
    }
}
