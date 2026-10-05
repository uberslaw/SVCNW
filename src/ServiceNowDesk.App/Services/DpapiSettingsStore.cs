using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;

namespace ServiceNowDesk.Services;

public sealed class DpapiSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ServiceNowDesk");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public DeskSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new DeskSettings();

            var stored = JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(FilePath));
            if (stored is null)
                return new DeskSettings();

            return new DeskSettings
            {
                InstanceUrl = stored.InstanceUrl ?? "",
                AuthMode = stored.AuthMode,
                Username = stored.Username ?? "",
                Password = Unprotect(stored.ProtectedPassword),
                ClientId = stored.ClientId ?? "",
                ClientSecret = Unprotect(stored.ProtectedClientSecret),
                SessionCookie = Unprotect(stored.ProtectedSessionCookie),
                UserToken = Unprotect(stored.ProtectedUserToken),
                SessionCapturedAt = stored.SessionCapturedAt,
                SignedInAt = stored.SignedInAt,
                SessionExpiresAt = stored.SessionExpiresAt,
                UseSampleData = stored.UseSampleData,
                JiggleFrequency = string.IsNullOrWhiteSpace(stored.JiggleFrequency) ? "00:01:00" : stored.JiggleFrequency,
                JiggleDurationSeconds = stored.JiggleDurationSeconds ?? 2,
                MaximizeWhenJiggling = stored.MaximizeWhenJiggling ?? true,
                PlaySoundWhenJiggling = stored.PlaySoundWhenJiggling ?? false,
                PlaySoundOnAlertMetric = stored.PlaySoundOnAlertMetric ?? true,
                AlertSoundPath = stored.AlertSoundPath ?? "",
                WatchedGroupName = stored.WatchedGroupName,
                OfficeLocations = stored.OfficeLocations,
                NotificationPollSeconds = stored.NotificationPollSeconds ?? 60
            };
        }
        catch
        {
            return new DeskSettings();
        }
    }

    public void Save(DeskSettings settings)
    {
        Directory.CreateDirectory(Folder);
        var stored = new StoredSettings
        {
            InstanceUrl = settings.InstanceUrl,
            AuthMode = settings.AuthMode,
            Username = settings.Username,
            ProtectedPassword = Protect(settings.Password),
            ClientId = settings.ClientId,
            ProtectedClientSecret = Protect(settings.ClientSecret),
            ProtectedSessionCookie = Protect(settings.SessionCookie),
            ProtectedUserToken = Protect(settings.UserToken),
            SessionCapturedAt = settings.SessionCapturedAt,
            SignedInAt = settings.SignedInAt,
            SessionExpiresAt = settings.SessionExpiresAt,
            UseSampleData = settings.UseSampleData,
            JiggleFrequency = settings.JiggleFrequency,
            JiggleDurationSeconds = settings.JiggleDurationSeconds,
            MaximizeWhenJiggling = settings.MaximizeWhenJiggling,
            PlaySoundWhenJiggling = settings.PlaySoundWhenJiggling,
            PlaySoundOnAlertMetric = settings.PlaySoundOnAlertMetric,
            AlertSoundPath = settings.AlertSoundPath,
            WatchedGroupName = settings.WatchedGroupName,
            OfficeLocations = settings.OfficeLocations is null ? null : [.. settings.OfficeLocations],
            NotificationPollSeconds = settings.NotificationPollSeconds
        };
        File.WriteAllText(FilePath, JsonSerializer.Serialize(stored, JsonOptions));
    }

    private static string Protect(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        var bytes = Encoding.UTF8.GetBytes(value);
        return Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
    }

    private static string Unprotect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }

    private sealed class StoredSettings
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
        public bool? MaximizeWhenJiggling { get; set; }
        public bool? PlaySoundWhenJiggling { get; set; }
        public bool? PlaySoundOnAlertMetric { get; set; }
        public string? AlertSoundPath { get; set; }
        public string? WatchedGroupName { get; set; }
        public List<string>? OfficeLocations { get; set; }
        public int? NotificationPollSeconds { get; set; }
    }
}
