using System.Diagnostics;
using System.Windows;
using ServiceNowDesk.Services;

namespace ServiceNowDesk.Services;

public sealed class WindowsDesktopServices : IDesktopServices
{
    public void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    public void CopyText(string text) => Clipboard.SetText(text);

    public void OpenFile(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
