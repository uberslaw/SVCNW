using System.Windows;
using System.Windows.Controls;

namespace ServiceNowDesk.Views;

/// <summary>
/// Hides the ticket list beside an existing editor so Leads can host that same form.
/// A named page action, such as Create New on request items, is hidden with the list.
/// </summary>
public static class EditorPane
{
    public static readonly DependencyProperty EditorOnlyProperty = DependencyProperty.RegisterAttached(
        "EditorOnly",
        typeof(bool),
        typeof(EditorPane),
        new PropertyMetadata(false, OnEditorOnlyChanged));

    public static void SetEditorOnly(DependencyObject element, bool value) => element.SetValue(EditorOnlyProperty, value);

    public static bool GetEditorOnly(DependencyObject element) => element.GetValue(EditorOnlyProperty) is true;

    private static void OnEditorOnlyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement view)
            return;
        view.Loaded -= ViewLoaded;
        view.Loaded += ViewLoaded;
        if (view.IsLoaded)
            Apply(view);
    }

    private static void ViewLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement view)
            Apply(view);
    }

    private static void Apply(FrameworkElement view)
    {
        var editorOnly = GetEditorOnly(view);
        if (view.FindName("RecordLayout") is not Grid layout || view.FindName("TicketList") is not UIElement list)
            return;
        list.Visibility = editorOnly ? Visibility.Collapsed : Visibility.Visible;
        if (view.FindName("PageActions") is UIElement actions)
            actions.Visibility = editorOnly ? Visibility.Collapsed : Visibility.Visible;
        if (layout.ColumnDefinitions.Count < 2)
            return;
        layout.ColumnDefinitions[0].Width = editorOnly ? new GridLength(0) : new GridLength(400);
        layout.ColumnDefinitions[1].Width = editorOnly ? new GridLength(0) : new GridLength(16);
        if (layout.RowDefinitions.Count > 0)
            layout.RowDefinitions[0].Height = editorOnly ? new GridLength(0) : GridLength.Auto;
    }
}
