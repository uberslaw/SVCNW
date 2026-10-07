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
        CancellationToken cancellationToken)
    {
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
        cancellationToken.ThrowIfCancellationRequested();
        var window = WorkEffortWindow.For(scale, localNow);
        var rows = WorkEffortScore.Build(people, SampleWorkEffort.Touches(localNow), window);
        return Task.FromResult(new WorkEffortReport(rows, WorkEffortWindow.CountsLabel(scale), ""));
    }
}
