using System.Globalization;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.Alerts;

public static class AlertClassifier
{
    public static bool IsSlaBreaching(WatchedRecord record, DateTime now, SlaBreachScope scope)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(scope);
        if (!IsStillOpen(record) || !scope.Includes(record))
            return false;
        if (record.SlaHasBreached)
            return true;
        return StageIsInProgress(record.SlaStage)
            && record.SlaPlannedEnd is DateTime planned
            && planned < now;
    }

    /// <summary>
    /// Resolved, closed, and cancelled records are finished. A breach flag stays true after
    /// that, so the ticket state is what keeps them out of the queue.
    /// </summary>
    public static bool IsStillOpen(WatchedRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (ContainsFinished(record.State) || ContainsFinished(record.StateValue))
            return false;
        var value = record.StateValue.Trim();
        if (record.Section == DeskSection.Incidents && value is "6" or "7" or "8")
            return false;
        return record.Section != DeskSection.RequestedItems || value is not ("3" or "4" or "7");
    }

    public static bool IsOnHold(DeskSection section, string? stateValue, string? stateLabel)
    {
        if (ContainsHold(stateLabel) || ContainsHold(stateValue))
            return true;
        return section == DeskSection.Incidents && string.Equals(stateValue?.Trim(), "3", StringComparison.Ordinal);
    }

    public static bool IsOnHoldPastFollowUp(WatchedRecord record, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(record);
        return IsOnHold(record.Section, record.StateValue, record.State)
            && record.FollowUp is DateTime followUp
            && followUp < now;
    }

    public static bool IsUpdatedByCaller(WatchedRecord record, CallerUpdateScope scope)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(scope);
        return SameUser(record.UpdatedBy, record.CallerUserName) && scope.Includes(record);
    }

    /// <summary>
    /// The newest journal author is neither the caller nor the assignee.
    /// A blank assignee does not match every author.
    /// </summary>
    public static bool IsReturnedWithNotes(WatchedRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrWhiteSpace(record.LatestJournalAuthor))
            return false;
        if (SameUser(record.LatestJournalAuthor, record.CallerUserName))
            return false;
        if (SameUser(record.LatestJournalAuthor, record.AssigneeUserName))
            return false;
        return true;
    }

    public static bool StageIsInProgress(string? stage)
    {
        if (string.IsNullOrWhiteSpace(stage))
            return false;
        var normalized = stage.Trim().Replace(' ', '_').Replace("-", "_", StringComparison.Ordinal);
        return normalized.Equals("in_progress", StringComparison.OrdinalIgnoreCase);
    }

    public static bool Matches(AlertKind kind, WatchedRecord record, DateTime now, CallerUpdateScope? callerScope = null, SlaBreachScope? slaScope = null) => kind switch
    {
        AlertKind.SlaBreaching => slaScope is not null && IsSlaBreaching(record, now, slaScope),
        AlertKind.OnHoldPastFollowUp => IsOnHoldPastFollowUp(record, now),
        AlertKind.UpdatedByCaller => callerScope is not null && IsUpdatedByCaller(record, callerScope),
        AlertKind.ReturnedWithNotes => IsReturnedWithNotes(record),
        _ => false
    };

    public static AlertBucket Bucket(AlertKind kind, IEnumerable<WatchedRecord> records, DateTime now, string? status = null) =>
        Bucket(kind, records, now, null, null, status);

    public static AlertBucket Bucket(AlertKind kind, IEnumerable<WatchedRecord> records, DateTime now, CallerUpdateScope? callerScope, string? status = null) =>
        Bucket(kind, records, now, callerScope, null, status);

    public static AlertBucket Bucket(AlertKind kind, IEnumerable<WatchedRecord> records, DateTime now, SlaBreachScope slaScope, string? status = null) =>
        Bucket(kind, records, now, null, slaScope, status);

    public static AlertBucket Bucket(AlertKind kind, IEnumerable<WatchedRecord> records, DateTime now, CallerUpdateScope? callerScope, SlaBreachScope? slaScope, string? status = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        var rows = records
            .Where(record => Matches(kind, record, now, callerScope, slaScope))
            .Select(record => ToRecord(record, kind))
            .ToArray();
        return new AlertBucket(rows, rows.Length, status ?? "");
    }

    public static AlertRecord ToRecord(WatchedRecord record, AlertKind kind)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new AlertRecord(
            kind,
            record.Section,
            record.SysId,
            record.Number,
            record.Title,
            record.State,
            record.Group,
            record.Location,
            record.Updated);
    }

    public static bool TryParseInstant(string? text, out DateTime value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();
        string[] formats =
        [
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd HH:mm",
            "yyyy-MM-ddTHH:mm:ss",
            "yyyy-MM-ddTHH:mm:ssZ",
            "yyyy-MM-dd"
        ];
        const DateTimeStyles styles = DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal;
        if (DateTime.TryParseExact(trimmed, formats, CultureInfo.InvariantCulture, styles, out value))
            return true;
        return DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, styles, out value);
    }

    private static bool ContainsHold(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.Contains("hold", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsFinished(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        return text.Contains("resolv", StringComparison.OrdinalIgnoreCase)
            || text.Contains("closed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("cancel", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameUser(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && left.Trim().Equals(right.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Updated by caller is limited to the signed-in user, the watched (main) group, or an unassigned
/// ticket in that group or with no group. Membership in other groups does not qualify.
/// A configured office list must match the record location. An empty office list does not hide tickets.
/// </summary>
public sealed class CallerUpdateScope
{
    public CallerUpdateScope(string? userSysId, string? mainGroupName, IEnumerable<string>? offices)
    {
        UserSysId = userSysId?.Trim() ?? "";
        MainGroupName = mainGroupName?.Trim() ?? "";
        Offices = new HashSet<string>(
            (offices ?? []).Select(office => office?.Trim() ?? "").Where(office => office.Length > 0),
            StringComparer.OrdinalIgnoreCase);
    }

    public string UserSysId { get; }

    public string MainGroupName { get; }

    public IReadOnlySet<string> Offices { get; }

    public bool Includes(WatchedRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!MatchesAssignment(record))
            return false;
        return MatchesOffice(record);
    }

    private bool MatchesAssignment(WatchedRecord record)
    {
        if (UserSysId.Length > 0 && record.AssignedToSysId.Trim().Equals(UserSysId, StringComparison.OrdinalIgnoreCase))
            return true;
        if (IsMainGroup(record))
            return true;
        return !HasAssignee(record) && !HasGroup(record);
    }

    private bool MatchesOffice(WatchedRecord record)
    {
        if (Offices.Count == 0)
            return true;
        var location = record.Location.Trim();
        return location.Length > 0 && Offices.Contains(location);
    }

    private bool IsMainGroup(WatchedRecord record)
    {
        if (MainGroupName.Length == 0)
            return false;
        if (record.Group.Trim().Equals(MainGroupName, StringComparison.OrdinalIgnoreCase))
            return true;
        return record.AssignmentGroupSysId.Trim().Equals(MainGroupName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasAssignee(WatchedRecord record) =>
        !string.IsNullOrWhiteSpace(record.AssignedToSysId);

    private static bool HasGroup(WatchedRecord record) =>
        !string.IsNullOrWhiteSpace(record.AssignmentGroupSysId) || !string.IsNullOrWhiteSpace(record.Group);
}

/// <summary>
/// SLA breaches are rebuilt on every poll. A ticket counts only when it is still open and
/// assigned to the signed-in user, or unassigned in one of that user's groups or the watched group.
/// </summary>
public sealed class SlaBreachScope
{
    public SlaBreachScope(string? userSysId, IEnumerable<string>? groupIds, string? watchedGroupName)
    {
        UserSysId = userSysId?.Trim() ?? "";
        GroupIds = new HashSet<string>(
            (groupIds ?? []).Select(id => id?.Trim() ?? "").Where(id => id.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        WatchedGroupName = watchedGroupName?.Trim() ?? "";
    }

    public string UserSysId { get; }

    public IReadOnlySet<string> GroupIds { get; }

    public string WatchedGroupName { get; }

    public bool Includes(WatchedRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var assignee = record.AssignedToSysId?.Trim() ?? "";
        var groupId = record.AssignmentGroupSysId?.Trim() ?? "";
        var group = record.Group?.Trim() ?? "";
        if (UserSysId.Length > 0 && assignee.Equals(UserSysId, StringComparison.OrdinalIgnoreCase))
            return true;
        if (assignee.Length > 0)
            return false;
        if (GroupIds.Contains(groupId))
            return true;
        if (WatchedGroupName.Length == 0)
            return false;
        if (group.Equals(WatchedGroupName, StringComparison.OrdinalIgnoreCase))
            return true;
        return groupId.Equals(WatchedGroupName, StringComparison.OrdinalIgnoreCase);
    }
}
