using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceNowDesk.WorkEffort;

namespace ServiceNowDesk.ViewModels;

public partial class WorkEffortViewModel : ObservableObject
{
    private readonly Dictionary<WorkEffortScale, WorkEffortCacheEntry> _cache = [];
    private DateOnly? _cachedDay;
    private string _loadingTeam = "";
    private WorkEffortCacheEntry? _shown;

    public WorkEffortViewModel()
    {
    }

    public ObservableCollection<WorkEffortRow> Rows { get; } = [];

    public event EventHandler? ScaleChanged;

    public event EventHandler? RefreshRequested;

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

    /// <summary>
    /// True when the screen needs a query. A cached scale from the same local day and the same team
    /// is shown and returns false. A load already running for the selected scale also returns false,
    /// so leaving the page and coming back does not start a second query. A new local day drops every
    /// cached scale. Force reloads only the scale that is selected.
    /// </summary>
    public bool BeginLoad(DateTime localNow, bool force, string? teamKey = null)
    {
        var key = teamKey ?? "";
        if (!force && IsLoading && _loadingScale == Scale)
            return false;
        if (!force && TryShowCached(localNow, key))
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
        var clock = WorkEffortWindow.Clock(localNow);
        var day = DateOnly.FromDateTime(clock);
        if (_cachedDay != day)
        {
            _cache.Clear();
            _cachedDay = day;
        }

        var key = teamKey ?? "";
        var entry = new WorkEffortCacheEntry(clock, report, key);
        _cache[scale] = entry;
        if (scale == Scale && string.Equals(key, _loadingTeam, StringComparison.Ordinal))
            ShowEntry(entry);
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
        if (!IsLoading)
            return;
        IsLoading = false;
        _loadingScale = null;
        Status = "";
    }

    public void MarkLoading()
    {
        Rows.Clear();
        HasRows = false;
        EmptyMessage = "";
        Shift = "";
        AsOf = "";
        _shown = null;
        _loadingScale = Scale;
        ProgressValue = 0;
        ProgressMaximum = WorkEffortEstimate.BarMaximum;
        IsLoading = true;
        Status = WorkEffortEstimate.Text(1);
    }

    public void Show(WorkEffortReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        _shown = null;
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

    public void ShowError(string message)
    {
        Rows.Clear();
        HasRows = false;
        EmptyMessage = "";
        Shift = "";
        AsOf = "";
        _shown = null;
        Status = message ?? "";
        IsLoading = false;
        _loadingScale = null;
    }

    public void Clear()
    {
        _cache.Clear();
        _cachedDay = null;
        _shown = null;
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
        if (UpdateMode != WorkEffortUpdateMode.Daily)
            UpdateMode = WorkEffortUpdateMode.Daily;
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
        Show(WorkEffortScore.Present(entry.Report, UpdateMode));
        _shown = entry;
        AsOf = "As of " + entry.LoadedAt.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    private readonly record struct WorkEffortCacheEntry(DateTime LoadedAt, WorkEffortReport Report, string TeamKey);
}
