using System.Windows;
using System.Windows.Input;
using ServiceNowDesk.Models;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
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
            Navigate(main, DeskSection.Requests, e);
        else if (ctrl && e.Key is Key.D3 or Key.NumPad3)
            Navigate(main, DeskSection.RequestedItems, e);
        else if (ctrl && e.Key is Key.D4 or Key.NumPad4)
            Navigate(main, DeskSection.Search, e);
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
}
