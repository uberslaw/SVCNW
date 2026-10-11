using ServiceNowDesk.Models;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class TopSearchVisibilityTests
{
    [Fact]
    public void TopSearchShowsOnlyOnTicketListsAndDailyWork()
    {
        var store = new MemorySettingsStore();
        store.Save(new DeskSettings { UseSampleData = true });
        var main = new MainViewModel(store, new RecordingDesktopServices());

        Assert.Equal(DeskSection.Connection, main.SelectedSection);
        Assert.False(main.ShowTopSearch);

        foreach (var section in new[]
                 {
                     DeskSection.DailyWork,
                     DeskSection.InTheMix,
                     DeskSection.Incidents,
                     DeskSection.RequestedItems,
                     DeskSection.WalkUps
                 })
        {
            main.SelectedSection = section;
            Assert.True(main.ShowTopSearch, $"{section} should show the top search box");
        }

        // Search keeps the same query field, hosted under its filters instead of in the chrome.
        foreach (var section in new[]
                 {
                     DeskSection.Search,
                     DeskSection.Hardware,
                     DeskSection.Knowledge,
                     DeskSection.Notifications,
                     DeskSection.Settings,
                     DeskSection.Connection,
                     DeskSection.Leads,
                     DeskSection.Legend,
                     DeskSection.Catalog,
                     DeskSection.Requests
                 })
        {
            main.SelectedSection = section;
            Assert.False(main.ShowTopSearch, $"{section} should hide the top search box");
        }
    }
}
