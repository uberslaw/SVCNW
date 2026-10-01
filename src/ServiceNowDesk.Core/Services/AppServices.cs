using ServiceNowDesk.Models;

namespace ServiceNowDesk.Services;

public interface IDesktopServices
{
    void OpenUrl(string url);
    void CopyText(string text);
}

public interface ISettingsStore
{
    DeskSettings Load();
    void Save(DeskSettings settings);
}

public sealed record BrowserSignInResult(string CookieHeader, string UserToken);

public interface IBrowserSignIn
{
    Task<BrowserSignInResult> SignInAsync(Uri instanceUri, CancellationToken cancellationToken);
}

public sealed class BrowserSignInCanceledException : InvalidOperationException
{
    public BrowserSignInCanceledException()
        : base("Browser sign-in was canceled.")
    {
    }
}

public sealed class RecordingDesktopServices : IDesktopServices
{
    public List<string> OpenedUrls { get; } = [];
    public List<string> CopiedText { get; } = [];

    public void OpenUrl(string url) => OpenedUrls.Add(url);
    public void CopyText(string text) => CopiedText.Add(text);
}

public sealed class MemorySettingsStore : ISettingsStore
{
    public DeskSettings Current { get; private set; } = new();

    public DeskSettings Load() => Current;

    public void Save(DeskSettings settings) => Current = settings;
}
