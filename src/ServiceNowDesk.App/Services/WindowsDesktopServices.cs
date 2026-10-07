using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
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

    public string? SaveTextFile(string suggestedFileName, string filter, string contents, Encoding? encoding = null)
    {
        var dialog = new SaveFileDialog
        {
            FileName = string.IsNullOrWhiteSpace(suggestedFileName) ? "export.csv" : suggestedFileName,
            Filter = string.IsNullOrWhiteSpace(filter) ? "CSV (*.csv)|*.csv|All files (*.*)|*.*" : filter,
            AddExtension = true,
            DefaultExt = ".csv",
            OverwritePrompt = true,
            Title = "Export Work Effort"
        };
        if (dialog.ShowDialog() != true)
            return null;

        File.WriteAllText(dialog.FileName, contents ?? "", encoding ?? Encoding.UTF8);
        return dialog.FileName;
    }
}
