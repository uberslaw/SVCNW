using System.Windows.Input;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Views;

public partial class HardwareView
{
    public HardwareView()
    {
        InitializeComponent();
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

    private void RowSerial_LostFocus(object sender, System.Windows.RoutedEventArgs e) =>
        LookupRow(sender);

    private void LookupRow(object sender)
    {
        if (sender is not System.Windows.Controls.TextBox box || box.DataContext is not HardwareScanRow row)
            return;
        if (DataContext is HardwareWorkspaceViewModel hardware)
            hardware.LookupRowCommand.Execute(row);
    }
}
