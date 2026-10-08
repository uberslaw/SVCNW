using System.Windows;

namespace ServiceNowDesk.Views;

public partial class RequestView
{
    public static readonly DependencyProperty EditorOnlyProperty =
        DependencyProperty.Register(nameof(EditorOnly), typeof(bool), typeof(RequestView), new PropertyMetadata(false, (d, _) => ((RequestView)d).ApplyEditorOnly()));

    public RequestView()
    {
        InitializeComponent();
    }

    public bool EditorOnly
    {
        get => (bool)GetValue(EditorOnlyProperty);
        set => SetValue(EditorOnlyProperty, value);
    }

    private void ApplyEditorOnly() => EditorPane.Apply(EditorOnly, Layout, ListPane, emptyState: EmptyState);
}
