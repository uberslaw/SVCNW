using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ServiceNowDesk.ViewModels;
using ServiceNowDesk.WorkEffort;

namespace ServiceNowDesk.Views;

public partial class LeadsView
{
    public LeadsView()
    {
        InitializeComponent();
    }

    private void Rows_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not LeadsViewModel leads)
            return;
        if (sender is not ListBox list)
            return;
        if (ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) is not ListBoxItem item)
            return;
        if (item.DataContext is not AlertRow row)
            return;

        leads.Board.OpenCommand.Execute(row);
        e.Handled = true;
    }

    private void Rows_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None)
            return;
        if (DataContext is not LeadsViewModel leads)
            return;
        if (sender is not ListBox list || list.SelectedItem is not AlertRow row)
            return;

        leads.Board.OpenCommand.Execute(row);
        e.Handled = true;
    }

    private void EffortName_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not LeadsViewModel leads)
            return;
        if (sender is not FrameworkElement { DataContext: WorkEffortRow row })
            return;
        leads.WorkEffort.ShowPersonDetail(row);
        e.Handled = true;
    }

    private void EffortCell_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not LeadsViewModel leads)
            return;
        if (sender is not FrameworkElement { DataContext: WorkEffortRow row, Tag: string column })
            return;
        leads.WorkEffort.ShowCellDetail(row, column);
        e.Handled = true;
    }

    private void EffortCreditNumber_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not LeadsViewModel leads)
            return;
        if (sender is not FrameworkElement { DataContext: WorkEffortCredit credit })
            return;
        leads.WorkEffort.OpenCreditCommand.Execute(credit);
        e.Handled = true;
    }
}
