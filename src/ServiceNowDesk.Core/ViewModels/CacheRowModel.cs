using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ServiceNowDesk.ViewModels;

public sealed partial class CacheRowModel : ObservableObject
{
    public CacheRowModel(string key, string name)
    {
        Key = key;
        Name = name;
    }

    public string Key { get; }
    public string Name { get; }

    [ObservableProperty] private string status = "";
    [ObservableProperty] private string lastGoodText = "Last good download: never";
    [ObservableProperty] private string countText = "0 stored";
    [ObservableProperty] private string lastAttemptText = "";
    [ObservableProperty] private bool isFailed;
    [ObservableProperty] private bool isBusy;

    public DateTimeOffset? LastGoodAt { get; private set; }
    public int StoredCount { get; private set; }

    public void SetStoredCount(int count)
    {
        StoredCount = Math.Max(0, count);
        CountText = StoredCount.ToString(CultureInfo.CurrentCulture)
            + (StoredCount == 1 ? " stored" : " stored");
    }

    public void RememberGoodDownload(DateTimeOffset when)
    {
        if (when == default)
            return;
        LastGoodAt = when;
        LastGoodText = "Last good download: " + FormatStamp(when);
    }

    public void ReportSuccess(int staleCleared, int freshCount, DateTimeOffset? capturedAt = null, string? note = null)
    {
        if (capturedAt is { } at && at != default)
            RememberGoodDownload(at);
        else
            RememberGoodDownload(DateTimeOffset.UtcNow);

        LastAttemptText = "";
        IsFailed = false;
        var summary = "Cleared "
            + staleCleared.ToString(CultureInfo.CurrentCulture)
            + " stale · Downloaded "
            + freshCount.ToString(CultureInfo.CurrentCulture)
            + " fresh";
        if (!string.IsNullOrWhiteSpace(note))
            summary += " — " + note.Trim();
        Status = summary;
    }

    public void ReportSuccess(string? note = null)
    {
        ReportSuccess(0, 0, LastGoodAt ?? DateTimeOffset.UtcNow, note);
    }

    public void ReportCached(DateTimeOffset? capturedAt = null)
    {
        if (capturedAt is { } at && at != default)
            RememberGoodDownload(at);
        LastAttemptText = "";
        IsFailed = false;
        Status = "Using saved copy.";
    }

    public void ReportFailure(string message, DateTimeOffset? attemptedAt = null)
    {
        var when = attemptedAt is { } at && at != default ? at : DateTimeOffset.UtcNow;
        LastAttemptText = "Last attempt: " + FormatStamp(when);
        Status = string.IsNullOrWhiteSpace(message) ? "Could not refresh this cache." : message.Trim();
        IsFailed = true;
    }

    private static string FormatStamp(DateTimeOffset when) =>
        when.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
}
