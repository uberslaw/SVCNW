using System.Globalization;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.Alerts;

public static class AlertClassifier
{
    /// <summary>An open ticket with no update for this long is unattended. On hold is included.</summary>
    public static readonly TimeSpan UnattendedQuiet = TimeSpan.FromHours(24);

    /// <summary>The breach condition itself. Callers that already limited the population use this.</summary>
    public static bool IsSlaBreaching(WatchedRecord record, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.SlaHasBreached)
            return true;
        return StageIsInProgress(record.SlaStage)
            && record.SlaPlannedEnd is DateTime planned
            && planned < now;
    }

    /// <summary>
    /// A personal SLA notification. The ticket must still be open and inside
    /// <paramref name="scope"/> (assigned to the signed-in user, or unassigned in one of that user's groups or the watched group).
    /// A breach flag stays true after the ticket is finished, so the open check is what keeps it out of the queue.
    /// </summary>
    public static bool IsSlaBreaching(WatchedRecord record, DateTime now, SlaBreachScope scope)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(scope);
        if (!IsStillOpen(record) || !scope.Includes(record))
            return false;
        return IsSlaBreaching(record, now);
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

    public static bool IsStillOpen(WatchedRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return IsStillOpen(record.Section, record.StateValue, record.State);
    }

    /// <summary>
    /// Resolved, closed, and cancelled records stay out of the queues. Search can still find them.
    /// On hold remains open. A label that says resolved, closed, or cancelled counts even when the value is custom.
    /// </summary>
    public static bool IsStillOpen(DeskSection section, string? stateValue, string? stateLabel)
    {
        if (LooksClosed(stateLabel) || LooksClosed(stateValue))
            return false;
        var value = stateValue?.Trim() ?? "";
        if (section == DeskSection.Incidents && value is "6" or "7" or "8")
            return false;
        if (section == DeskSection.RequestedItems && value is "3" or "4" or "7")
            return false;
        return true;
    }

    /// <summary>
    /// Still open, including on hold, and nobody has updated the record within <see cref="UnattendedQuiet"/>.
    /// A missing update time is not treated as unattended.
    /// </summary>
    public static bool IsUnattended(WatchedRecord record, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!IsStillOpen(record))
            return false;
        if (!LastUpdate(record, out var updated))
            return false;
        return now - updated >= UnattendedQuiet;
    }

    public static bool IsUpdatedByCaller(WatchedRecord record, CallerUpdateScope scope)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(scope);
        return CallerMadeTheLatestUpdate(record) && scope.Includes(record);
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

    public static bool CallerMadeTheLatestUpdate(WatchedRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return SameUser(record.UpdatedBy, record.CallerUserName);
    }

    public static bool Matches(
        AlertKind kind,
        WatchedRecord record,
        DateTime now,
        CallerUpdateScope? callerScope = null,
        AssigneeScope? holdScope = null,
        SlaBreachScope? slaScope = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!IsStillOpen(record))
            return false;
        return kind switch
        {
            AlertKind.SlaBreaching => slaScope is null
                ? IsSlaBreaching(record, now)
                : IsSlaBreaching(record, now, slaScope),
            AlertKind.OnHoldPastFollowUp => IsOnHoldPastFollowUp(record, now) && (holdScope is null || holdScope.Includes(record)),
            AlertKind.UpdatedByCaller => callerScope is not null && CallerMadeTheLatestUpdate(record) && callerScope.Includes(record),
            AlertKind.ReturnedWithNotes => IsReturnedWithNotes(record),
            AlertKind.Unattended => IsUnattended(record, now) && (holdScope is null || holdScope.Includes(record)),
            _ => false
        };
    }

    public static AlertBucket Bucket(AlertKind kind, IEnumerable<WatchedRecord> records, DateTime now, string? status = null) =>
        Collect(kind, records, now, null, null, null, status);

    public static AlertBucket Bucket(AlertKind kind, IEnumerable<WatchedRecord> records, DateTime now, AssigneeScope holdScope, string? status = null)
    {
        ArgumentNullException.ThrowIfNull(holdScope);
        return Collect(kind, records, now, null, holdScope, null, status);
    }

    public static AlertBucket Bucket(AlertKind kind, IEnumerable<WatchedRecord> records, DateTime now, CallerUpdateScope callerScope, string? status = null)
    {
        ArgumentNullException.ThrowIfNull(callerScope);
        return Collect(kind, records, now, callerScope, null, null, status);
    }

    public static AlertBucket Bucket(AlertKind kind, IEnumerable<WatchedRecord> records, DateTime now, SlaBreachScope slaScope, string? status = null)
    {
        ArgumentNullException.ThrowIfNull(slaScope);
        return Collect(kind, records, now, null, null, slaScope, status);
    }

    public static AlertBucket Bucket(AlertKind kind, IEnumerable<WatchedRecord> records, DateTime now, CallerUpdateScope? callerScope, SlaBreachScope? slaScope, string? status = null) =>
        Collect(kind, records, now, callerScope, null, slaScope, status);

    private static AlertBucket Collect(
        AlertKind kind,
        IEnumerable<WatchedRecord> records,
        DateTime now,
        CallerUpdateScope? callerScope,
        AssigneeScope? holdScope,
        SlaBreachScope? slaScope,
        string? status)
    {
        ArgumentNullException.ThrowIfNull(records);
        var rows = records
            .Where(record => Matches(kind, record, now, callerScope, holdScope, slaScope))
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
            record.Updated,
            record.AssigneeDisplay?.Trim() ?? "",
            record.AssignedToSysId?.Trim() ?? "");
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

    private static bool LastUpdate(WatchedRecord record, out DateTime updated)
    {
        if (record.UpdatedAt is DateTime known)
        {
            updated = known;
            return true;
        }

        return TryParseInstant(record.Updated, out updated);
    }

    private static bool LooksClosed(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        return text.Contains("resolv", StringComparison.OrdinalIgnoreCase)
            || text.Contains("closed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("cancel", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsHold(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.Contains("hold", StringComparison.OrdinalIgnoreCase);

    private static bool SameUser(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && left.Trim().Equals(right.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>On hold past follow-up in the personal notifications is limited to the signed-in user.</summary>
public sealed class AssigneeScope
{
    public AssigneeScope(string? userSysId) => UserSysId = userSysId?.Trim() ?? "";

    public string UserSysId { get; }

    public bool Includes(WatchedRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (UserSysId.Length == 0)
            return false;
        return string.Equals(record.AssignedToSysId?.Trim(), UserSysId, StringComparison.OrdinalIgnoreCase);
    }
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
