using System.Windows.Controls;
using System.Windows.Input;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Views;

public partial class DailyWorkView
{
    public DailyWorkView()
    {
        InitializeComponent();
    }

    private void Attend_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => OpenFromClick(sender, e);

    private void Cleared_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => OpenFromClick(sender, e);

    private void Arrived_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => OpenFromClick(sender, e);

    private void Attend_PreviewKeyDown(object sender, KeyEventArgs e) => OpenFromEnter(sender, e);

    private void Cleared_PreviewKeyDown(object sender, KeyEventArgs e) => OpenFromEnter(sender, e);

    private void Arrived_PreviewKeyDown(object sender, KeyEventArgs e) => OpenFromEnter(sender, e);

    private void NoteText_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None)
            return;
        if (DataContext is not DailyWorkViewModel daily)
            return;

        daily.AddNoteCommand.Execute(null);
        e.Handled = true;
    }

    private void OpenFromClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not DailyWorkViewModel daily)
            return;
        if (sender is not ListBox list)
            return;
        if (ItemsControl.ContainerFromElement(list, e.OriginalSource as System.Windows.DependencyObject) is not ListBoxItem item)
            return;
        if (item.DataContext is not DailyWorkRow row)
            return;

        daily.OpenCommand.Execute(row);
        e.Handled = true;
    }

    private void OpenFromEnter(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None)
            return;
        if (DataContext is not DailyWorkViewModel daily)
            return;
        if (sender is not ListBox list || list.SelectedItem is not DailyWorkRow row)
            return;

        daily.OpenCommand.Execute(row);
        e.Handled = true;
    }
}
