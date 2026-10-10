using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using ServiceNowDesk.Models;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Views;

public partial class SearchView
{
    private SearchWorkspaceViewModel? _search;
    private bool _syncingStates;

    public SearchView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookSearch();
        Loaded += (_, _) =>
        {
            HookSearch();
            HookColumnHeaders();
            ApplyGrouping();
        };
    }

    private void HookSearch()
    {
        if (_search is not null)
        {
            _search.SearchFiltersChanged -= SearchFiltersChanged;
            _search.PresentationChanged -= PresentationChanged;
        }

        _search = DataContext as SearchWorkspaceViewModel;
        if (_search is not null)
        {
            _search.SearchFiltersChanged += SearchFiltersChanged;
            _search.PresentationChanged += PresentationChanged;
            SyncStateSelectionFromViewModel();
            ApplyGrouping();
        }
    }

    private void FilterChanged(object sender, RoutedEventArgs e) => RefreshSearch();

    private void SearchFiltersChanged(object? sender, EventArgs e) => RefreshSearch();

    private void PresentationChanged(object? sender, EventArgs e)
    {
        SyncStateSelectionFromViewModel();
        ApplyGrouping();
    }

    private void RefreshSearch()
    {
        if (!IsLoaded)
            return;
        if (Window.GetWindow(this)?.DataContext is MainViewModel main && main.SelectedSection == DeskSection.Search)
            main.RefreshActiveCommand.Execute(null);
    }

    private void StateFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Rebuilding StateOptions during a search clears ListBox selection; ignore until load finishes.
        if (_syncingStates || DataContext is not SearchWorkspaceViewModel search || search.IsLoading)
            return;

        var labels = StateFilter.SelectedItems.Cast<object>().Select(item => item?.ToString() ?? "").Where(label => label.Length > 0);
        search.SetSelectedStates(labels);
    }

    private void ClearStates_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SearchWorkspaceViewModel search)
            return;
        search.ClearStateFilter();
        SyncStateSelectionFromViewModel();
    }

    private void SyncStateSelectionFromViewModel()
    {
        if (DataContext is not SearchWorkspaceViewModel search)
            return;

        _syncingStates = true;
        try
        {
            StateFilter.SelectedItems.Clear();
            if (search.SelectedStates.Count == 0)
                return;

            foreach (var option in StateFilter.Items.Cast<object>())
            {
                var label = option?.ToString() ?? "";
                if (search.IsStateSelected(label))
                    StateFilter.SelectedItems.Add(option);
            }
        }
        finally
        {
            _syncingStates = false;
        }
    }

    private void HookColumnHeaders()
    {
        Results.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(ColumnHeader_Click), true);
        Results.AddHandler(UIElement.MouseRightButtonUpEvent, new MouseButtonEventHandler(ColumnHeader_RightClick), true);
    }

    private void ColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
            return;
        var header = FindAncestor<DataGridColumnHeader>(source);
        if (header?.Column is null || DataContext is not SearchWorkspaceViewModel search)
            return;

        var column = ColumnKey(header.Column);
        if (column.Length == 0)
            return;

        search.ToggleSort(column);
        e.Handled = true;
    }

    private void ColumnHeader_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
            return;
        var header = FindAncestor<DataGridColumnHeader>(source);
        if (header?.Column is null || DataContext is not SearchWorkspaceViewModel search)
            return;

        var column = ColumnKey(header.Column);
        if (column.Length == 0)
            return;

        search.GroupBy(column);
        e.Handled = true;
    }

    private void ApplyGrouping()
    {
        if (!IsLoaded)
            return;

        var view = CollectionViewSource.GetDefaultView(Results.ItemsSource);
        if (view is null)
            return;

        using (view.DeferRefresh())
        {
            view.GroupDescriptions.Clear();
            if (DataContext is SearchWorkspaceViewModel search && search.HasGrouping)
            {
                var property = GroupProperty(search.GroupColumn);
                if (property.Length > 0)
                    view.GroupDescriptions.Add(new PropertyGroupDescription(property));
            }
        }
    }

    private static string ColumnKey(DataGridColumn column)
    {
        var header = column.Header?.ToString() ?? "";
        return header switch
        {
            "Type" => SearchWorkspaceViewModel.SortType,
            "Number" => SearchWorkspaceViewModel.SortNumber,
            "Title" => SearchWorkspaceViewModel.SortTitle,
            "Meta" => SearchWorkspaceViewModel.SortMeta,
            "State" => SearchWorkspaceViewModel.SortState,
            "Updated" => SearchWorkspaceViewModel.SortWhen,
            _ => column.SortMemberPath switch
            {
                "TableLabel" => SearchWorkspaceViewModel.SortType,
                "Number" => SearchWorkspaceViewModel.SortNumber,
                "Title" => SearchWorkspaceViewModel.SortTitle,
                "Meta" => SearchWorkspaceViewModel.SortMeta,
                "StateLabel" => SearchWorkspaceViewModel.SortState,
                "When" => SearchWorkspaceViewModel.SortWhen,
                _ => ""
            }
        };
    }

    private static string GroupProperty(string column) => column switch
    {
        SearchWorkspaceViewModel.SortType => nameof(SearchHit.TableLabel),
        SearchWorkspaceViewModel.SortNumber => nameof(SearchHit.Number),
        SearchWorkspaceViewModel.SortTitle => nameof(SearchHit.Title),
        SearchWorkspaceViewModel.SortMeta => nameof(SearchHit.Meta),
        SearchWorkspaceViewModel.SortState => nameof(SearchHit.StateLabel),
        SearchWorkspaceViewModel.SortWhen => nameof(SearchHit.When),
        _ => ""
    };

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
                return match;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    // ListBox input bindings never see an item double-click; the item handles the mouse event first.
    private void Results_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not SearchWorkspaceViewModel search)
            return;
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item is not SearchHit hit)
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
