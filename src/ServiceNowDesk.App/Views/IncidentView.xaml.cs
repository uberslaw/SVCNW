using System.Windows;
using System.Windows.Documents;
using ServiceNowDesk.Models;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Views;

public partial class IncidentView
{
    public IncidentView()
    {
        InitializeComponent();
    }

    private void OpenAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Hyperlink link || link.DataContext is not AttachmentSummary attachment)
            return;
        if (DataContext is not RecordWorkspaceViewModel workspace)
            return;
        if (workspace.OpenAttachmentCommand.CanExecute(attachment))
            workspace.OpenAttachmentCommand.Execute(attachment);
    }
}
