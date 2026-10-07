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

    public WorkEffortViewModel()
    {
    }

    public ObservableCollection<WorkEffortRow> Rows { get; } = [];

    public event EventHandler? ScaleChanged;

    public event EventHandler? RefreshRequested;

    private WorkEffortScale? _loadingScale;

    [ObservableProperty] private WorkEffortScale scale = WorkEffortScale.Today;
    [ObservableProperty] private string status = "";
    [ObservableProperty] private string emptyMessage = "";
    [ObservableProperty] private string asOf = "";
    [ObservableProperty] private bool hasRows;
    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private int progressValue;
    [ObservableProperty] private int progressMaximum = WorkEffortProgress.Steps;

    /// <summary>
    /// True when the screen needs a query. A cached scale from the same local day is shown and returns false.
    /// A load already running for the selected scale also returns false, so leaving the page and coming back
    /// does not start a second query. A new local day drops every cached scale. Force reloads only the scale
    /// that is selected.
    /// </summary>
    public bool BeginLoad(DateTime localNow, bool force)
    {
        if (!force && IsLoading && _loadingScale == Scale)
            return false;
        if (!force && TryShowCached(localNow))
            return false;

        MarkLoading();
        return true;
    }

    public bool TryShowCached(DateTime localNow)
    {
        if (!TryGet(localNow, Scale, out var entry))
            return false;
        ShowEntry(entry);
        return true;
    }

    public void Remember(WorkEffortScale scale, DateTime localNow, WorkEffortReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var clock = WorkEffortWindow.Clock(localNow);
        var day = DateOnly.FromDateTime(clock);
        if (_cachedDay != day)
        {
            _cache.Clear();
            _cachedDay = day;
        }

        var entry = new WorkEffortCacheEntry(clock, report);
        _cache[scale] = entry;
        if (scale == Scale)
            ShowEntry(entry);
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
        AsOf = "";
        _loadingScale = Scale;
        ProgressValue = 0;
        ProgressMaximum = WorkEffortProgress.Steps;
        IsLoading = true;
        Status = WorkEffortProgress.Loading(Scale, 0).Status;
    }

    public void Show(WorkEffortReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        Rows.Clear();
        foreach (var row in report.Rows)
            Rows.Add(row);
        HasRows = Rows.Count > 0;
        EmptyMessage = HasRows ? "" : report.EmptyMessage;
        Status = report.Status;
        IsLoading = false;
        _loadingScale = null;
    }

    public void ShowError(string message)
    {
        Rows.Clear();
        HasRows = false;
        EmptyMessage = "";
        AsOf = "";
        Status = message ?? "";
        IsLoading = false;
        _loadingScale = null;
    }

    public void Clear()
    {
        _cache.Clear();
        _cachedDay = null;
        Rows.Clear();
        HasRows = false;
        EmptyMessage = "";
        AsOf = "";
        Status = "";
        IsLoading = false;
        ProgressValue = 0;
        ProgressMaximum = WorkEffortProgress.Steps;
        _loadingScale = null;
    }

    [RelayCommand]
    private void Refresh() => RefreshRequested?.Invoke(this, EventArgs.Empty);

    partial void OnScaleChanged(WorkEffortScale value)
    {
        _ = value;
        ScaleChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool TryGet(DateTime localNow, WorkEffortScale scale, out WorkEffortCacheEntry entry)
    {
        entry = default;
        var day = DateOnly.FromDateTime(WorkEffortWindow.Clock(localNow));
        if (_cachedDay != day)
            return false;
        return _cache.TryGetValue(scale, out entry);
    }

    private void ShowEntry(WorkEffortCacheEntry entry)
    {
        Show(entry.Report);
        AsOf = "As of " + entry.LoadedAt.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    private readonly record struct WorkEffortCacheEntry(DateTime LoadedAt, WorkEffortReport Report);
}
