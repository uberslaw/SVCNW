using System.Globalization;
using System.Text;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.WorkEffort;

/// <summary>
/// One credited event that makes up a Work Effort cell. Open / resolve / close stay one line each.
/// Daily updates keep one line per local day. Allow multiple updates keeps one line per save.
/// <see cref="UpdateCount"/> is the raw times this person updated the ticket in the window,
/// even when Daily mode only awards one credit per day. <see cref="WorkTypeLabel"/> lists every
/// contribution that ticket made for this person (Opened, Resolved, and/or X Updates).
/// </summary>
public sealed record WorkEffortCredit(
    string PersonSysId,
    string PersonName,
    string RecordSysId,
    string Number,
    string Title,
    WorkEffortKind Kind,
    WorkEffortMetric Metric,
    DateTime When,
    string Day = "",
    int UpdateCount = 0,
    bool TicketOpened = false,
    bool TicketResolved = false)
{
    public string TableLabel => Kind switch
    {
        WorkEffortKind.RequestedItem => "RITM",
        WorkEffortKind.Interaction => "IMS",
        _ => "INC"
    };

    public string MetricLabel => Metric switch
    {
        WorkEffortMetric.Opened => "opened",
        WorkEffortMetric.Resolved => "resolved",
        WorkEffortMetric.Closed => "closed",
        _ => "updated"
    };

    /// <summary>
    /// Opened, Resolved, and/or X Updates for this person on this ticket.
    /// X is the raw update count, not the Daily-mode credit count.
    /// </summary>
    public string WorkTypeLabel
    {
        get
        {
            var opened = TicketOpened || Metric == WorkEffortMetric.Opened;
            var resolved = TicketResolved || Metric is WorkEffortMetric.Resolved or WorkEffortMetric.Closed;
            var updates = UpdateCount > 0
                ? UpdateCount
                : Metric == WorkEffortMetric.Updated ? 1 : 0;
            var parts = new List<string>(3);
            if (opened)
                parts.Add("Opened");
            if (resolved)
                parts.Add("Resolved");
            if (updates > 0)
                parts.Add(FormatUpdates(updates));
            return parts.Count == 0 ? MetricLabel : string.Join(", ", parts);
        }
    }

    public string DisplayNumber =>
        string.IsNullOrWhiteSpace(Number) ? RecordSysId : Number.Trim();

    public string WhenText =>
        When.TimeOfDay == TimeSpan.Zero
            ? When.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : When.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public DeskSection Section => Kind switch
    {
        WorkEffortKind.RequestedItem => DeskSection.RequestedItems,
        WorkEffortKind.Interaction => DeskSection.WalkUps,
        _ => DeskSection.Incidents
    };

    public WorkEffortColumn Column => (Kind, Metric) switch
    {
        (WorkEffortKind.Incident, WorkEffortMetric.Opened) => WorkEffortColumn.IncOpened,
        (WorkEffortKind.Incident, WorkEffortMetric.Resolved or WorkEffortMetric.Closed) => WorkEffortColumn.IncResolved,
        (WorkEffortKind.Incident, _) => WorkEffortColumn.IncUpdated,
        (WorkEffortKind.RequestedItem, WorkEffortMetric.Opened) => WorkEffortColumn.RitmOpened,
        (WorkEffortKind.RequestedItem, WorkEffortMetric.Resolved or WorkEffortMetric.Closed) => WorkEffortColumn.RitmResolved,
        (WorkEffortKind.RequestedItem, _) => WorkEffortColumn.RitmUpdated,
        (WorkEffortKind.Interaction, WorkEffortMetric.Opened) => WorkEffortColumn.ImsOpened,
        (WorkEffortKind.Interaction, WorkEffortMetric.Resolved or WorkEffortMetric.Closed) => WorkEffortColumn.ImsResolved,
        _ => WorkEffortColumn.ImsUpdated
    };

    internal static string FormatUpdates(int count)
    {
        var n = count < 1 ? 1 : count;
        return n == 1 ? "1 Update" : n.ToString(CultureInfo.InvariantCulture) + " Updates";
    }
}

public enum WorkEffortMetric
{
    Opened,
    Resolved,
    Closed,
    Updated
}

/// <summary>
/// One count column on the Work Effort board.
/// </summary>
public enum WorkEffortColumn
{
    IncOpened,
    IncResolved,
    IncUpdated,
    RitmOpened,
    RitmResolved,
    RitmUpdated,
    ImsOpened,
    ImsResolved,
    ImsUpdated
}

/// <summary>
/// Sortable columns on the Work Effort ticket detail list.
/// </summary>
public enum WorkEffortDetailSortColumn
{
    Number,
    Title,
    Table,
    Metric,
    WorkType,
    When,
    Person,
    Day
}

public static class WorkEffortCreditSort
{
    public static int Compare(WorkEffortCredit left, WorkEffortCredit right, WorkEffortDetailSortColumn column, bool descending)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var compared = column switch
        {
            WorkEffortDetailSortColumn.When => left.When.CompareTo(right.When),
            WorkEffortDetailSortColumn.Number => CompareNumber(left.DisplayNumber, right.DisplayNumber),
            _ => CompareText(Text(left, column), Text(right, column))
        };
        return descending ? -compared : compared;
    }

    private static int CompareNumber(string left, string right)
    {
        if (TryDigits(left, out var leftNumber) && TryDigits(right, out var rightNumber))
        {
            var numeric = leftNumber.CompareTo(rightNumber);
            if (numeric != 0)
                return numeric;
        }

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static int CompareText(string left, string right)
    {
        var leftEmpty = string.IsNullOrWhiteSpace(left);
        var rightEmpty = string.IsNullOrWhiteSpace(right);
        if (leftEmpty || rightEmpty)
        {
            if (leftEmpty && rightEmpty)
                return 0;
            return leftEmpty ? 1 : -1;
        }

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryDigits(string text, out long value)
    {
        var digits = new string(text.Where(char.IsDigit).ToArray());
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value) && digits.Length > 0;
    }

    private static string Text(WorkEffortCredit line, WorkEffortDetailSortColumn column) => column switch
    {
        WorkEffortDetailSortColumn.Number => line.DisplayNumber,
        WorkEffortDetailSortColumn.Title => line.Title ?? "",
        WorkEffortDetailSortColumn.Table => line.TableLabel,
        WorkEffortDetailSortColumn.Metric => line.MetricLabel,
        WorkEffortDetailSortColumn.WorkType => line.WorkTypeLabel,
        WorkEffortDetailSortColumn.When => line.WhenText,
        WorkEffortDetailSortColumn.Person => line.PersonName,
        WorkEffortDetailSortColumn.Day => line.Day ?? "",
        _ => ""
    };
}

public static class WorkEffortDetail
{
    public const string EmptyCellMessage = "Nothing credited in this cell for the loaded period.";
    public const string EmptyPersonMessage = "Nothing credited for this person for the loaded period.";
    public const string NotLoadedMessage = "Load Work Effort for a time scale before opening detail.";

    public static IReadOnlyList<WorkEffortCredit> Build(
        IReadOnlyList<WorkEffortPerson> people,
        IEnumerable<WorkEffortTouch> touches,
        WorkEffortWindow window,
        WorkEffortUpdateMode mode = WorkEffortUpdateMode.Daily)
    {
        ArgumentNullException.ThrowIfNull(people);
        ArgumentNullException.ThrowIfNull(touches);
        var distinct = new List<WorkEffortPerson>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var person in people)
        {
            var id = (person.SysId ?? "").Trim();
            if (id.Length == 0 || !seen.Add(id))
                continue;
            distinct.Add(person);
        }

        var attempt = new WorkEffortAttempt(distinct, window, int.MaxValue, mode);
        foreach (var touch in WorkEffortScore.Merge(touches))
            attempt.TakeRow(touch);
        return attempt.Credits();
    }

    public static IReadOnlyList<WorkEffortCredit> ForPerson(
        IReadOnlyList<WorkEffortCredit> credits,
        string personSysId)
    {
        ArgumentNullException.ThrowIfNull(credits);
        var id = (personSysId ?? "").Trim();
        if (id.Length == 0)
            return [];
        return credits
            .Where(line => line.PersonSysId.Equals(id, StringComparison.OrdinalIgnoreCase))
            .OrderBy(line => line.When)
            .ThenBy(line => line.DisplayNumber, StringComparer.OrdinalIgnoreCase)
            .ThenBy(line => line.MetricLabel, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<WorkEffortCredit> ForCell(
        IReadOnlyList<WorkEffortCredit> credits,
        string personSysId,
        WorkEffortColumn column)
    {
        return ForPerson(credits, personSysId)
            .Where(line => line.Column == column)
            .ToArray();
    }

    public static int CellScore(WorkEffortRow row, WorkEffortColumn column) => column switch
    {
        WorkEffortColumn.IncOpened => row.IncOpened,
        WorkEffortColumn.IncResolved => row.IncResolved,
        WorkEffortColumn.IncUpdated => row.IncUpdated,
        WorkEffortColumn.RitmOpened => row.RitmOpened,
        WorkEffortColumn.RitmResolved => row.RitmResolved,
        WorkEffortColumn.RitmUpdated => row.RitmUpdated,
        WorkEffortColumn.ImsOpened => row.ImsOpened,
        WorkEffortColumn.ImsResolved => row.ImsResolved,
        WorkEffortColumn.ImsUpdated => row.ImsUpdated,
        _ => 0
    };

    public static string ColumnLabel(WorkEffortColumn column) => column switch
    {
        WorkEffortColumn.IncOpened => "INC opened",
        WorkEffortColumn.IncResolved => "INC resolved",
        WorkEffortColumn.IncUpdated => "INC updated",
        WorkEffortColumn.RitmOpened => "RITM opened",
        WorkEffortColumn.RitmResolved => "RITM resolved",
        WorkEffortColumn.RitmUpdated => "RITM updated",
        WorkEffortColumn.ImsOpened => "IMS opened",
        WorkEffortColumn.ImsResolved => "IMS resolved",
        WorkEffortColumn.ImsUpdated => "IMS updated",
        _ => "Work Effort"
    };

    public static bool TryParseColumn(string? value, out WorkEffortColumn column)
    {
        column = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        return Enum.TryParse(value.Trim(), ignoreCase: true, out column);
    }
}

/// <summary>
/// UTF-8 CSV for Excel. Callers that write a file should prepend a BOM.
/// </summary>
public static class WorkEffortCsv
{
    public static readonly Encoding Utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    public static string Format(IEnumerable<WorkEffortCredit> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var builder = new StringBuilder();
        builder.Append("Person,Number,Title,Table,Metric,Work Type,When,Day,Assignee,RecordSysId");
        builder.Append('\n');
        foreach (var line in lines)
        {
            builder.Append(Escape(line.PersonName));
            builder.Append(',');
            builder.Append(Escape(line.DisplayNumber));
            builder.Append(',');
            builder.Append(Escape(line.Title ?? ""));
            builder.Append(',');
            builder.Append(Escape(line.TableLabel));
            builder.Append(',');
            builder.Append(Escape(line.MetricLabel));
            builder.Append(',');
            builder.Append(Escape(line.WorkTypeLabel));
            builder.Append(',');
            builder.Append(Escape(line.WhenText));
            builder.Append(',');
            builder.Append(Escape(line.Day ?? ""));
            builder.Append(',');
            builder.Append(Escape(line.PersonName));
            builder.Append(',');
            builder.Append(Escape(line.RecordSysId));
            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static string Escape(string value)
    {
        if (value.IndexOfAny(['"', ',', '\r', '\n']) < 0)
            return value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
