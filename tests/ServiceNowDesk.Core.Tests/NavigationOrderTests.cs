using System.Text.RegularExpressions;
using ServiceNowDesk.Models;
using ServiceNowDesk.Navigation;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public partial class NavigationOrderTests
{
    private static readonly string[] MainOrder =
    [
        "Daily Work",
        "In The Mix",
        "Incidents",
        "Request items",
        "Walk-up",
        "Hardware",
        "Search",
        "Knowledge",
        "Notifications",
        "Settings",
        "Connection"
    ];

    [Fact]
    public void VisibleNavFollowsTheDeskOrderAndSkipsOrderCatalog()
    {
        var labels = DeskNavigation.Visible(leadsEnabled: false).Select(item => item.Label).ToArray();
        Assert.Equal(MainOrder, labels);
        var incidents = Array.IndexOf(labels, "Incidents");
        Assert.True(incidents > 0);
        Assert.Equal("In The Mix", labels[incidents - 1]);

        Assert.DoesNotContain(DeskNavigation.Items, item => item.Section == DeskSection.Catalog);
        Assert.Equal(DeskSection.Catalog, DeskNavigation.OrderCatalogSection);

        var items = DeskNavigation.Items;
        var requestItems = IndexOf(items, DeskSection.RequestedItems);
        Assert.Equal(DeskSection.Requests, items[requestItems + 1].Section);
        Assert.Equal("Requests", items[requestItems + 1].Label);
        Assert.False(items[requestItems + 1].ShownInNav);
        Assert.DoesNotContain(labels, label => label == "Requests");
        Assert.DoesNotContain(labels, label => label == "Order catalog");
        Assert.DoesNotContain(labels, label => label == "Legend");
        Assert.False(DeskNavigation.Items.Single(item => item.Section == DeskSection.Legend).ShownInNav);
    }

    [Fact]
    public void LeadsAppearsFirstOnlyWhenUnlocked()
    {
        var locked = DeskNavigation.Visible(false).Select(item => item.Section).ToArray();
        Assert.DoesNotContain(DeskSection.Leads, locked);
        Assert.DoesNotContain(DeskSection.Legend, locked);
        Assert.Equal(DeskSection.Connection, locked[^1]);
        Assert.Equal(DeskSection.DailyWork, locked[0]);

        var unlocked = DeskNavigation.Visible(true).Select(item => item.Section).ToArray();
        Assert.Equal(DeskSection.Leads, unlocked[0]);
        Assert.Equal(DeskSection.DailyWork, unlocked[1]);
        Assert.DoesNotContain(DeskSection.Legend, unlocked);
        Assert.Equal(DeskSection.Connection, unlocked[^1]);
        Assert.Equal(MainOrder, unlocked.Where(section => section != DeskSection.Leads).Select(LabelOf).ToArray());
    }

    [Fact]
    public void CreateNewOnRequestItemsOpensTheOrderCatalogSection()
    {
        var main = new MainViewModel(new MemorySettingsStore(), new RecordingDesktopServices());
        main.SelectedSection = DeskSection.RequestedItems;

        main.RequestedItems.CreateNewCommand.Execute(null);

        Assert.Equal(DeskNavigation.OrderCatalogSection, main.SelectedSection);
        Assert.Equal(DeskSection.Catalog, main.SelectedSection);
        Assert.DoesNotContain(main.Navigation, item => item.Section == DeskSection.Catalog);
    }

    [Fact]
    public void LeadsToggleOnTheShellStillSitsFirstWhenUnlocked()
    {
        var main = new MainViewModel(new MemorySettingsStore(), new RecordingDesktopServices());
        Assert.Equal(MainOrder, main.Navigation.Select(item => item.Label).ToArray());
        Assert.DoesNotContain(main.Navigation, item => item.Section == DeskSection.Leads);
        Assert.DoesNotContain(main.Navigation, item => item.Section == DeskSection.Legend);
        Assert.False(main.TrySelect(DeskSection.Leads));

        main.Connection.LeadsPassword = "iddqd";
        main.Connection.UnlockLeadsCommand.Execute(null);

        var unlocked = main.Navigation.Select(item => item.Section).ToArray();
        Assert.Equal(DeskSection.Leads, unlocked[0]);
        Assert.Equal(DeskSection.DailyWork, unlocked[1]);
        Assert.True(main.TrySelect(DeskSection.Leads));
        Assert.Equal(DeskSection.Leads, main.SelectedSection);
        Assert.True(main.Navigation.Single(item => item.Section == DeskSection.Leads).IsSelected);

        main.Connection.HideLeadsCommand.Execute(null);
        Assert.Equal(DeskSection.Incidents, main.SelectedSection);
        Assert.DoesNotContain(main.Navigation, item => item.Section == DeskSection.Leads);
        Assert.False(main.TrySelect(DeskSection.Leads));
        Assert.True(main.Navigation.Single(item => item.Section == DeskSection.Incidents).IsSelected);
    }

    [Fact]
    public void RequestsPageStaysReachableAndOutOfTheNav()
    {
        var main = new MainViewModel(new MemorySettingsStore(), new RecordingDesktopServices());
        Assert.True(main.TrySelect(DeskSection.Requests));
        Assert.Equal(DeskSection.Requests, main.SelectedSection);
        Assert.DoesNotContain(main.Navigation, item => item.Section == DeskSection.Requests);
    }

    [Fact]
    public void LegendOpensSettingsAndStaysOutOfTheNav()
    {
        var main = new MainViewModel(new MemorySettingsStore(), new RecordingDesktopServices());
        Assert.DoesNotContain(main.Navigation, item => item.Section == DeskSection.Legend);
        Assert.True(main.TrySelect(DeskSection.Legend));
        Assert.Equal(DeskSection.Settings, main.SelectedSection);
        Assert.True(main.Navigation.Single(item => item.Section == DeskSection.Settings).IsSelected);

        main.SelectedSection = DeskSection.Legend;
        Assert.Equal(DeskSection.Settings, main.SelectedSection);

        var settings = RepoFile("src/ServiceNowDesk.App/Views/SettingsView.xaml");
        Assert.Contains("<views:LegendView", settings, StringComparison.Ordinal);
        var window = RepoFile("src/ServiceNowDesk.App/MainWindow.xaml");
        Assert.DoesNotContain("ConverterParameter=Legend", window, StringComparison.Ordinal);
    }

    [Fact]
    public void CacheSectionLaysOutThreeColumns()
    {
        var settings = RepoFile("src/ServiceNowDesk.App/Views/SettingsView.xaml");
        var caches = settings.IndexOf("ItemsSource=\"{Binding Caches}\"", StringComparison.Ordinal);
        Assert.True(caches > settings.IndexOf("Text=\"Cache\"", StringComparison.Ordinal));
        Assert.Contains("<UniformGrid Columns=\"3\"/>", settings[caches..], StringComparison.Ordinal);
        Assert.Contains("RefreshCacheCommand", settings[caches..], StringComparison.Ordinal);
        Assert.Contains("DownloadOnLaunch", settings[caches..], StringComparison.Ordinal);
    }

    [Fact]
    public void RequestItemCloseNotesReplaceJournalComposeOnlyWhileClosing()
    {
        var view = RepoFile("src/ServiceNowDesk.App/Views/RequestedItemView.xaml");
        Assert.Contains("Text=\"Close notes\"", view, StringComparison.Ordinal);
        Assert.Contains("Binding ResolveNotes", view, StringComparison.Ordinal);
        Assert.Contains("Binding ShowResolvePanel, Converter={StaticResource BoolVis}", view, StringComparison.Ordinal);
        Assert.Contains("Binding ShowResolvePanel, Converter={StaticResource InverseBoolVis}", view, StringComparison.Ordinal);
        Assert.DoesNotContain("Binding CloseNotes", view, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding BeginResolveCommand}\"", view, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding CancelResolveCommand}\"", view, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateNewButtonAndNavBindToThatCatalogSection()
    {
        var view = RepoFile("src/ServiceNowDesk.App/Views/RequestedItemView.xaml");
        Assert.Contains("CreateText=\"Create New\"", view, StringComparison.Ordinal);
        Assert.Contains("CreateCommand=\"{Binding CreateNewCommand}\"", view, StringComparison.Ordinal);

        var window = RepoFile("src/ServiceNowDesk.App/MainWindow.xaml");
        Assert.Contains("ItemsSource=\"{Binding Navigation}\"", window, StringComparison.Ordinal);
        Assert.DoesNotContain("Order catalog", window, StringComparison.Ordinal);
        Assert.Contains("ConverterParameter=Catalog", window, StringComparison.Ordinal);
        Assert.Contains("ConverterParameter=Requests", window, StringComparison.Ordinal);

        var shortcuts = RepoFile("src/ServiceNowDesk.App/MainWindow.xaml.cs");
        var found = new Dictionary<string, string>();
        foreach (Match match in ShortcutPattern().Matches(shortcuts))
            found[match.Groups[1].Value] = match.Groups[2].Value;

        Assert.Equal("Incidents", found["1"]);
        Assert.Equal("RequestedItems", found["2"]);
        Assert.Equal("RequestedItems", found["3"]);
        Assert.Equal("Search", found["4"]);
        Assert.Equal("Catalog", found["5"]);
        Assert.Equal("Connection", found["6"]);
        Assert.Equal("Knowledge", found["7"]);
        Assert.Equal("WalkUps", found["8"]);
        Assert.Equal("Notifications", found["9"]);
    }

    private static int IndexOf(IReadOnlyList<DeskNavItem> items, DeskSection section)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Section == section)
                return i;
        }

        return -1;
    }

    private static string LabelOf(DeskSection section) =>
        DeskNavigation.Items.Single(item => item.Section == section).Label;

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var solution = Path.Combine(dir.FullName, "ServiceNowDesk.sln");
            if (File.Exists(solution))
                return File.ReadAllText(Path.Combine(dir.FullName, relative));
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not find ServiceNowDesk.sln from " + AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"Key\.D(\d) or Key\.NumPad\1\)\s*Navigate\(main, DeskSection\.(\w+)", RegexOptions.CultureInvariant)]
    private static partial Regex ShortcutPattern();
}
