namespace ServiceNowDesk.Client;

public sealed record BrowserCookie(string Name, string Value, string Domain, string Path);

public static class BrowserSessionCookies
{
    public static bool HasServiceNowSession(IEnumerable<BrowserCookie> cookies) =>
        cookies.Any(cookie => IsSessionCookie(cookie.Name) && !string.IsNullOrWhiteSpace(cookie.Value));

    public static string BuildHeader(IEnumerable<BrowserCookie> cookies, Uri instance)
    {
        ArgumentNullException.ThrowIfNull(cookies);
        ArgumentNullException.ThrowIfNull(instance);

        var selected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cookie in cookies)
        {
            if (string.IsNullOrWhiteSpace(cookie.Name) || string.IsNullOrWhiteSpace(cookie.Value))
                continue;
            if (!AppliesTo(cookie.Domain, instance.Host))
                continue;
            if (HasHeaderBreak(cookie.Name) || HasHeaderBreak(cookie.Value))
                continue;
            selected[cookie.Name.Trim()] = cookie.Value.Trim();
        }

        if (!selected.Keys.Any(IsSessionCookie))
            throw new ArgumentException("ServiceNow did not return a signed-in session. Finish the company sign-in and try again.");

        return string.Join("; ", selected.Select(pair => pair.Key + "=" + pair.Value));
    }

    public static string NormalizeToken(string? token)
    {
        var value = (token ?? "").Trim();
        if (value.Length == 0 || value.Length > 8000 || HasHeaderBreak(value))
            throw new ArgumentException("ServiceNow did not return a session token. Finish the company sign-in and try again.");
        return value;
    }

    private static bool IsSessionCookie(string name) =>
        name.Equals("glide_user_session", StringComparison.OrdinalIgnoreCase)
        || name.Equals("glide_session_store", StringComparison.OrdinalIgnoreCase);

    private static bool AppliesTo(string? domain, string host)
    {
        var cookieDomain = (domain ?? "").Trim().TrimStart('.');
        if (cookieDomain.Length == 0)
            return true;

        return host.Equals(cookieDomain, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + cookieDomain, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasHeaderBreak(string value) =>
        value.Contains(';') || value.Contains('\r') || value.Contains('\n');
}
