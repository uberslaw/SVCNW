using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Views;

public partial class NotificationsView
{
    public NotificationsView()
    {
        InitializeComponent();
    }

    private void Rows_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not NotificationWorkspaceViewModel notifications)
            return;
        if (sender is not ListBox list)
            return;
        if (ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) is not ListBoxItem item)
            return;
        if (item.DataContext is not AlertRow row)
            return;

        notifications.OpenCommand.Execute(row);
        e.Handled = true;
    }

    private void Rows_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None)
            return;
        if (DataContext is not NotificationWorkspaceViewModel notifications)
            return;
        if (sender is not ListBox list || list.SelectedItem is not AlertRow row)
            return;

        notifications.OpenCommand.Execute(row);
        e.Handled = true;
    }

    private void BrowseSound_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not NotificationWorkspaceViewModel notifications)
            return;

        var dialog = new OpenFileDialog
        {
            Filter = "Wave audio (*.wav)|*.wav",
            CheckFileExists = true,
            Title = "Choose a notification sound"
        };
        if (!string.IsNullOrWhiteSpace(notifications.SoundPath))
        {
            try
            {
                var directory = Path.GetDirectoryName(notifications.SoundPath);
                if (!string.IsNullOrWhiteSpace(directory))
                    dialog.InitialDirectory = directory;
                dialog.FileName = Path.GetFileName(notifications.SoundPath);
            }
            catch (ArgumentException)
            {
            }
        }

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            notifications.SetSoundPath(dialog.FileName);
    }
}
