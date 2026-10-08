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

        UseQueueSortDefault();
    }

    public ObservableCollection<AlertSectionModel> Sections { get; } = [];
    public ObservableCollection<AlertCircleModel> Circles { get; } = [];
    public ObservableCollection<AlertRow> WidgetItems { get; } = [];
    public ObservableCollection<AlertRow> DashboardRows { get; } = [];

    public event EventHandler<AlertRow>? OpenRequested;
    public event EventHandler<AlertKind>? QueueSelected;
    public event EventHandler<AlertAttention>? Attention;

    private readonly object _gate = new();
    private string _viewerSysId = "";
    private HighlightPreferences _highlights = HighlightPreferences.Default;
    private LeadSortColumn? _sortColumn;
    private bool _sortDescending;

    public bool ShowAssigneeColumn => _alwaysShowAssignee || SelectedQueue == AlertKind.SlaBreaching;

    /// <summary>Days assigned is only on the personal Assigned to me queue.</summary>
    public bool ShowDaysAssigned => !_alwaysShowAssignee && SelectedQueue == AlertKind.AssignedToMe;

    public bool ShowStandardColumns => !ShowAssigneeColumn && !ShowDaysAssigned;

    public void Show(AlertSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
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
    }

    public void RememberViewer(string? userSysId, HighlightPreferences highlights)
    {
        ArgumentNullException.ThrowIfNull(highlights);
        lock (_gate)
        {
            _viewerSysId = userSysId?.Trim() ?? "";
            _highlights = highlights;
            PaintSlaAssignees();
        }
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
        AlertAttention? attention = null;
        lock (_gate)
        {
            var counts = new Dictionary<AlertKind, int>();
            foreach (var kind in AlertCatalog.All)
                counts[kind] = DisplayCount(kind, snapshot.Bucket(kind));
            var decision = watch.Observe(counts);
            foreach (var section in Sections)
                WriteSection(section, snapshot.Bucket(section.Kind), watch.IsUnacknowledged(section.Kind));

            foreach (var circle in Circles)
                WriteCircle(circle, snapshot.Bucket(circle.Kind), watch.IsUnacknowledged(circle.Kind));

            AnyUnacknowledged = watch.AnyUnacknowledged;
            PollError = "";
            LastChecked = "Last checked " + DateTime.Now.ToString("t", CultureInfo.CurrentCulture) + ".";
            if (decision.HasIncrease)
                attention = new AlertAttention { PlaySound = true, Increased = decision.Increased };

            RefreshWidget();
        }

        if (attention is not null)
            Attention?.Invoke(this, attention);
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
        lock (_gate)
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
    }

    public void NotePollError(string message) => PollError = ShortPollError(message);

    private int DisplayCount(AlertKind kind, AlertBucket bucket)
    {
        var previous = Sections.FirstOrDefault(section => section.Kind == kind)?.Count
            ?? Circles.FirstOrDefault(circle => circle.Kind == kind)?.Count
            ?? 0;
        return Keep(previous, bucket) ? previous : bucket.TotalCount;
    }

    private static void WriteSection(AlertSectionModel section, AlertBucket bucket, bool unacknowledged)
    {
        if (Keep(section.Count, bucket))
        {
            section.Status = bucket.Status;
            section.IsUnacknowledged = unacknowledged;
            return;
        }

        section.Count = bucket.TotalCount;
        section.Status = bucket.Status;
        section.IsUnacknowledged = unacknowledged;
        section.Replace(bucket.Rows);
    }

    private static void WriteCircle(AlertCircleModel circle, AlertBucket bucket, bool unacknowledged)
    {
        if (Keep(circle.Count, bucket))
        {
            circle.Status = bucket.Status;
            circle.IsUnacknowledged = unacknowledged;
            if (!unacknowledged)
                circle.IsJiggleCause = false;
            return;
        }

        circle.Count = bucket.TotalCount;
        circle.Status = bucket.Status;
        circle.IsUnacknowledged = unacknowledged;
        if (!unacknowledged)
            circle.IsJiggleCause = false;
    }

    private static bool Keep(int previous, AlertBucket bucket) =>
        previous > 0 && bucket.TotalCount == 0 && !string.IsNullOrWhiteSpace(bucket.Status);

    private static string ShortPollError(string? message)
    {
        if (ServiceNowException.IsQueryTooLong(message))
            return ServiceNowException.QueryTooLongMessage;
        return message ?? "";
    }

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
        UseQueueSortDefault();
        RefreshDashboard();
        OnPropertyChanged(nameof(ShowAssigneeColumn));
        OnPropertyChanged(nameof(ShowDaysAssigned));
        OnPropertyChanged(nameof(ShowStandardColumns));
    }

    private void UseQueueSortDefault()
    {
        if (_alwaysShowAssignee)
            return;
        if (SelectedQueue == AlertKind.AssignedToMe)
        {
            _sortColumn = LeadSortColumn.DaysAssigned;
            _sortDescending = true;
        }
        else
        {
            _sortColumn = null;
            _sortDescending = false;
        }
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
        ApplySort();
    }

    public string NumberHeader => SortHeader("Number", LeadSortColumn.Number);
    public string TitleHeader => SortHeader("Title", LeadSortColumn.Title);
    public string StateHeader => SortHeader("State", LeadSortColumn.State);
    public string AssigneeHeader => SortHeader("Assigned to", LeadSortColumn.Assignee);
    public string GroupHeader => SortHeader("Group", LeadSortColumn.Group);
    public string QueueHeader => SortHeader("Queue", LeadSortColumn.Queue);
    public string DaysAssignedHeader => SortHeader("Days assigned", LeadSortColumn.DaysAssigned);

    [RelayCommand]
    private void SortBy(string? column)
    {
        if (!Enum.TryParse<LeadSortColumn>(column, ignoreCase: true, out var parsed))
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

    public void PatchTicket(string sysId, string? title, string? state, string? assignee, string? assigneeId)
    {
        var id = sysId?.Trim() ?? "";
        if (id.Length == 0)
            return;
        foreach (var section in Sections)
        {
            foreach (var row in section.Rows)
            {
                if (row.SysId.Equals(id, StringComparison.OrdinalIgnoreCase))
                    row.Apply(title, state, assignee, assigneeId);
            }
        }
    }

    private void ApplySort()
    {
        NotifySortHeaders();
        if (_sortColumn is not LeadSortColumn column || DashboardRows.Count < 2)
            return;
        var sorted = DashboardRows.Select((row, index) => (row, index)).ToList();
        sorted.Sort((left, right) =>
        {
            var compared = LeadRowSort.Compare(left.row, right.row, column, _sortDescending);
            return compared != 0 ? compared : left.index.CompareTo(right.index);
        });
        for (var index = 0; index < sorted.Count; index++)
        {
            var current = DashboardRows.IndexOf(sorted[index].row);
            if (current != index)
                DashboardRows.Move(current, index);
        }
    }

    private string SortHeader(string title, LeadSortColumn column) =>
        _sortColumn == column ? title + (_sortDescending ? " ▼" : " ▲") : title;

    private void NotifySortHeaders()
    {
        OnPropertyChanged(nameof(NumberHeader));
        OnPropertyChanged(nameof(TitleHeader));
        OnPropertyChanged(nameof(StateHeader));
        OnPropertyChanged(nameof(AssigneeHeader));
        OnPropertyChanged(nameof(GroupHeader));
        OnPropertyChanged(nameof(QueueHeader));
        OnPropertyChanged(nameof(DaysAssignedHeader));
    }

    private void PaintSlaAssignees()
    {
        foreach (var section in Sections)
        {
            foreach (var row in section.Rows.ToArray())
            {
                if (row is null)
                    continue;
                row.HighlightHex = SlaAssigneeHex(row);
            }
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

    private readonly object _rowsGate = new();

    public void Replace(IReadOnlyList<AlertRecord> rows)
    {
        lock (_rowsGate)
        {
            var selectedId = Selected?.SysId;
            Rows.Clear();
            foreach (var row in rows ?? [])
                Rows.Add(AlertRow.From(row));
            Selected = selectedId is null ? null : Rows.FirstOrDefault(row => row.SysId == selectedId);
            OnPropertyChanged(nameof(Heading));
        }
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

public enum LeadSortColumn
{
    Number,
    Title,
    State,
    Assignee,
    Group,
    Queue,
    DaysAssigned
}

public static class LeadRowSort
{
    public static int Compare(AlertRow left, AlertRow right, LeadSortColumn column, bool descending)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var leftText = Text(left, column);
        var rightText = Text(right, column);
        var leftEmpty = string.IsNullOrWhiteSpace(leftText);
        var rightEmpty = string.IsNullOrWhiteSpace(rightText);
        if (leftEmpty || rightEmpty)
        {
            if (leftEmpty && rightEmpty)
                return 0;
            return leftEmpty ? 1 : -1;
        }

        var compared = CompareFilled(leftText, rightText, column);
        return descending ? -compared : compared;
    }

    private static int CompareFilled(string left, string right, LeadSortColumn column)
    {
        if (column == LeadSortColumn.Number && TryDigits(left, out var leftNumber) && TryDigits(right, out var rightNumber))
        {
            var numeric = leftNumber.CompareTo(rightNumber);
            return numeric != 0 ? numeric : string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        }

        if (AlertClassifier.TryParseInstant(left, out var leftWhen) && AlertClassifier.TryParseInstant(right, out var rightWhen))
            return leftWhen.CompareTo(rightWhen);

        if (decimal.TryParse(left, NumberStyles.Number, CultureInfo.InvariantCulture, out var leftAmount)
            && decimal.TryParse(right, NumberStyles.Number, CultureInfo.InvariantCulture, out var rightAmount))
            return leftAmount.CompareTo(rightAmount);

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryDigits(string text, out long value)
    {
        var digits = new string(text.Where(char.IsDigit).ToArray());
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value) && digits.Length > 0;
    }

    private static string Text(AlertRow row, LeadSortColumn column) => column switch
    {
        LeadSortColumn.Number => row.Number,
        LeadSortColumn.Title => row.Title,
        LeadSortColumn.State => row.State,
        LeadSortColumn.Assignee => row.AssigneeLabel,
        LeadSortColumn.Group => row.Group,
        LeadSortColumn.Queue => row.QueueLabel,
        LeadSortColumn.DaysAssigned => row.DaysAssigned,
        _ => ""
    };
}

public sealed class AlertRow : INotifyPropertyChanged
{
    private string _highlightHex = "";
    private string _title = "";
    private string _state = "";
    private string _assignee = "";
    private string _assignedToSysId = "";

    public required AlertKind Kind { get; init; }
    public required DeskSection Section { get; init; }
    public required string SysId { get; init; }
    public required string Number { get; init; }

    public required string Title
    {
        get => _title;
        set => Assign(ref _title, value, nameof(Title));
    }

    public required string State
    {
        get => _state;
        set => Assign(ref _state, value, nameof(State));
    }

    public required string Group { get; init; }
    public required string Location { get; init; }
    public required string Updated { get; init; }

    /// <summary>When this ticket was assigned to the signed-in user. Blank when that time is unknown.</summary>
    public string AssignedOn { get; init; } = "";

    public string DaysAssigned => AssignmentAge.Format(AssignedOn, DateTime.Today);

    public string Assignee
    {
        get => _assignee;
        set
        {
            if (!Assign(ref _assignee, value, nameof(Assignee)))
                return;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AssigneeLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AssignedLine)));
        }
    }

    public string AssignedToSysId
    {
        get => _assignedToSysId;
        set => Assign(ref _assignedToSysId, value, nameof(AssignedToSysId));
    }

    public void Apply(string? title, string? state, string? assignee, string? assigneeId)
    {
        if (title is not null)
            Title = title;
        if (state is not null)
            State = state;
        if (assigneeId is null)
            return;
        AssignedToSysId = assigneeId;
        if (string.IsNullOrEmpty(assigneeId) || !string.IsNullOrWhiteSpace(assignee))
            Assignee = assignee ?? "";
    }

    private bool Assign(ref string field, string? value, string name)
    {
        var next = value ?? "";
        if (string.Equals(field, next, StringComparison.Ordinal))
            return false;
        field = next;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

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
        AssignedToSysId = record.AssignedToSysId,
        AssignedOn = record.AssignedOn
    };
}

public readonly record struct NotificationTarget(DeskSection Section, string SysId);
