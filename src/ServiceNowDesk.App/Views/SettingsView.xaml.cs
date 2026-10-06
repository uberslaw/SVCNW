using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void BrowseSound_Click(object sender, RoutedEventArgs e)
    {
        var settings = (sender as FrameworkElement)?.DataContext as NotificationSettingsViewModel;
        if (settings is null && DataContext is MainViewModel main)
            settings = main.NotificationSettings;
        if (settings is null)
            return;

        var dialog = new OpenFileDialog
        {
            Filter = "Wave audio (*.wav)|*.wav",
            CheckFileExists = true,
            Title = "Choose a notification sound"
        };
        if (!string.IsNullOrWhiteSpace(settings.SoundPath))
        {
            try
            {
                var directory = Path.GetDirectoryName(settings.SoundPath);
                if (!string.IsNullOrWhiteSpace(directory))
                    dialog.InitialDirectory = directory;
                dialog.FileName = Path.GetFileName(settings.SoundPath);
            }
            catch (ArgumentException)
            {
            }
        }

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            settings.SetSoundPath(dialog.FileName);
    }
}
