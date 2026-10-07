using System.Globalization;
using System.Text.Json;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.Alerts;

/// <summary>
/// One open ticket the signed-in user, or a ticked teammate, should attend to.
/// </summary>
public sealed record WorkItem(
    string SysId,
    DeskSection Section,
    string Number,
    string Title,
    string State,
    string Assignee,
    string PriorityValue,
    string PriorityLabel,
    bool SlaBreaching,
    bool UpdatedByCaller,
    bool FollowUpPassed,
    bool Unattended,
    bool ReturnedWithNotes)
{
    public int PriorityRank => DailyWorkRanker.PriorityRank(PriorityValue, PriorityLabel);

    public string PriorityText => string.IsNullOrWhiteSpace(PriorityLabel) ? "No priority" : PriorityLabel.Trim();

    public string AssigneeText => string.IsNullOrWhiteSpace(Assignee) ? "Unassigned" : Assignee.Trim();

    public string Reasons
    {
        get
        {
            var parts = new List<string>();
            if (SlaBreaching)
                parts.Add("SLA");
            if (UpdatedByCaller)
                parts.Add("Caller updated");
            if (FollowUpPassed)
                parts.Add("Follow-up passed");
            if (Unattended)
                parts.Add("Unattended");
            if (ReturnedWithNotes)
                parts.Add("Returned by DT");
            return string.Join(", ", parts);
        }
    }

    public static WorkItem From(WatchedRecord record, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(record);
        var label = string.IsNullOrWhiteSpace(record.PriorityLabel) ? record.PriorityValue : record.PriorityLabel;
        return new WorkItem(
            record.SysId,
            record.Section,
            record.Number,
            record.Title,
            record.State,
            record.AssigneeDisplay?.Trim() ?? "",
            record.PriorityValue?.Trim() ?? "",
            label?.Trim() ?? "",
            AlertClassifier.IsSlaBreaching(record, now),
            AlertClassifier.CallerMadeTheLatestUpdate(record),
            AlertClassifier.IsOnHoldPastFollowUp(record, now),
            AlertClassifier.IsUnattended(record, now),
            AlertClassifier.IsReturnedWithNotes(record));
    }
}

/// <summary>The live ranked lists for the signed-in user and for the ticked team.</summary>
public sealed class DailyWorkBoard
{
    public DailyWorkBoard(IReadOnlyList<WorkItem> personal, IReadOnlyList<WorkItem> team)
    {
        Personal = personal ?? [];
        Team = team ?? [];
    }

    public static DailyWorkBoard Empty { get; } = new([], []);

    public IReadOnlyList<WorkItem> Personal { get; }

    public IReadOnlyList<WorkItem> Team { get; }

    public static DailyWorkBoard From(
        IEnumerable<WatchedRecord> personalPopulation,
        IEnumerable<WatchedRecord> leadPopulation,
        DateTime now,
        string? userSysId,
        IEnumerable<string>? teamMemberIds)
    {
        ArgumentNullException.ThrowIfNull(personalPopulation);
        ArgumentNullException.ThrowIfNull(leadPopulation);
        var mine = new AssigneeScope(userSysId);
        var team = TeamIds(teamMemberIds);
        var teamItems = team.Count == 0
            ? Array.Empty<WorkItem>()
            : DailyWorkRanker.Rank(leadPopulation.Where(record => LeadBoard.AssignedToAny(record, team)), now);
        return new DailyWorkBoard(
            DailyWorkRanker.Rank(personalPopulation.Where(mine.Includes), now),
            teamItems);
    }

    public static HashSet<string> TeamIds(IEnumerable<string>? memberIds) =>
        new(
            (memberIds ?? []).Select(id => id?.Trim() ?? "").Where(id => id.Length > 0),
            StringComparer.OrdinalIgnoreCase);
}

public static class DailyWorkRanker
{
    public static IReadOnlyList<WorkItem> Order(IEnumerable<WorkItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items.OrderBy(item => item, WorkOrder.Instance).ToArray();
    }

    public static IReadOnlyList<WorkItem> Rank(IEnumerable<WatchedRecord> records, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(records);
        return records
            .Where(record => NeedsAttention(record, now))
            .Select(record => WorkItem.From(record, now))
            .OrderBy(item => item, WorkOrder.Instance)
            .ToArray();
    }

    public static bool NeedsAttention(WatchedRecord record, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!AlertClassifier.IsStillOpen(record))
            return false;
        if (AlertClassifier.IsSlaBreaching(record, now))
            return true;
        if (AlertClassifier.CallerMadeTheLatestUpdate(record))
            return true;
        if (AlertClassifier.IsOnHoldPastFollowUp(record, now))
            return true;
        if (AlertClassifier.IsUnattended(record, now))
            return true;
        return AlertClassifier.IsReturnedWithNotes(record);
    }

    /// <summary>Pale red. Priority 1, or an SLA that is breaching. Act on these first.</summary>
    public const string ActFirstHex = "#F8D6D6";

    /// <summary>Pale yellow. The caller updated the ticket, or the follow-up has passed.</summary>
    public const string NextHex = "#FFF3C4";

    /// <summary>Pale green. Still on the daily list, after the red and yellow rows.</summary>
    public const string AfterThoseHex = "#D8F5D6";

    /// <summary>
    /// How soon to act on a daily-work row. The highest matching tier wins.
    /// A missing priority is not red.
    /// </summary>
    public static string HighlightHex(WorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.PriorityRank == 1 || item.SlaBreaching)
            return ActFirstHex;
        if (item.UpdatedByCaller || item.FollowUpPassed)
            return NextHex;
        return AfterThoseHex;
    }

    /// <summary>ServiceNow priority 1 is the most urgent. A blank priority sorts last.</summary>
    public static int PriorityRank(string? value, string? label)
    {
        if (TryRank(value, out var rank) || TryRank(label, out rank))
            return rank;
        return 99;
    }

    private static bool TryRank(string? text, out int rank)
    {
        rank = 99;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var digits = new string(text.Trim().TakeWhile(char.IsDigit).ToArray());
        return digits.Length > 0 && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out rank) && rank > 0;
    }

    private sealed class WorkOrder : IComparer<WorkItem>
    {
        public static WorkOrder Instance { get; } = new();

        public int Compare(WorkItem? left, WorkItem? right)
        {
            if (ReferenceEquals(left, right))
                return 0;
            if (left is null)
                return -1;
            if (right is null)
                return 1;

            var priority = left.PriorityRank.CompareTo(right.PriorityRank);
            if (priority != 0)
                return priority;
            var sla = right.SlaBreaching.CompareTo(left.SlaBreaching);
            if (sla != 0)
                return sla;
            var caller = right.UpdatedByCaller.CompareTo(left.UpdatedByCaller);
            if (caller != 0)
                return caller;
            var followUp = right.FollowUpPassed.CompareTo(left.FollowUpPassed);
            if (followUp != 0)
                return followUp;
            return string.Compare(left.Number, right.Number, StringComparison.Ordinal);
        }
    }
}

public sealed record DailyWorkLine(string SysId, string Number, string Title, string Section)
{
    public static DailyWorkLine From(WorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new DailyWorkLine(item.SysId, item.Number, item.Title, item.Section.ToString());
    }

    public bool TrySection(out DeskSection section) =>
        Enum.TryParse(Section, ignoreCase: true, out section);
}

public sealed record DailyWorkDay(string ScopeKey, DateOnly LocalDate, DateTime GeneratedAt, IReadOnlyList<DailyWorkLine> Lines);

public sealed record DailyWorkView(
    DateTime? GeneratedAt,
    int MorningCount,
    bool CreatedNow,
    IReadOnlyList<WorkItem> Attend,
    IReadOnlyList<DailyWorkLine> Cleared,
    IReadOnlyList<WorkItem> Arrived)
{
    public static DailyWorkView Empty { get; } = new(null, 0, false, [], [], []);
}

public static class DailyWorkKeys
{
    public static string? User(string? userSysId)
    {
        var id = userSysId?.Trim() ?? "";
        return id.Length == 0 ? null : "user:" + id;
    }

    public static string? Team(IEnumerable<string>? memberIds)
    {
        var ids = DailyWorkBoard.TeamIds(memberIds)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return ids.Length == 0 ? null : "team:" + string.Join(",", ids);
    }
}

/// <summary>
/// The prospective list is frozen at the first check of the local calendar day.
/// Later checks compare the live list with that report.
/// </summary>
public static class DailyWorkReportBuilder
{
    public static DailyWorkView Build(string? scopeKey, DateTime localNow, IReadOnlyList<WorkItem>? current, IDailyWorkStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var live = current ?? [];
        if (string.IsNullOrWhiteSpace(scopeKey))
            return new DailyWorkView(null, 0, false, live, [], []);

        var day = DateOnly.FromDateTime(localNow);
        var existing = store.Find(scopeKey, day);
        var created = false;
        if (existing is null)
        {
            existing = new DailyWorkDay(scopeKey, day, localNow, live.Select(DailyWorkLine.From).ToArray());
            store.Save(existing);
            created = true;
        }

        var morningIds = new HashSet<string>(existing.Lines.Select(line => line.SysId), StringComparer.OrdinalIgnoreCase);
        var currentIds = new HashSet<string>(live.Select(item => item.SysId), StringComparer.OrdinalIgnoreCase);
        var cleared = existing.Lines.Where(line => line.SysId.Length > 0 && !currentIds.Contains(line.SysId)).ToArray();
        var arrived = live.Where(item => item.SysId.Length > 0 && !morningIds.Contains(item.SysId)).ToArray();
        return new DailyWorkView(existing.GeneratedAt, existing.Lines.Count, created, live, cleared, arrived);
    }
}

public interface IDailyWorkStore
{
    DailyWorkDay? Find(string scopeKey, DateOnly day);

    void Save(DailyWorkDay day);

    UnassignedSeen FindUnassigned(string scopeKey, DateOnly day);

    void SaveUnassigned(string scopeKey, DateOnly day, UnassignedSeen seen);
}

public sealed class MemoryDailyWorkStore : IDailyWorkStore
{
    private readonly Dictionary<string, DailyWorkDay> _days = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, UnassignedSeen> _unassigned = new(StringComparer.OrdinalIgnoreCase);

    public DailyWorkDay? Find(string scopeKey, DateOnly day) =>
        _days.TryGetValue(Key(scopeKey, day), out var dayRecord) ? dayRecord : null;

    public void Save(DailyWorkDay day)
    {
        ArgumentNullException.ThrowIfNull(day);
        _days[Key(day.ScopeKey, day.LocalDate)] = day;
    }

    public UnassignedSeen FindUnassigned(string scopeKey, DateOnly day) =>
        _unassigned.TryGetValue(Key(scopeKey, day), out var seen) ? seen : UnassignedSeen.None;

    public void SaveUnassigned(string scopeKey, DateOnly day, UnassignedSeen seen)
    {
        if (string.IsNullOrWhiteSpace(scopeKey))
            return;
        _unassigned[Key(scopeKey, day)] = seen;
    }

    private static string Key(string scopeKey, DateOnly day) =>
        (scopeKey ?? "") + "|" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>One JSON file of daily reports. A bad file starts again instead of blocking the desk.</summary>
public sealed class FileDailyWorkStore : IDailyWorkStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private readonly object _gate = new();

    public FileDailyWorkStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Enter a path for the daily work file.", nameof(path));
        _path = path;
    }

    public static FileDailyWorkStore InApplicationData()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ServiceNowDesk");
        return new FileDailyWorkStore(Path.Combine(folder, "daily-work.json"));
    }

    public DailyWorkDay? Find(string scopeKey, DateOnly day)
    {
        lock (_gate)
        {
            return Load().Days
                .Select(Parse)
                .FirstOrDefault(record => record is not null
                    && string.Equals(record.ScopeKey, scopeKey, StringComparison.OrdinalIgnoreCase)
                    && record.LocalDate == day);
        }
    }

    public void Save(DailyWorkDay day)
    {
        ArgumentNullException.ThrowIfNull(day);
        lock (_gate)
        {
            var file = Load();
            var keepAfter = day.LocalDate.AddDays(-1);
            file.Days = file.Days
                .Where(stored => ParseDate(stored.LocalDate) is DateOnly saved && saved >= keepAfter)
                .Where(stored => !(string.Equals(stored.ScopeKey, day.ScopeKey, StringComparison.OrdinalIgnoreCase)
                    && ParseDate(stored.LocalDate) == day.LocalDate))
                .ToList();
            file.Days.Add(new DailyWorkStoredDay
            {
                ScopeKey = day.ScopeKey,
                LocalDate = day.LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                GeneratedAt = day.GeneratedAt.ToString("o", CultureInfo.InvariantCulture),
                Lines = day.Lines.Select(line => new DailyWorkStoredLine
                {
                    SysId = line.SysId,
                    Number = line.Number,
                    Title = line.Title,
                    Section = line.Section
                }).ToList()
            });
            file.UnassignedSeen = KeepSeen(file.UnassignedSeen, keepAfter);
            Write(file);
        }
    }

    public UnassignedSeen FindUnassigned(string scopeKey, DateOnly day)
    {
        lock (_gate)
        {
            var stored = (Load().UnassignedSeen ?? [])
                .FirstOrDefault(record => record is not null
                    && string.Equals(record.ScopeKey, scopeKey, StringComparison.OrdinalIgnoreCase)
                    && ParseDate(record.LocalDate) == day);
            return ToSeen(stored);
        }
    }

    public void SaveUnassigned(string scopeKey, DateOnly day, UnassignedSeen seen)
    {
        if (string.IsNullOrWhiteSpace(scopeKey))
            return;

        lock (_gate)
        {
            var file = Load();
            var keepAfter = day.AddDays(-1);
            file.Days = file.Days
                .Where(stored => ParseDate(stored.LocalDate) is DateOnly saved && saved >= keepAfter)
                .ToList();
            file.UnassignedSeen = KeepSeen(file.UnassignedSeen, keepAfter)
                .Where(stored => !(string.Equals(stored.ScopeKey, scopeKey, StringComparison.OrdinalIgnoreCase)
                    && ParseDate(stored.LocalDate) == day))
                .ToList();
            file.UnassignedSeen.Add(new StoredUnassignedSeen
            {
                ScopeKey = scopeKey.Trim(),
                LocalDate = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                BaselineTaken = seen.BaselineTaken,
                Seen = CleanIds(seen.Seen),
                Announced = CleanIds(seen.Announced)
            });
            Write(file);
        }
    }

    private void Write(DailyWorkFile file)
    {
        var folder = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(folder))
            Directory.CreateDirectory(folder);
        File.WriteAllText(_path, JsonSerializer.Serialize(file, JsonOptions));
    }

    private static List<StoredUnassignedSeen> KeepSeen(List<StoredUnassignedSeen>? rows, DateOnly keepAfter) =>
        (rows ?? [])
            .Where(stored => stored is not null && ParseDate(stored.LocalDate) is DateOnly saved && saved >= keepAfter)
            .ToList();

    private static UnassignedSeen ToSeen(StoredUnassignedSeen? stored)
    {
        if (stored is null || !stored.BaselineTaken)
            return UnassignedSeen.None;
        return new UnassignedSeen(true, CleanIds(stored.Seen), CleanIds(stored.Announced));
    }

    private static List<string> CleanIds(IEnumerable<string>? ids) =>
        (ids ?? [])
            .Select(id => id?.Trim() ?? "")
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private DailyWorkFile Load()
    {
        try
        {
            if (!File.Exists(_path))
                return new DailyWorkFile();
            return JsonSerializer.Deserialize<DailyWorkFile>(File.ReadAllText(_path), JsonOptions) ?? new DailyWorkFile();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new DailyWorkFile();
        }
    }

    private static DailyWorkDay? Parse(DailyWorkStoredDay? stored)
    {
        if (stored is null || string.IsNullOrWhiteSpace(stored.ScopeKey) || ParseDate(stored.LocalDate) is not DateOnly day)
            return null;
        var generated = DateTime.TryParse(stored.GeneratedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : day.ToDateTime(TimeOnly.MinValue);
        var lines = (stored.Lines ?? [])
            .Where(line => line is not null && !string.IsNullOrWhiteSpace(line.SysId))
            .Select(line => new DailyWorkLine(line.SysId.Trim(), line.Number ?? "", line.Title ?? "", line.Section ?? ""))
            .ToArray();
        return new DailyWorkDay(stored.ScopeKey.Trim(), day, generated, lines);
    }

    private static DateOnly? ParseDate(string? text) =>
        DateOnly.TryParseExact(text?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : null;

    private sealed class DailyWorkFile
    {
        public List<DailyWorkStoredDay> Days { get; set; } = [];

        public List<StoredUnassignedSeen> UnassignedSeen { get; set; } = [];
    }

    private sealed class StoredUnassignedSeen
    {
        public string ScopeKey { get; set; } = "";
        public string LocalDate { get; set; } = "";
        public bool BaselineTaken { get; set; }
        public List<string> Seen { get; set; } = [];
        public List<string> Announced { get; set; } = [];
    }

    private sealed class DailyWorkStoredDay
    {
        public string ScopeKey { get; set; } = "";
        public string LocalDate { get; set; } = "";
        public string GeneratedAt { get; set; } = "";
        public List<DailyWorkStoredLine> Lines { get; set; } = [];
    }

    private sealed class DailyWorkStoredLine
    {
        public string SysId { get; set; } = "";
        public string Number { get; set; } = "";
        public string Title { get; set; } = "";
        public string Section { get; set; } = "";
    }
}
