using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Views;

public partial class HardwareView
{
    private HardwareWorkspaceViewModel? _hardware;

    public HardwareView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookHardware();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        HardwareList.LayoutUpdated += OnHardwareListLayoutUpdated;
        HardwareList.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(ColumnHeader_Click), true);
        HookHardware();
        SyncFilterColumns();
        SyncSortHeaders();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        HardwareList.LayoutUpdated -= OnHardwareListLayoutUpdated;
        HardwareList.RemoveHandler(ButtonBase.ClickEvent, new RoutedEventHandler(ColumnHeader_Click));
        HookHardware(detachOnly: true);
    }

    private void HookHardware(bool detachOnly = false)
    {
        if (_hardware is not null)
            _hardware.PropertyChanged -= Hardware_PropertyChanged;

        _hardware = detachOnly ? null : DataContext as HardwareWorkspaceViewModel;
        if (_hardware is not null)
        {
            _hardware.PropertyChanged += Hardware_PropertyChanged;
            SyncSortHeaders();
        }
    }

    private void Hardware_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null
            || e.PropertyName is nameof(HardwareWorkspaceViewModel.SerialHeader)
                or nameof(HardwareWorkspaceViewModel.ModelHeader)
                or nameof(HardwareWorkspaceViewModel.AssignedHeader)
                or nameof(HardwareWorkspaceViewModel.LocationHeader)
                or nameof(HardwareWorkspaceViewModel.StateHeader)
                or nameof(HardwareWorkspaceViewModel.SubstateHeader)
                or nameof(HardwareWorkspaceViewModel.CommentsHeader))
        {
            SyncSortHeaders();
        }
    }

    private void ColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
            return;
        var header = FindAncestor<DataGridColumnHeader>(source);
        if (header?.Column is null || DataContext is not HardwareWorkspaceViewModel hardware)
            return;

        var column = ColumnKey(header.Column);
        if (column.Length == 0)
            return;

        hardware.SortByCommand.Execute(column);
        e.Handled = true;
    }

    private void SyncSortHeaders()
    {
        if (DataContext is not HardwareWorkspaceViewModel hardware || HardwareList.Columns.Count < 7)
            return;

        HardwareList.Columns[0].Header = hardware.SerialHeader;
        HardwareList.Columns[1].Header = hardware.ModelHeader;
        HardwareList.Columns[2].Header = hardware.AssignedHeader;
        HardwareList.Columns[3].Header = hardware.LocationHeader;
        HardwareList.Columns[4].Header = hardware.StateHeader;
        HardwareList.Columns[5].Header = hardware.SubstateHeader;
        HardwareList.Columns[6].Header = hardware.CommentsHeader;
    }

    private static string ColumnKey(DataGridColumn column) => column.SortMemberPath switch
    {
        "SerialNumber" => nameof(HardwareSortColumn.Serial),
        "Model" => nameof(HardwareSortColumn.Model),
        "AssignedTo.Display" => nameof(HardwareSortColumn.Assigned),
        "Location.Display" => nameof(HardwareSortColumn.Location),
        "InstallStatusLabel" => nameof(HardwareSortColumn.State),
        "SubstatusLabel" => nameof(HardwareSortColumn.Substate),
        "Comments" => nameof(HardwareSortColumn.Comments),
        _ => ""
    };

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
                return match;
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
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
