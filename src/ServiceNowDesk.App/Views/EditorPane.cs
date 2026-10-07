using System.Windows;
using System.Windows.Controls;

namespace ServiceNowDesk.Views;

public static class EditorPane
{
    public static void Apply(bool editorOnly, Grid layout, FrameworkElement list, FrameworkElement? extra = null)
    {
        var hidden = editorOnly ? Visibility.Collapsed : Visibility.Visible;
        list.Visibility = hidden;
        if (extra is not null)
            extra.Visibility = hidden;
        if (layout.ColumnDefinitions.Count < 2)
            return;
        layout.ColumnDefinitions[0].Width = editorOnly ? new GridLength(0) : new GridLength(400);
        layout.ColumnDefinitions[1].Width = editorOnly ? new GridLength(0) : new GridLength(16);
    }
}
