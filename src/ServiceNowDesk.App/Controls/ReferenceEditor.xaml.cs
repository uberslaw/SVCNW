using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ServiceNowDesk.Models;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Controls;

public partial class ReferenceEditor
{
    public ReferenceEditor()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => SuggestionPopup.DataContext = DataContext;
    }

    private ReferenceFieldModel? Model => DataContext as ReferenceFieldModel;

    private void Input_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        SuggestionBorder.MinWidth = Math.Max(220, Input.ActualWidth);
    }

    private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var model = Model;
        if (model is null)
            return;

        if (e.Key == Key.Down && model.Suggestions.Count > 0)
        {
            SuggestionList.Focus();
            if (SuggestionList.SelectedIndex < 0)
                SuggestionList.SelectedIndex = 0;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            model.Dismiss();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && model.Highlighted is ReferenceSuggestion suggestion)
        {
            model.Choose(suggestion);
            e.Handled = true;
        }
    }

    private void SuggestionList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SuggestionList.SelectedItem is ReferenceSuggestion suggestion)
        {
            Model?.Choose(suggestion);
            Input.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Model?.Dismiss();
            Input.Focus();
            e.Handled = true;
        }
    }

    private void SuggestionList_Click(object sender, MouseButtonEventArgs e)
    {
        var suggestion = SuggestionAt(e.OriginalSource) ?? SuggestionList.SelectedItem as ReferenceSuggestion;
        if (suggestion is null)
            return;
        Model?.Choose(suggestion);
        Input.Focus();
        e.Handled = true;
    }

    private static ReferenceSuggestion? SuggestionAt(object? source)
    {
        var node = source as DependencyObject;
        while (node is not null && node is not ListBox)
        {
            if (node is ListBoxItem item && item.DataContext is ReferenceSuggestion suggestion)
                return suggestion;
            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }
}
