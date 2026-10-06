using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceNowDesk.Alerts;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.ViewModels;

public partial class NotificationWorkspaceViewModel : ObservableObject
{
    private readonly bool _alwaysShowAssignee;

    public NotificationWorkspaceViewModel(IEnumerable<AlertKind>? kinds = null, bool alwaysShowAssignee = false)
    {
        _alwaysShowAssignee = alwaysShowAssignee;
        var shown = (kinds ?? AlertCatalog.All).Distinct().ToArray();
        if (shown.Length == 0)
            shown = AlertCatalog.All.ToArray();
        foreach (var kind in shown)
        {
            Sections.Add(new AlertSectionModel(kind));
            Circles.Add(new AlertCircleModel(kind));
        }

        if (SelectedQueue != shown[0])
            SelectedQueue = shown[0];
        else
            MarkSelectedQueue();
    }

    public ObservableCollection<AlertSectionModel> Sections { get; } = [];
    public ObservableCollection<AlertCircleModel> Circles { get; } = [];
    public ObservableCollection<AlertRow> WidgetItems { get; } = [];
    public ObservableCollection<AlertRow> DashboardRows { get; } = [];

    public event EventHandler<AlertRow>? OpenRequested;
    public event EventHandler<AlertKind>? QueueSelected;
    public event EventHandler<AlertAttention>? Attention;

    private string _viewerSysId = "";
    private HighlightPreferences _highlights = HighlightPreferences.Default;

    public bool ShowAssigneeColumn => _alwaysShowAssignee || SelectedQueue == AlertKind.SlaBreaching;

    public void Show(AlertSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        foreach (var section in Sections)
        {
            var bucket = snapshot.Bucket(section.Kind);
            section.Count = bucket.TotalCount;
            section.Status = bucket.Status;
            section.IsUnacknowledged = false;
            section.Replace(bucket.Rows);
        }

        foreach (var circle in Circles)
        {
            var bucket = snapshot.Bucket(circle.Kind);
            circle.Count = bucket.TotalCount;
            circle.Status = bucket.Status;
            circle.IsUnacknowledged = false;
            circle.IsJiggleCause = false;
        }

        AnyUnacknowledged = false;
        PollError = "";
        LastChecked = "Last checked " + DateTime.Now.ToString("t", CultureInfo.CurrentCulture) + ".";
        RefreshWidget();
    }

    public void RememberViewer(string? userSysId, HighlightPreferences highlights)
    {
        ArgumentNullException.ThrowIfNull(highlights);
        _viewerSysId = userSysId?.Trim() ?? "";
        _highlights = highlights;
        PaintSlaAssignees();
    }

    [ObservableProperty] private bool anyUnacknowledged;
    [ObservableProperty] private string pollError = "";
    [ObservableProperty] private string lastChecked = "Not checked yet.";
    [ObservableProperty] private string selectedStatus = "";
    [ObservableProperty] private string widgetSummary = "No notifications";
    [ObservableProperty] private string newestTitle = "";
    [ObservableProperty] private bool isWidgetOpen;
    [ObservableProperty] private bool hasWidgetItems;
    [ObservableProperty] private bool hasDashboardRows;
    [ObservableProperty] private bool widgetHasUnread;
    [ObservableProperty] private AlertKind selectedQueue = AlertKind.AssignedToMe;

    public void Apply(AlertSnapshot snapshot, AlertWatchState watch)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(watch);
        var decision = watch.Observe(snapshot.Counts);
        foreach (var section in Sections)
        {
            var bucket = snapshot.Bucket(section.Kind);
            section.Count = bucket.TotalCount;
            section.Status = bucket.Status;
            section.IsUnacknowledged = watch.IsUnacknowledged(section.Kind);
            section.Replace(bucket.Rows);
        }

        foreach (var circle in Circles)
        {
            var bucket = snapshot.Bucket(circle.Kind);
            circle.Count = bucket.TotalCount;
            circle.Status = bucket.Status;
            circle.IsUnacknowledged = watch.IsUnacknowledged(circle.Kind);
            if (!circle.IsUnacknowledged)
                circle.IsJiggleCause = false;
        }

        AnyUnacknowledged = watch.AnyUnacknowledged;
        PollError = "";
        LastChecked = "Last checked " + DateTime.Now.ToString("t", CultureInfo.CurrentCulture) + ".";
        if (decision.HasIncrease)
            Attention?.Invoke(this, new AlertAttention { PlaySound = true, Increased = decision.Increased });

        RefreshWidget();
    }

    public void RefreshAcknowledgement(AlertWatchState watch)
    {
        ArgumentNullException.ThrowIfNull(watch);
        foreach (var section in Sections)
            section.IsUnacknowledged = watch.IsUnacknowledged(section.Kind);
        foreach (var circle in Circles)
        {
            circle.IsUnacknowledged = watch.IsUnacknowledged(circle.Kind);
            circle.IsJiggleCause = false;
        }

        AnyUnacknowledged = watch.AnyUnacknowledged;
        RefreshWidget();
    }

    public void Clear()
    {
        foreach (var section in Sections)
        {
            section.Count = 0;
            section.Status = "";
            section.IsUnacknowledged = false;
            section.Replace([]);
        }

        foreach (var circle in Circles)
        {
            circle.Count = 0;
            circle.Status = "";
            circle.IsUnacknowledged = false;
            circle.IsJiggleCause = false;
        }

        AnyUnacknowledged = false;
        PollError = "";
        LastChecked = "Not checked yet.";
        RefreshWidget();
    }

    public void NotePollError(string message) => PollError = message;

    public IReadOnlyList<AlertKind> UnacknowledgedKinds =>
        Circles.Where(circle => circle.IsUnacknowledged).Select(circle => circle.Kind).ToArray();

    public IReadOnlyList<AlertKind> JiggleHighlight =>
        Circles.Where(circle => circle.IsJiggleCause).Select(circle => circle.Kind).ToArray();

    public void ShowJiggle(IReadOnlyList<AlertKind> causes)
    {
        ArgumentNullException.ThrowIfNull(causes);
        var causeSet = causes as IReadOnlySet<AlertKind> ?? causes.ToHashSet();
        foreach (var circle in Circles)
            circle.IsJiggleCause = causeSet.Contains(circle.Kind);
    }

    [RelayCommand]
    private void Open(AlertRow? row)
    {
        if (Resolve(row) is null)
            return;
        OpenRequested?.Invoke(this, row!);
    }

    [RelayCommand]
    private void SelectQueue(AlertKind? kind)
    {
        if (kind is not AlertKind queue)
            return;
        if (SelectedQueue != queue)
            SelectedQueue = queue;
        else
        {
            MarkSelectedQueue();
            RefreshDashboard();
        }

        QueueSelected?.Invoke(this, queue);
    }

    public NotificationTarget? Resolve(AlertRow? row)
    {
        if (row is null || string.IsNullOrWhiteSpace(row.SysId))
            return null;
        if (row.Section is not (DeskSection.Incidents or DeskSection.Requests or DeskSection.RequestedItems or DeskSection.WalkUps))
            return null;
        return new NotificationTarget(row.Section, row.SysId);
    }

    [RelayCommand]
    private void ToggleWidget() => IsWidgetOpen = !IsWidgetOpen;

    private void RefreshWidget()
    {
        var rows = new List<AlertRow>();
        var active = 0;
        var unread = 0;
        foreach (var section in Sections)
        {
            active += section.Count;
            if (section.IsUnacknowledged)
                unread += section.Count;
            foreach (var row in section.Rows)
                rows.Add(row);
        }

        rows.Sort(static (left, right) =>
        {
            var byTime = UpdatedStamp(right.Updated).CompareTo(UpdatedStamp(left.Updated));
            return byTime != 0
                ? byTime
                : string.Compare(right.Number, left.Number, StringComparison.Ordinal);
        });

        WidgetItems.Clear();
        foreach (var row in rows)
            WidgetItems.Add(row);

        if (active == 0)
            active = rows.Count;
        NewestTitle = rows.Count == 0
            ? ""
            : string.IsNullOrWhiteSpace(rows[0].Title) ? rows[0].Number : rows[0].Title;
        WidgetHasUnread = unread > 0;
        WidgetSummary = active == 0
            ? "No notifications"
            : unread > 0
                ? unread.ToString(CultureInfo.InvariantCulture) + " unread"
                : active.ToString(CultureInfo.InvariantCulture) + " active";
        HasWidgetItems = rows.Count > 0;
        RefreshDashboard();
    }

    partial void OnSelectedQueueChanged(AlertKind value)
    {
        MarkSelectedQueue();
        RefreshDashboard();
        OnPropertyChanged(nameof(ShowAssigneeColumn));
    }

    private void MarkSelectedQueue()
    {
        foreach (var circle in Circles)
            circle.IsSelected = circle.Kind == SelectedQueue;
    }

    private void RefreshDashboard()
    {
        DashboardRows.Clear();
        foreach (var section in Sections)
        {
            if (section.Kind != SelectedQueue)
                continue;
            foreach (var row in section.Rows)
                DashboardRows.Add(row);
        }

        HasDashboardRows = DashboardRows.Count > 0;
        SelectedStatus = Sections.FirstOrDefault(section => section.Kind == SelectedQueue)?.Status ?? "";
        PaintSlaAssignees();
    }

    private void PaintSlaAssignees()
    {
        foreach (var section in Sections)
        {
            foreach (var row in section.Rows)
                row.HighlightHex = SlaAssigneeHex(row);
        }
    }

    private string SlaAssigneeHex(AlertRow row)
    {
        if (row.Kind != AlertKind.SlaBreaching)
            return "";
        var mine = _viewerSysId.Length > 0
            && string.Equals(row.AssignedToSysId?.Trim(), _viewerSysId, StringComparison.OrdinalIgnoreCase);
        return _highlights.ChooseSlaAssigneeHex(mine);
    }

    private static DateTime UpdatedStamp(string updated)
    {
        if (DateTime.TryParse(updated, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
            return parsed;
        if (DateTime.TryParse(updated, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out parsed))
            return parsed;
        return DateTime.MinValue;
    }
}

public partial class AlertSectionModel : ObservableObject
{
    public AlertSectionModel(AlertKind kind)
    {
        Kind = kind;
        Title = AlertCatalog.Title(kind);
    }

    public AlertKind Kind { get; }
    public string Title { get; }
    public ObservableCollection<AlertRow> Rows { get; } = [];

    [ObservableProperty] private int count;
    [ObservableProperty] private string status = "";
    [ObservableProperty] private bool isUnacknowledged;
    [ObservableProperty] private AlertRow? selected;

    public string Heading => Title + " (" + Count.ToString(CultureInfo.InvariantCulture) + ")";

    public bool IsEmpty => Count == 0;

    public void Replace(IReadOnlyList<AlertRecord> rows)
    {
        var selectedId = Selected?.SysId;
        Rows.Clear();
        foreach (var row in rows)
            Rows.Add(AlertRow.From(row));
        Selected = selectedId is null ? null : Rows.FirstOrDefault(row => row.SysId == selectedId);
        OnPropertyChanged(nameof(Heading));
    }

    partial void OnCountChanged(int value)
    {
        OnPropertyChanged(nameof(Heading));
        OnPropertyChanged(nameof(IsEmpty));
    }
}

public partial class AlertCircleModel : ObservableObject
{
    public AlertCircleModel(AlertKind kind)
    {
        Kind = kind;
        Title = AlertCatalog.Title(kind);
        Description = AlertCatalog.Description(kind);
        AutomationName = AlertCatalog.AutomationName(kind, 0);
    }

    public AlertKind Kind { get; }
    public string Title { get; }

    /// <summary>What this chip counts. Shown even when the count is zero.</summary>
    public string Description { get; }

    [ObservableProperty] private int count;
    [ObservableProperty] private string status = "";
    [ObservableProperty] private bool isUnacknowledged;
    [ObservableProperty] private bool isJiggleCause;
    [ObservableProperty] private bool isSelected;
    [ObservableProperty] private string automationName;

    public bool IsVisible => Count > 0;

    public string StatusLabel => Title + " " + Count.ToString(CultureInfo.InvariantCulture);

    public string ToolTipText => string.IsNullOrWhiteSpace(Status) ? StatusLabel : StatusLabel + ". " + Status;

    partial void OnCountChanged(int value)
    {
        AutomationName = AlertCatalog.AutomationName(Kind, value);
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(ToolTipText));
    }

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(ToolTipText));
}

public sealed class AlertRow : INotifyPropertyChanged
{
    private string _highlightHex = "";

    public required AlertKind Kind { get; init; }
    public required DeskSection Section { get; init; }
    public required string SysId { get; init; }
    public required string Number { get; init; }
    public required string Title { get; init; }
    public required string State { get; init; }
    public required string Group { get; init; }
    public required string Location { get; init; }
    public required string Updated { get; init; }
    public string Assignee { get; init; } = "";
    public string AssignedToSysId { get; init; } = "";

    public string AssigneeLabel => string.IsNullOrWhiteSpace(Assignee) ? "Unassigned" : Assignee;

    /// <summary>Short line for the shared notification row. Blank when nobody is assigned.</summary>
    public string AssignedLine => string.IsNullOrWhiteSpace(Assignee) ? "" : "Assigned to " + Assignee.Trim();

    public string HighlightHex
    {
        get => _highlightHex;
        set
        {
            var next = value ?? "";
            if (string.Equals(_highlightHex, next, StringComparison.Ordinal))
                return;
            _highlightHex = next;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HighlightHex)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string QueueLabel => AlertCatalog.Title(Kind);

    public string TableLabel => Section switch
    {
        DeskSection.Incidents => "Incident",
        DeskSection.Requests => "Request",
        DeskSection.RequestedItems => "Request item",
        DeskSection.WalkUps => "Walk-up",
        _ => "Record"
    };

    public string Detail
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(State))
                parts.Add(State);
            if (!string.IsNullOrWhiteSpace(Group))
                parts.Add(Group);
            if (!string.IsNullOrWhiteSpace(Location))
                parts.Add(Location);
            return string.Join(" · ", parts);
        }
    }

    public static AlertRow From(AlertRecord record) => new()
    {
        Kind = record.Kind,
        Section = record.Section,
        SysId = record.SysId,
        Number = record.Number,
        Title = record.Title,
        State = record.State,
        Group = record.Group,
        Location = record.Location,
        Updated = record.Updated,
        Assignee = record.Assignee,
        AssignedToSysId = record.AssignedToSysId
    };
}

public readonly record struct NotificationTarget(DeskSection Section, string SysId);
