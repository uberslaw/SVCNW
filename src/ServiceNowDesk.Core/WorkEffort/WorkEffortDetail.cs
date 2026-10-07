using System.Globalization;
using System.Text;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.WorkEffort;

/// <summary>
/// One credited event that makes up a Work Effort cell. Open / resolve / close stay one line each.
/// Daily updates keep one line per local day. Allow multiple updates keeps one line per save.
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
    string Day = "")
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
        builder.Append("Person,Number,Title,Table,Metric,When,Day,Assignee,RecordSysId");
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
