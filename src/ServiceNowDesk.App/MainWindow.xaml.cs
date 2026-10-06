using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ServiceNowDesk.Models;
using ServiceNowDesk.ViewModels;
using ServiceNowDesk.Views;

namespace ServiceNowDesk;

public partial class MainWindow : Window
{
    private readonly AlertWidgetWindow _alerts = new();

    public MainWindow()
    {
        InitializeComponent();
        _alerts.Opened += (_, _) => OpenFromAlerts();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel main)
                _alerts.Attach(main.Notifications);
        };
        StateChanged += (_, _) => _alerts.SetMainMinimized(WindowState == WindowState.Minimized);
        Closed += (_, _) => _alerts.Shutdown();
        Loaded += async (_, _) =>
        {
            if (DataContext is MainViewModel main)
                await main.InitializeAsync();
        };
    }

    private MainViewModel? Model => DataContext as MainViewModel;

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Model is null)
            return;
        Model.RefreshActiveCommand.Execute(null);
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var main = Model;
        if (main is null)
            return;

        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        var ctrlShift = Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift);

        if (ctrl && e.Key is Key.K or Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key is Key.D1 or Key.NumPad1)
            Navigate(main, DeskSection.Incidents, e);
        else if (ctrl && e.Key is Key.D2 or Key.NumPad2)
            Navigate(main, DeskSection.RequestedItems, e);
        else if (ctrl && e.Key is Key.D3 or Key.NumPad3)
            Navigate(main, DeskSection.RequestedItems, e);
        else if (ctrl && e.Key is Key.D4 or Key.NumPad4)
            Navigate(main, DeskSection.Search, e);
        else if (ctrl && e.Key is Key.D7 or Key.NumPad7)
            Navigate(main, DeskSection.Knowledge, e);
        else if (ctrl && e.Key is Key.D8 or Key.NumPad8)
            Navigate(main, DeskSection.WalkUps, e);
        else if (ctrl && e.Key is Key.D9 or Key.NumPad9)
            Navigate(main, DeskSection.Notifications, e);
        else if (ctrl && e.Key is Key.D5 or Key.NumPad5)
            Navigate(main, DeskSection.Catalog, e);
        else if (ctrl && e.Key is Key.D6 or Key.NumPad6)
            Navigate(main, DeskSection.Connection, e);
        else if (ctrl && e.Key == Key.N)
        {
            main.NewActiveCommand.Execute(null);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.S)
        {
            main.SaveActiveCommand.Execute(null);
            e.Handled = true;
        }
        else if (ctrlShift && e.Key == Key.R)
        {
            main.ResolveActiveCommand.Execute(null);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Enter)
        {
            main.PostActiveCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            main.RefreshActiveCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && main.ResolvePanelOpen)
        {
            main.CancelActiveCommand.Execute(null);
            e.Handled = true;
        }
    }

    private static void Navigate(MainViewModel main, DeskSection section, KeyEventArgs e)
    {
        main.SelectedSection = section;
        e.Handled = true;
    }

    private void OpenFromAlerts()
    {
        if (DataContext is MainViewModel main)
            main.AcknowledgeNotifications();

        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        if (DataContext is MainViewModel model)
            model.SelectedSection = DeskSection.Notifications;
    }

    private void NotificationWidgetList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TryOpenWidgetRow(sender, e.OriginalSource as DependencyObject))
            e.Handled = true;
    }

    private void NotificationWidgetList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None)
            return;
        if (sender is not ListBox list || list.SelectedItem is not AlertRow row || Model is null)
            return;

        Model.Notifications.OpenCommand.Execute(row);
        e.Handled = true;
    }

    private bool TryOpenWidgetRow(object sender, DependencyObject? source)
    {
        if (Model is null || sender is not ListBox list)
            return false;
        if (ItemsControl.ContainerFromElement(list, source) is not ListBoxItem item)
            return false;
        if (item.DataContext is not AlertRow row)
            return false;

        Model.Notifications.OpenCommand.Execute(row);
        return true;
    }

    private void Documentation_Click(object sender, RoutedEventArgs e)
    {
        foreach (Window window in OwnedWindows)
        {
            if (window is not HelpWindow help)
                continue;
            help.Activate();
            return;
        }

        new HelpWindow { Owner = this }.Show();
    }
}
