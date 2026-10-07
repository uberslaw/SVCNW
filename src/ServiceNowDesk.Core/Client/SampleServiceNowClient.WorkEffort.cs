using ServiceNowDesk.WorkEffort;

namespace ServiceNowDesk.Client;

public sealed partial class SampleServiceNowClient
{
    public Task<WorkEffortReport> GetWorkEffortAsync(WorkEffortScale scale, DateTime localNow, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Record("GET", "api/now/table/sys_user_grmember");
        Record("GET", "api/now/table/incident");
        Record("GET", "api/now/table/sc_req_item");
        Record("GET", "api/now/table/interaction");
        return Task.FromResult(SampleWorkEffort.Report(scale, localNow));
    }
}
