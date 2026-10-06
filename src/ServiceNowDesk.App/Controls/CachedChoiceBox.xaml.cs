using System.Windows;
using System.Windows.Input;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Controls;

public partial class CachedChoiceBox
{
    public CachedChoiceBox()
    {
        InitializeComponent();
    }

    private ReferenceChoiceField? Field => DataContext as ReferenceChoiceField;

    private async void Filter_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Field is null)
            return;
        e.Handled = true;
        await Field.CommitAsync();
    }

    private async void Filter_LostFocus(object sender, RoutedEventArgs e)
    {
        if (Field is null)
            return;
        await Field.CommitAsync();
    }
}
