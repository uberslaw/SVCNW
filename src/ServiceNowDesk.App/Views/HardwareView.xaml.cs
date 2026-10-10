using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Views;

public partial class HardwareView
{
    public HardwareView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        HardwareList.LayoutUpdated += OnHardwareListLayoutUpdated;
        SyncFilterColumns();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        HardwareList.LayoutUpdated -= OnHardwareListLayoutUpdated;
    }

    private void OnHardwareListLayoutUpdated(object? sender, EventArgs e) =>
        SyncFilterColumns();

    /// <summary>
    /// Keep the external filter row aligned with resizable DataGrid columns.
    /// </summary>
    private void SyncFilterColumns()
    {
        var columns = HardwareList.Columns;
        var definitions = HardwareFilters.ColumnDefinitions;
        if (columns.Count != definitions.Count)
            return;

        for (var i = 0; i < columns.Count; i++)
        {
            var width = columns[i].ActualWidth;
            if (width <= 0)
                continue;
            var current = definitions[i].Width;
            if (current.IsAbsolute && Math.Abs(current.Value - width) < 0.5)
                continue;
            definitions[i].Width = new GridLength(width);
        }
    }

    private void ScanBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        if (DataContext is HardwareWorkspaceViewModel hardware)
            hardware.ReceiveScanCommand.Execute(null);
    }

    private void RowSerial_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        LookupRow(sender);
    }

    private void RowSerial_LostFocus(object sender, RoutedEventArgs e) =>
        LookupRow(sender);

    private void LookupRow(object sender)
    {
        if (sender is not TextBox box || box.DataContext is not HardwareScanRow row)
            return;
        if (DataContext is HardwareWorkspaceViewModel hardware)
            hardware.LookupRowCommand.Execute(row);
    }
}
