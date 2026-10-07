using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceNowDesk.Alerts;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;

namespace ServiceNowDesk.ViewModels;

public enum DailyWorkArea
{
    Mine,
    Team
}

public partial class DailyWorkViewModel : ObservableObject
{
    private readonly IDailyWorkStore _store;
    private DailyWorkBoard _board = DailyWorkBoard.Empty;
    private string _userId = "";
    private IReadOnlyList<string> _teamIds = [];
    private DateTime? _now;
    private IReadOnlyList<WatchedRecord> _groupTickets = [];
    private UnassignedSeen _groupSeen = UnassignedSeen.None;

    public DailyWorkViewModel(IDailyWorkStore store, IPersonalTaskStore? personalTasks = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _personalTasks = personalTasks ?? new MemoryPersonalTaskStore();
        LoadNotes();
    }

    public ObservableCollection<DailyWorkRow> NewUnassigned { get; } = [];
    public ObservableCollection<DailyWorkRow> Attend { get; } = [];
    public ObservableCollection<DailyWorkRow> Cleared { get; } = [];
    public ObservableCollection<DailyWorkRow> Arrived { get; } = [];

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

    public string ActFirstHex => DailyWorkRanker.ActFirstHex;

    public string NextHex => DailyWorkRanker.NextHex;

    public string AfterThoseHex => DailyWorkRanker.AfterThoseHex;

    public void Show(DailyWorkBoard? board, string? userSysId, IReadOnlyList<string>? teamMemberIds, DateTime? localNow = null)
    {
        _board = board ?? DailyWorkBoard.Empty;
        _userId = userSysId?.Trim() ?? "";
        _teamIds = teamMemberIds ?? [];
        _now = localNow;
        Apply();
    }

    public void ShowGroupQueue(IReadOnlyList<WatchedRecord>? tickets, UnassignedSeen seen, DateTime? localNow = null)
    {
        _groupTickets = tickets ?? [];
        _groupSeen = seen;
        if (localNow is not null)
            _now = localNow;
        Apply();
    }

    public void Clear()
    {
        _board = DailyWorkBoard.Empty;
        _userId = "";
        _teamIds = [];
        _groupTickets = [];
        _groupSeen = UnassignedSeen.None;
        NewUnassigned.Clear();
        Attend.Clear();
        Cleared.Clear();
        Arrived.Clear();
        HasNewUnassigned = false;
        HasAttend = false;
        HasCleared = false;
        HasArrived = false;
        TeamPrompt = "";
        ReportNote = "";
        DifferenceNote = "";
        Headline = "Attend to these first";
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

    partial void OnAreaChanged(DailyWorkArea value)
    {
        _ = value;
        Apply();
    }

    private void Apply()
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
    }

    private void Fill(DailyWorkView view, bool teamWithoutPeople, bool disconnected)
    {
        Headline = Area == DailyWorkArea.Team ? "Your team, most urgent first" : "Attend to these first";
        TeamPrompt = teamWithoutPeople
            ? "Tick the people on your team under Leads. This report stays empty until you do."
            : "";
        Replace(Attend, teamWithoutPeople ? [] : view.Attend.Select(DailyWorkRow.From));
        Replace(Cleared, teamWithoutPeople ? [] : view.Cleared.Select(DailyWorkRow.FromCleared));
        Replace(Arrived, teamWithoutPeople ? [] : view.Arrived.Select(DailyWorkRow.From));
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

    private IReadOnlyList<DailyWorkRow> QueueRows()
    {
        var announced = new HashSet<string>(_groupSeen.Announced ?? [], StringComparer.OrdinalIgnoreCase);
        if (announced.Count == 0)
            return [];

        return _groupTickets
            .Where(record => record.SysId.Length > 0 && announced.Contains(record.SysId))
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
            .Where(record => announced.Contains(record.SysId) && DailyWorkRanker.NeedsAttention(record, now))
            .Select(record => WorkItem.From(record, now))
            .ToArray();
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
    public string When { get; init; } = "";
    public string Reasons { get; init; } = "";
    public string State { get; init; } = "";
    public string AssigneeText { get; init; } = "";
    public string HighlightHex { get; init; } = "";

    public static DailyWorkRow From(WorkItem item) => new()
    {
        SysId = item.SysId,
        Section = item.Section,
        Number = item.Number,
        Title = item.Title,
        PriorityText = item.PriorityText,
        Reasons = item.Reasons,
        State = item.State,
        AssigneeText = item.AssigneeText,
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
