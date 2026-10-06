namespace ServiceNowDesk.Models;

public enum AssignmentScope
{
    Any,
    Mine,
    MyGroups,
    Unassigned
}

public enum ActivityFilter
{
    Any,
    Open,
    Closed
}

public enum ServiceNowAuthMode
{
    Basic,
    OAuthPassword,
    OAuthClientCredentials,
    BrowserSession
}

public enum JournalKind
{
    WorkNotes,
    Comments
}

public enum DeskSection
{
    Incidents,
    Requests,
    RequestedItems,
    WalkUps,
    Search,
    Knowledge,
    Catalog,
    Connection,
    Notifications,
    Settings
}

public sealed record Choice(string Value, string Label);

public sealed record PresetOption(AssignmentScope Assignment, ActivityFilter Activity, string Label, string? AssignmentClause = null);

public sealed record TicketQuery
{
    public string? Text { get; init; }
    public AssignmentScope Assignment { get; init; } = AssignmentScope.Any;
    public ActivityFilter Activity { get; init; } = ActivityFilter.Open;
    public string? AssignmentClause { get; init; }
    public string? ParentRequestId { get; init; }
    public string? ExtraClause { get; init; }
    public int Limit { get; init; } = 50;
    public int Offset { get; init; }
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int? TotalCount);

public readonly record struct ReferenceValue(string SysId, string Display)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(SysId);

    public static ReferenceValue Empty { get; } = new("", "");
}

public sealed record ReferenceSuggestion(string SysId, string Display, string Detail)
{
    public string UserName { get; init; } = "";
    public string Email { get; init; } = "";
}

public sealed record JournalEntry(string SysId, string Kind, string KindLabel, string Text, string Author, string CreatedDisplay);

public sealed record ApiActivity(DateTimeOffset Time, string Method, string Path, int StatusCode, long ElapsedMilliseconds);

public sealed record CurrentUser(string SysId, string Name, string UserName, string Email);

public sealed class DeskSettings
{
    public string InstanceUrl { get; set; } = "";
    public ServiceNowAuthMode AuthMode { get; set; } = ServiceNowAuthMode.Basic;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string SessionCookie { get; set; } = "";
    public string UserToken { get; set; } = "";
    public DateTimeOffset? SessionCapturedAt { get; set; }
    public DateTimeOffset? SignedInAt { get; set; }
    public DateTimeOffset? SessionExpiresAt { get; set; }
    public bool UseSampleData { get; set; }
    public string JiggleFrequency { get; set; } = "00:01:00";
    public int JiggleDurationSeconds { get; set; } = 2;
    public bool MaximizeWhenJiggling { get; set; } = true;
    public bool PlaySoundWhenJiggling { get; set; }
    public bool PlaySoundOnAlertMetric { get; set; } = true;
    public string AlertSoundPath { get; set; } = "";
    public string? WatchedGroupName { get; set; } = "Aus DT - Client Services";
    public List<string>? OfficeLocations { get; set; } =
    [
        "Brisbane",
        "Maroochydore",
        "Gold Coast",
        "Townsville",
        "Cairns"
    ];
    public int NotificationPollSeconds { get; set; } = 60;
}

public sealed class ServiceNowSession
{
    private ServiceNowSession(
        Uri instanceUri,
        ServiceNowAuthMode authMode,
        string username,
        string password,
        string clientId,
        string clientSecret,
        string sessionCookie,
        string userToken)
    {
        InstanceUri = instanceUri;
        AuthMode = authMode;
        Username = username;
        Password = password;
        ClientId = clientId;
        ClientSecret = clientSecret;
        SessionCookie = sessionCookie;
        UserToken = userToken;
    }

    public Uri InstanceUri { get; }
    public ServiceNowAuthMode AuthMode { get; }
    public string Username { get; }
    public string Password { get; }
    public string ClientId { get; }
    public string ClientSecret { get; }
    public string SessionCookie { get; }
    public string UserToken { get; }

    public static Uri NormalizeInstance(string? input)
    {
        var text = (input ?? "").Trim().TrimEnd('/');
        if (text.Length == 0)
            throw new ArgumentException("Enter the ServiceNow instance URL, for example https://company.service-now.com.");

        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("The instance URL must start with http:// or https://, for example https://company.service-now.com.");
        }

        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
    }

    public static ServiceNowSession FromSettings(DeskSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var uri = NormalizeInstance(settings.InstanceUrl);
        var username = settings.Username?.Trim() ?? "";
        var password = settings.Password ?? "";
        var clientId = settings.ClientId?.Trim() ?? "";
        var clientSecret = settings.ClientSecret ?? "";
        var sessionCookie = settings.SessionCookie ?? "";
        var userToken = settings.UserToken ?? "";

        switch (settings.AuthMode)
        {
            case ServiceNowAuthMode.Basic:
                if (username.Length == 0 || password.Length == 0)
                    throw new ArgumentException("Enter the user name and password for the ServiceNow account.");
                break;
            case ServiceNowAuthMode.OAuthPassword:
                if (username.Length == 0 || password.Length == 0 || clientId.Length == 0 || clientSecret.Length == 0)
                    throw new ArgumentException("OAuth password grant needs a user name, password, client ID, and client secret.");
                break;
            case ServiceNowAuthMode.OAuthClientCredentials:
                if (clientId.Length == 0 || clientSecret.Length == 0)
                    throw new ArgumentException("OAuth client credentials need a client ID and client secret.");
                break;
            case ServiceNowAuthMode.BrowserSession:
                if (sessionCookie.Length == 0 || userToken.Length == 0)
                    throw new ArgumentException("Sign in with the browser first. This instance uses company single sign-on, so a user name and password are not enough.");
                break;
            default:
                throw new ArgumentException("Choose a sign-in method.");
        }

        return new ServiceNowSession(uri, settings.AuthMode, username, password, clientId, clientSecret, sessionCookie, userToken);
    }
}
