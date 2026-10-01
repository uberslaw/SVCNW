using System.Windows;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "ServiceNow Desk");
            args.Handled = true;
        };

        var main = new MainViewModel(new DpapiSettingsStore(), new WindowsDesktopServices());
        var window = new MainWindow { DataContext = main };
        MainWindow = window;
        window.Show();
    }
}
