namespace ServiceNowDesk.Alerts;

public enum LeadArea
{
    Team,
    Regional,
    WorkEffort
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

    public LeadBoard KeepingCounts(LeadBoard next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return new LeadBoard(AlertCountKeep.Apply(Team, next.Team), AlertCountKeep.Apply(Regional, next.Regional));
    }

    public static LeadBoard Build(
        IEnumerable<WatchedRecord> records,
        DateTime now,
        IEnumerable<string>? teamMemberIds,
        string? groupName,
        IEnumerable<string>? offices = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        var rows = Distinct(records);
        var team = new HashSet<string>(
            (teamMemberIds ?? []).Select(id => id?.Trim() ?? "").Where(id => id.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        var officeNames = new HashSet<string>(
            (offices ?? []).Select(office => office?.Trim() ?? "").Where(office => office.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        var regional = rows.Where(record => InGroup(record, groupName)).ToArray();
        var assigned = rows.Where(record => AssignedToAny(record, team)).ToArray();
        var teamInRegion = assigned.Where(record => InGroup(record, groupName)).ToArray();
        return new LeadBoard(
            Snapshot(assigned, teamInRegion, now, officeNames),
            Snapshot(regional, regional, now, officeNames));
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

    /// <summary>
    /// Updated by caller and unattended count only tickets already in the region.
    /// The other queues keep this view's own population.
    /// </summary>
    private static AlertSnapshot Snapshot(
        IReadOnlyList<WatchedRecord> population,
        IReadOnlyList<WatchedRecord> callerAndQuiet,
        DateTime now,
        IReadOnlySet<string> offices)
    {
        var rows = population.ToArray();
        var shared = callerAndQuiet.ToArray();
        var buckets = new Dictionary<AlertKind, AlertBucket>
        {
            [AlertKind.SlaBreaching] = AlertClassifier.Bucket(AlertKind.SlaBreaching, rows, now),
            [AlertKind.OnHoldPastFollowUp] = AlertClassifier.Bucket(AlertKind.OnHoldPastFollowUp, rows, now),
            [AlertKind.UpdatedByCaller] = CallerUpdates(shared, offices),
            [AlertKind.ReturnedWithNotes] = AlertClassifier.Bucket(AlertKind.ReturnedWithNotes, rows, now),
            [AlertKind.Unattended] = Unattended(shared, now)
        };
        return new AlertSnapshot(buckets);
    }

    private static AlertBucket CallerUpdates(IReadOnlyList<WatchedRecord> records, IReadOnlySet<string> offices)
    {
        var rows = Distinct(records)
            .Where(record => AlertClassifier.IsStillOpen(record) && AlertClassifier.CallerMadeTheLatestUpdate(record))
            .Where(record => InOffice(record, offices))
            .Select(record => AlertClassifier.ToRecord(record, AlertKind.UpdatedByCaller))
            .ToArray();
        return new AlertBucket(rows, rows.Length);
    }

    private static AlertBucket Unattended(IReadOnlyList<WatchedRecord> records, DateTime now)
    {
        var rows = Distinct(records)
            .Where(record => AlertClassifier.IsUnattended(record, now))
            .Select(record => AlertClassifier.ToRecord(record, AlertKind.Unattended))
            .ToArray();
        return new AlertBucket(rows, rows.Length);
    }

    /// <summary>
    /// An empty office list does not hide tickets. A configured list must match the location name.
    /// </summary>
    private static bool InOffice(WatchedRecord record, IReadOnlySet<string> offices)
    {
        if (offices.Count == 0)
            return true;
        var location = record.Location?.Trim() ?? "";
        return location.Length > 0 && offices.Contains(location);
    }

    private static WatchedRecord[] Distinct(IEnumerable<WatchedRecord> records)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<WatchedRecord>();
        foreach (var record in records)
        {
            if (record is null)
                continue;
            var id = (record.SysId ?? "").Trim();
            if (id.Length == 0 || !seen.Add(record.Section + "\n" + id))
                continue;
            rows.Add(record);
        }

        return rows.ToArray();
    }

    private static AlertSnapshot EmptySnapshot() => new(new Dictionary<AlertKind, AlertBucket>());
}
