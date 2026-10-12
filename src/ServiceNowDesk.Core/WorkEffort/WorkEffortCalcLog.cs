using System.Globalization;
using System.Text;

namespace ServiceNowDesk.WorkEffort;

/// <summary>
/// What one Work Effort download actually did. Collected while the rows are read,
/// then rendered with <see cref="WorkEffortCalcLog"/> without another ServiceNow call.
/// </summary>
public sealed class WorkEffortLoadFacts
{
    private readonly List<WorkEffortSliceRecord> _slices = [];
    private readonly List<WorkEffortHistoryRecord> _history = [];
    private readonly List<WorkEffortSeenDrop> _drops = [];

    public WorkEffortLoadFacts(IReadOnlyList<string> offices, int safetyCap, bool practice = false)
    {
        Offices = offices ?? [];
        SafetyCap = safetyCap < 1 ? 1 : safetyCap;
        Practice = practice;
    }

    public IReadOnlyList<string> Offices { get; }

    public int SafetyCap { get; }

    /// <summary>Practice rows are scored on this PC. They are not downloaded slice by slice.</summary>
    public bool Practice { get; }

    public IReadOnlyList<WorkEffortSliceRecord> Slices => _slices;

    public IReadOnlyList<WorkEffortHistoryRecord> History => _history;

    public IReadOnlyList<WorkEffortSeenDrop> DownloadDrops => _drops;

    public void AddSlice(WorkEffortSliceRecord slice) => _slices.Add(slice);

    public void AddHistory(WorkEffortHistoryRecord row) => _history.Add(row);

    public void AddDrop(WorkEffortSeenDrop drop) => _drops.Add(drop);

    public static WorkEffortLoadFacts PracticeData(IReadOnlyList<string>? offices) =>
        new(offices ?? [], WorkEffortQuery.SafetyCap, practice: true);
}

/// <summary>One person, one local day, one office, one opener query.</summary>
public sealed record WorkEffortSliceRecord(
    string PersonName,
    DateOnly LocalDay,
    string Office,
    string TableName,
    string QueryRole,
    int RowsReturned,
    bool HitSafetyCap,
    int? ReportedTotal);

/// <summary>Journal or audit pages for the tickets that were kept. Not tied to one person or office.</summary>
public sealed record WorkEffortHistoryRecord(
    string Source,
    int RowsReturned,
    bool HitSafetyCap,
    int? ReportedTotal);

/// <summary>A row the download saw and did not keep for scoring.</summary>
public sealed record WorkEffortSeenDrop(
    string PersonName,
    string TicketNumber,
    string RecordSysId,
    string TableName,
    string Location,
    DateOnly? LocalDay,
    string Office,
    string Reason);

/// <summary>Decisions made while scoring one ledger. Filled only when a log is rendered.</summary>
public sealed class WorkEffortScoreNotes
{
    public List<WorkEffortKeptCredit> Kept { get; } = [];

    public List<WorkEffortScoreDrop> Dropped { get; } = [];
}

public sealed record WorkEffortKeptCredit(
    string PersonSysId,
    string PersonName,
    string TicketNumber,
    string RecordSysId,
    string TableName,
    string Action,
    DateOnly LocalDay,
    decimal Amount,
    string Location,
    string Rule);

public sealed record WorkEffortScoreDrop(
    string PersonName,
    string TicketNumber,
    string RecordSysId,
    string TableName,
    string Location,
    DateOnly? LocalDay,
    string Reason);

/// <summary>
/// Copyable account of one Work Effort run. Rendering uses the ledger and the facts
/// already collected; it does not call ServiceNow.
/// </summary>
public static class WorkEffortCalcLog
{
    public const string OutsideRegion = "outside region";
    public const string DuplicateMerged = "duplicate already merged";
    public const string UpdateSuppressed =
        "update suppressed because an open, resolve, or close already counted this person on this ticket this local day";
    public const string NotSelected = "not the selected people";
    public const string OutsideWindow = "outside the selected window";
    public const string DailyAlready =
        "daily updates already counted one update for this person, this ticket, and this local day";
    public const string CloseAlreadyResolved =
        "close not counted separately because this person also resolved it";
    public const string OpenWeight = "open weight";
    public const string ResolveWeight = "resolve weight";
    public const string CloseAsResolve = "close counted as resolve";
    public const string OneDailyUpdate = "one daily update";
    public const string EachSave = "each save";
    public const string CapExplanation =
        "Later rows in this slice were not downloaded, so the total can be lower than a previous uncapped or differently sliced run.";

    public static string TableName(WorkEffortKind kind) => kind switch
    {
        WorkEffortKind.RequestedItem => "request item",
        WorkEffortKind.Interaction => "interaction",
        _ => "incident"
    };

    public static string TicketLabel(string? number, string? sysId)
    {
        var ticket = (number ?? "").Trim();
        if (ticket.Length > 0)
            return ticket;
        var id = (sysId ?? "").Trim();
        return id.Length == 0 ? "(no ticket number)" : "(no ticket number) " + id;
    }

    public static string Format(
        WorkEffortReport report,
        WorkEffortUpdateMode mode,
        DateTime capturedAt,
        DateTime? previousCapturedAt)
    {
        ArgumentNullException.ThrowIfNull(report);
        var facts = report.Facts;
        var ledger = report.Ledger;
        var notes = new WorkEffortScoreNotes();
        IReadOnlyList<WorkEffortRow> rows = [];
        if (ledger is not null)
            rows = WorkEffortScore.Build(ledger.People, ledger.Touches, ledger.Window, mode, notes);

        var window = ledger?.Window ?? default;
        var builder = new StringBuilder();
        builder.AppendLine("Work Effort calculation log");
        builder.Append("Captured: ").AppendLine(Stamp(capturedAt));
        if (previousCapturedAt is DateTime previous)
        {
            builder.Append("This run replaced the previous calculation log from ")
                .Append(Stamp(previous))
                .AppendLine(".");
        }

        if (ledger is not null)
            builder.Append("Scale: ").AppendLine(ScaleLabel(window.Scale));
        else
            builder.AppendLine("Scale: (not scored)");
        if (ledger is not null)
        {
            builder.Append("Window: ")
                .Append(Stamp(window.Start))
                .Append(" through ")
                .Append(Stamp(window.End))
                .AppendLine(" inclusive");
        }
        else
        {
            builder.AppendLine("Window: (no tickets were scored)");
        }

        builder.Append("Updates: ").AppendLine(mode == WorkEffortUpdateMode.Multiple
            ? "Allow multiple updates"
            : "Daily updates");
        builder.Append("Region offices: ").AppendLine(OfficeLine(facts));
        builder.AppendLine(
            "Weights: incident 1, request item 0.7, walk-up 0.6, update 0.3. Daily updates keeps one update credit per person, per ticket, per local day. Allow multiple updates counts each save. An open, resolve, or close is not also an update on that same day. Closing counts as the resolve when that person did not also resolve it.");

        var cap = facts?.SafetyCap ?? WorkEffortQuery.SafetyCap;
        var anyCap = facts is not null
            && (facts.Slices.Any(slice => slice.HitSafetyCap) || facts.History.Any(row => row.HitSafetyCap));
        if (anyCap)
        {
            builder.Append("The safety cap of ")
                .Append(cap.ToString(CultureInfo.InvariantCulture))
                .Append(" was reached. ")
                .AppendLine(CapExplanation);
        }

        builder.AppendLine();
        builder.AppendLine("Query slices");
        WriteSlices(builder, facts, cap);

        builder.AppendLine();
        builder.AppendLine("Update history");
        WriteHistory(builder, facts, cap);

        builder.AppendLine();
        builder.AppendLine("Counted");
        if (notes.Kept.Count == 0)
            builder.AppendLine("(none)");
        else
        {
            foreach (var line in notes.Kept)
            {
                builder.Append("Counted | ")
                    .Append(Show(line.PersonName)).Append(" | ")
                    .Append(line.TicketNumber).Append(" | ")
                    .Append(line.TableName).Append(" | ")
                    .Append(line.Action).Append(" | ")
                    .Append(line.LocalDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(" | ")
                    .Append(Amount(line.Amount)).Append(" | ")
                    .Append(Show(line.Location)).Append(" | ")
                    .AppendLine(line.Rule);
            }
        }

        builder.AppendLine();
        builder.AppendLine("Not counted");
        var drops = 0;
        if (facts is not null)
        {
            foreach (var drop in facts.DownloadDrops)
            {
                drops++;
                builder.Append("Not counted | ")
                    .Append(string.IsNullOrWhiteSpace(drop.TicketNumber)
                        ? TicketLabel(null, drop.RecordSysId)
                        : drop.TicketNumber.Trim())
                    .Append(" | ")
                    .Append(Show(drop.TableName)).Append(" | ")
                    .Append(Show(drop.PersonName)).Append(" | ")
                    .Append(Day(drop.LocalDay)).Append(" | ")
                    .Append(Show(drop.Location)).Append(" | ")
                    .Append(Show(drop.Office)).Append(" | ")
                    .AppendLine(drop.Reason);
            }
        }

        foreach (var drop in notes.Dropped)
        {
            drops++;
            builder.Append("Not counted | ")
                .Append(drop.TicketNumber).Append(" | ")
                .Append(drop.TableName).Append(" | ")
                .Append(Show(drop.PersonName)).Append(" | ")
                .Append(Day(drop.LocalDay)).Append(" | ")
                .Append(Show(drop.Location)).Append(" | ")
                .Append("- | ")
                .AppendLine(drop.Reason);
        }

        if (drops == 0)
            builder.AppendLine("(none)");

        builder.AppendLine();
        builder.AppendLine("Board totals");
        if (rows.Count == 0)
            builder.AppendLine("(none)");
        foreach (var row in rows)
            WriteTotals(builder, row, notes.Kept);

        return builder.ToString();
    }

    private static void WriteSlices(StringBuilder builder, WorkEffortLoadFacts? facts, int cap)
    {
        if (facts is null)
        {
            builder.AppendLine("Query slices were not recorded for this run.");
            return;
        }

        if (facts.Practice)
        {
            builder.AppendLine("Practice data is scored on this PC. No ServiceNow query slices were downloaded.");
            return;
        }

        if (facts.Slices.Count == 0)
        {
            builder.AppendLine("No query slices were run.");
            return;
        }

        foreach (var slice in facts.Slices)
        {
            builder.Append("Slice | ")
                .Append(Show(slice.PersonName)).Append(" | ")
                .Append(slice.LocalDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(" | ")
                .Append(Show(slice.Office)).Append(" | ")
                .Append(slice.TableName).Append(" | ")
                .Append(Show(slice.QueryRole)).Append(" | ")
                .Append("rows returned ")
                .Append(slice.RowsReturned.ToString(CultureInfo.InvariantCulture));
            if (slice.ReportedTotal is int reported)
            {
                builder.Append(" | ServiceNow reported ")
                    .Append(reported.ToString(CultureInfo.InvariantCulture));
            }

            builder.Append(" | safety cap of ")
                .Append(cap.ToString(CultureInfo.InvariantCulture))
                .Append(slice.HitSafetyCap ? " reached" : " not reached")
                .AppendLine();
            if (slice.HitSafetyCap)
                builder.AppendLine(CapExplanation);
        }
    }

    private static void WriteHistory(StringBuilder builder, WorkEffortLoadFacts? facts, int cap)
    {
        if (facts is null || facts.Practice)
        {
            builder.AppendLine("Update history was not read as a separate ServiceNow download for this run.");
            return;
        }

        if (facts.History.Count == 0)
        {
            builder.AppendLine("Update history was not read for this run.");
            return;
        }

        foreach (var row in facts.History)
        {
            builder.Append("History | ")
                .Append(row.Source)
                .Append(" | rows returned ")
                .Append(row.RowsReturned.ToString(CultureInfo.InvariantCulture));
            if (row.ReportedTotal is int reported)
            {
                builder.Append(" | ServiceNow reported ")
                    .Append(reported.ToString(CultureInfo.InvariantCulture));
            }

            builder.Append(" | safety cap of ")
                .Append(cap.ToString(CultureInfo.InvariantCulture))
                .Append(row.HitSafetyCap ? " reached" : " not reached")
                .AppendLine();
            if (row.HitSafetyCap)
                builder.AppendLine(CapExplanation);
        }
    }

    private static void WriteTotals(StringBuilder builder, WorkEffortRow row, IReadOnlyList<WorkEffortKeptCredit> kept)
    {
        var columns = new (string Label, int Count, decimal Weight)[]
        {
            ("INC opened", row.IncOpened, WorkEffortScore.IncidentWeight),
            ("INC resolved", row.IncResolved, WorkEffortScore.IncidentWeight),
            ("INC updated", row.IncUpdated, WorkEffortScore.UpdateWeight),
            ("RITM opened", row.RitmOpened, WorkEffortScore.RequestedItemWeight),
            ("RITM resolved", row.RitmResolved, WorkEffortScore.RequestedItemWeight),
            ("RITM updated", row.RitmUpdated, WorkEffortScore.UpdateWeight),
            ("IMS opened", row.ImsOpened, WorkEffortScore.InteractionWeight),
            ("IMS resolved", row.ImsResolved, WorkEffortScore.InteractionWeight),
            ("IMS updated", row.ImsUpdated, WorkEffortScore.UpdateWeight)
        };

        builder.Append(row.Name);
        foreach (var column in columns)
        {
            builder.Append(" | ")
                .Append(column.Label)
                .Append(' ')
                .Append(column.Count.ToString(CultureInfo.InvariantCulture));
        }

        builder.Append(" | Total ").AppendLine(row.WeightedText);

        builder.Append("Arithmetic | ").Append(row.Name).Append(" | ");
        decimal sum = 0;
        for (var index = 0; index < columns.Length; index++)
        {
            if (index > 0)
                builder.Append(" + ");
            var product = columns[index].Count * columns[index].Weight;
            sum += product;
            builder.Append(columns[index].Label)
                .Append(' ')
                .Append(columns[index].Count.ToString(CultureInfo.InvariantCulture))
                .Append(" x ")
                .Append(Amount(columns[index].Weight));
        }

        builder.Append(" = ").AppendLine(sum.ToString("0.0", CultureInfo.InvariantCulture));

        var lines = kept.Where(line => line.PersonSysId.Equals(row.PersonSysId, StringComparison.OrdinalIgnoreCase)).ToArray();
        decimal creditSum = 0;
        builder.Append("Credit lines | ").Append(row.Name).Append(" | ");
        if (lines.Length == 0)
            builder.Append("(none)");
        else
        {
            for (var index = 0; index < lines.Length; index++)
            {
                if (index > 0)
                    builder.Append(" + ");
                creditSum += lines[index].Amount;
                builder.Append(Amount(lines[index].Amount));
            }
        }

        builder.Append(" = ").Append(creditSum.ToString("0.0", CultureInfo.InvariantCulture));
        if (creditSum == row.Weighted)
            builder.Append(". Credit lines add up to Total ").Append(row.WeightedText).AppendLine(".");
        else
        {
            builder.Append(". Credit lines add up to ")
                .Append(creditSum.ToString("0.0", CultureInfo.InvariantCulture))
                .Append(". The board total is ")
                .Append(row.WeightedText)
                .AppendLine(".");
        }
    }

    private static string OfficeLine(WorkEffortLoadFacts? facts)
    {
        if (facts is null || facts.Offices.Count == 0)
            return "(none)";
        return string.Join(", ", facts.Offices);
    }

    private static string ScaleLabel(WorkEffortScale scale) => scale switch
    {
        WorkEffortScale.ThisWeek => "This week",
        WorkEffortScale.ThisMonth => "This month",
        WorkEffortScale.Last4Weeks => "Last 4 weeks",
        WorkEffortScale.SixMonths => "6 months",
        WorkEffortScale.TwelveMonths => "12 months",
        WorkEffortScale.DateRange => "Date range",
        _ => "Today"
    };

    private static string Stamp(DateTime moment) =>
        moment.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Amount(decimal value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Day(DateOnly? day) =>
        day is DateOnly value ? value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "-";

    private static string Show(string? value)
    {
        var text = (value ?? "").Trim();
        return text.Length == 0 ? "-" : text;
    }
}
