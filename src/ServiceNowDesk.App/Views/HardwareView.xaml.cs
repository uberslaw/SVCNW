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
        if (DataContext is HardwareWorkspaceViewModel hardware)
            hardware.ReceiveScanCommand.Execute(null);
        e.Handled = true;
    }
}
