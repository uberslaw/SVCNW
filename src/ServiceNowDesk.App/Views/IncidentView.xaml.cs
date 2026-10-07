using System.Windows;
using System.Windows.Documents;
using ServiceNowDesk.Models;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Views;

public partial class IncidentView
{
    public static readonly DependencyProperty EditorOnlyProperty =
        DependencyProperty.Register(nameof(EditorOnly), typeof(bool), typeof(IncidentView), new PropertyMetadata(false, (d, _) => ((IncidentView)d).ApplyEditorOnly()));

    public IncidentView()
    {
        InitializeComponent();
    }

    public bool EditorOnly
    {
        get => (bool)GetValue(EditorOnlyProperty);
        set => SetValue(EditorOnlyProperty, value);
    }

    private void ApplyEditorOnly() => EditorPane.Apply(EditorOnly, Layout, ListPane);

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
