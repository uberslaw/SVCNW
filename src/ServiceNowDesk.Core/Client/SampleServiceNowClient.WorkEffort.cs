using ServiceNowDesk.WorkEffort;

namespace ServiceNowDesk.Client;

public sealed partial class SampleServiceNowClient
{
    public Task<WorkEffortReport> GetWorkEffortAsync(
        WorkEffortScale scale,
        DateTime localNow,
        IReadOnlyList<WorkEffortPerson> team,
        CancellationToken cancellationToken) =>
        GetWorkEffortAsync(scale, localNow, team, progress: null, cancellationToken);

    public Task<WorkEffortReport> GetWorkEffortAsync(
        WorkEffortScale scale,
        DateTime localNow,
        IReadOnlyList<WorkEffortPerson> team,
        IProgress<WorkEffortProgress>? progress,
        CancellationToken cancellationToken) =>
        GetWorkEffortAsync(scale, localNow, team, regionOffices: null, progress, cancellationToken);

    public Task<WorkEffortReport> GetWorkEffortAsync(
        WorkEffortScale scale,
        DateTime localNow,
        IReadOnlyList<WorkEffortPerson> team,
        IReadOnlyList<string>? regionOffices,
        IProgress<WorkEffortProgress>? progress,
        CancellationToken cancellationToken,
        DateTime? rangeStart = null,
        DateTime? rangeEnd = null)
    {
        // Practice rows do not need a saved region. Presets and a date range both stay local.
        _ = regionOffices;
        cancellationToken.ThrowIfCancellationRequested();
        var people = SampleWorkEffort.WithLogins(team);
        if (people.Count == 0)
            return Task.FromResult(WorkEffortReport.NoTeam());

        var pace = new WorkEffortPace();
        progress?.Report(pace.Snapshot());
        pace.BeginTable();
        Record("GET", "api/now/table/incident");
        pace.CompleteTable();
        progress?.Report(pace.Snapshot());
        pace.BeginTable();
        Record("GET", "api/now/table/sc_req_item");
        pace.CompleteTable();
        progress?.Report(pace.Snapshot());
        pace.BeginTable();
        Record("GET", "api/now/table/interaction");
        pace.CompleteTable();
        progress?.Report(pace.Snapshot());
        pace.BeginTable();
        Record("GET", "api/now/table/sys_journal_field");
        pace.CompleteTable();
        progress?.Report(pace.Snapshot());
        pace.BeginTable();
        Record("GET", "api/now/table/sys_audit");
        pace.CompleteTable();
        progress?.Report(pace.Snapshot());
        cancellationToken.ThrowIfCancellationRequested();
        var window = WorkEffortWindow.Resolve(scale, localNow, rangeStart, rangeEnd);
        var touches = SampleWorkEffort.Touches(localNow);
        var rows = WorkEffortScore.Build(people, touches, window, WorkEffortUpdateMode.Daily);
        var ledger = new WorkEffortLedger(people, touches, window);
        var facts = WorkEffortLoadFacts.PracticeData(TeamListScope.Places(regionOffices));
        return Task.FromResult(new WorkEffortReport(rows, WorkEffortWindow.CountsLabel(window), "", ledger, "", facts));
    }
}
