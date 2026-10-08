using System.Windows;

namespace ServiceNowDesk.Views;

public partial class InteractionView
{
    public static readonly DependencyProperty EditorOnlyProperty =
        DependencyProperty.Register(nameof(EditorOnly), typeof(bool), typeof(InteractionView), new PropertyMetadata(false, (d, _) => ((InteractionView)d).ApplyEditorOnly()));

    public InteractionView()
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
