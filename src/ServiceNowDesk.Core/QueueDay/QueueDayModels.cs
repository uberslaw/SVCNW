using System.Globalization;

namespace ServiceNowDesk.QueueDay;

public enum QueueDayKind
{
    Changed,
    New,
    Missing,
    Unchanged
}

/// <summary>One open ticket on a person's assigned queue, or a follow-up row for a ticket that left it.</summary>
public sealed record QueueDayTicket(
    string Number,
    string SysId,
    string Table,
    string ShortDescription,
    string AssigneeId,
    string AssigneeName,
    string AssigneeUserName,
    string AssignmentGroupId,
    string AssignmentGroupName,
    string StateValue,
    string StateDisplay,
    string SysUpdatedOn,
    DateTime? UpdatedAt,
    string UpdatedBy,
    string CallerId,
    string CallerName,
    string CallerUserName)
{
    public string Key => QueueDayQuery.NormalizeTable(Table) + "|" + (SysId ?? "").Trim();
}

public readonly record struct QueueDayTicketRef(string Table, string SysId);

public sealed record QueueDayPersonBaseline(
    string PersonId,
    string PersonName,
    IReadOnlyList<QueueDayTicket> Tickets);

/// <summary>The first queue scan of one local calendar day. Later scans the same day do not replace it.</summary>
public sealed record QueueDayBaseline(
    DateOnly LocalDate,
    DateTime TakenAt,
    IReadOnlyList<QueueDayPersonBaseline> People);

public sealed record QueueDayLine(
    string PersonId,
    string PersonName,
    string Number,
    string ShortDescription,
    string Table,
    string SysId,
    QueueDayKind Kind,
    string Breakdown);

public sealed record QueueDayPersonResult(
    string PersonId,
    string PersonName,
    IReadOnlyList<QueueDayLine> Lines);

public sealed record QueueDayReport(
    DateTime? BaselineAt,
    DateTime? CheckedAt,
    bool CreatedBaseline,
    IReadOnlyList<QueueDayPersonResult> People,
    string Status,
    string EmptyMessage)
{
    public static QueueDayReport DefineTeam() =>
        new(null, null, false, [], QueueDayText.DefineTeamMessage, QueueDayText.DefineTeamMessage);

    public IEnumerable<QueueDayLine> Lines => People.SelectMany(person => person.Lines);
}

public interface IQueueDayStore
{
    QueueDayBaseline? Find(DateOnly localDate);

    void Save(QueueDayBaseline baseline);
}

public interface IQueueDaySource
{
    Task<IReadOnlyList<QueueDayTicket>> ListOpenAssignedAsync(string assigneeSysId, CancellationToken cancellationToken);

    Task<IReadOnlyList<QueueDayTicket>> LookupAsync(IReadOnlyList<QueueDayTicketRef> tickets, CancellationToken cancellationToken);
}

public static class QueueDayText
{
    public const string DefineTeamMessage =
        "Define your team on My team first. Tick the people to include. Queue check does not call ServiceNow until you do.";

    public const string Checking = "Checking queues…";

    public const string BaselineSaved = "Baseline saved for today.";

    public const string Compared = "Compared with today's baseline. The baseline was not replaced.";

    public const string Connect = "Connect to check queues.";

    public const string UpdatedByAssignee = "Updated by the assignee";

    public const string UpdatedByCaller = "Updated by the caller";

    public const string New = "New";

    public const string Unchanged = "Unchanged";

    public const string LookupMissed = "No longer in the queue. The follow-up search did not return it.";

    public const string NoLonger = "No longer in the queue.";

    public const string Unassigned = "Unassigned";

    public const string NoOpenTickets = "No open tickets.";

    public const string Updated = "Updated";

    public static string UpdatedBy(string name) => "Updated by " + name.Trim();

    public static string GroupChanged(string previous, string next) =>
        "Assignment group changed from " + previous + " to " + next;

    public static string Closed(string state) => "Closed, state " + state;

    public static string Resolved(string state) => "Resolved, state " + state;

    public static string Cancelled(string state) => "Cancelled, state " + state;

    public static string AssignedTo(string name) => "Assigned to " + name;

    public static string BaselineAt(DateTime at) =>
        "Baseline " + at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static string CheckedAt(DateTime at) =>
        "Last check " + at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static string Join(IEnumerable<string> facts)
    {
        var parts = (facts ?? [])
            .Select(fact => fact?.Trim() ?? "")
            .Where(fact => fact.Length > 0)
            .ToArray();
        return string.Join(". ", parts);
    }
}
