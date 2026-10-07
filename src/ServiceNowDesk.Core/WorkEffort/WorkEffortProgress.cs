namespace ServiceNowDesk.WorkEffort;

/// <summary>
/// Measured progress for one Work Effort query: incidents, then request items, then interactions.
/// <see cref="Completed"/> is how many of those tables have finished.
/// </summary>
public readonly record struct WorkEffortProgress(int Completed, int Total, string Status)
{
    public const int Steps = 3;

    public static WorkEffortProgress Loading(WorkEffortScale scale, int completedTables)
    {
        var done = completedTables < 0 ? 0 : completedTables;
        if (done > Steps - 1)
            done = Steps - 1;
        var table = done switch
        {
            0 => "incidents",
            1 => "request items",
            _ => "interactions"
        };
        return new WorkEffortProgress(done, Steps, "Loading " + table + " for " + Phrase(scale) + ".");
    }

    public static string Phrase(WorkEffortScale scale) => scale switch
    {
        WorkEffortScale.ThisWeek => "this week",
        WorkEffortScale.ThisMonth => "this month",
        WorkEffortScale.Last4Weeks => "the last 4 weeks",
        WorkEffortScale.SixMonths => "the last 6 months",
        WorkEffortScale.TwelveMonths => "the last 12 months",
        _ => "today"
    };
}
