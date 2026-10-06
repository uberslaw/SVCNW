namespace ServiceNowDesk.Alerts;

public enum LeadArea
{
    Team,
    Regional
}

/// <summary>
/// The same work queues as the personal notifications, scoped to a chosen team or to the whole watched group.
/// </summary>
public sealed class LeadBoard
{
    public LeadBoard(AlertSnapshot team, AlertSnapshot regional)
    {
        Team = team;
        Regional = regional;
    }

    public static LeadBoard Empty { get; } = new(EmptySnapshot(), EmptySnapshot());

    public AlertSnapshot Team { get; }

    public AlertSnapshot Regional { get; }

    public AlertSnapshot For(LeadArea area) => area == LeadArea.Regional ? Regional : Team;

    public static LeadBoard Build(IEnumerable<WatchedRecord> records, DateTime now, IEnumerable<string>? teamMemberIds, string? groupName)
    {
        ArgumentNullException.ThrowIfNull(records);
        var rows = records.ToArray();
        var team = new HashSet<string>(
            (teamMemberIds ?? []).Select(id => id?.Trim() ?? "").Where(id => id.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        return new LeadBoard(
            Snapshot(rows.Where(record => AssignedToAny(record, team)), now),
            Snapshot(rows.Where(record => InGroup(record, groupName)), now));
    }

    public static bool AssignedToAny(WatchedRecord record, IReadOnlySet<string> memberIds)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(memberIds);
        var assignee = record.AssignedToSysId?.Trim() ?? "";
        return assignee.Length > 0 && memberIds.Contains(assignee);
    }

    public static bool InGroup(WatchedRecord record, string? groupName)
    {
        ArgumentNullException.ThrowIfNull(record);
        var name = groupName?.Trim() ?? "";
        if (name.Length == 0)
            return false;
        if (string.Equals(record.Group?.Trim(), name, StringComparison.OrdinalIgnoreCase))
            return true;
        return string.Equals(record.AssignmentGroupSysId?.Trim(), name, StringComparison.OrdinalIgnoreCase);
    }

    private static AlertSnapshot Snapshot(IEnumerable<WatchedRecord> records, DateTime now)
    {
        var rows = records.ToArray();
        var buckets = new Dictionary<AlertKind, AlertBucket>
        {
            [AlertKind.SlaBreaching] = AlertClassifier.Bucket(AlertKind.SlaBreaching, rows, now),
            [AlertKind.OnHoldPastFollowUp] = AlertClassifier.Bucket(AlertKind.OnHoldPastFollowUp, rows, now),
            [AlertKind.UpdatedByCaller] = CallerUpdates(rows),
            [AlertKind.ReturnedWithNotes] = AlertClassifier.Bucket(AlertKind.ReturnedWithNotes, rows, now),
            [AlertKind.Unattended] = AlertClassifier.Bucket(AlertKind.Unattended, rows, now)
        };
        return new AlertSnapshot(buckets);
    }

    private static AlertBucket CallerUpdates(IReadOnlyList<WatchedRecord> records)
    {
        var rows = records
            .Where(record => AlertClassifier.IsStillOpen(record) && AlertClassifier.CallerMadeTheLatestUpdate(record))
            .Select(record => AlertClassifier.ToRecord(record, AlertKind.UpdatedByCaller))
            .ToArray();
        return new AlertBucket(rows, rows.Length);
    }

    private static AlertSnapshot EmptySnapshot() => new(new Dictionary<AlertKind, AlertBucket>());
}
