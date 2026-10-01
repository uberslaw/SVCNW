using System.Windows;
using ServiceNowDesk.Models;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Views;

public partial class SearchView
{
    public SearchView()
    {
        InitializeComponent();
    }

    private void FilterChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;
        if (Window.GetWindow(this)?.DataContext is MainViewModel main && main.SelectedSection == DeskSection.Search)
            main.RefreshActiveCommand.Execute(null);
    }
}
