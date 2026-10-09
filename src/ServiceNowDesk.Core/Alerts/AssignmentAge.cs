using System.Globalization;

namespace ServiceNowDesk.Alerts;

/// <summary>
/// Whole local calendar days since a ticket was assigned. Today is 0. Yesterday is 1.
/// The stamp comes from <c>sys_audit</c> assigned_to→me when available, otherwise from
/// the newest journal note that indicates assignment to the current assignee (see
/// <see cref="AssignmentNoteReader"/>).
/// </summary>
public static class AssignmentAge
{
    public static int? WholeDays(string? assignedOn, DateTime today)
    {
        if (!AlertClassifier.TryParseInstant(assignedOn, out var when))
            return null;

        var assignedDate = when.Kind == DateTimeKind.Utc ? when.ToLocalTime().Date : when.Date;
        var todayDate = today.Kind == DateTimeKind.Utc ? today.ToLocalTime().Date : today.Date;
        var days = (todayDate - assignedDate).Days;
        return days < 0 ? 0 : days;
    }

    public static string Format(string? assignedOn, DateTime today)
    {
        var days = WholeDays(assignedOn, today);
        return days is int count ? count.ToString(CultureInfo.InvariantCulture) : "";
    }
}
