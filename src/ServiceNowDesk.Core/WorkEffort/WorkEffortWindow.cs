using System.Globalization;

namespace ServiceNowDesk.WorkEffort;

public enum WorkEffortScale
{
    Today,
    ThisWeek,
    ThisMonth,
    Last4Weeks,
    SixMonths,
    TwelveMonths
}

/// <summary>
/// Local calendar windows. Today, this week, and this month run through the end of the local day.
/// Week starts Monday. The longer scales are rolling windows that end at the local clock.
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
            WorkEffortScale.ThisWeek => new(Monday(now), endOfToday, scale),
            WorkEffortScale.ThisMonth => new(new DateTime(now.Year, now.Month, 1), endOfToday, scale),
            WorkEffortScale.Last4Weeks => new(now.AddDays(-28), now, scale),
            WorkEffortScale.SixMonths => new(now.AddMonths(-6), now, scale),
            WorkEffortScale.TwelveMonths => new(now.AddMonths(-12), now, scale),
            _ => new(now.Date, endOfToday, WorkEffortScale.Today)
        };
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
        WorkEffortScale.ThisWeek => "Counts for this week.",
        WorkEffortScale.ThisMonth => "Counts for this month so far.",
        WorkEffortScale.Last4Weeks => "Counts for the last 4 weeks.",
        WorkEffortScale.SixMonths => "Counts for the last 6 months.",
        WorkEffortScale.TwelveMonths => "Counts for the last 12 months.",
        _ => "Counts for today."
    };

    private static DateTime Monday(DateTime now)
    {
        var days = ((int)now.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return now.Date.AddDays(-days);
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
