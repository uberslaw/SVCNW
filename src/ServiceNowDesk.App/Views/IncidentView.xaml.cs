using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using ServiceNowDesk.Models;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Views;

public partial class IncidentView
{
    const double PreferredListWidth = 400;
    const double ColumnGutter = 16;
    const double MinimumEditorWidth = 240;

    bool _fittingList;
    bool _fittingForm;

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

    private void ApplyEditorOnly()
    {
        EditorPane.Apply(EditorOnly, Layout, ListPane);
        if (!EditorOnly)
            FitListColumn();
    }

    private void Layout_SizeChanged(object sender, SizeChangedEventArgs e) => FitListColumn();

    private void EditorScroll_SizeChanged(object sender, SizeChangedEventArgs e) => FitEditorForm();

    private void EditorScroll_ScrollChanged(object sender, ScrollChangedEventArgs e) => FitEditorForm();

    private void FitListColumn()
    {
        if (EditorOnly || _fittingList)
            return;

        var available = Layout.ActualWidth;
        if (available <= 0 || double.IsNaN(available) || double.IsInfinity(available))
            return;

        var list = Math.Min(PreferredListWidth, Math.Max(0, available - ColumnGutter - MinimumEditorWidth));
        if (ListColumn.Width.GridUnitType == GridUnitType.Pixel && Math.Abs(ListColumn.Width.Value - list) < 0.5)
            return;

        _fittingList = true;
        try
        {
            ListColumn.Width = new GridLength(list);
        }
        finally
        {
            _fittingList = false;
        }
    }

    private void FitEditorForm()
    {
        if (_fittingForm)
            return;

        var width = EditorScroll.ViewportWidth;
        if (width <= 0 || double.IsNaN(width) || double.IsInfinity(width))
            return;
        if (!double.IsNaN(EditorForm.Width) && Math.Abs(EditorForm.Width - width) < 0.5)
            return;

        _fittingForm = true;
        try
        {
            EditorForm.Width = width;
        }
        finally
        {
            _fittingForm = false;
        }
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
