namespace ServiceNowDesk.WorkEffort;

/// <summary>
/// Progress for one Work Effort run. <see cref="Completed"/> and <see cref="Total"/> drive the bar.
/// <see cref="Status"/> is the minute estimate while the run is still going.
/// </summary>
public readonly record struct WorkEffortProgress(int Completed, int Total, string Status)
{
    public const int Steps = 3;

    public static WorkEffortProgress Loading(WorkEffortScale scale, int completedTables)
    {
        _ = scale;
        var done = completedTables < 0 ? 0 : completedTables;
        if (done > Steps)
            done = Steps;
        var open = done < Steps;
        var bar = WorkEffortEstimate.Bar(done, Steps, open, 0, null);
        return new WorkEffortProgress(bar, WorkEffortEstimate.BarMaximum, WorkEffortEstimate.Text(1));
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
