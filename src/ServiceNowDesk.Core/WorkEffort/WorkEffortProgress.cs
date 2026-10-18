using System.Globalization;

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

    /// <summary>
    /// Loading line for the person (or history pass) and the local slice being read.
    /// </summary>
    public static string Reading(string subject, WorkEffortWindow window, int page)
    {
        var who = string.IsNullOrWhiteSpace(subject) ? "records" : subject.Trim();
        var start = window.Start;
        var end = window.End;
        var day = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var wholeDay = start.TimeOfDay == TimeSpan.Zero && end.Hour == 23 && end.Minute == 59;
        string when;
        if (start.Date != end.Date)
            when = day + " to " + end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        else if (!wholeDay)
            when = day + " " + start.ToString("HH:mm", CultureInfo.InvariantCulture) + "-" + end.ToString("HH:mm", CultureInfo.InvariantCulture);
        else
            when = day;
        var shown = page < 1 ? 1 : page;
        return "Reading " + who + " for " + when + ", page " + shown.ToString(CultureInfo.InvariantCulture) + ".";
    }

    public static string Phrase(WorkEffortScale scale) => scale switch
    {
        WorkEffortScale.ThisWeek => "the last 7 days",
        WorkEffortScale.ThisMonth => "the last 30 days",
        WorkEffortScale.Last4Weeks => "the last 4 weeks",
        WorkEffortScale.SixMonths => "the last 6 months",
        WorkEffortScale.TwelveMonths => "the last 12 months",
        WorkEffortScale.DateRange => "the selected dates",
        _ => "today"
    };
}
