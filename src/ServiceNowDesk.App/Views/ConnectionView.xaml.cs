using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ServiceNowDesk.Models;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Views;

public partial class ConnectionView
{
    private bool _syncing;
    private bool _userChoseSignInMethod;

    public TextBlock SignInMethodCaptionControl => SignInMethodCaption;

    public ComboBox SignInMethodComboControl => SignInMethodCombo;

    public Button SignInBrowserButtonControl => SignInBrowserButton;

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

    private void SignInMethod_DropDownOpened(object sender, EventArgs e) => _userChoseSignInMethod = true;

    private void SignInMethod_PreviewKeyDown(object sender, KeyEventArgs e) => _userChoseSignInMethod = true;

    private void SignInMethod_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_userChoseSignInMethod || DataContext is not MainViewModel main)
            return;
        if (SignInMethodCombo.SelectedValue is ServiceNowAuthMode mode)
            main.Guided.NotifyAuthMode(mode);
    }

    private void SignInMethod_DropDownClosed(object sender, EventArgs e)
    {
        if (!_userChoseSignInMethod || DataContext is not MainViewModel main)
            return;
        main.Guided.ConfirmSignInMethod();
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
