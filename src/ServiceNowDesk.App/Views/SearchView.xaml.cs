using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ServiceNowDesk.Models;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Views;

public partial class SearchView
{
    private SearchWorkspaceViewModel? _search;

    public SearchView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookSearch();
    }

    private void HookSearch()
    {
        if (_search is not null)
            _search.SearchFiltersChanged -= SearchFiltersChanged;
        _search = DataContext as SearchWorkspaceViewModel;
        if (_search is not null)
            _search.SearchFiltersChanged += SearchFiltersChanged;
    }

    private void FilterChanged(object sender, RoutedEventArgs e) => RefreshSearch();

    private void SearchFiltersChanged(object? sender, EventArgs e) => RefreshSearch();

    private void RefreshSearch()
    {
        if (!IsLoaded)
            return;
        if (Window.GetWindow(this)?.DataContext is MainViewModel main && main.SelectedSection == DeskSection.Search)
            main.RefreshActiveCommand.Execute(null);
    }

    // ListBox input bindings never see an item double-click; the item handles the mouse event first.
    private void Results_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not SearchWorkspaceViewModel search)
            return;
        if (ItemsControl.ContainerFromElement(Results, e.OriginalSource as DependencyObject) is not ListBoxItem item)
            return;
        if (item.DataContext is not SearchHit hit)
            return;

        search.Selected = hit;
        search.OpenSelectedCommand.Execute(null);
        e.Handled = true;
    }

    private void Results_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None)
            return;
        if (DataContext is not SearchWorkspaceViewModel search)
            return;

        search.OpenSelectedCommand.Execute(null);
        e.Handled = true;
    }
}
