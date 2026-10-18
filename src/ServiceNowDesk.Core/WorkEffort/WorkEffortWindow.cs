using System.Globalization;

namespace ServiceNowDesk.WorkEffort;

public enum WorkEffortScale
{
    Today,
    /// <summary>Last 7 local days ending today. The identifier stays <c>ThisWeek</c>.</summary>
    ThisWeek,
    /// <summary>Last 30 local days ending today. The identifier stays <c>ThisMonth</c>.</summary>
    ThisMonth,
    Last4Weeks,
    SixMonths,
    TwelveMonths,
    DateRange
}

/// <summary>
/// Local calendar windows. Today, the last 7 days, and the last 30 days run through the end of the local day.
/// Last 7 days is today and the six days before it. Last 30 days is today and the 29 days before it.
/// The longer preset scales are rolling windows that end at the local clock.
/// </summary>
public readonly record struct WorkEffortWindow(DateTime Start, DateTime End, WorkEffortScale Scale)
{
    public static DateTime Clock(DateTime localNow)
    {
        var clock = localNow.Kind == DateTimeKind.Utc ? localNow.ToLocalTime() : localNow;
        return new DateTime(clock.Year, clock.Month, clock.Day, clock.Hour, clock.Minute, clock.Second);
    }

    public static WorkEffortWindow For(WorkEffortScale scale, DateTime localNow)
    {
        var now = Clock(localNow);
        var endOfToday = now.Date.AddDays(1).AddSeconds(-1);
        return scale switch
        {
            WorkEffortScale.ThisWeek => new(now.Date.AddDays(-6), endOfToday, scale),
            WorkEffortScale.ThisMonth => new(now.Date.AddDays(-29), endOfToday, scale),
            WorkEffortScale.Last4Weeks => new(now.AddDays(-28), now, scale),
            WorkEffortScale.SixMonths => new(now.AddMonths(-6), now, scale),
            WorkEffortScale.TwelveMonths => new(now.AddMonths(-12), now, scale),
            WorkEffortScale.DateRange => new(now.Date, endOfToday, scale),
            _ => new(now.Date, endOfToday, WorkEffortScale.Today)
        };
    }

    /// <summary>
    /// Inclusive local dates. The window starts at midnight on the first day and
    /// ends at 23:59:59 on the last day. A reversed pair is swapped.
    /// </summary>
    public static WorkEffortWindow ForRange(DateTime start, DateTime end)
    {
        var first = LocalDate(start);
        var last = LocalDate(end);
        if (last < first)
            (first, last) = (last, first);
        var startAt = first.ToDateTime(TimeOnly.MinValue);
        var endAt = last.ToDateTime(new TimeOnly(23, 59, 59));
        return new(startAt, endAt, WorkEffortScale.DateRange);
    }

    /// <summary>
    /// Presets use <see cref="For"/>. Date range uses the picked local days, or the clock's day when a side is blank.
    /// </summary>
    public static WorkEffortWindow Resolve(WorkEffortScale scale, DateTime localNow, DateTime? rangeStart, DateTime? rangeEnd)
    {
        if (scale != WorkEffortScale.DateRange)
            return For(scale, localNow);
        var start = rangeStart ?? rangeEnd ?? localNow;
        var end = rangeEnd ?? rangeStart ?? localNow;
        return ForRange(start, end);
    }

    /// <summary>
    /// True when <paramref name="day"/> is one of the local calendar days this window covers.
    /// </summary>
    public bool IncludesLocalDay(DateOnly day)
    {
        var first = DateOnly.FromDateTime(Start);
        var last = DateOnly.FromDateTime(End);
        return day >= first && day <= last;
    }

    /// <summary>
    /// The part of this window that falls on <paramref name="day"/>.
    /// A day outside the window is ignored and this window is returned unchanged.
    /// </summary>
    public WorkEffortWindow ClippedTo(DateOnly day)
    {
        if (!IncludesLocalDay(day))
            return this;
        var start = day.ToDateTime(TimeOnly.MinValue);
        var end = day.ToDateTime(new TimeOnly(23, 59, 59));
        if (start < Start)
            start = Start;
        if (end > End)
            end = End;
        return start > end ? this : new WorkEffortWindow(start, end, Scale);
    }

    public bool Contains(DateTime moment)
    {
        var value = moment.Kind == DateTimeKind.Utc
            ? DateTime.SpecifyKind(moment.ToLocalTime(), DateTimeKind.Unspecified)
            : DateTime.SpecifyKind(moment, DateTimeKind.Unspecified);
        return value >= Start && value <= End;
    }

    /// <summary>
    /// Local wall time to the second. UTC is converted to local. Unspecified values are already
    /// the local time ServiceNow displayed. Two stamps in the same second are one moment.
    /// </summary>
    public static DateTime LocalStamp(DateTime moment)
    {
        var value = moment.Kind == DateTimeKind.Utc ? moment.ToLocalTime() : moment;
        return new DateTime(value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second, DateTimeKind.Unspecified);
    }

    /// <summary>
    /// The local calendar day of <paramref name="moment"/>. Monday and Wednesday are two days.
    /// Several times on Monday share one day.
    /// </summary>
    public static DateOnly LocalDay(DateTime moment) => DateOnly.FromDateTime(LocalStamp(moment));

    public static string CountsLabel(WorkEffortScale scale) => scale switch
    {
        WorkEffortScale.ThisWeek => "Counts for the last 7 days.",
        WorkEffortScale.ThisMonth => "Counts for the last 30 days.",
        WorkEffortScale.Last4Weeks => "Counts for the last 4 weeks.",
        WorkEffortScale.SixMonths => "Counts for the last 6 months.",
        WorkEffortScale.TwelveMonths => "Counts for the last 12 months.",
        WorkEffortScale.DateRange => "Counts for the selected dates.",
        _ => "Counts for today."
    };

    public static string CountsLabel(WorkEffortWindow window)
    {
        if (window.Scale != WorkEffortScale.DateRange)
            return CountsLabel(window.Scale);
        return "Counts for "
            + window.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            + " to "
            + window.End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            + ".";
    }

    private static DateOnly LocalDate(DateTime moment)
    {
        var value = moment.Kind == DateTimeKind.Utc ? moment.ToLocalTime() : moment;
        return DateOnly.FromDateTime(value);
    }
}

public static class WorkEffortClock
{
    private static readonly string[] Formats =
    [
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-dd",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm:ssZ",
        "yyyy-MM-ddTHH:mm:ss.fffZ"
    ];

    public static bool TryParse(string? text, out DateTime moment)
    {
        moment = default;
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
            return false;
        if (DateTime.TryParseExact(trimmed, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
        {
            moment = DateTime.SpecifyKind(exact, DateTimeKind.Unspecified);
            return true;
        }

        if (DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var invariant))
        {
            moment = DateTime.SpecifyKind(invariant, DateTimeKind.Unspecified);
            return true;
        }

        if (DateTime.TryParse(trimmed, CultureInfo.CurrentCulture, DateTimeStyles.None, out var local))
        {
            moment = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            return true;
        }

        return false;
    }
}
