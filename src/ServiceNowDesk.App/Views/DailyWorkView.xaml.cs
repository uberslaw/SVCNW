using System.Windows;
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

    private void NewUnassigned_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => OpenFromClick(sender, e);

    private void Attend_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => OpenFromClick(sender, e);

    private void Cleared_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => OpenFromClick(sender, e);

    private void Arrived_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => OpenFromClick(sender, e);

    private void NewUnassigned_PreviewKeyDown(object sender, KeyEventArgs e) => OpenFromEnter(sender, e);

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

    private void NotPartOfMyQueue_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DailyWorkViewModel daily)
            return;
        if (sender is not MenuItem menu)
            return;

        var row = menu.DataContext as DailyWorkRow
            ?? (menu.Parent as ContextMenu)?.PlacementTarget is FrameworkElement target
                ? target.DataContext as DailyWorkRow
                : null;
        if (row is null && menu.Parent is ContextMenu context && context.PlacementTarget is ListBoxItem item)
            row = item.DataContext as DailyWorkRow;
        if (row is null)
            return;

        var list = menu.Tag as string ?? DailyWorkViewModel.UnassignedListName;
        var reason = PromptReason(row.Number);
        if (reason is null)
            return;

        daily.NotPartOfMyQueue(row, reason, list);
        e.Handled = true;
    }

    private static string? PromptReason(string number)
    {
        var dialog = new Window
        {
            Title = "Not part of my queue",
            Width = 420,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false
        };
        if (Application.Current?.MainWindow is { IsVisible: true } owner)
            dialog.Owner = owner;

        var box = new TextBox { Margin = new Thickness(0, 8, 0, 0), MaxLength = 240 };
        var ok = new Button
        {
            Content = "Remove",
            IsDefault = true,
            MinWidth = 88,
            Margin = new Thickness(0, 0, 8, 0),
            Style = dialog.TryFindResource("PrimaryButton") as Style
        };
        var cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            MinWidth = 88,
            Style = dialog.TryFindResource("SecondaryButton") as Style
        };
        string? result = null;
        ok.Click += (_, _) =>
        {
            result = box.Text ?? "";
            dialog.DialogResult = true;
        };
        cancel.Click += (_, _) => dialog.DialogResult = false;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
            Children = { ok, cancel }
        };
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                new TextBlock
                {
                    Text = "Optional reason for removing " + (string.IsNullOrWhiteSpace(number) ? "this ticket" : number) + " from Daily Work today:",
                    TextWrapping = TextWrapping.Wrap
                },
                box,
                buttons
            }
        };
        box.Focus();
        return dialog.ShowDialog() == true ? result ?? "" : null;
    }

    private void OpenFromClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not DailyWorkViewModel daily)
            return;
        if (sender is not ListBox list)
            return;
        if (ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) is not ListBoxItem item)
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
