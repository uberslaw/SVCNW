using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceNowDesk.Services;
using ServiceNowDesk.WorkEffort;

namespace ServiceNowDesk.ViewModels;

public partial class WorkEffortViewModel : ObservableObject
{
    private readonly Dictionary<WorkEffortScale, WorkEffortCacheEntry> _cache = [];
    private readonly object _gate = new();
    private DateOnly? _cachedDay;
    private string _loadingTeam = "";
    private WorkEffortCacheEntry? _shown;
    private IDesktopServices? _desktop;
    private IReadOnlyList<WorkEffortCredit> _boardCredits = [];

    public WorkEffortViewModel()
    {
    }

    public ObservableCollection<WorkEffortRow> Rows { get; } = [];

    public ObservableCollection<WorkEffortCredit> DetailLines { get; } = [];

    public event EventHandler? ScaleChanged;

    public event EventHandler? RefreshRequested;

    public event EventHandler<WorkEffortCredit>? OpenTicketRequested;

    private WorkEffortScale? _loadingScale;

    [ObservableProperty] private WorkEffortScale scale = WorkEffortScale.Today;
    [ObservableProperty] private WorkEffortUpdateMode updateMode = WorkEffortUpdateMode.Daily;
    [ObservableProperty] private string status = "";
    [ObservableProperty] private string emptyMessage = "";
    [ObservableProperty] private string shift = "";
    [ObservableProperty] private string asOf = "";
    [ObservableProperty] private bool hasRows;
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private int progressValue;
    [ObservableProperty] private int progressMaximum = WorkEffortEstimate.BarMaximum;
    [ObservableProperty] private bool showDetail;
    [ObservableProperty] private string detailTitle = "";
    [ObservableProperty] private string detailEmptyMessage = "";
    [ObservableProperty] private bool detailHasRows;
    [ObservableProperty] private string exportNote = "";

    public void UseDesktop(IDesktopServices desktop) => _desktop = desktop;

    /// <summary>
    /// True when the screen needs a query. A cached scale from the same local day and the same team
    /// is shown and returns false. A load already running for the selected scale also returns false,
    /// so leaving the page and coming back does not start a second query. A new local day drops every
    /// cached scale. Force reloads only the scale that is selected.
    /// </summary>
    public bool BeginLoad(DateTime localNow, bool force, string? teamKey = null)
    {
        var key = teamKey ?? "";
        // Prefer a finished cache for this scale/team even when a load flag is still set,
        // so returning to the page never looks stuck after the query already completed.
        if (!force && TryShowCached(localNow, key))
            return false;
        if (!force && IsLoading && _loadingScale == Scale
            && string.Equals(key, _loadingTeam, StringComparison.Ordinal))
            return false;

        _loadingTeam = key;
        MarkLoading();
        return true;
    }

    public bool TryShowCached(DateTime localNow, string? teamKey = null)
    {
        if (!TryGet(localNow, Scale, teamKey ?? "", out var entry))
            return false;
        ShowEntry(entry);
        return true;
    }

    public void Remember(WorkEffortScale scale, DateTime localNow, WorkEffortReport report, string? teamKey = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        var key = teamKey ?? "";
        var entry = Store(scale, localNow, report, key);
        // Paint when this result matches the in-flight team. If the team key drifted but
        // this scale is still loading, still clear IsLoading so the page cannot stick.
        if (scale != Scale)
            return;
        if (string.Equals(key, _loadingTeam, StringComparison.Ordinal))
        {
            ShowEntry(entry);
            return;
        }

        if (IsLoading && _loadingScale == scale)
            AbandonLoad();
    }

    /// <summary>
    /// Keeps a finished query for the local day and team without painting the board.
    /// Used when the roster changed mid-flight so a later visit can still hit the cache.
    /// </summary>
    public void Cache(WorkEffortScale scale, DateTime localNow, WorkEffortReport report, string? teamKey = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        Store(scale, localNow, report, teamKey ?? "");
    }

    private WorkEffortCacheEntry Store(WorkEffortScale scale, DateTime localNow, WorkEffortReport report, string key)
    {
        var clock = WorkEffortWindow.Clock(localNow);
        var day = DateOnly.FromDateTime(clock);
        if (_cachedDay != day)
        {
            _cache.Clear();
            _cachedDay = day;
        }

        var entry = new WorkEffortCacheEntry(clock, report, key);
        _cache[scale] = entry;
        return entry;
    }

    /// <summary>
    /// Queues the report and returns before the rows are counted. An empty team shows the prompt
    /// and does not call <paramref name="tables"/>.
    /// </summary>
    public Task<WorkEffortReport> StartBackground(
        DateTime localNow,
        IReadOnlyList<WorkEffortPerson> team,
        Func<int, IEnumerable<WorkEffortTouch>> tables,
        CancellationToken cancellationToken,
        int safetyCap = WorkEffortQuery.SafetyCap)
    {
        ArgumentNullException.ThrowIfNull(tables);
        var people = WorkEffortTeam.Normalize(team);
        if (people.Count == 0)
        {
            var empty = WorkEffortReport.NoTeam();
            Show(empty);
            return Task.FromResult(empty);
        }

        var key = WorkEffortTeam.Key(people);
        var scale = Scale;
        if (!BeginLoad(localNow, force: true, key))
            return Task.FromResult(WorkEffortReport.NoTeam());

        var window = WorkEffortWindow.For(scale, localNow);
        var progress = new Progress<WorkEffortProgress>(update => Apply(scale, update));
        return WorkEffortEngine.Start(people, window, tables, progress, cancellationToken, safetyCap);
    }

    public void Apply(WorkEffortScale scale, WorkEffortProgress progress)
    {
        if (!IsLoading || _loadingScale != scale)
            return;
        ProgressMaximum = progress.Total < 1 ? WorkEffortProgress.Steps : progress.Total;
        var value = progress.Completed;
        if (value < 0)
            value = 0;
        if (value > ProgressMaximum)
            value = ProgressMaximum;
        ProgressValue = value;
        if (!string.IsNullOrWhiteSpace(progress.Status))
            Status = progress.Status;
    }

    /// <summary>
    /// Drops the running flag after the query is cancelled because the connection went away.
    /// Cached figures for the day stay put.
    /// </summary>
    public void AbandonLoad()
    {
        lock (_gate)
        {
            if (!IsLoading)
                return;
            IsLoading = false;
            _loadingScale = null;
            Status = "";
        }
    }

    public void MarkLoading()
    {
        lock (_gate)
        {
            Rows.Clear();
            HasRows = false;
            EmptyMessage = "";
            Shift = "";
            AsOf = "";
            _shown = null;
            _boardCredits = [];
            ClearDetail();
            _loadingScale = Scale;
            ProgressValue = 0;
            ProgressMaximum = WorkEffortEstimate.BarMaximum;
            IsLoading = true;
            Status = WorkEffortEstimate.Text(1);
        }
    }

    public void Show(WorkEffortReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        ApplyReport(report);
    }

    public void ShowError(string message)
    {
        lock (_gate)
        {
            Rows.Clear();
            HasRows = false;
            EmptyMessage = "";
            Shift = "";
            AsOf = "";
            _shown = null;
            _boardCredits = [];
            ClearDetail();
            Status = message ?? "";
            IsLoading = false;
            _loadingScale = null;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _cache.Clear();
            _cachedDay = null;
            _shown = null;
            _boardCredits = [];
            Rows.Clear();
            HasRows = false;
            EmptyMessage = "";
            Shift = "";
            AsOf = "";
            Status = "";
            IsLoading = false;
            ProgressValue = 0;
            ProgressMaximum = WorkEffortEstimate.BarMaximum;
            _loadingScale = null;
            _loadingTeam = "";
            ClearDetail();
            if (UpdateMode != WorkEffortUpdateMode.Daily)
                UpdateMode = WorkEffortUpdateMode.Daily;
        }
    }

    public void ShowPersonDetail(WorkEffortRow? row)
    {
        if (row is null)
            return;
        if (!HasLoadedLedger())
        {
            PresentDetail("Work Effort detail", [], WorkEffortDetail.NotLoadedMessage);
            return;
        }

        var lines = WorkEffortDetail.ForPerson(_boardCredits, row.PersonSysId);
        var empty = lines.Count == 0 ? WorkEffortDetail.EmptyPersonMessage : "";
        PresentDetail(row.Name + " — all metrics", lines, empty);
    }

    public void ShowCellDetail(WorkEffortRow? row, WorkEffortColumn column)
    {
        if (row is null)
            return;
        if (!HasLoadedLedger())
        {
            PresentDetail("Work Effort detail", [], WorkEffortDetail.NotLoadedMessage);
            return;
        }

        var lines = WorkEffortDetail.ForCell(_boardCredits, row.PersonSysId, column);
        var title = row.Name + " — " + WorkEffortDetail.ColumnLabel(column);
        var empty = lines.Count == 0 ? WorkEffortDetail.EmptyCellMessage : "";
        PresentDetail(title, lines, empty);
    }

    public void ShowCellDetail(WorkEffortRow? row, string? columnName)
    {
        if (!WorkEffortDetail.TryParseColumn(columnName, out var column))
            return;
        ShowCellDetail(row, column);
    }

    [RelayCommand]
    private void CloseDetail() => ClearDetail();

    [RelayCommand]
    private void OpenCredit(WorkEffortCredit? credit)
    {
        if (credit is null || string.IsNullOrWhiteSpace(credit.RecordSysId))
            return;
        OpenTicketRequested?.Invoke(this, credit);
    }

    [RelayCommand]
    private void ExportDetail()
    {
        if (!ShowDetail)
            return;
        if (!HasLoadedLedger())
        {
            ExportNote = WorkEffortDetail.NotLoadedMessage;
            return;
        }

        var name = SanitizeFileName(DetailTitle) + ".csv";
        WriteCsv(name, DetailLines);
    }

    [RelayCommand]
    private void ExportBoard()
    {
        if (!HasLoadedLedger())
        {
            ExportNote = WorkEffortDetail.NotLoadedMessage;
            return;
        }

        var scale = Scale.ToString().ToLowerInvariant();
        var name = "work-effort-board-" + scale + ".csv";
        WriteCsv(name, _boardCredits);
    }

    [RelayCommand]
    private void Refresh() => RefreshRequested?.Invoke(this, EventArgs.Empty);

    partial void OnScaleChanged(WorkEffortScale value)
    {
        _ = value;
        ScaleChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnUpdateModeChanged(WorkEffortUpdateMode value)
    {
        _ = value;
        if (IsLoading || _shown is null)
            return;
        ShowEntry(_shown.Value);
    }

    private bool TryGet(DateTime localNow, WorkEffortScale scale, string teamKey, out WorkEffortCacheEntry entry)
    {
        entry = default;
        var day = DateOnly.FromDateTime(WorkEffortWindow.Clock(localNow));
        if (_cachedDay != day)
            return false;
        if (!_cache.TryGetValue(scale, out entry))
            return false;
        return string.Equals(entry.TeamKey, teamKey, StringComparison.Ordinal);
    }

    private void ShowEntry(WorkEffortCacheEntry entry)
    {
        // Set AsOf before ApplyReport clears IsLoading so waiters never see rows without a stamp.
        var presented = WorkEffortScore.Present(entry.Report, UpdateMode);
        lock (_gate)
        {
            _shown = entry;
            AsOf = "As of " + entry.LoadedAt.ToString("HH:mm", CultureInfo.InvariantCulture);
            WriteReport(presented);
        }

        RebuildBoardCredits(presented);
        if (ShowDetail)
            RefreshOpenDetail();
    }

    private void ApplyReport(WorkEffortReport report)
    {
        lock (_gate)
        {
            _shown = null;
            WriteReport(report);
        }

        RebuildBoardCredits(report);
        if (ShowDetail)
            RefreshOpenDetail();
    }

    private void WriteReport(WorkEffortReport report)
    {
        Rows.Clear();
        foreach (var row in report.Rows)
            Rows.Add(row);
        HasRows = Rows.Count > 0;
        EmptyMessage = HasRows ? "" : report.EmptyMessage;
        Shift = HasRows ? report.Shift : "";
        Status = report.Status;
        IsLoading = false;
        _loadingScale = null;
    }

    private void RebuildBoardCredits(WorkEffortReport report)
    {
        if (report.Ledger is null)
        {
            _boardCredits = [];
            return;
        }

        _boardCredits = WorkEffortDetail.Build(
            report.Ledger.People,
            report.Ledger.Touches,
            report.Ledger.Window,
            UpdateMode);
    }

    private bool HasLoadedLedger() => _shown is not null && _shown.Value.Report.Ledger is not null && !IsLoading;

    private void RefreshOpenDetail()
    {
        if (!ShowDetail)
            return;
        // Re-score keeps an open detail in sync when Daily / Multiple flips.
        if (_shown?.Report.Ledger is null)
        {
            PresentDetail(DetailTitle, [], WorkEffortDetail.NotLoadedMessage);
            return;
        }

        // Title still describes the selection; rebuild lines from the current board credits.
        var title = DetailTitle;
        IReadOnlyList<WorkEffortCredit> lines;
        string empty;
        if (title.Contains(" — all metrics", StringComparison.Ordinal))
        {
            var name = title.Replace(" — all metrics", "", StringComparison.Ordinal);
            var row = Rows.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                PresentDetail(title, [], WorkEffortDetail.EmptyPersonMessage);
                return;
            }

            lines = WorkEffortDetail.ForPerson(_boardCredits, row.PersonSysId);
            empty = lines.Count == 0 ? WorkEffortDetail.EmptyPersonMessage : "";
        }
        else
        {
            var sep = title.LastIndexOf(" — ", StringComparison.Ordinal);
            if (sep < 0)
            {
                PresentDetail(title, _boardCredits, "");
                return;
            }

            var name = title[..sep];
            var columnLabel = title[(sep + 3)..];
            var row = Rows.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var column = Enum.GetValues<WorkEffortColumn>()
                .FirstOrDefault(value => WorkEffortDetail.ColumnLabel(value) == columnLabel);
            if (row is null)
            {
                PresentDetail(title, [], WorkEffortDetail.EmptyCellMessage);
                return;
            }

            lines = WorkEffortDetail.ForCell(_boardCredits, row.PersonSysId, column);
            empty = lines.Count == 0 ? WorkEffortDetail.EmptyCellMessage : "";
        }

        PresentDetail(title, lines, empty);
    }

    private void PresentDetail(string title, IReadOnlyList<WorkEffortCredit> lines, string empty)
    {
        DetailTitle = title;
        DetailLines.Clear();
        foreach (var line in lines)
            DetailLines.Add(line);
        DetailHasRows = DetailLines.Count > 0;
        DetailEmptyMessage = DetailHasRows ? "" : empty;
        ShowDetail = true;
        ExportNote = "";
    }

    private void ClearDetail()
    {
        ShowDetail = false;
        DetailTitle = "";
        DetailEmptyMessage = "";
        DetailHasRows = false;
        DetailLines.Clear();
        ExportNote = "";
    }

    private void WriteCsv(string fileName, IEnumerable<WorkEffortCredit> lines)
    {
        if (_desktop is null)
        {
            ExportNote = "Export is not available.";
            return;
        }

        var csv = WorkEffortCsv.Format(lines);
        var path = _desktop.SaveTextFile(
            fileName,
            "CSV (*.csv)|*.csv|All files (*.*)|*.*",
            csv,
            WorkEffortCsv.Utf8Bom);
        ExportNote = path is null ? "Export cancelled." : "Saved " + path;
    }

    private static string SanitizeFileName(string value)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0)
            return "work-effort-detail";
        foreach (var bad in Path.GetInvalidFileNameChars())
            text = text.Replace(bad, '-');
        text = text.Replace(' ', '-');
        while (text.Contains("--", StringComparison.Ordinal))
            text = text.Replace("--", "-", StringComparison.Ordinal);
        return text.Length == 0 ? "work-effort-detail" : text;
    }

    private readonly record struct WorkEffortCacheEntry(DateTime LoadedAt, WorkEffortReport Report, string TeamKey);
}
