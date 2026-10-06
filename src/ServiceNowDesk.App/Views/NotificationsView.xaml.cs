using System.Windows.Controls;
using System.Windows.Input;
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
        if (ItemsControl.ContainerFromElement(list, e.OriginalSource as System.Windows.DependencyObject) is not ListBoxItem item)
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
}
