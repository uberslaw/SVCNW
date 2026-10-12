using ServiceNowDesk.Models;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class RegionAccordionSelectionTests
{
    [Fact]
    public void AmericasListsOfficesDirectlyAndOtherRegionsKeepPortions()
    {
        var region = new RegionSettingsViewModel();

        Assert.Equal(
            ["Americas", "APAC (Asia Pacific)", "Europe", "UKIMEA (UK, India, Middle East & Africa)"],
            region.Groups.Select(group => group.Label).ToArray());
        Assert.All(region.Groups, group => Assert.False(group.IsExpanded));

        var americas = region.Find("Americas")!;
        Assert.False(americas.HasPortions);
        Assert.True(americas.HasOfficeChildren);
        Assert.Empty(americas.PortionChildren);
        Assert.Contains("Bogotá", americas.OfficeChildren.Select(office => office.Label));
        Assert.Contains("New Jersey (Newark)", americas.OfficeChildren.Select(office => office.Label));

        Assert.Equal(
            ["Australia & New Zealand", "Southeast Asia", "East Asia", "Other APAC Offices"],
            region.Find("APAC (Asia Pacific)")!.PortionChildren.Select(portion => portion.Label).ToArray());
        Assert.Equal(
            ["Denmark", "Germany", "Ireland", "Netherlands", "Poland / Southeast Europe", "Southern Europe", "Türkiye"],
            region.Find("Europe")!.PortionChildren.Select(portion => portion.Label).ToArray());
        Assert.Equal(
            ["United Kingdom", "India", "Middle East", "Africa"],
            region.Find("UKIMEA (UK, India, Middle East & Africa)")!.PortionChildren.Select(portion => portion.Label).ToArray());
        Assert.Contains("Solihull / Birmingham", region.Find("United Kingdom")!.OfficeChildren.Select(office => office.Label));
        Assert.Contains("Mauritius (Bagatelle)", region.Find("Africa")!.OfficeChildren.Select(office => office.Label));
    }

    [Fact]
    public void CheckingAllOfficesSelectsEveryOfficeInTheRegionIncludingPortions()
    {
        var region = new RegionSettingsViewModel();
        region.SetChecked("APAC (Asia Pacific)", true);

        var apac = region.Find("APAC (Asia Pacific)")!;
        Assert.Equal(true, apac.IsChecked);
        Assert.Equal("27 selected", apac.SelectionSummary);
        Assert.All(apac.Offices(), office => Assert.True(office.IsChecked));
        Assert.All(apac.PortionChildren, portion => Assert.Equal(true, portion.IsChecked));
        Assert.Contains("Sydney", region.SelectedOffices);
        Assert.Contains("Singapore", region.SelectedOffices);
        Assert.Contains("Tokyo", region.SelectedOffices);
        Assert.Contains("Manila", region.SelectedOffices);
        Assert.DoesNotContain("Boston", region.SelectedOffices);
        Assert.DoesNotContain(region.SelectedOffices, name =>
            name is "APAC (Asia Pacific)" or "Australia & New Zealand" or "East Asia" or "All offices");
        Assert.False(apac.IsExpanded);
    }

    [Fact]
    public void UncheckingAllOfficesClearsThatRegionOnly()
    {
        var region = new RegionSettingsViewModel();
        region.SetChecked("APAC (Asia Pacific)", true);
        region.SetChecked("Europe", true);
        region.SetChecked("APAC (Asia Pacific)", false);

        var apac = region.Find("APAC (Asia Pacific)")!;
        Assert.Equal(false, apac.IsChecked);
        Assert.Equal("", apac.SelectionSummary);
        Assert.All(apac.Offices(), office => Assert.False(office.IsChecked));
        Assert.All(apac.PortionChildren, portion => Assert.Equal(false, portion.IsChecked));

        Assert.Equal(true, region.Find("Europe")!.IsChecked);
        Assert.Equal(true, region.Find("Germany")!.IsChecked);
        Assert.Contains("Berlin", region.SelectedOffices);
        Assert.Contains("Copenhagen", region.SelectedOffices);
        Assert.Contains("Istanbul", region.SelectedOffices);
        Assert.DoesNotContain("Sydney", region.SelectedOffices);
        Assert.DoesNotContain("Tokyo", region.SelectedOffices);
    }

    [Fact]
    public void AMixOfOfficesAcrossTwoRegionsSurvives()
    {
        var region = new RegionSettingsViewModel();
        region.SetChecked("Boston", true);
        region.SetChecked("Seattle", true);
        region.SetChecked("Berlin", true);
        region.SetChecked("Madrid", true);

        Assert.Equal(["Berlin", "Boston", "Madrid", "Seattle"], region.SelectedOffices.Order(StringComparer.Ordinal).ToArray());
        Assert.Null(region.Find("Americas")!.IsChecked);
        Assert.Equal("2 selected", region.Find("Americas")!.SelectionSummary);
        Assert.Null(region.Find("Europe")!.IsChecked);
        Assert.Equal("2 selected", region.Find("Europe")!.SelectionSummary);
        Assert.Null(region.Find("Germany")!.IsChecked);
        Assert.Equal(true, region.Find("Berlin")!.IsChecked);
        Assert.Equal(false, region.Find("Frankfurt")!.IsChecked);
        Assert.Equal(false, region.Find("APAC (Asia Pacific)")!.IsChecked);
        Assert.Equal("", region.Find("APAC (Asia Pacific)")!.SelectionSummary);
        Assert.False(region.Find("Americas")!.IsExpanded);
        Assert.False(region.Find("Europe")!.IsExpanded);
    }

    [Fact]
    public void CheckingOnePortionSelectsOnlyThatPortionsOffices()
    {
        var region = new RegionSettingsViewModel();
        region.SetChecked("Boston", true);
        region.SetChecked("East Asia", true);
        region.SetChecked("Germany", true);

        var germany = region.Find("Germany")!;
        Assert.Equal(true, germany.IsChecked);
        Assert.Equal(["Berlin", "Düsseldorf", "Frankfurt"], germany.Offices().Select(office => office.Label).ToArray());
        Assert.All(germany.Offices(), office => Assert.True(office.IsChecked));
        Assert.Equal("3 selected", germany.SelectionSummary);

        Assert.Equal(true, region.Find("East Asia")!.IsChecked);
        Assert.Contains("Tokyo", region.SelectedOffices);
        Assert.Contains("Hong Kong", region.SelectedOffices);
        Assert.Equal(false, region.Find("Southeast Asia")!.IsChecked);
        Assert.Equal(false, region.Find("Australia & New Zealand")!.IsChecked);
        Assert.DoesNotContain("Singapore", region.SelectedOffices);
        Assert.DoesNotContain("Sydney", region.SelectedOffices);

        Assert.Equal(true, region.Find("Boston")!.IsChecked);
        Assert.Equal(false, region.Find("Chicago")!.IsChecked);
        Assert.Null(region.Find("Americas")!.IsChecked);
        Assert.Null(region.Find("Europe")!.IsChecked);
        Assert.Null(region.Find("APAC (Asia Pacific)")!.IsChecked);
        Assert.Equal(false, region.Find("Denmark")!.IsChecked);
        Assert.DoesNotContain("Copenhagen", region.SelectedOffices);
        Assert.DoesNotContain("London", region.SelectedOffices);
    }

    [Fact]
    public void ClearingOneOfficeInAPortionUnchecksPortionAndRegionAllCheckboxes()
    {
        var region = new RegionSettingsViewModel();
        region.SetChecked("Europe", true);
        region.SetChecked("East Asia", true);
        region.SetChecked("Berlin", false);

        Assert.Equal(false, region.Find("Berlin")!.IsChecked);
        Assert.Equal(true, region.Find("Düsseldorf")!.IsChecked);
        Assert.Equal(true, region.Find("Frankfurt")!.IsChecked);
        Assert.Null(region.Find("Germany")!.IsChecked);
        Assert.Null(region.Find("Europe")!.IsChecked);
        Assert.Equal(true, region.Find("Denmark")!.IsChecked);
        Assert.Equal(true, region.Find("Ireland")!.IsChecked);
        Assert.Equal("2 selected", region.Find("Germany")!.SelectionSummary);

        Assert.Equal(true, region.Find("East Asia")!.IsChecked);
        Assert.Contains("Tokyo", region.SelectedOffices);
        Assert.DoesNotContain("Berlin", region.SelectedOffices);
        Assert.Contains("Frankfurt", region.SelectedOffices);

        region.SetChecked("Berlin", true);
        Assert.Equal(true, region.Find("Germany")!.IsChecked);
        Assert.Equal(true, region.Find("Europe")!.IsChecked);
        Assert.Equal(true, region.Find("East Asia")!.IsChecked);
    }

    [Fact]
    public void AllOfficesClickChecksWhenPartialAndClearsWhenFull()
    {
        var region = new RegionSettingsViewModel();
        var germany = region.Find("Germany")!;
        region.SetChecked("Sydney", true);

        germany.ToggleFromUser();
        Assert.Equal(true, germany.IsChecked);
        Assert.All(germany.Offices(), office => Assert.True(office.IsChecked));
        Assert.Null(region.Find("Europe")!.IsChecked);
        Assert.Equal(true, region.Find("Sydney")!.IsChecked);

        region.SetChecked("Berlin", false);
        Assert.Null(germany.IsChecked);
        Assert.Null(region.Find("Europe")!.IsChecked);

        germany.ToggleFromUser();
        Assert.Equal(true, germany.IsChecked);
        Assert.Equal(true, region.Find("Berlin")!.IsChecked);

        germany.ToggleFromUser();
        Assert.Equal(false, germany.IsChecked);
        Assert.All(germany.Offices(), office => Assert.False(office.IsChecked));
        Assert.Equal(false, region.Find("Europe")!.IsChecked);
        Assert.Equal(true, region.Find("Sydney")!.IsChecked);
        Assert.Null(region.Find("Australia & New Zealand")!.IsChecked);
    }

    [Fact]
    public void SettingsRoundTripStoresOnlyOfficeNamesAndRestoresCheckboxStates()
    {
        var region = new RegionSettingsViewModel();
        region.Find("APAC (Asia Pacific)")!.IsExpanded = true;
        region.Find("Germany")!.IsExpanded = true;
        region.SetChecked("Germany", true);
        region.SetChecked("York", true);
        region.SetChecked("Sydney", true);

        Assert.Null(region.Find("Australia & New Zealand")!.IsChecked);
        Assert.Null(region.Find("APAC (Asia Pacific)")!.IsChecked);
        Assert.Equal(true, region.Find("Germany")!.IsChecked);
        Assert.Null(region.Find("Europe")!.IsChecked);
        Assert.Null(region.Find("United Kingdom")!.IsChecked);
        Assert.Null(region.Find("UKIMEA (UK, India, Middle East & Africa)")!.IsChecked);

        var names = region.SelectedOffices;
        Assert.Contains("Berlin", names);
        Assert.Contains("Düsseldorf", names);
        Assert.Contains("Frankfurt", names);
        Assert.Contains("York", names);
        Assert.Contains("Sydney", names);
        Assert.DoesNotContain("Boston", names);
        Assert.DoesNotContain("London", names);
        Assert.DoesNotContain(names, name =>
            name is "Germany" or "Europe" or "APAC (Asia Pacific)" or "United Kingdom" or "Australia & New Zealand" or "All offices");

        var settings = new DeskSettings { RegionOffices = names.ToList() };
        var json = DeskSettingsFile.Serialize(settings, text => text ?? "");
        Assert.Contains("\"RegionOffices\"", json);
        Assert.Contains("Berlin", json);
        Assert.Contains("York", json);
        Assert.Contains("Sydney", json);
        Assert.DoesNotContain("Germany", json);
        Assert.DoesNotContain("IsExpanded", json);
        Assert.DoesNotContain("All offices", json);

        var loaded = DeskSettingsFile.Deserialize(json, text => text ?? "");
        Assert.Equal(names, loaded.RegionOffices);

        var again = new RegionSettingsViewModel();
        again.Load(loaded.RegionOffices);
        Assert.Equal(names, again.SelectedOffices);
        Assert.False(again.Find("APAC (Asia Pacific)")!.IsExpanded);
        Assert.False(again.Find("Germany")!.IsExpanded);
        Assert.Equal(true, again.Find("Berlin")!.IsChecked);
        Assert.Equal(true, again.Find("Germany")!.IsChecked);
        Assert.Equal("3 selected", again.Find("Germany")!.SelectionSummary);
        Assert.Null(again.Find("Europe")!.IsChecked);
        Assert.Equal(true, again.Find("Sydney")!.IsChecked);
        Assert.Null(again.Find("Australia & New Zealand")!.IsChecked);
        Assert.Equal("1 selected", again.Find("Australia & New Zealand")!.SelectionSummary);
        Assert.Null(again.Find("APAC (Asia Pacific)")!.IsChecked);
        Assert.Equal(true, again.Find("York")!.IsChecked);
        Assert.Equal(false, again.Find("London")!.IsChecked);
        Assert.Null(again.Find("United Kingdom")!.IsChecked);
        Assert.Null(again.Find("UKIMEA (UK, India, Middle East & Africa)")!.IsChecked);

        region.Load(loaded.RegionOffices);
        Assert.True(region.Find("APAC (Asia Pacific)")!.IsExpanded);
        Assert.True(region.Find("Germany")!.IsExpanded);
        Assert.Equal(true, region.Find("Germany")!.IsChecked);
        Assert.Equal(names, region.SelectedOffices);
    }
}
