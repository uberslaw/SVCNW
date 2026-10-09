using System.Windows;
using ServiceNowDesk.Alerts;
using ServiceNowDesk.Client;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk;

public partial class App : Application
{
    private int _reportingException;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            if (Interlocked.Exchange(ref _reportingException, 1) == 1)
                return;

            try
            {
                MessageBox.Show(Describe(args.Exception), "ServiceNow Desk");
            }
            finally
            {
                Interlocked.Exchange(ref _reportingException, 0);
            }
        };

        var main = new MainViewModel(
            new DpapiSettingsStore(),
            new WindowsDesktopServices(),
            new WebViewBrowserSignIn(),
            FileFormCatalogStore.InApplicationData(),
            FileIncidentTemplateStore.InApplicationData(),
            recentGroups: FileRecentAssignmentGroupStore.InApplicationData(),
            lists: FileDeskListStore.InApplicationData(),
            dailyWork: FileDailyWorkStore.InApplicationData(),
            personalTasks: FilePersonalTaskStore.InApplicationData(),
            hardwareCatalog: FileHardwareCatalogStore.InApplicationData(),
            queueDismissals: FileQueueDismissalStore.InApplicationData(),
            ticketPopOut: new WindowsTicketPopOut());
        var window = new MainWindow { DataContext = main };
        MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// Prefer the inner message when XAML wraps a cast failure as "Set connectionId threw an exception."
    /// </summary>
    internal static string Describe(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var parts = new List<string>();
        for (var current = error; current is not null; current = current.InnerException)
        {
            var text = current.Message?.Trim() ?? "";
            if (text.Length == 0)
                continue;
            if (parts.Count > 0 && string.Equals(parts[^1], text, StringComparison.Ordinal))
                continue;
            parts.Add(text);
        }

        return parts.Count == 0 ? error.GetType().Name : string.Join(" → ", parts);
    }
}
