using ServiceNowDesk.Models;

namespace ServiceNowDesk.Navigation;

/// <summary>
/// Left-nav order. Hidden entries stay in this sequence so a later unhide keeps their place.
/// Order catalog is not a nav entry. Request items opens that same section from Create New.
/// </summary>
public sealed record DeskNavItem(DeskSection Section, string Label, string Icon, bool ShownInNav = true, bool RequiresLeads = false);

public static class DeskNavigation
{
    /// <summary>Section the Order catalog nav item selected. Create New on Request items uses it.</summary>
    public static DeskSection OrderCatalogSection => DeskSection.Catalog;

    public static IReadOnlyList<DeskNavItem> Items { get; } =
    [
        new(DeskSection.DailyWork, "Daily Work", "\uE787"),
        new(DeskSection.Incidents, "Incidents", "\uE7BA"),
        new(DeskSection.RequestedItems, "Request items", "\uE8FD"),
        new(DeskSection.Requests, "Requests", "\uE7BF", ShownInNav: false),
        new(DeskSection.WalkUps, "Walk-up", "\uE716"),
        new(DeskSection.Hardware, "Hardware", "\uE7F8"),
        new(DeskSection.Search, "Search", "\uE721"),
        new(DeskSection.Knowledge, "Knowledge", "\uE82D"),
        new(DeskSection.Notifications, "Notifications", "\uEA8F"),
        new(DeskSection.Settings, "Settings", "\uE713"),
        new(DeskSection.Connection, "Connection", "\uE774"),
        new(DeskSection.Leads, "Leads", "\uE902", RequiresLeads: true),
        new(DeskSection.Legend, "Legend", "\uE790")
    ];

    public static IReadOnlyList<DeskNavItem> Visible(bool leadsEnabled) =>
        Items.Where(item => item.ShownInNav && (!item.RequiresLeads || leadsEnabled)).ToArray();
}
