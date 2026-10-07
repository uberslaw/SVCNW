using System.Globalization;
using ServiceNowDesk.Alerts;

namespace ServiceNowDesk.Services;

/// <summary>
/// Follow-up text saved to ServiceNow. The desk writes <c>yyyy-MM-dd HH:mm:ss</c>.
/// A typed value is kept when it already parses.
/// </summary>
public static class FollowUpValue
{
    public const string SaveFormat = "yyyy-MM-dd HH:mm:ss";

    public static string Format(DateTime value) =>
        value.ToString(SaveFormat, CultureInfo.InvariantCulture);

    /// <summary>Seven days after <paramref name="now"/>, at the same time of day.</summary>
    public static string SevenDaysFrom(DateTime now) => Format(now.AddDays(7));

    public static bool TryParse(string? text, out DateTime value) =>
        AlertClassifier.TryParseInstant(text, out value);

    /// <summary>
    /// The instant the picker should open on. A stored or typed value wins.
    /// Otherwise the clock's current local time.
    /// </summary>
    public static DateTime PickerSeed(string? text, DateTime now) =>
        TryParse(text, out var parsed) ? parsed : now;

    public static string FormatSelection(DateTime day, int hour, int minute, int second)
    {
        hour = Math.Clamp(hour, 0, 23);
        minute = Math.Clamp(minute, 0, 59);
        second = Math.Clamp(second, 0, 59);
        return Format(new DateTime(day.Year, day.Month, day.Day, hour, minute, second));
    }

    /// <summary>
    /// When the ticket is put on hold and the follow-up is blank, returns seven days ahead.
    /// A value that is already set is left alone.
    /// </summary>
    public static string? BlankHoldDefault(bool onHold, string? current, DateTime now)
    {
        if (!onHold || !string.IsNullOrWhiteSpace(current))
            return null;
        return SevenDaysFrom(now);
    }

    public static bool TryValidateHold(string? holdReason, string? followUp, out string message)
    {
        var missingReason = string.IsNullOrWhiteSpace(holdReason);
        var missingFollowUp = string.IsNullOrWhiteSpace(followUp);
        if (missingReason && missingFollowUp)
        {
            message = "Choose an on hold reason and enter a follow up.";
            return false;
        }

        if (missingReason)
        {
            message = "Choose an on hold reason.";
            return false;
        }

        if (missingFollowUp)
        {
            message = "Enter a follow up.";
            return false;
        }

        if (!TryParse(followUp, out _))
        {
            message = "Enter a follow up as a date and time, for example 2026-11-20 10:10:23.";
            return false;
        }

        message = "";
        return true;
    }
}
