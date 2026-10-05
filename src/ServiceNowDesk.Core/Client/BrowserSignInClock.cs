using ServiceNowDesk.Models;

namespace ServiceNowDesk.Client;

/// <summary>
/// Browser sign-in lasts until the token expiry or 24 hours after sign-in, whichever is earlier.
/// A token refresh replaces the token expiry and leaves the original sign-in time in place.
/// </summary>
public readonly record struct BrowserSignInClock
{
    public const int MaximumHours = 24;

    public const string ExpiredStatus =
        "The previous browser sign-in expired. You can sign in with the browser again.";

    public BrowserSignInClock(DateTimeOffset signedInAt, DateTimeOffset? tokenExpiresAt = null)
    {
        SignedInAtUtc = signedInAt.ToUniversalTime();
        TokenExpiresAtUtc = tokenExpiresAt?.ToUniversalTime();
    }

    public DateTimeOffset SignedInAtUtc { get; }

    public DateTimeOffset? TokenExpiresAtUtc { get; }

    public DateTimeOffset ExpiresAtUtc
    {
        get
        {
            var cap = SignedInAtUtc.AddHours(MaximumHours);
            if (TokenExpiresAtUtc is DateTimeOffset token && token < cap)
                return token;
            return cap;
        }
    }

    public bool IsExpiredAt(DateTimeOffset utcNow) => utcNow.ToUniversalTime() >= ExpiresAtUtc;

    public BrowserSignInClock RefreshToken(DateTimeOffset? refreshedTokenExpiresAt) =>
        new(SignedInAtUtc, refreshedTokenExpiresAt);

    public static BrowserSignInClock FromSignIn(DateTimeOffset signedInAt, int? expiresInSeconds = null, DateTimeOffset? absoluteExpiry = null)
    {
        DateTimeOffset? tokenExpires = null;
        if (expiresInSeconds is int seconds)
            tokenExpires = signedInAt.ToUniversalTime().AddSeconds(Math.Max(0, seconds));
        if (absoluteExpiry is DateTimeOffset absolute)
        {
            var absoluteUtc = absolute.ToUniversalTime();
            tokenExpires = tokenExpires is DateTimeOffset fromLifetime && fromLifetime < absoluteUtc
                ? fromLifetime
                : absoluteUtc;
        }

        return new BrowserSignInClock(signedInAt, tokenExpires);
    }

    public static bool IsSavedSessionExpired(DeskSettings settings, DateTimeOffset utcNow)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.UseSampleData || settings.AuthMode != ServiceNowAuthMode.BrowserSession)
            return false;

        var hasToken = !string.IsNullOrWhiteSpace(settings.SessionCookie)
            || !string.IsNullOrWhiteSpace(settings.UserToken);
        if (!hasToken)
            return false;

        var signedIn = settings.SignedInAt ?? settings.SessionCapturedAt;
        if (signedIn is null)
            return true;

        return new BrowserSignInClock(signedIn.Value, settings.SessionExpiresAt).IsExpiredAt(utcNow);
    }

    public static bool IsRejection(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is ServiceNowException snow)
        {
            if (snow.Message.Contains("browser sign-in expired", StringComparison.OrdinalIgnoreCase))
                return true;
            return ContainsInvalidGrant(snow.Message) || ContainsInvalidGrant(snow.Detail);
        }

        return exception.Message.Contains("browser sign-in expired", StringComparison.OrdinalIgnoreCase);
    }

    public static void Preserve(DeskSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.AuthMode != ServiceNowAuthMode.BrowserSession)
            return;

        var signedIn = settings.SignedInAt ?? settings.SessionCapturedAt;
        if (signedIn is null)
            return;

        var clock = new BrowserSignInClock(signedIn.Value, settings.SessionExpiresAt);
        settings.SignedInAt = clock.SignedInAtUtc;
        settings.SessionCapturedAt = clock.SignedInAtUtc;
        settings.SessionExpiresAt = clock.ExpiresAtUtc;
    }

    public static void ApplyTokenRefresh(DeskSettings settings, DateTimeOffset? refreshedTokenExpiresAt)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var signedIn = settings.SignedInAt ?? settings.SessionCapturedAt
            ?? throw new InvalidOperationException("Browser sign-in has no start time.");
        var clock = new BrowserSignInClock(signedIn).RefreshToken(refreshedTokenExpiresAt);
        settings.SignedInAt = clock.SignedInAtUtc;
        settings.SessionCapturedAt = clock.SignedInAtUtc;
        settings.SessionExpiresAt = clock.ExpiresAtUtc;
    }

    private static bool ContainsInvalidGrant(string? text) =>
        text?.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase) == true;
}
