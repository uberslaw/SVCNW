using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceNowDesk.Alerts;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.ViewModels;

public partial class NotificationSettingsViewModel : ObservableObject
{
    private NotificationPreferences _committed = NotificationPreferences.From(new DeskSettings());
    private bool _loading;

    public NotificationSettingsViewModel()
    {
        Load(_committed);
    }

    public NotificationPreferences Committed => _committed.Copy();

    public event EventHandler? SettingsChanged;

    /// <summary>
    /// The slider moved. The speed is already on <see cref="Committed"/> and <see cref="ActiveJiggleSpeed"/>.
    /// This does not restart the alert poll; only the next strip shake uses the new speed.
    /// </summary>
    public event EventHandler? JiggleSpeedChanged;

    [ObservableProperty] private string settingsMessage = "";
    [ObservableProperty] private string frequencyText = NotificationPreferences.DefaultFrequency;
    [ObservableProperty] private string durationText = NotificationPreferences.DefaultDurationSeconds.ToString(CultureInfo.InvariantCulture);
    [ObservableProperty] private double jiggleSpeed = JiggleMotion.DefaultMovesPerSecond;
    [ObservableProperty] private string pollSecondsText = NotificationPreferences.DefaultPollSeconds.ToString(CultureInfo.InvariantCulture);
    [ObservableProperty] private DesktopWidgetWhen showDesktopWidget = DesktopWidgetWhen.WhileOpen;
    [ObservableProperty] private JiggleWhen jiggleWhen = JiggleWhen.Persistent;
    [ObservableProperty] private bool maximizeWhenJiggling = true;
    [ObservableProperty] private bool playSoundWhenJiggling;
    [ObservableProperty] private bool playSoundOnAlertMetric = true;
    [ObservableProperty] private string soundPath = "";
    [ObservableProperty] private string groupNameText = NotificationPreferences.DefaultGroupName;
    [ObservableProperty] private string locationsText = string.Join(Environment.NewLine, NotificationPreferences.DefaultLocations);
    [ObservableProperty] private string activeFrequency = NotificationPreferences.DefaultFrequency;
    [ObservableProperty] private int activeDurationSeconds = NotificationPreferences.DefaultDurationSeconds;
    [ObservableProperty] private double activeJiggleSpeed = JiggleMotion.DefaultMovesPerSecond;
    [ObservableProperty] private DesktopWidgetWhen activeShowDesktopWidget = DesktopWidgetWhen.WhileOpen;
    [ObservableProperty] private JiggleWhen activeJiggleWhen = JiggleWhen.Persistent;
    [ObservableProperty] private bool activeMaximizeWhenJiggling = true;
    [ObservableProperty] private bool activePlaySoundWhenJiggling;
    [ObservableProperty] private bool activePlaySoundOnAlertMetric = true;
    [ObservableProperty] private string activeSoundPath = "";

    public TimeSpan ActiveJiggleInterval =>
        NotificationPreferences.TryParseFrequency(ActiveFrequency, out var frequency)
            ? frequency
            : TimeSpan.FromMinutes(1);

    public void Load(NotificationPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        _loading = true;
        _committed = preferences.Copy();
        CopyCommittedToDraft();
        PublishActive();
        SettingsMessage = "";
        _loading = false;
    }

    public void Load(DeskSettings settings) => Load(NotificationPreferences.From(settings));

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

        next.JiggleSpeed = JiggleMotion.Snap(JiggleSpeed);

        if (NotificationPreferences.TryParsePollSeconds(PollSecondsText, out var pollSeconds))
            next.PollSeconds = pollSeconds;
        else
        {
            errors.Add("Check ServiceNow every (seconds) must be at least 15.");
            PollSecondsText = _committed.PollSeconds.ToString(CultureInfo.InvariantCulture);
        }

        next.ShowDesktopWidget = ShowDesktopWidget;
        next.JiggleWhen = JiggleWhen;
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

    public void SetSoundPath(string? path) => SoundPath = path?.Trim() ?? "";

    partial void OnJiggleSpeedChanged(double value)
    {
        var snapped = JiggleMotion.Snap(value);
        if (snapped != value)
        {
            JiggleSpeed = snapped;
            return;
        }

        if (_loading)
            return;

        if (_committed.JiggleSpeed == snapped && ActiveJiggleSpeed == snapped)
            return;

        _committed.JiggleSpeed = snapped;
        ActiveJiggleSpeed = snapped;
        JiggleSpeedChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CopyCommittedToDraft()
    {
        FrequencyText = _committed.JiggleFrequency;
        DurationText = _committed.JiggleDurationSeconds.ToString(CultureInfo.InvariantCulture);
        JiggleSpeed = _committed.JiggleSpeed;
        PollSecondsText = _committed.PollSeconds.ToString(CultureInfo.InvariantCulture);
        ShowDesktopWidget = _committed.ShowDesktopWidget;
        JiggleWhen = _committed.JiggleWhen;
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
        ActiveJiggleSpeed = _committed.JiggleSpeed;
        ActiveShowDesktopWidget = _committed.ShowDesktopWidget;
        ActiveJiggleWhen = _committed.JiggleWhen;
        ActiveMaximizeWhenJiggling = _committed.MaximizeWhenJiggling;
        ActivePlaySoundWhenJiggling = _committed.PlaySoundWhenJiggling;
        ActivePlaySoundOnAlertMetric = _committed.PlaySoundOnAlertMetric;
        ActiveSoundPath = _committed.AlertSoundPath;
    }
}
