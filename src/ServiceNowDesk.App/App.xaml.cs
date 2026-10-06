using System.Windows;
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
                MessageBox.Show(args.Exception.Message, "ServiceNow Desk");
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
            recentGroups: FileRecentAssignmentGroupStore.InApplicationData());
        var window = new MainWindow { DataContext = main };
        MainWindow = window;
        window.Show();
    }
}
