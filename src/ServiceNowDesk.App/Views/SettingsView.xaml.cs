using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Views;

public partial class SettingsView : UserControl
{
    private bool _syncing;
    private ConnectionViewModel? _connection;

    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => SyncLeadsPassword();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => SyncLeadsPassword();

    private void LeadsPasswordInput_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || DataContext is not MainViewModel main)
            return;
        main.Connection.LeadsPassword = LeadsPasswordInput.Password;
    }

    private void SyncLeadsPassword()
    {
        if (DataContext is not MainViewModel main)
            return;
        WatchConnection(main.Connection);
        if (LeadsPasswordInput.Password == main.Connection.LeadsPassword)
            return;
        _syncing = true;
        LeadsPasswordInput.Password = main.Connection.LeadsPassword;
        _syncing = false;
    }

    private void WatchConnection(ConnectionViewModel connection)
    {
        if (ReferenceEquals(_connection, connection))
            return;
        if (_connection is not null)
            _connection.PropertyChanged -= OnConnectionPropertyChanged;
        _connection = connection;
        _connection.PropertyChanged += OnConnectionPropertyChanged;
    }

    private void OnConnectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ConnectionViewModel.LeadsPassword))
            return;
        SyncLeadsPassword();
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
