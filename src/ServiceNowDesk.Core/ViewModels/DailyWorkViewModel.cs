using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceNowDesk.Alerts;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;

namespace ServiceNowDesk.ViewModels;

public enum DailyWorkArea
{
    Mine,
    Team
}

public enum DailyWorkSortColumn
{
    Colour,
    Number,
    Priority,
    Title,
    Why,
    State,
    Assignee,
    Office
}

public partial class DailyWorkViewModel : ObservableObject
{
    public const string UnassignedListName = "new-unassigned";
    public const string AttendListName = "attend";

    private readonly IDailyWorkStore _store;
    private readonly IQueueDismissalStore _dismissals;
    private readonly object _gate = new();
    private DailyWorkBoard _board = DailyWorkBoard.Empty;
    private string _userId = "";
    private IReadOnlyList<string> _teamIds = [];
    private IReadOnlyList<string> _officeCities = [];
    private DateTime? _now;
    private IReadOnlyList<WatchedRecord> _groupTickets = [];
    private UnassignedSeen _groupSeen = UnassignedSeen.None;
    private HashSet<string> _dismissedIds = new(StringComparer.OrdinalIgnoreCase);
    private DailyWorkSortColumn? _sortColumn;
    private bool _sortDescending;

    public const string IntroText =
        "What to attend to from the notification queues. "
        + "Red = act first (priority 1 or SLA breaching). Yellow = next (caller update or follow-up passed). Green = after those. "
        + "The report is saved once each local day.";

    public DailyWorkViewModel(
        IDailyWorkStore store,
        IPersonalTaskStore? personalTasks = null,
        IQueueDismissalStore? queueDismissals = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _dismissals = queueDismissals ?? new MemoryQueueDismissalStore();
        _personalTasks = personalTasks ?? new MemoryPersonalTaskStore();
        LoadNotes();
    }

    public ObservableCollection<DailyWorkRow> NewUnassigned { get; } = [];
    public ObservableCollection<DailyWorkRow> Attend { get; } = [];
    public ObservableCollection<DailyWorkRow> Cleared { get; } = [];
    public ObservableCollection<DailyWorkRow> Arrived { get; } = [];
    public ObservableCollection<QueueDismissalRow> Dismissed { get; } = [];

    public event EventHandler<DailyWorkRow>? OpenRequested;

    [ObservableProperty] private DailyWorkArea area = DailyWorkArea.Mine;
    [ObservableProperty] private string headline = "Attend to these first";
    [ObservableProperty] private string reportNote = "The day's report is saved on the first check after midnight, or the next time the desk opens.";
    [ObservableProperty] private string differenceNote = "";
    [ObservableProperty] private string teamPrompt = "";
    [ObservableProperty] private bool hasNewUnassigned;
    [ObservableProperty] private bool hasAttend;
    [ObservableProperty] private bool hasCleared;
    [ObservableProperty] private bool hasArrived;
    [ObservableProperty] private bool hasDismissed;

    public string ActFirstHex => DailyWorkRanker.ActFirstHex;

    public string NextHex => DailyWorkRanker.NextHex;

    public string AfterThoseHex => DailyWorkRanker.AfterThoseHex;

    public string ColourHeader => SortHeader("Colour", DailyWorkSortColumn.Colour);
    public string NumberHeader => SortHeader("Number", DailyWorkSortColumn.Number);
    public string PriorityHeader => SortHeader("Priority", DailyWorkSortColumn.Priority);
    public string TitleHeader => SortHeader("Title", DailyWorkSortColumn.Title);
    public string WhyHeader => SortHeader("Why", DailyWorkSortColumn.Why);
    public string StateHeader => SortHeader("State", DailyWorkSortColumn.State);
    public string AssigneeHeader => SortHeader("Assigned to", DailyWorkSortColumn.Assignee);
    public string OfficeHeader => SortHeader("Office", DailyWorkSortColumn.Office);

    public void Show(DailyWorkBoard? board, string? userSysId, IReadOnlyList<string>? teamMemberIds, DateTime? localNow = null)
    {
        _board = board ?? DailyWorkBoard.Empty;
        _userId = userSysId?.Trim() ?? "";
        _teamIds = teamMemberIds ?? [];
        _now = localNow;
        RefreshDismissals(_now ?? DateTime.Now);
        Apply();
    }

    public void ShowGroupQueue(
        IReadOnlyList<WatchedRecord>? tickets,
        UnassignedSeen seen,
        DateTime? localNow = null,
        IReadOnlyList<string>? officeCities = null)
    {
        _groupTickets = tickets ?? [];
        _groupSeen = seen;
        if (officeCities is not null)
            _officeCities = officeCities;
        if (localNow is not null)
            _now = localNow;
        RefreshDismissals(_now ?? DateTime.Now);
        Apply();
    }

    /// <summary>Watched / account offices used to filter the group queue when non-empty.</summary>
    public void UseOfficeCities(IReadOnlyList<string>? cities)
    {
        _officeCities = cities ?? [];
        Apply();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _board = DailyWorkBoard.Empty;
            _userId = "";
            _teamIds = [];
            _officeCities = [];
            _groupTickets = [];
            _groupSeen = UnassignedSeen.None;
            _dismissedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _sortColumn = null;
            _sortDescending = false;
            NewUnassigned.Clear();
            Attend.Clear();
            Cleared.Clear();
            Arrived.Clear();
            Dismissed.Clear();
            HasNewUnassigned = false;
            HasAttend = false;
            HasCleared = false;
            HasArrived = false;
            HasDismissed = false;
            TeamPrompt = "";
            ReportNote = "";
            DifferenceNote = "";
            Headline = "Attend to these first";
            NotifySortHeaders();
        }
    }

    /// <summary>
    /// Removes a ticket from Daily Work for the local day and records the dismissal under AppData.
    /// </summary>
    public bool NotPartOfMyQueue(DailyWorkRow? row, string? reason, string listName, DateTime? localNow = null)
    {
        if (row is null || (string.IsNullOrWhiteSpace(row.SysId) && string.IsNullOrWhiteSpace(row.Number)))
            return false;

        var now = localNow ?? _now ?? DateTime.Now;
        var day = DateOnly.FromDateTime(now);
        var key = DismissalKey(row.SysId, row.Number);
        var existing = _dismissals.Load().ToList();
        if (existing.Any(item =>
                item.LocalDay == day
                && string.Equals(DismissalKey(item.SysId, item.Number), key, StringComparison.OrdinalIgnoreCase)))
            return false;

        existing.Add(new QueueDismissal(
            row.SysId?.Trim() ?? "",
            row.Number?.Trim() ?? "",
            row.Office?.Trim() ?? "",
            string.IsNullOrWhiteSpace(listName) ? UnassignedListName : listName.Trim(),
            reason?.Trim() ?? "",
            now,
            day));
        _dismissals.Save(existing);
        RefreshDismissals(now);
        Apply();
        return true;
    }

    [RelayCommand]
    private void ClearDismissals()
    {
        var now = _now ?? DateTime.Now;
        var day = DateOnly.FromDateTime(now);
        var keep = _dismissals.Load().Where(item => item.LocalDay != day).ToArray();
        _dismissals.Save(keep);
        RefreshDismissals(now);
        Apply();
    }

    [RelayCommand]
    private void Open(DailyWorkRow? row)
    {
        if (row is null || string.IsNullOrWhiteSpace(row.SysId))
            return;
        if (row.Section is not (DeskSection.Incidents or DeskSection.Requests or DeskSection.RequestedItems or DeskSection.WalkUps))
            return;
        OpenRequested?.Invoke(this, row);
    }

    [RelayCommand]
    private void SortBy(string? column)
    {
        if (!Enum.TryParse<DailyWorkSortColumn>(column, ignoreCase: true, out var parsed))
            return;
        if (_sortColumn != parsed)
        {
            _sortColumn = parsed;
            _sortDescending = false;
        }
        else
        {
            _sortDescending = !_sortDescending;
        }

        ApplySort();
    }

    partial void OnAreaChanged(DailyWorkArea value)
    {
        _ = value;
        _sortColumn = null;
        _sortDescending = false;
        Apply();
    }

    private void Apply()
    {
        lock (_gate)
        {
            var now = _now ?? DateTime.Now;
            var disconnected = _userId.Length == 0;
            Replace(NewUnassigned, QueueRows());
            HasNewUnassigned = NewUnassigned.Count > 0;
            var personal = DailyWorkRanker.Order(Merge(_board.Personal, QueueAdditions(now)));
            var mineView = DailyWorkReportBuilder.Build(DailyWorkKeys.User(_userId), now, personal, _store);
            var teamKey = DailyWorkKeys.Team(_teamIds);
            var teamView = teamKey is null
                ? DailyWorkView.Empty
                : DailyWorkReportBuilder.Build(teamKey, now, _board.Team, _store);
            if (Area == DailyWorkArea.Team)
                Fill(teamView, teamWithoutPeople: teamKey is null && !disconnected, disconnected);
            else
                Fill(mineView, teamWithoutPeople: false, disconnected);
            ApplySort();
        }
    }

    private void Fill(DailyWorkView view, bool teamWithoutPeople, bool disconnected)
    {
        Headline = Area == DailyWorkArea.Team ? "Your team, most urgent first" : "Attend to these first";
        TeamPrompt = teamWithoutPeople
            ? "Tick the people on your team under Leads. This report stays empty until you do."
            : "";
        Replace(Attend, teamWithoutPeople ? [] : view.Attend.Where(StillOnQueue).Select(DailyWorkRow.From));
        Replace(Cleared, teamWithoutPeople ? [] : view.Cleared.Select(DailyWorkRow.FromCleared));
        Replace(Arrived, teamWithoutPeople ? [] : view.Arrived.Where(StillOnQueue).Select(DailyWorkRow.From));
        HasAttend = Attend.Count > 0;
        HasCleared = Cleared.Count > 0;
        HasArrived = Arrived.Count > 0;
        if (disconnected)
        {
            ReportNote = "";
            DifferenceNote = "";
            return;
        }

        if (teamWithoutPeople)
        {
            ReportNote = "";
            DifferenceNote = "";
            return;
        }

        var when = view.GeneratedAt?.ToString("g", CultureInfo.CurrentCulture) ?? "";
        ReportNote = view.CreatedNow
            ? "Today's report was saved at " + when + ". It matches the list above. A later check shows what changed."
            : "Today's report was saved at " + when + " and listed " + view.MorningCount.ToString(CultureInfo.InvariantCulture)
                + " tickets. The list above is what to attend to now.";
        DifferenceNote = HasCleared || HasArrived
            ? "Compared with today's report."
            : "Nothing has changed since today's report.";
    }

    private void ApplySort()
    {
        NotifySortHeaders();
        if (_sortColumn is not DailyWorkSortColumn column)
            return;
        SortCollection(Attend, column);
        SortCollection(Arrived, column);
        SortCollection(Cleared, column);
    }

    private void SortCollection(ObservableCollection<DailyWorkRow> rows, DailyWorkSortColumn column)
    {
        if (rows.Count < 2 || _sortColumn is null)
            return;
        var sorted = rows.ToList();
        sorted.Sort((left, right) => DailyWorkRowSort.Compare(left, right, column, _sortDescending));
        for (var index = 0; index < sorted.Count; index++)
        {
            var current = rows.IndexOf(sorted[index]);
            if (current != index)
                rows.Move(current, index);
        }
    }

    private string SortHeader(string title, DailyWorkSortColumn column) =>
        _sortColumn == column ? title + (_sortDescending ? " ▼" : " ▲") : title;

    private void NotifySortHeaders()
    {
        OnPropertyChanged(nameof(ColourHeader));
        OnPropertyChanged(nameof(NumberHeader));
        OnPropertyChanged(nameof(PriorityHeader));
        OnPropertyChanged(nameof(TitleHeader));
        OnPropertyChanged(nameof(WhyHeader));
        OnPropertyChanged(nameof(StateHeader));
        OnPropertyChanged(nameof(AssigneeHeader));
        OnPropertyChanged(nameof(OfficeHeader));
    }

    private IReadOnlyList<DailyWorkRow> QueueRows()
    {
        var announced = new HashSet<string>(_groupSeen.Announced ?? [], StringComparer.OrdinalIgnoreCase);
        if (announced.Count == 0)
            return [];

        return _groupTickets
            .Where(record => record.SysId.Length > 0
                && announced.Contains(record.SysId)
                && InOfficeScope(record)
                && StillOnQueue(record.SysId, record.Number))
            .Select(QueueRow)
            .OrderBy(row => row.PriorityRank)
            .ThenBy(row => row.Number, StringComparer.Ordinal)
            .ToArray();
    }

    private IReadOnlyList<WorkItem> QueueAdditions(DateTime now)
    {
        var announced = new HashSet<string>(_groupSeen.Announced ?? [], StringComparer.OrdinalIgnoreCase);
        if (announced.Count == 0)
            return [];

        return _groupTickets
            .Where(record => announced.Contains(record.SysId)
                && InOfficeScope(record)
                && StillOnQueue(record.SysId, record.Number)
                && DailyWorkRanker.NeedsAttention(record, now))
            .Select(record => WorkItem.From(record, now))
            .ToArray();
    }

    private bool InOfficeScope(WatchedRecord record) =>
        _officeCities.Count == 0 || OfficeQueue.Matches(record.Location, _officeCities);

    private bool StillOnQueue(WorkItem item) => StillOnQueue(item.SysId, item.Number);

    private bool StillOnQueue(string? sysId, string? number) =>
        !_dismissedIds.Contains(DismissalKey(sysId, number));

    private void RefreshDismissals(DateTime localNow)
    {
        var day = DateOnly.FromDateTime(localNow);
        var today = _dismissals.Load().Where(item => item.LocalDay == day).ToArray();
        _dismissedIds = new HashSet<string>(
            today.Select(item => DismissalKey(item.SysId, item.Number)),
            StringComparer.OrdinalIgnoreCase);
        Replace(Dismissed, today.OrderByDescending(item => item.DismissedAtLocal).Select(QueueDismissalRow.From));
        HasDismissed = Dismissed.Count > 0;
    }

    private static string DismissalKey(string? sysId, string? number)
    {
        var id = sysId?.Trim() ?? "";
        if (id.Length > 0)
            return id;
        return number?.Trim() ?? "";
    }

    private static void Replace(ObservableCollection<QueueDismissalRow> target, IEnumerable<QueueDismissalRow> rows)
    {
        target.Clear();
        foreach (var row in rows)
            target.Add(row);
    }

    private static IEnumerable<WorkItem> Merge(IReadOnlyList<WorkItem> current, IReadOnlyList<WorkItem> extras)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in current)
        {
            if (item.SysId.Length == 0 || !seen.Add(item.SysId))
                continue;
            yield return item;
        }

        foreach (var item in extras)
        {
            if (item.SysId.Length == 0 || !seen.Add(item.SysId))
                continue;
            yield return item;
        }
    }

    private static DailyWorkRow QueueRow(WatchedRecord record)
    {
        var rank = DailyWorkRanker.PriorityRank(record.PriorityValue, record.PriorityLabel);
        var label = string.IsNullOrWhiteSpace(record.PriorityLabel) ? record.PriorityValue : record.PriorityLabel;
        return new DailyWorkRow
        {
            SysId = record.SysId,
            Section = DeskSection.Incidents,
            Number = record.Number,
            Title = record.Title,
            PriorityText = string.IsNullOrWhiteSpace(label) ? "No priority" : label.Trim(),
            PriorityRank = rank,
            PriorityBadge = rank is 1 or 2 ? "P" + rank.ToString(CultureInfo.InvariantCulture) : "",
            EmphasizePriority = rank is 1 or 2,
            Group = record.Group?.Trim() ?? "",
            Office = record.Location?.Trim() ?? "",
            When = WhenOf(record),
            State = record.State,
            AssigneeText = "Unassigned",
            HighlightHex = rank switch
            {
                1 => DailyWorkRanker.ActFirstHex,
                2 => DailyWorkRanker.NextHex,
                _ => ""
            }
        };
    }

    private static string WhenOf(WatchedRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.Opened))
            return record.Opened.Trim();
        return record.Updated?.Trim() ?? "";
    }

    private static void Replace(ObservableCollection<DailyWorkRow> target, IEnumerable<DailyWorkRow> rows)
    {
        target.Clear();
        foreach (var row in rows)
            target.Add(row);
    }
}

public sealed class DailyWorkRow
{
    public string SysId { get; init; } = "";
    public DeskSection Section { get; init; }
    public string Number { get; init; } = "";
    public string Title { get; init; } = "";
    public string PriorityText { get; init; } = "";
    public int PriorityRank { get; init; } = 99;
    public string PriorityBadge { get; init; } = "";
    public bool EmphasizePriority { get; init; }
    public string Group { get; init; } = "";
    public string Office { get; init; } = "";
    public string When { get; init; } = "";
    public string Reasons { get; init; } = "";
    public string State { get; init; } = "";
    public string AssigneeText { get; init; } = "";
    public string HighlightHex { get; init; } = "";

    public int ColourTier => DailyWorkRanker.ColourTier(HighlightHex);

    public static DailyWorkRow From(WorkItem item) => new()
    {
        SysId = item.SysId,
        Section = item.Section,
        Number = item.Number,
        Title = item.Title,
        PriorityText = item.PriorityText,
        PriorityRank = item.PriorityRank,
        Reasons = item.Reasons,
        State = item.State,
        AssigneeText = item.AssigneeText,
        Office = item.Location?.Trim() ?? "",
        HighlightHex = DailyWorkRanker.HighlightHex(item)
    };

    public static DailyWorkRow FromCleared(DailyWorkLine line)
    {
        _ = line.TrySection(out var section);
        return new DailyWorkRow
        {
            SysId = line.SysId,
            Section = section,
            Number = line.Number,
            Title = line.Title,
            PriorityText = "",
            Reasons = "Left the list",
            State = "",
            AssigneeText = ""
        };
    }
}

public sealed class QueueDismissalRow
{
    public string SysId { get; init; } = "";
    public string Number { get; init; } = "";
    public string Office { get; init; } = "";
    public string List { get; init; } = "";
    public string Reason { get; init; } = "";
    public string When { get; init; } = "";

    public static QueueDismissalRow From(QueueDismissal item) => new()
    {
        SysId = item.SysId,
        Number = item.Number,
        Office = item.Office,
        List = item.List switch
        {
            DailyWorkViewModel.AttendListName => "Attend",
            DailyWorkViewModel.UnassignedListName => "New unassigned",
            _ => item.List
        },
        Reason = string.IsNullOrWhiteSpace(item.Reason) ? "(no reason)" : item.Reason,
        When = item.DismissedAtLocal.ToString("g", CultureInfo.CurrentCulture)
    };
}

public static class DailyWorkRowSort
{
    public static int Compare(DailyWorkRow left, DailyWorkRow right, DailyWorkSortColumn column, bool descending)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var compared = column switch
        {
            DailyWorkSortColumn.Colour => left.ColourTier.CompareTo(right.ColourTier),
            DailyWorkSortColumn.Priority => left.PriorityRank.CompareTo(right.PriorityRank),
            DailyWorkSortColumn.Number => CompareNumber(left.Number, right.Number),
            _ => CompareText(Text(left, column), Text(right, column))
        };
        if (compared == 0)
            compared = string.Compare(left.Number, right.Number, StringComparison.OrdinalIgnoreCase);
        return descending ? -compared : compared;
    }

    private static int CompareNumber(string left, string right)
    {
        var leftEmpty = string.IsNullOrWhiteSpace(left);
        var rightEmpty = string.IsNullOrWhiteSpace(right);
        if (leftEmpty || rightEmpty)
        {
            if (leftEmpty && rightEmpty)
                return 0;
            return leftEmpty ? 1 : -1;
        }

        if (TryDigits(left, out var leftNumber) && TryDigits(right, out var rightNumber))
        {
            var numeric = leftNumber.CompareTo(rightNumber);
            return numeric != 0 ? numeric : string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
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

    private static string Text(DailyWorkRow row, DailyWorkSortColumn column) => column switch
    {
        DailyWorkSortColumn.Title => row.Title,
        DailyWorkSortColumn.Why => row.Reasons,
        DailyWorkSortColumn.State => row.State,
        DailyWorkSortColumn.Assignee => row.AssigneeText,
        DailyWorkSortColumn.Office => row.Office,
        _ => ""
    };
}
