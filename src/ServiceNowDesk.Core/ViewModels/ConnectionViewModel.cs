using CommunityToolkit.Mvvm.ComponentModel;
using ServiceNowDesk.Alerts;
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

    [ObservableProperty] private string instanceUrl = "";
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

    public event EventHandler? DownloadCachePreferenceChanged;

    private bool _loadingSettings;

    public NotificationPreferences Notifications { get; private set; } = NotificationPreferences.From(new DeskSettings());

    public HighlightPreferences Highlights { get; private set; } = HighlightPreferences.Default;

    partial void OnAuthModeChanged(ServiceNowAuthMode value) => SyncFlags();
    partial void OnUseSampleDataChanged(bool value) => SyncFlags();
    partial void OnSessionCookieChanged(string value) => UpdateBrowserStatus();
    partial void OnSessionCapturedAtChanged(DateTimeOffset? value) => UpdateBrowserStatus();
    partial void OnDownloadCacheOnLaunchChanged(bool value)
    {
        if (!_loadingSettings)
            DownloadCachePreferenceChanged?.Invoke(this, EventArgs.Empty);
    }

    public DeskSettings BuildSettings()
    {
        var settings = new DeskSettings
        {
            InstanceUrl = InstanceUrl.Trim(),
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
            DownloadCacheOnLaunch = DownloadCacheOnLaunch
        };
        Notifications.ApplyTo(settings);
        Highlights.ApplyTo(settings);
        return settings;
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

    public void Load(DeskSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _loadingSettings = true;
        InstanceUrl = settings.InstanceUrl ?? "";
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
        DownloadCacheOnLaunch = settings.DownloadCacheOnLaunch;
        Notifications = NotificationPreferences.From(settings);
        Highlights = HighlightPreferences.From(settings);
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

    private void UpdateBrowserStatus()
    {
        BrowserSessionStatus = SessionCapturedAt is DateTimeOffset captured && !string.IsNullOrWhiteSpace(SessionCookie)
            ? "Browser sign-in saved " + captured.ToLocalTime().ToString("g") + ". Connect uses that session until it expires."
            : "No browser sign-in yet.";
    }
}
