using System.Text;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.WorkEffort;

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

    /// <summary>Opens copyable text, such as the Work Effort calculation log.</summary>
    void ShowCopyableText(string title, string text);
}

/// <summary>
/// Opens a ticket in its own window so several can stay open at once (Work Effort and Search).
/// </summary>
public interface ITicketPopOut
{
    void Show(WorkEffortCredit credit, IServiceNowClient client);

    /// <summary>Opens any desk ticket or knowledge article without changing the main window section.</summary>
    void ShowTicket(DeskSection section, string sysId, string number, string title, IServiceNowClient client);
}

public sealed class RecordingTicketPopOut : ITicketPopOut
{
    public List<WorkEffortCredit> Opened { get; } = [];

    public List<(DeskSection Section, string SysId, string Number, string Title)> OpenedTickets { get; } = [];

    public void Show(WorkEffortCredit credit, IServiceNowClient client)
    {
        ArgumentNullException.ThrowIfNull(credit);
        ArgumentNullException.ThrowIfNull(client);
        Opened.Add(credit);
        OpenedTickets.Add((credit.Section, credit.RecordSysId, credit.DisplayNumber, credit.Title ?? ""));
    }

    public void ShowTicket(DeskSection section, string sysId, string number, string title, IServiceNowClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        OpenedTickets.Add((section, sysId ?? "", number ?? "", title ?? ""));
    }
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
    public List<(string Title, string Text)> ShownText { get; } = [];

    public void OpenUrl(string url) => OpenedUrls.Add(url);
    public void CopyText(string text) => CopiedText.Add(text);
    public void OpenFile(string path) => OpenedFiles.Add(path);

    public void ShowCopyableText(string title, string text) => ShownText.Add((title ?? "", text ?? ""));

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
