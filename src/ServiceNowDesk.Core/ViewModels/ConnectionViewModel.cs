using CommunityToolkit.Mvvm.ComponentModel;
using ServiceNowDesk.Alerts;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.ViewModels;

public sealed record AuthChoice(ServiceNowAuthMode Mode, string Label);

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
    [ObservableProperty] private bool useSampleData;
    [ObservableProperty] private bool showOAuth;
    [ObservableProperty] private bool showUserPassword = true;
    [ObservableProperty] private bool showBrowserSignIn;
    [ObservableProperty] private string browserSessionStatus = "No browser sign-in yet.";

    public NotificationPreferences Notifications { get; private set; } = NotificationPreferences.From(new DeskSettings());

    partial void OnAuthModeChanged(ServiceNowAuthMode value) => SyncFlags();
    partial void OnUseSampleDataChanged(bool value) => SyncFlags();
    partial void OnSessionCookieChanged(string value) => UpdateBrowserStatus();
    partial void OnSessionCapturedAtChanged(DateTimeOffset? value) => UpdateBrowserStatus();

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
            UseSampleData = UseSampleData
        };
        Notifications.ApplyTo(settings);
        return settings;
    }

    public void RememberNotifications(NotificationPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        Notifications = preferences.Copy();
    }

    public void Load(DeskSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        InstanceUrl = settings.InstanceUrl ?? "";
        AuthMode = settings.AuthMode;
        Username = settings.Username ?? "";
        Password = settings.Password ?? "";
        ClientId = settings.ClientId ?? "";
        ClientSecret = settings.ClientSecret ?? "";
        SessionCookie = settings.SessionCookie ?? "";
        UserToken = settings.UserToken ?? "";
        SessionCapturedAt = settings.SessionCapturedAt;
        UseSampleData = settings.UseSampleData;
        Notifications = NotificationPreferences.From(settings);
        SyncFlags();
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
