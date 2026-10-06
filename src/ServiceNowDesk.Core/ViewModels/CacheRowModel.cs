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
    [ObservableProperty] private bool isFailed;
    [ObservableProperty] private bool isBusy;

    public void ReportSuccess(string? note = null)
    {
        Status = string.IsNullOrWhiteSpace(note) ? "Refreshed." : note.Trim();
        IsFailed = false;
    }

    public void ReportFailure(string message)
    {
        Status = string.IsNullOrWhiteSpace(message) ? "Could not refresh this cache." : message.Trim();
        IsFailed = true;
    }
}
