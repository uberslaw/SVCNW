using System.Text;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.Services;

public interface IDesktopServices
{
    void OpenUrl(string url);
    void CopyText(string text);
    void OpenFile(string path);

    /// <summary>
    /// Shows a save dialog (or writes to a temp path in tests) and stores the text.
    /// Returns the path written, or null when the user cancels.
    /// </summary>
    string? SaveTextFile(string suggestedFileName, string filter, string contents, Encoding? encoding = null);
}

public interface ISettingsStore
{
    DeskSettings Load();
    void Save(DeskSettings settings);
}

public sealed record BrowserSignInResult(string CookieHeader, string UserToken, DateTimeOffset? ExpiresAt = null, int? ExpiresInSeconds = null);

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
    public List<string> OpenedFiles { get; } = [];
    public List<string> SavedFiles { get; } = [];

    public void OpenUrl(string url) => OpenedUrls.Add(url);
    public void CopyText(string text) => CopiedText.Add(text);
    public void OpenFile(string path) => OpenedFiles.Add(path);

    public string? SaveTextFile(string suggestedFileName, string filter, string contents, Encoding? encoding = null)
    {
        _ = filter;
        var name = string.IsNullOrWhiteSpace(suggestedFileName) ? "export.txt" : Path.GetFileName(suggestedFileName);
        var path = Path.Combine(Path.GetTempPath(), "servicenow-desk-tests", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents ?? "", encoding ?? Encoding.UTF8);
        SavedFiles.Add(path);
        return path;
    }
}

public sealed class MemorySettingsStore : ISettingsStore
{
    public DeskSettings Current { get; private set; } = new();

    public DeskSettings Load() => Current;

    public void Save(DeskSettings settings) => Current = settings;
}
