using System.Windows;
using System.Windows.Controls;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Views;

public partial class ConnectionView
{
    private bool _syncing;

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

    private void SyncSecrets()
    {
        if (DataContext is not MainViewModel main)
            return;
        _syncing = true;
        if (PasswordInput.Password != main.Connection.Password)
            PasswordInput.Password = main.Connection.Password;
        if (SecretInput.Password != main.Connection.ClientSecret)
            SecretInput.Password = main.Connection.ClientSecret;
        _syncing = false;
    }
}
