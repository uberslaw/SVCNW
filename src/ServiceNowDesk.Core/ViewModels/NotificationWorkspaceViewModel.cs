using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceNowDesk.Alerts;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.ViewModels;

public partial class NotificationWorkspaceViewModel : ObservableObject
{
    private NotificationPreferences _committed = NotificationPreferences.From(new DeskSettings());

    public NotificationWorkspaceViewModel()
    {
        foreach (var kind in AlertCatalog.All)
        {
            Sections.Add(new AlertSectionModel(kind));
            Circles.Add(new AlertCircleModel(kind));
        }

        Load(_committed);
    }

    public ObservableCollection<AlertSectionModel> Sections { get; } = [];
    public ObservableCollection<AlertCircleModel> Circles { get; } = [];
    public ObservableCollection<AlertRow> WidgetItems { get; } = [];

    public NotificationPreferences Committed => _committed.Copy();

    public event EventHandler<AlertRow>? OpenRequested;
    public event EventHandler? SettingsChanged;
    public event EventHandler<AlertAttention>? Attention;

    [ObservableProperty] private bool anyUnacknowledged;
    [ObservableProperty] private string pollError = "";
    [ObservableProperty] private string lastChecked = "Not checked yet.";
    [ObservableProperty] private string settingsMessage = "";
    [ObservableProperty] private string frequencyText = NotificationPreferences.DefaultFrequency;
    [ObservableProperty] private string durationText = NotificationPreferences.DefaultDurationSeconds.ToString(CultureInfo.InvariantCulture);
    [ObservableProperty] private string pollSecondsText = NotificationPreferences.DefaultPollSeconds.ToString(CultureInfo.InvariantCulture);
    [ObservableProperty] private bool maximizeWhenJiggling = true;
    [ObservableProperty] private bool playSoundWhenJiggling;
    [ObservableProperty] private bool playSoundOnAlertMetric = true;
    [ObservableProperty] private string soundPath = "";
    [ObservableProperty] private string groupNameText = NotificationPreferences.DefaultGroupName;
    [ObservableProperty] private string locationsText = string.Join(Environment.NewLine, NotificationPreferences.DefaultLocations);
    [ObservableProperty] private string activeFrequency = NotificationPreferences.DefaultFrequency;
    [ObservableProperty] private int activeDurationSeconds = NotificationPreferences.DefaultDurationSeconds;
    [ObservableProperty] private bool activeMaximizeWhenJiggling = true;
    [ObservableProperty] private bool activePlaySoundWhenJiggling;
    [ObservableProperty] private bool activePlaySoundOnAlertMetric = true;
    [ObservableProperty] private string activeSoundPath = "";
    [ObservableProperty] private string widgetSummary = "No notifications";
    [ObservableProperty] private string newestTitle = "";
    [ObservableProperty] private bool isWidgetOpen;
    [ObservableProperty] private bool hasWidgetItems;
    [ObservableProperty] private bool widgetHasUnread;

    public TimeSpan ActiveJiggleInterval =>
        NotificationPreferences.TryParseFrequency(ActiveFrequency, out var frequency)
            ? frequency
            : TimeSpan.FromMinutes(1);

    public void Load(NotificationPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        _committed = preferences.Copy();
        CopyCommittedToDraft();
        PublishActive();
        SettingsMessage = "";
    }

    public void Load(DeskSettings settings) => Load(NotificationPreferences.From(settings));

    public void Apply(AlertSnapshot snapshot, AlertWatchState watch)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(watch);
        var decision = watch.Observe(snapshot.Counts);
        foreach (var section in Sections)
        {
            var bucket = snapshot.Bucket(section.Kind);
            section.Count = bucket.TotalCount;
            section.IsUnacknowledged = watch.IsUnacknowledged(section.Kind);
            section.Replace(bucket.Rows);
        }

        foreach (var circle in Circles)
        {
            circle.Count = snapshot.Count(circle.Kind);
            circle.IsUnacknowledged = watch.IsUnacknowledged(circle.Kind);
        }

        AnyUnacknowledged = watch.AnyUnacknowledged;
        PollError = "";
        LastChecked = "Last checked " + DateTime.Now.ToString("t", CultureInfo.CurrentCulture) + ".";
        if (decision.HasIncrease)
        {
            Attention?.Invoke(this, new AlertAttention
            {
                PlaySound = _committed.PlaySoundOnAlertMetric
            });
        }

        RefreshWidget();
    }

    public void RefreshAcknowledgement(AlertWatchState watch)
    {
        ArgumentNullException.ThrowIfNull(watch);
        foreach (var section in Sections)
            section.IsUnacknowledged = watch.IsUnacknowledged(section.Kind);
        foreach (var circle in Circles)
            circle.IsUnacknowledged = watch.IsUnacknowledged(circle.Kind);
        AnyUnacknowledged = watch.AnyUnacknowledged;
        RefreshWidget();
    }

    public void Clear()
    {
        foreach (var section in Sections)
        {
            section.Count = 0;
            section.IsUnacknowledged = false;
            section.Replace([]);
        }

        foreach (var circle in Circles)
        {
            circle.Count = 0;
            circle.IsUnacknowledged = false;
        }

        AnyUnacknowledged = false;
        PollError = "";
        LastChecked = "Not checked yet.";
        RefreshWidget();
    }

    public void NotePollError(string message) => PollError = message;

    [RelayCommand]
    private void Open(AlertRow? row)
    {
        if (row is null || row.Section == DeskSection.Knowledge)
            return;
        OpenRequested?.Invoke(this, row);
    }

    [RelayCommand]
    private void ToggleWidget() => IsWidgetOpen = !IsWidgetOpen;

    [RelayCommand]
    private void SaveSettings()
    {
        var next = _committed.Copy();
        var errors = new List<string>();
        if (NotificationPreferences.TryParseFrequency(FrequencyText, out var frequency))
            next.JiggleFrequency = frequency.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
        else
        {
            errors.Add("Jiggle frequency must be HH:MM:SS.");
            FrequencyText = _committed.JiggleFrequency;
        }

        if (NotificationPreferences.TryParseDuration(DurationText, out var duration))
            next.JiggleDurationSeconds = duration;
        else
        {
            errors.Add("Jiggle duration must be at least 1 second.");
            DurationText = _committed.JiggleDurationSeconds.ToString(CultureInfo.InvariantCulture);
        }

        if (NotificationPreferences.TryParsePollSeconds(PollSecondsText, out var pollSeconds))
            next.PollSeconds = pollSeconds;
        else
        {
            errors.Add("Check ServiceNow every (seconds) must be at least 15.");
            PollSecondsText = _committed.PollSeconds.ToString(CultureInfo.InvariantCulture);
        }

        next.MaximizeWhenJiggling = MaximizeWhenJiggling;
        next.PlaySoundWhenJiggling = PlaySoundWhenJiggling;
        next.PlaySoundOnAlertMetric = PlaySoundOnAlertMetric;
        next.AlertSoundPath = SoundPath?.Trim() ?? "";
        next.WatchedGroupName = GroupNameText?.Trim() ?? "";
        next.OfficeLocations = NotificationPreferences.ParseLocations(LocationsText).ToList();
        _committed = next;
        CopyCommittedToDraft();
        PublishActive();
        SettingsMessage = errors.Count == 0
            ? "Notification settings saved."
            : "Notification settings saved. " + string.Join(" ", errors) + " The previous value was kept.";
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetSoundPath(string? path)
    {
        SoundPath = path?.Trim() ?? "";
    }

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
    }

    private static DateTime UpdatedStamp(string updated)
    {
        if (DateTime.TryParse(updated, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
            return parsed;
        if (DateTime.TryParse(updated, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out parsed))
            return parsed;
        return DateTime.MinValue;
    }

    private void CopyCommittedToDraft()
    {
        FrequencyText = _committed.JiggleFrequency;
        DurationText = _committed.JiggleDurationSeconds.ToString(CultureInfo.InvariantCulture);
        PollSecondsText = _committed.PollSeconds.ToString(CultureInfo.InvariantCulture);
        MaximizeWhenJiggling = _committed.MaximizeWhenJiggling;
        PlaySoundWhenJiggling = _committed.PlaySoundWhenJiggling;
        PlaySoundOnAlertMetric = _committed.PlaySoundOnAlertMetric;
        SoundPath = _committed.AlertSoundPath;
        GroupNameText = _committed.WatchedGroupName;
        LocationsText = string.Join(Environment.NewLine, _committed.OfficeLocations);
    }

    private void PublishActive()
    {
        ActiveFrequency = _committed.JiggleFrequency;
        ActiveDurationSeconds = _committed.JiggleDurationSeconds;
        ActiveMaximizeWhenJiggling = _committed.MaximizeWhenJiggling;
        ActivePlaySoundWhenJiggling = _committed.PlaySoundWhenJiggling;
        ActivePlaySoundOnAlertMetric = _committed.PlaySoundOnAlertMetric;
        ActiveSoundPath = _committed.AlertSoundPath;
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
        AutomationName = AlertCatalog.AutomationName(kind, 0);
    }

    public AlertKind Kind { get; }
    public string Title { get; }

    [ObservableProperty] private int count;
    [ObservableProperty] private bool isUnacknowledged;
    [ObservableProperty] private string automationName;

    public bool IsVisible => Count > 0;

    partial void OnCountChanged(int value)
    {
        AutomationName = AlertCatalog.AutomationName(Kind, value);
        OnPropertyChanged(nameof(IsVisible));
    }
}

public sealed class AlertRow
{
    public required AlertKind Kind { get; init; }
    public required DeskSection Section { get; init; }
    public required string SysId { get; init; }
    public required string Number { get; init; }
    public required string Title { get; init; }
    public required string State { get; init; }
    public required string Group { get; init; }
    public required string Location { get; init; }
    public required string Updated { get; init; }

    public string TableLabel => Section switch
    {
        DeskSection.Incidents => "Incident",
        DeskSection.Requests => "Request",
        DeskSection.RequestedItems => "Request item",
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
        Updated = record.Updated
    };
}
