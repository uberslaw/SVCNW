using System.Text.Json;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.Services;

/// <summary>
/// The settings.json document written by the DPAPI store. Secrets are passed through the protect and unprotect functions.
/// The Leads password is not a field on this document.
/// </summary>
public static class DeskSettingsFile
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string Serialize(DeskSettings settings, Func<string?, string> protect)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(protect);
        return JsonSerializer.Serialize(Stored.From(settings, protect), JsonOptions);
    }

    public static DeskSettings Deserialize(string json, Func<string?, string> unprotect)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(unprotect);
        var stored = JsonSerializer.Deserialize<Stored>(json, JsonOptions);
        return stored is null ? new DeskSettings() : stored.ToSettings(unprotect);
    }

    private sealed class Stored
    {
        public string? InstanceUrl { get; set; }
        public ServiceNowAuthMode AuthMode { get; set; }
        public string? Username { get; set; }
        public string? ProtectedPassword { get; set; }
        public string? ClientId { get; set; }
        public string? ProtectedClientSecret { get; set; }
        public string? ProtectedSessionCookie { get; set; }
        public string? ProtectedUserToken { get; set; }
        public DateTimeOffset? SessionCapturedAt { get; set; }
        public DateTimeOffset? SignedInAt { get; set; }
        public DateTimeOffset? SessionExpiresAt { get; set; }
        public bool UseSampleData { get; set; }
        public string? JiggleFrequency { get; set; }
        public int? JiggleDurationSeconds { get; set; }
        public DesktopWidgetWhen? ShowDesktopWidget { get; set; }
        public JiggleWhen? JiggleWhen { get; set; }
        public bool? MaximizeWhenJiggling { get; set; }
        public bool? PlaySoundWhenJiggling { get; set; }
        public bool? PlaySoundOnAlertMetric { get; set; }
        public string? AlertSoundPath { get; set; }
        public string? WatchedGroupName { get; set; }
        public List<string>? OfficeLocations { get; set; }
        public List<string>? HardwareOfficeLocations { get; set; }
        public int? NotificationPollSeconds { get; set; }
        public bool? DownloadCacheOnLaunch { get; set; }
        public List<string>? EnabledHighlights { get; set; }
        public List<string>? LeadTeamMemberIds { get; set; }
        public bool LeadsEnabled { get; set; }

        public static Stored From(DeskSettings settings, Func<string?, string> protect) => new()
        {
            InstanceUrl = settings.InstanceUrl,
            AuthMode = settings.AuthMode,
            Username = settings.Username,
            ProtectedPassword = protect(settings.Password),
            ClientId = settings.ClientId,
            ProtectedClientSecret = protect(settings.ClientSecret),
            ProtectedSessionCookie = protect(settings.SessionCookie),
            ProtectedUserToken = protect(settings.UserToken),
            SessionCapturedAt = settings.SessionCapturedAt,
            SignedInAt = settings.SignedInAt,
            SessionExpiresAt = settings.SessionExpiresAt,
            UseSampleData = settings.UseSampleData,
            JiggleFrequency = settings.JiggleFrequency,
            JiggleDurationSeconds = settings.JiggleDurationSeconds,
            ShowDesktopWidget = settings.ShowDesktopWidget,
            JiggleWhen = settings.JiggleWhen,
            MaximizeWhenJiggling = settings.MaximizeWhenJiggling,
            PlaySoundWhenJiggling = settings.PlaySoundWhenJiggling,
            PlaySoundOnAlertMetric = settings.PlaySoundOnAlertMetric,
            AlertSoundPath = settings.AlertSoundPath,
            WatchedGroupName = settings.WatchedGroupName,
            OfficeLocations = settings.OfficeLocations is null ? null : [.. settings.OfficeLocations],
            HardwareOfficeLocations = settings.HardwareOfficeLocations is null ? null : [.. settings.HardwareOfficeLocations],
            NotificationPollSeconds = settings.NotificationPollSeconds,
            DownloadCacheOnLaunch = settings.DownloadCacheOnLaunch,
            EnabledHighlights = settings.EnabledHighlights is null ? null : [.. settings.EnabledHighlights],
            LeadTeamMemberIds = settings.LeadTeamMemberIds is null ? [] : [.. settings.LeadTeamMemberIds],
            LeadsEnabled = settings.LeadsEnabled
        };

        public DeskSettings ToSettings(Func<string?, string> unprotect) => new()
        {
            InstanceUrl = InstanceUrl ?? "",
            AuthMode = AuthMode,
            Username = Username ?? "",
            Password = unprotect(ProtectedPassword),
            ClientId = ClientId ?? "",
            ClientSecret = unprotect(ProtectedClientSecret),
            SessionCookie = unprotect(ProtectedSessionCookie),
            UserToken = unprotect(ProtectedUserToken),
            SessionCapturedAt = SessionCapturedAt,
            SignedInAt = SignedInAt,
            SessionExpiresAt = SessionExpiresAt,
            UseSampleData = UseSampleData,
            JiggleFrequency = string.IsNullOrWhiteSpace(JiggleFrequency) ? "00:01:00" : JiggleFrequency,
            JiggleDurationSeconds = JiggleDurationSeconds ?? 2,
            ShowDesktopWidget = ShowDesktopWidget ?? DesktopWidgetWhen.WhileOpen,
            JiggleWhen = JiggleWhen ?? ServiceNowDesk.Models.JiggleWhen.Persistent,
            MaximizeWhenJiggling = MaximizeWhenJiggling ?? true,
            PlaySoundWhenJiggling = PlaySoundWhenJiggling ?? false,
            PlaySoundOnAlertMetric = PlaySoundOnAlertMetric ?? true,
            AlertSoundPath = AlertSoundPath ?? "",
            WatchedGroupName = WatchedGroupName,
            OfficeLocations = OfficeLocations,
            HardwareOfficeLocations = HardwareOfficeLocations is null ? null : [.. HardwareOfficeLocations],
            NotificationPollSeconds = NotificationPollSeconds ?? 60,
            DownloadCacheOnLaunch = DownloadCacheOnLaunch ?? true,
            EnabledHighlights = EnabledHighlights is null ? null : [.. EnabledHighlights],
            LeadTeamMemberIds = LeadTeamMemberIds is null ? [] : [.. LeadTeamMemberIds],
            LeadsEnabled = LeadsEnabled
        };
    }
}
