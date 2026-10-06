using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Views;

public partial class ConnectionView
{
    private bool _syncing;
    private ConnectionViewModel? _connection;

    public ConnectionView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => SyncSecrets();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => SyncSecrets();

    private void PasswordInput_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || DataContext is not MainViewModel main)
            return;
        main.Connection.Password = PasswordInput.Password;
    }

    private void SecretInput_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || DataContext is not MainViewModel main)
            return;
        main.Connection.ClientSecret = SecretInput.Password;
    }

    private void LeadsPasswordInput_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing || DataContext is not MainViewModel main)
            return;
        main.Connection.LeadsPassword = LeadsPasswordInput.Password;
    }

    private void SyncSecrets()
    {
        if (DataContext is not MainViewModel main)
            return;
        WatchConnection(main.Connection);
        _syncing = true;
        if (PasswordInput.Password != main.Connection.Password)
            PasswordInput.Password = main.Connection.Password;
        if (SecretInput.Password != main.Connection.ClientSecret)
            SecretInput.Password = main.Connection.ClientSecret;
        if (LeadsPasswordInput.Password != main.Connection.LeadsPassword)
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
        if (e.PropertyName != nameof(ConnectionViewModel.LeadsPassword) || DataContext is not MainViewModel main)
            return;
        if (LeadsPasswordInput.Password == main.Connection.LeadsPassword)
            return;
        _syncing = true;
        LeadsPasswordInput.Password = main.Connection.LeadsPassword;
        _syncing = false;
    }
}
