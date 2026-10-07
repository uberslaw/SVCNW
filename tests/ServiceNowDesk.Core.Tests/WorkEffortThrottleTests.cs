using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Xunit.Abstractions;
using ServiceNowDesk.Alerts;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;
using ServiceNowDesk.WorkEffort;

namespace ServiceNowDesk.Tests;

public class WorkEffortThrottleTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 15, 0, 0);

    private readonly ITestOutputHelper _output;

    public WorkEffortThrottleTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task NoTeamMeansNoClientQueryAndShowsThePrompt()
    {
        var handler = new StubHandler((_, _) => throw new InvalidOperationException("ServiceNow was called."));
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);
        var report = await client.GetWorkEffortAsync(WorkEffortScale.Today, Now, [], CancellationToken.None);
        Assert.Equal(WorkEffortReport.DefineTeamMessage, report.EmptyMessage);
        Assert.Empty(report.Rows);
        Assert.Empty(handler.Calls);

        var calls = 0;
        var page = new WorkEffortViewModel();
        var skipped = await page.StartBackground(
            Now,
            [],
            _ =>
            {
                calls++;
                return Array.Empty<WorkEffortTouch>();
            },
            CancellationToken.None);
        Assert.Equal(0, calls);
        Assert.Equal(WorkEffortReport.DefineTeamMessage, skipped.EmptyMessage);
        Assert.Equal(WorkEffortReport.DefineTeamMessage, page.EmptyMessage);
        Assert.False(page.IsLoading);
        Assert.False(page.HasRows);
    }

    [Fact]
    public async Task OpeningWorkEffortWithNoTeamDoesNotQuery()
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
        main.Connection.UnlockLeads();
        await main.ConnectCommand.ExecuteAsync(null);

        Assert.True(main.TrySelect(DeskSection.Leads));
        main.Leads.Area = LeadArea.WorkEffort;
        await Task.Delay(250);

        Assert.Equal(0, hold.Queries);
        Assert.Equal(WorkEffortReport.DefineTeamMessage, main.Leads.WorkEffort.EmptyMessage);
        Assert.False(main.Leads.WorkEffort.IsLoading);
        main.DisconnectCommand.Execute(null);
    }

    [Fact]
    public void ADefinedTeamDoesNotIncludeOutsidersAndTheEstimateTextIsProduced()
    {
        var leads = new LeadsViewModel();
        leads.SetRoster(
            [
                new Choice("sample-user", "Alex Rivera"),
                new Choice("user-jordan", "Jordan Lee"),
                new Choice("user-sam", "Sam Patel")
            ],
            ["sample-user", "user-jordan"]);
        var team = leads.DefinedTeam(["user-sam"]);
        Assert.Equal(["sample-user", "user-jordan"], team.Select(person => person.SysId).OrderBy(id => id).ToArray());
        Assert.DoesNotContain(team, person => person.SysId == "user-sam");

        var untouched = new LeadsViewModel();
        Assert.Empty(untouched.DefinedTeam());
        Assert.Equal("sample-user", Assert.Single(untouched.DefinedTeam(["sample-user"])).SysId);

        var window = WorkEffortWindow.For(WorkEffortScale.Today, Now);
        var attempt = new WorkEffortAttempt(team, window, WorkEffortQuery.SafetyCap);
        foreach (var touch in SampleWorkEffort.Touches(Now))
            attempt.TakeRow(touch);
        var streamed = attempt.ToRows();
        var built = WorkEffortScore.Build(team, SampleWorkEffort.Touches(Now), window);
        Assert.DoesNotContain(streamed, row => row.Name == "Sam Patel");
        Assert.Equal(built.Select(row => row.Name), streamed.Select(row => row.Name));
        Assert.Equal(built.Select(row => row.Weighted), streamed.Select(row => row.Weighted));

        Assert.Equal("Generating report. check back in: 1 minutes", WorkEffortEstimate.Text(1));
        Assert.Equal("Generating report. check back in: 2 minutes", WorkEffortEstimate.Text(2));
        Assert.Equal(1, WorkEffortEstimate.Minutes(0, 0, 10_000));
        Assert.Equal(1, WorkEffortEstimate.Minutes(10, 600, 600));
        Assert.Equal(2, WorkEffortEstimate.Minutes(10, 600, 7_200));
        Assert.Equal(3, WorkEffortEstimate.Minutes(10, 600, 9_000));
    }

    [Fact]
    public async Task TableRequestsStayOneAtATime()
    {
        var inflight = 0;
        var max = 0;
        var handler = new StubHandler((_, _) =>
        {
            var now = Interlocked.Increment(ref inflight);
            var seen = max;
            while (now > seen)
                seen = Interlocked.CompareExchange(ref max, now, seen);
            Thread.Sleep(25);
            Interlocked.Decrement(ref inflight);
            return Api.Json("""{"result":[]}""");
        });
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler);
        var team = new[]
        {
            new WorkEffortPerson("sample-user", "Alex Rivera", "alex.rivera"),
            new WorkEffortPerson("user-jordan", "Jordan Lee", "jordan.lee")
        };
        await client.GetWorkEffortAsync(WorkEffortScale.Today, Now, team, CancellationToken.None);
        Assert.Equal(1, max);
        Assert.DoesNotContain(handler.Calls, call => call.PathAndQuery.Contains("sys_user_grmember", StringComparison.Ordinal));
        Assert.Contains(handler.Calls, call => call.PathAndQuery.Contains("table/incident", StringComparison.Ordinal));
    }

    [Fact(Timeout = 180000)]
    public async Task HeavyReportReturnsPromptlyAndStaysBounded()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        var team = Enumerable.Range(0, 8)
            .Select(index => new WorkEffortPerson(
                "user-" + index.ToString(CultureInfo.InvariantCulture),
                "Person " + index.ToString(CultureInfo.InvariantCulture),
                "login-" + index.ToString(CultureInfo.InvariantCulture)))
            .ToArray();
        var caller = Environment.CurrentManagedThreadId;
        var worker = 0;
        ThreadPriority? priority = null;
        var page = new WorkEffortViewModel();
        var started = Stopwatch.StartNew();
        var first = page.StartBackground(
            Now,
            team,
            table =>
            {
                worker = Environment.CurrentManagedThreadId;
                priority = Thread.CurrentThread.Priority;
                if (table == 0)
                    Thread.Sleep(800);
                return HeavyRows(table, 40_000, Now);
            },
            budget.Token);
        started.Stop();
        _output.WriteLine("UI start returned in {0} ms", started.ElapsedMilliseconds);
        Console.WriteLine("WorkEffort UI start returned in " + started.ElapsedMilliseconds + " ms");
        Assert.True(started.Elapsed < TimeSpan.FromMilliseconds(500), "The UI call blocked for " + started.Elapsed + ".");
        Assert.True(page.IsLoading);
        Assert.Equal(WorkEffortEstimate.Text(1), page.Status);

        var finished = await Task.WhenAny(first, Task.Delay(TimeSpan.FromMinutes(4), budget.Token));
        Assert.Same(first, finished);
        var report = await first;
        page.Remember(page.Scale, Now, report, WorkEffortTeam.Key(team));
        Assert.False(page.IsLoading);
        Assert.True(page.HasRows);
        Assert.DoesNotContain("Generating report", page.Status, StringComparison.Ordinal);
        Assert.NotEqual(caller, worker);
        if (priority == ThreadPriority.BelowNormal || OperatingSystem.IsWindows())
            Assert.Equal(ThreadPriority.BelowNormal, priority);
        _output.WriteLine("worker priority {0}", priority);

        var old = MeasureOld(people: 250, rowsPerTable: 4_000);
        _output.WriteLine(
            "OLD wall={0:0.00}s cpu={1:0.00}s allocated={2:0.0}MB retained={3:0.0}MB",
            old.Wall.TotalSeconds,
            old.Cpu.TotalSeconds,
            old.Allocated / 1_048_576d,
            old.Retained / 1_048_576d);
        Console.WriteLine(
            "WorkEffort OLD wall=" + old.Wall.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)
            + "s cpu=" + old.Cpu.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)
            + "s allocated=" + (old.Allocated / 1_048_576d).ToString("0.0", CultureInfo.InvariantCulture)
            + "MB retained=" + (old.Retained / 1_048_576d).ToString("0.0", CultureInfo.InvariantCulture) + "MB");

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long peakWorkingSet = Process.GetCurrentProcess().WorkingSet64;
        long peakRound = 0;
        var streamCpu = Process.GetCurrentProcess().TotalProcessorTime;
        var streamWall = Stopwatch.StartNew();
        var rounds = 0;
        var until = DateTime.UtcNow.AddSeconds(50);
        using (new WorkEffortQuietScope())
        {
        while (DateTime.UtcNow < until)
        {
            budget.Token.ThrowIfCancellationRequested();
            var allocStart = GC.GetAllocatedBytesForCurrentThread();
            var round = StreamRound(team, 200_000);
            var roundAlloc = GC.GetAllocatedBytesForCurrentThread() - allocStart;
            if (roundAlloc > peakRound)
                peakRound = roundAlloc;
            Assert.Contains(WorkEffortQuery.CapNotice, round.Status, StringComparison.Ordinal);
            Assert.True(
                roundAlloc < 48L * 1024 * 1024,
                "One capped round allocated " + roundAlloc + " bytes. The cap is not holding.");
            rounds++;
            var workingSet = Process.GetCurrentProcess().WorkingSet64;
            if (workingSet > peakWorkingSet)
                peakWorkingSet = workingSet;
            if (peakWorkingSet > 1200L * 1024 * 1024)
                Assert.Fail("Working set climbed to " + peakWorkingSet + " bytes.");
        }
        }

        streamWall.Stop();
        var streamCpuDelta = Process.GetCurrentProcess().TotalProcessorTime - streamCpu;
        _output.WriteLine(
            "NEW rounds={0} wall={1:0.00}s cpu={2:0.00}s peakRound={3:0.0}MB peakWorkingSet={4:0.0}MB",
            rounds,
            streamWall.Elapsed.TotalSeconds,
            streamCpuDelta.TotalSeconds,
            peakRound / 1_048_576d,
            peakWorkingSet / 1_048_576d);
        Console.WriteLine(
            "WorkEffort NEW rounds=" + rounds
            + " wall=" + streamWall.Elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)
            + "s cpu=" + streamCpuDelta.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)
            + "s peakRound=" + (peakRound / 1_048_576d).ToString("0.0", CultureInfo.InvariantCulture)
            + "MB peakWorkingSet=" + (peakWorkingSet / 1_048_576d).ToString("0.0", CultureInfo.InvariantCulture) + "MB");
        Assert.True(rounds > 0);
        Assert.InRange(streamWall.Elapsed.TotalSeconds, 45, 95);
        Assert.True(old.Allocated > 5L * 1024 * 1024, "The old path did not allocate a heavy fixture.");
    }

    private static WorkEffortReport StreamRound(IReadOnlyList<WorkEffortPerson> team, int availableRows)
    {
        var window = WorkEffortWindow.For(WorkEffortScale.Today, Now);
        var pace = new WorkEffortPace();
        var totals = new int[team.Count * WorkEffortAttempt.Width];
        var truncated = false;
        for (var table = 0; table < 3; table++)
        {
            pace.BeginTable();
            var attempt = new WorkEffortAttempt(team, window, WorkEffortQuery.SafetyCap);
            foreach (var touch in HeavyRows(table, availableRows, Now))
            {
                if (!attempt.TakeRow(touch))
                {
                    truncated = true;
                    break;
                }
            }

            truncated |= attempt.Truncated;
            attempt.FoldInto(totals);
            pace.CompleteTable();
        }

        return new WorkEffortReport(
            WorkEffortAttempt.ToRows(team, totals),
            WorkEffortQuery.Status(WorkEffortScale.Today, truncated, null),
            "");
    }

    private static OldMeasurement MeasureOld(int people, int rowsPerTable)
    {
        var roster = Enumerable.Range(0, people)
            .Select(index => new WorkEffortPerson(
                "crowd-" + index.ToString(CultureInfo.InvariantCulture),
                "Crowd " + index.ToString(CultureInfo.InvariantCulture),
                "crowd" + index.ToString(CultureInfo.InvariantCulture)))
            .ToArray();
        var window = WorkEffortWindow.For(WorkEffortScale.Today, Now);
        GC.Collect();
        var floor = GC.GetTotalMemory(true);
        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        var wall = Stopwatch.StartNew();
        var before = GC.GetTotalAllocatedBytes(false);
        var touches = new List<WorkEffortTouch>(rowsPerTable * 3);
        var pages = new List<string>(rowsPerTable);
        for (var table = 0; table < 3; table++)
        {
            var kind = table switch
            {
                1 => WorkEffortKind.RequestedItem,
                2 => WorkEffortKind.Interaction,
                _ => WorkEffortKind.Incident
            };
            for (var index = 0; index < rowsPerTable; index++)
            {
                touches.Add(new WorkEffortTouch(
                    "old-" + table + "-" + index.ToString(CultureInfo.InvariantCulture),
                    kind,
                    roster[index % roster.Length].SysId,
                    Now,
                    roster[index % roster.Length].SysId,
                    Now,
                    null,
                    null,
                    roster[index % roster.Length].UserName,
                    Now));
                if (index % 30 == 0)
                    pages.Add(new string('x', 16 * 1024));
            }
        }

        var rows = WorkEffortScore.Build(roster, touches, window);
        wall.Stop();
        var allocated = GC.GetTotalAllocatedBytes(false) - before;
        var cpuDelta = Process.GetCurrentProcess().TotalProcessorTime - cpu;
        GC.Collect();
        var retained = GC.GetTotalMemory(true) - floor;
        GC.KeepAlive(rows);
        GC.KeepAlive(touches);
        GC.KeepAlive(pages);
        return new OldMeasurement(wall.Elapsed, cpuDelta, allocated, retained);
    }

    private static IEnumerable<WorkEffortTouch> HeavyRows(int table, int count, DateTime when)
    {
        var kind = table switch
        {
            1 => WorkEffortKind.RequestedItem,
            2 => WorkEffortKind.Interaction,
            _ => WorkEffortKind.Incident
        };
        for (var index = 0; index < count; index++)
        {
            var owner = index % 8;
            yield return new WorkEffortTouch(
                "t" + table.ToString(CultureInfo.InvariantCulture) + "-" + index.ToString(CultureInfo.InvariantCulture),
                kind,
                "user-" + owner.ToString(CultureInfo.InvariantCulture),
                when,
                null,
                null,
                null,
                null,
                "login-" + owner.ToString(CultureInfo.InvariantCulture),
                when);
        }
    }

    private readonly record struct OldMeasurement(TimeSpan Wall, TimeSpan Cpu, long Allocated, long Retained);
}
