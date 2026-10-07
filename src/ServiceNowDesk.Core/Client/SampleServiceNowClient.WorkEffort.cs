using ServiceNowDesk.WorkEffort;

namespace ServiceNowDesk.Client;

public sealed partial class SampleServiceNowClient
{
    public Task<WorkEffortReport> GetWorkEffortAsync(WorkEffortScale scale, DateTime localNow, CancellationToken cancellationToken) =>
        GetWorkEffortAsync(scale, localNow, progress: null, cancellationToken);

    public Task<WorkEffortReport> GetWorkEffortAsync(
        WorkEffortScale scale,
        DateTime localNow,
        IProgress<WorkEffortProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(WorkEffortProgress.Loading(scale, 0));
        Record("GET", "api/now/table/sys_user_grmember");
        Record("GET", "api/now/table/incident");
        progress?.Report(WorkEffortProgress.Loading(scale, 1));
        Record("GET", "api/now/table/sc_req_item");
        progress?.Report(WorkEffortProgress.Loading(scale, 2));
        Record("GET", "api/now/table/interaction");
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(SampleWorkEffort.Report(scale, localNow));
    }
}
