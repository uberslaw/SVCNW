using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServiceNowDesk.Alerts;
using ServiceNowDesk.GuidedSetup;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.ViewModels;

public sealed record AuthChoice(ServiceNowAuthMode Mode, string Label)
{
    public override string ToString() => Label ?? "";
}

public partial class ConnectionViewModel : ObservableObject
{
    public IReadOnlyList<AuthChoice> AuthChoices { get; } =
    [
        new(ServiceNowAuthMode.Basic, "Username and password"),
        new(ServiceNowAuthMode.OAuthPassword, "OAuth password grant"),
        new(ServiceNowAuthMode.OAuthClientCredentials, "OAuth client credentials"),
        new(ServiceNowAuthMode.BrowserSession, "Browser sign-in (SSO)")
    ];

    [ObservableProperty] private string instanceUrl = DeskSettings.DefaultInstanceUrl;
    [ObservableProperty] private ServiceNowAuthMode authMode = ServiceNowAuthMode.Basic;
    [ObservableProperty] private string username = "";
    [ObservableProperty] private string password = "";
    [ObservableProperty] private string clientId = "";
    [ObservableProperty] private string clientSecret = "";
    [ObservableProperty] private string sessionCookie = "";
    [ObservableProperty] private string userToken = "";
    [ObservableProperty] private DateTimeOffset? sessionCapturedAt;
    [ObservableProperty] private DateTimeOffset? signedInAt;
    [ObservableProperty] private DateTimeOffset? sessionExpiresAt;
    [ObservableProperty] private bool useSampleData;
    [ObservableProperty] private bool showOAuth;
    [ObservableProperty] private bool showUserPassword = true;
    [ObservableProperty] private bool showBrowserSignIn;
    [ObservableProperty] private string browserSessionStatus = "No browser sign-in yet.";
    [ObservableProperty] private bool downloadCacheOnLaunch = true;
    [ObservableProperty] private string leadsPassword = "";
    [ObservableProperty] private bool leadsEnabled;
    [ObservableProperty] private string leadsAccessStatus = "";

    public event EventHandler? DownloadCachePreferenceChanged;

    /// <summary>Raised after Unlock or Hide Leads so the desk can save the flag and leave the page.</summary>
    public event EventHandler? LeadsAccessChanged;

    private bool _loadingSettings;
    private bool _syncingLaunchMaster;

    /// <summary>Per-cache launch download flags keyed like Settings cache rows (incidents, hardware, …).</summary>
    public Dictionary<string, bool> CacheDownloadOnLaunch { get; private set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public NotificationPreferences Notifications { get; private set; } = NotificationPreferences.From(new DeskSettings());

    public HighlightPreferences Highlights { get; private set; } = HighlightPreferences.Default;

    public List<string> LeadTeamMemberIds { get; private set; } = [];

    /// <summary>True after Save team. Checkboxes stay hidden across restart until Edit team.</summary>
    public bool LeadTeamSaved { get; private set; }

    /// <summary>True when the locked leads password is the mode currently in effect.</summary>
    public bool LeadsTeamLocked { get; private set; }

    /// <summary>Null when the hardware tab has no saved office override.</summary>
    public List<string>? HardwareOfficeLocations { get; private set; }

    /// <summary>True when <see cref="HardwareOfficeLocations"/> was set with Override office.</summary>
    public bool HardwareOfficeOverride { get; private set; }

    public GuidedSetupOfferChoice GuidedSetupOffer { get; private set; }

    public bool GuidedSetupFinished { get; private set; }

    partial void OnAuthModeChanged(ServiceNowAuthMode value) => SyncFlags();
    partial void OnUseSampleDataChanged(bool value) => SyncFlags();
    partial void OnSessionCookieChanged(string value) => UpdateBrowserStatus();
    partial void OnSessionCapturedAtChanged(DateTimeOffset? value) => UpdateBrowserStatus();
    partial void OnDownloadCacheOnLaunchChanged(bool value)
    {
        if (_loadingSettings || _syncingLaunchMaster)
            return;
        // Master checkbox selects or clears every individual launch toggle.
        foreach (var key in CacheDownloadOnLaunch.Keys.ToArray())
            CacheDownloadOnLaunch[key] = value;
        DownloadCachePreferenceChanged?.Invoke(this, EventArgs.Empty);
    }

    public void EnsureLaunchKeys(IEnumerable<string> keys)
    {
        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;
            var trimmed = key.Trim();
            if (!CacheDownloadOnLaunch.ContainsKey(trimmed))
                CacheDownloadOnLaunch[trimmed] = DownloadCacheOnLaunch;
        }
    }

    public void RememberCacheLaunch(string key, bool downloadOnLaunch)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;
        CacheDownloadOnLaunch[key.Trim()] = downloadOnLaunch;
        if (_loadingSettings)
            return;
        _syncingLaunchMaster = true;
        try
        {
            DownloadCacheOnLaunch = CacheDownloadOnLaunch.Count > 0 && CacheDownloadOnLaunch.Values.All(on => on);
        }
        finally
        {
            _syncingLaunchMaster = false;
        }

        DownloadCachePreferenceChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool LaunchDownloadEnabled(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return DownloadCacheOnLaunch;
        return CacheDownloadOnLaunch.TryGetValue(key.Trim(), out var on) ? on : DownloadCacheOnLaunch;
    }

    private static Dictionary<string, bool>? CopyLaunchMap(Dictionary<string, bool>? map) =>
        map is null || map.Count == 0
            ? null
            : new Dictionary<string, bool>(map, StringComparer.OrdinalIgnoreCase);

    public DeskSettings BuildSettings()
    {
        var settings = new DeskSettings
        {
            InstanceUrl = string.IsNullOrWhiteSpace(InstanceUrl) ? DeskSettings.DefaultInstanceUrl : InstanceUrl.Trim(),
            AuthMode = AuthMode,
            Username = Username.Trim(),
            Password = Password,
            ClientId = ClientId.Trim(),
            ClientSecret = ClientSecret,
            SessionCookie = SessionCookie,
            UserToken = UserToken,
            SessionCapturedAt = SessionCapturedAt,
            SignedInAt = SignedInAt,
            SessionExpiresAt = SessionExpiresAt,
            UseSampleData = UseSampleData,
            DownloadCacheOnLaunch = DownloadCacheOnLaunch,
            CacheDownloadOnLaunch = CopyLaunchMap(CacheDownloadOnLaunch)
        };
        Notifications.ApplyTo(settings);
        Highlights.ApplyTo(settings);
        settings.LeadTeamMemberIds = [.. LeadTeamMemberIds];
        settings.LeadTeamSaved = LeadTeamSaved;
        settings.LeadsEnabled = LeadsEnabled;
        settings.LeadsTeamLocked = LeadsTeamLocked;
        settings.HardwareOfficeLocations = HardwareOfficeOverride
            ? CopyHardwareOffices(HardwareOfficeLocations) ?? []
            : CopyHardwareOffices(HardwareOfficeLocations);
        settings.HardwareOfficeOverride = HardwareOfficeOverride;
        settings.GuidedSetupOffer = GuidedSetupOffer;
        settings.GuidedSetupFinished = GuidedSetupFinished;
        return settings;
    }

    public void RememberGuidedSetup(GuidedSetupOfferChoice choice, bool finished)
    {
        GuidedSetupOffer = choice;
        GuidedSetupFinished = finished;
    }

    [RelayCommand]
    public void UnlockLeads()
    {
        var entered = LeadsPassword;
        if (!LeadsAccess.Unlocks(entered))
        {
            if (string.IsNullOrEmpty(entered))
                return;

            LeadsAccessStatus = LeadsAccess.WrongPasswordMessage;
            return;
        }

        LeadsPassword = "";
        LeadsAccessStatus = "";
        LeadsEnabled = true;
        LeadsTeamLocked = LeadsAccess.IsLocked(entered);
        LeadsAccessChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public void HideLeads()
    {
        LeadsPassword = "";
        LeadsAccessStatus = "";
        LeadsEnabled = false;
        LeadsTeamLocked = false;
        LeadsAccessChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RememberNotifications(NotificationPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        Notifications = preferences.Copy();
    }

    public void RememberHighlights(HighlightPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        Highlights = preferences;
    }

    public void RememberLeadTeam(IEnumerable<string>? memberIds) =>
        LeadTeamMemberIds = (memberIds ?? [])
            .Select(id => id?.Trim() ?? "")
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public void RememberLeadTeamSaved(bool saved) => LeadTeamSaved = saved;

    public void RememberHardwareOffices(IReadOnlyList<string>? offices, bool overridden)
    {
        HardwareOfficeOverride = overridden;
        HardwareOfficeLocations = overridden
            ? CopyHardwareOffices(offices) ?? []
            : CopyHardwareOffices(offices);
    }

    public void Load(DeskSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _loadingSettings = true;
        InstanceUrl = string.IsNullOrWhiteSpace(settings.InstanceUrl)
            ? DeskSettings.DefaultInstanceUrl
            : settings.InstanceUrl.Trim();
        AuthMode = settings.AuthMode;
        Username = settings.Username ?? "";
        Password = settings.Password ?? "";
        ClientId = settings.ClientId ?? "";
        ClientSecret = settings.ClientSecret ?? "";
        SessionCookie = settings.SessionCookie ?? "";
        UserToken = settings.UserToken ?? "";
        SessionCapturedAt = settings.SessionCapturedAt;
        SignedInAt = settings.SignedInAt;
        SessionExpiresAt = settings.SessionExpiresAt;
        UseSampleData = settings.UseSampleData;
        CacheDownloadOnLaunch = settings.CacheDownloadOnLaunch is null
            ? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, bool>(settings.CacheDownloadOnLaunch, StringComparer.OrdinalIgnoreCase);
        // Prefer the per-cache map when present so the master checkbox matches the boxes.
        DownloadCacheOnLaunch = CacheDownloadOnLaunch.Count > 0
            ? CacheDownloadOnLaunch.Values.All(on => on)
            : settings.DownloadCacheOnLaunch;
        Notifications = NotificationPreferences.From(settings);
        Highlights = HighlightPreferences.From(settings);
        LeadTeamMemberIds = settings.LeadTeamMemberIds is null
            ? []
            : settings.LeadTeamMemberIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        LeadTeamSaved = settings.LeadTeamSaved;
        LeadsEnabled = settings.LeadsEnabled;
        LeadsTeamLocked = settings.LeadsEnabled && settings.LeadsTeamLocked;
        HardwareOfficeLocations = CopyHardwareOffices(settings.HardwareOfficeLocations);
        HardwareOfficeOverride = settings.HardwareOfficeOverride;
        GuidedSetupOffer = settings.GuidedSetupOffer;
        GuidedSetupFinished = settings.GuidedSetupFinished;
        LeadsPassword = "";
        LeadsAccessStatus = "";
        SyncFlags();
        _loadingSettings = false;
    }

    private void SyncFlags()
    {
        ShowOAuth = !UseSampleData && AuthMode is ServiceNowAuthMode.OAuthPassword or ServiceNowAuthMode.OAuthClientCredentials;
        ShowUserPassword = !UseSampleData && AuthMode is ServiceNowAuthMode.Basic or ServiceNowAuthMode.OAuthPassword;
        ShowBrowserSignIn = !UseSampleData && AuthMode == ServiceNowAuthMode.BrowserSession;
        UpdateBrowserStatus();
    }

    private static List<string>? CopyHardwareOffices(IReadOnlyList<string>? offices)
    {
        if (offices is null)
            return null;

        return offices
            .Select(name => name?.Trim() ?? "")
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void UpdateBrowserStatus()
    {
        BrowserSessionStatus = SessionCapturedAt is DateTimeOffset captured && !string.IsNullOrWhiteSpace(SessionCookie)
            ? "Browser sign-in saved " + captured.ToLocalTime().ToString("g") + ". Connect uses that session until it expires."
            : "No browser sign-in yet.";
    }
}
