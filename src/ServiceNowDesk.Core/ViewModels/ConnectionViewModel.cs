using CommunityToolkit.Mvvm.ComponentModel;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.ViewModels;

public sealed record AuthChoice(ServiceNowAuthMode Mode, string Label);

public partial class ConnectionViewModel : ObservableObject
{
    public IReadOnlyList<AuthChoice> AuthChoices { get; } =
    [
        new(ServiceNowAuthMode.Basic, "Username and password"),
        new(ServiceNowAuthMode.OAuthPassword, "OAuth password grant"),
        new(ServiceNowAuthMode.OAuthClientCredentials, "OAuth client credentials")
    ];

    [ObservableProperty] private string instanceUrl = "";
    [ObservableProperty] private ServiceNowAuthMode authMode = ServiceNowAuthMode.Basic;
    [ObservableProperty] private string username = "";
    [ObservableProperty] private string password = "";
    [ObservableProperty] private string clientId = "";
    [ObservableProperty] private string clientSecret = "";
    [ObservableProperty] private bool useSampleData;
    [ObservableProperty] private bool showOAuth;
    [ObservableProperty] private bool showUserPassword = true;

    partial void OnAuthModeChanged(ServiceNowAuthMode value) => SyncFlags();
    partial void OnUseSampleDataChanged(bool value) => SyncFlags();

    public DeskSettings BuildSettings() => new()
    {
        InstanceUrl = InstanceUrl.Trim(),
        AuthMode = AuthMode,
        Username = Username.Trim(),
        Password = Password,
        ClientId = ClientId.Trim(),
        ClientSecret = ClientSecret,
        UseSampleData = UseSampleData
    };

    public void Load(DeskSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        InstanceUrl = settings.InstanceUrl ?? "";
        AuthMode = settings.AuthMode;
        Username = settings.Username ?? "";
        Password = settings.Password ?? "";
        ClientId = settings.ClientId ?? "";
        ClientSecret = settings.ClientSecret ?? "";
        UseSampleData = settings.UseSampleData;
        SyncFlags();
    }

    private void SyncFlags()
    {
        ShowOAuth = !UseSampleData && AuthMode != ServiceNowAuthMode.Basic;
        ShowUserPassword = !UseSampleData && AuthMode != ServiceNowAuthMode.OAuthClientCredentials;
    }
}
