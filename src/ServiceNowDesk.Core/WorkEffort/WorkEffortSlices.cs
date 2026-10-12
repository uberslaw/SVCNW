namespace ServiceNowDesk.WorkEffort;

/// <summary>
/// How a Work Effort download is split. One person is always one download.
/// The whole window stays one query for that person when people × days is at most
/// <see cref="PersonDayBudget"/>. Above the budget, each person is queried one local
/// day at a time, then the next day, then the next person.
/// </summary>
public static class WorkEffortSlices
{
    /// <summary>
    /// Person-days that still fit in one query per person.
    /// A single local day is never split, so Today stays one call per person.
    /// The budget is 1: two days for one person is already over it, and that range is day by day.
    /// A longer range or a larger team is over it as soon as the window covers more than one day.
    /// </summary>
    public const int PersonDayBudget = 1;

    /// <summary>
    /// True when the download should use one-day slices. A one-day window is always false.
    /// </summary>
    public static bool UseDaySlices(int people, int days)
    {
        if (people < 1 || days <= 1)
            return false;
        return (long)people * days > PersonDayBudget;
    }

    public static int Days(WorkEffortWindow window)
    {
        var start = DateOnly.FromDateTime(window.Start);
        var end = DateOnly.FromDateTime(window.End);
        var count = end.DayNumber - start.DayNumber + 1;
        return count < 1 ? 1 : count;
    }

    /// <summary>
    /// The query windows for one person, in order. Either the original window, or one clipped local day.
    /// </summary>
    public static IReadOnlyList<WorkEffortWindow> Plan(WorkEffortWindow window, int people)
    {
        if (!UseDaySlices(people, Days(window)))
            return [window];

        var slices = new List<WorkEffortWindow>();
        var day = DateOnly.FromDateTime(window.Start);
        var last = DateOnly.FromDateTime(window.End);
        while (day <= last)
        {
            var sliceStart = day.ToDateTime(TimeOnly.MinValue);
            var sliceEnd = day.ToDateTime(new TimeOnly(23, 59, 59));
            if (sliceStart < window.Start)
                sliceStart = window.Start;
            if (sliceEnd > window.End)
                sliceEnd = window.End;
            if (sliceStart <= sliceEnd)
                slices.Add(new WorkEffortWindow(sliceStart, sliceEnd, window.Scale));
            day = day.AddDays(1);
        }

        return slices.Count == 0 ? [window] : slices;
    }

    /// <summary>
    /// Smaller windows when a full page cannot be continued. A span longer than half a day
    /// is cut in half. A shorter span is cut into hours. One hour is the smallest piece.
    /// The two halves share the midpoint so a row on that second is not dropped; the batch merges it.
    /// </summary>
    public static IReadOnlyList<WorkEffortWindow> Subdivide(WorkEffortWindow window)
    {
        if (window.End <= window.Start)
            return [];
        var seconds = (window.End - window.Start).TotalSeconds;
        if (double.IsNaN(seconds) || seconds <= 3600)
            return [];

        if (seconds > 12 * 3600)
        {
            var mid = window.Start.AddSeconds(Math.Floor(seconds / 2));
            if (mid <= window.Start || mid >= window.End)
                return [];
            return
            [
                new WorkEffortWindow(window.Start, mid, window.Scale),
                new WorkEffortWindow(mid, window.End, window.Scale)
            ];
        }

        var parts = new List<WorkEffortWindow>();
        var cursor = window.Start;
        var guard = 0;
        while (cursor < window.End && guard++ < 48)
        {
            var hourEnd = cursor.AddHours(1).AddSeconds(-1);
            if (hourEnd > window.End)
                hourEnd = window.End;
            if (hourEnd < cursor)
                break;
            parts.Add(new WorkEffortWindow(cursor, hourEnd, window.Scale));
            var next = hourEnd.AddSeconds(1);
            if (next <= cursor)
                break;
            cursor = next;
        }

        return parts;
    }
}
