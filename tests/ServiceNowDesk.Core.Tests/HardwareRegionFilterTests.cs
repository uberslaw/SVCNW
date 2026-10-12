using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class HardwareRegionFilterTests
{
    [Fact]
    public void EmptySelectionDoesNotFilterAndChoicesUseStateOrder()
    {
        Assert.True(HardwareColumnFilter.Matches("In transit", null));
        Assert.True(HardwareColumnFilter.Matches("In transit", []));
        Assert.True(HardwareColumnFilter.Matches("In transit", ["", "  "]));
        Assert.False(HardwareColumnFilter.Matches("In use", ["In transit", "In stock"]));
        Assert.True(HardwareColumnFilter.Matches("in stock", ["In stock"]));

        var ordered = HardwareColumnFilter.Distinct(
            ["in use", "In stock", "Retired", "In stock", "  ", null, "Custom"],
            HardwareColumnFilter.StateOrder);
        Assert.Equal(["In stock", "in use", "Retired", "Custom"], ordered);
    }

    [Fact]
    public async Task ChoiceFiltersMatchAnyValueInAFieldAndEveryFieldTogether()
    {
        using var client = new SampleServiceNowClient();
        client.AddComputer(Computer("hw-lost", "LOST0001", "HP ZBook", "Brisbane Office", "In transit", "Lost"));
        client.AddComputer(Computer("hw-stolen", "STOLEN0001", "Dell Latitude", "Sydney Office", "In use", "Stolen"));
        var workspace = await OpenAllLocationsAsync(client);
        var all = workspace.ShownCount;
        Assert.True(all > 5);

        workspace.SetSubstateFilter(["Lost", "Stolen"]);
        await workspace.ColumnFiltersReady;
        Assert.Equal(["LOST0001", "STOLEN0001"], workspace.Items.Select(asset => asset.SerialNumber).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("2", workspace.SubstateFilterCountText);

        workspace.ClearSubstateFilter();
        workspace.SetStateFilter(["In transit", "In stock"]);
        workspace.SetLocationFilter(["Hong Kong Office", "Sydney Office"]);
        workspace.SetModelFilter(["HP ZBook Fury 16 G9", "Dell Latitude"]);
        await workspace.ColumnFiltersReady;

        Assert.Equal(
            ["5CG30710BR", "ABCDEFG"],
            workspace.Items.Select(asset => asset.SerialNumber).Order(StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(workspace.Items, asset => asset.SerialNumber == "STOLEN0001");
        Assert.DoesNotContain(workspace.Items, asset => asset.Location.Display == "Brisbane Office");
        Assert.True(workspace.ShownCount < all);

        workspace.SetStateFilter(["In stock"]);
        await workspace.ColumnFiltersReady;
        Assert.Equal("5CG30710BR", Assert.Single(workspace.Items).SerialNumber);

        workspace.ClearStateFilter();
        await workspace.ColumnFiltersReady;
        Assert.Empty(workspace.SelectedStateFilters);
        Assert.Equal(["Hong Kong Office", "Sydney Office"], workspace.SelectedLocationFilters.ToArray());
        Assert.Equal(["Dell Latitude", "HP ZBook Fury 16 G9"], workspace.SelectedModelFilters.ToArray());
        Assert.Equal(
            ["5CG30710BR", "ABCDEFG", "STOLEN0001"],
            workspace.Items.Select(asset => asset.SerialNumber).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task ClearRemovesOnlyThatChoiceFilter()
    {
        using var client = new SampleServiceNowClient();
        client.AddComputer(Computer("hw-bne-stock", "BNESTOCK1", "HP ZBook", "Brisbane Office", "In stock", "Available"));
        var workspace = await OpenAllLocationsAsync(client);

        workspace.SetStateFilter(["In stock"]);
        workspace.SetLocationFilter(["Hong Kong Office"]);
        await workspace.ColumnFiltersReady;
        Assert.Equal("5CG30710BR", Assert.Single(workspace.Items).SerialNumber);

        workspace.ClearLocationFilter();
        await workspace.ColumnFiltersReady;
        Assert.Empty(workspace.SelectedLocationFilters);
        Assert.Equal(["In stock"], workspace.SelectedStateFilters.ToArray());
        Assert.Equal("0", workspace.LocationFilterCountText);
        Assert.Equal(
            ["5CG30710BR", "BNESTOCK1"],
            workspace.Items.Select(asset => asset.SerialNumber).Order(StringComparer.Ordinal).ToArray());

        workspace.ClearModelFilter();
        workspace.ClearSubstateFilter();
        await workspace.ColumnFiltersReady;
        Assert.Equal(["In stock"], workspace.SelectedStateFilters.ToArray());
    }

    [Theory]
    [InlineData("Sydney", "Sydney", true)]
    [InlineData("Sydney Office", "Sydney", true)]
    [InlineData("AU Sydney Office", "Sydney", true)]
    [InlineData("sydney office", "SYDNEY", true)]
    [InlineData("New York", "York", false)]
    [InlineData("New York Office", "York", false)]
    [InlineData("York", "York", true)]
    [InlineData("York Office", "York", true)]
    [InlineData("Yorkshire", "York", false)]
    [InlineData("Newark", "New Jersey (Newark)", true)]
    [InlineData("New Jersey", "New Jersey (Newark)", true)]
    [InlineData("New Jersey Office", "New Jersey (Newark)", true)]
    [InlineData("Jersey", "New Jersey (Newark)", false)]
    [InlineData("New Jersey", "Jersey", false)]
    [InlineData("Solihull", "Solihull / Birmingham", true)]
    [InlineData("Birmingham Office", "Solihull / Birmingham", true)]
    [InlineData("Kuala Lumpur Office", "Kuala Lumpur / Penang (Malaysia)", true)]
    [InlineData("Penang", "Kuala Lumpur / Penang (Malaysia)", true)]
    [InlineData("Malaysia Office", "Kuala Lumpur / Penang (Malaysia)", true)]
    [InlineData("San Francisco", "San Diego", false)]
    [InlineData("Hong Kong Office", "Hong Kong", true)]
    public void OfficeNameMatchesALocationAsWholeTokens(string location, string office, bool expected) =>
        Assert.Equal(expected, RegionOfficeMatch.LocationMatchesOffice(location, office));

    [Fact]
    public void NoSavedOfficesDoesNotMarkEveryLocationInRegion()
    {
        var locations = new[] { "Sydney Office", "New York", "York Office" };
        Assert.Empty(RegionOfficeMatch.LocationsInRegion(locations, null));
        Assert.Empty(RegionOfficeMatch.LocationsInRegion(locations, []));
        Assert.Empty(RegionOfficeMatch.LocationsInRegion(locations, ["", "  "]));

        Assert.Equal(
            ["Sydney Office", "York Office"],
            RegionOfficeMatch.LocationsInRegion(locations, ["Sydney", "York"]));
    }

    [Fact]
    public async Task RegionAppliesSavedOfficesOntoLocations()
    {
        using var client = new SampleServiceNowClient();
        client.AddComputer(Computer("hw-ny", "NY0001", "HP ZBook", "New York", "In use", ""));
        client.AddComputer(Computer("hw-york", "YORK0001", "HP ZBook", "York Office", "In use", ""));
        var store = new MemorySettingsStore();
        store.Save(new DeskSettings { RegionOffices = ["Sydney", "York"] });
        var workspace = new HardwareWorkspaceViewModel(store);
        workspace.Attach(client);
        await workspace.RefreshAsync();
        await workspace.SearchAllLocationsCommand.ExecuteAsync(null);
        Assert.Contains(workspace.Items, asset => asset.Location.Display == "New York");

        workspace.SetLocationFilter(["Brisbane Office"]);
        await workspace.ColumnFiltersReady;

        workspace.ApplyRegionCommand.Execute(null);
        await workspace.ColumnFiltersReady;

        Assert.Equal(["Sydney Office", "York Office"], workspace.SelectedLocationFilters.ToArray());
        Assert.Equal("2", workspace.LocationFilterCountText);
        Assert.Equal(HardwareWorkspaceViewModel.RegionAppliedMessage, workspace.OfficeStatus);
        Assert.NotEmpty(workspace.Items);
        Assert.All(workspace.Items, asset =>
            Assert.True(asset.Location.Display is "Sydney Office" or "York Office"));
        Assert.Contains(workspace.Items, asset => asset.SerialNumber == "YORK0001");
        Assert.DoesNotContain(workspace.Items, asset => asset.Location.Display == "New York");
        Assert.DoesNotContain(workspace.Items, asset => asset.Location.Display == "Brisbane Office");

        workspace.ClearLocationFilter();
        await workspace.ColumnFiltersReady;
        Assert.Empty(workspace.SelectedLocationFilters);
        Assert.Equal("0", workspace.LocationFilterCountText);
        Assert.Contains(workspace.Items, asset => asset.Location.Display == "New York");
    }

    [Fact]
    public async Task RegionWithoutSavedOfficesLeavesTheListUnfiltered()
    {
        using var client = new SampleServiceNowClient();
        var workspace = new HardwareWorkspaceViewModel(new MemorySettingsStore());
        workspace.Attach(client);
        await workspace.RefreshAsync();
        await workspace.SearchAllLocationsCommand.ExecuteAsync(null);
        var all = workspace.ShownCount;
        Assert.True(all > 1);

        workspace.ApplyRegionCommand.Execute(null);
        await workspace.ColumnFiltersReady;

        Assert.Equal(all, workspace.ShownCount);
        Assert.Empty(workspace.SelectedLocationFilters);
        Assert.Equal("0", workspace.LocationFilterCountText);
        Assert.Equal(HardwareWorkspaceViewModel.RegionOfficesMissingMessage, workspace.OfficeStatus);

        var unset = new HardwareWorkspaceViewModel();
        unset.ApplyRegionCommand.Execute(null);
        Assert.Equal(HardwareWorkspaceViewModel.RegionOfficesMissingMessage, unset.OfficeStatus);
        Assert.Empty(unset.SelectedLocationFilters);
    }

    [Fact]
    public void CatalogUsesTheArupOfficeLabels()
    {
        Assert.Equal(
            ["Americas", "APAC (Asia Pacific)", "Europe", "UKIMEA (UK, India, Middle East & Africa)"],
            RegionOfficeCatalog.Regions.Select(region => region.Label).ToArray());

        Assert.Equal(
        [
            "Boston",
            "Toronto",
            "Montreal",
            "Chicago",
            "Los Angeles",
            "New York",
            "New Jersey (Newark)",
            "San Francisco",
            "Oakland",
            "Bogotá",
            "Seattle",
            "Dallas",
            "Houston",
            "Washington DC",
            "San Diego"
        ],
            RegionOfficeCatalog.Regions[0].Offices.ToArray());

        Assert.Equal(
            ["Australia & New Zealand", "Southeast Asia", "East Asia", "Other APAC Offices"],
            RegionOfficeCatalog.Regions[1].Groups.Select(group => group.Label).ToArray());
        Assert.Contains("Kuala Lumpur / Penang (Malaysia)", RegionOfficeCatalog.Regions[1].Groups[1].Offices);
        Assert.Contains("Ho Chi Minh City", RegionOfficeCatalog.Regions[1].Groups[3].Offices);

        Assert.Equal(
            ["Denmark", "Germany", "Ireland", "Netherlands", "Poland / Southeast Europe", "Southern Europe", "Türkiye"],
            RegionOfficeCatalog.Regions[2].Groups.Select(group => group.Label).ToArray());
        Assert.Contains("Düsseldorf", RegionOfficeCatalog.Regions[2].Groups[1].Offices);
        Assert.Contains("Kraków", RegionOfficeCatalog.Regions[2].Groups[4].Offices);

        Assert.Equal(
            ["United Kingdom", "India", "Middle East", "Africa"],
            RegionOfficeCatalog.Regions[3].Groups.Select(group => group.Label).ToArray());
        Assert.Contains("Solihull / Birmingham", RegionOfficeCatalog.Regions[3].Groups[0].Offices);
        Assert.Contains("York", RegionOfficeCatalog.Regions[3].Groups[0].Offices);
        Assert.Contains("Mauritius (Bagatelle)", RegionOfficeCatalog.Regions[3].Groups[3].Offices);

        var names = RegionOfficeCatalog.OfficeNames;
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(RegionOfficeCatalog.Regions, region =>
        {
            if (region.Offices.Count > 0)
                Assert.Empty(region.Groups);
            Assert.All(region.Groups, group => Assert.Empty(group.Groups));
        });
    }

    [Fact]
    public void SelectedRegionOfficesRoundTripThroughSettings()
    {
        var region = new RegionSettingsViewModel();
        region.SetChecked("Australia & New Zealand", true);
        region.SetChecked("Americas", true);
        region.SetChecked("York", true);
        region.SetChecked("Boston", false);

        Assert.Null(region.Find("Americas")!.IsChecked);
        Assert.Equal(true, region.Find("Australia & New Zealand")!.IsChecked);
        Assert.DoesNotContain(region.SelectedOffices, name => name is "Americas" or "Australia & New Zealand" or "Boston");
        Assert.Contains("Sydney", region.SelectedOffices);
        Assert.Contains("York", region.SelectedOffices);
        Assert.Contains("New Jersey (Newark)", region.SelectedOffices);
        Assert.Contains("Bogotá", region.SelectedOffices);
        Assert.Equal(24, region.SelectedOffices.Count);

        var settings = new DeskSettings { RegionOffices = region.SelectedOffices.ToList() };
        var json = DeskSettingsFile.Serialize(settings, text => text ?? "");
        var loaded = DeskSettingsFile.Deserialize(json, text => text ?? "");
        Assert.Equal(region.SelectedOffices, loaded.RegionOffices);

        var again = new RegionSettingsViewModel();
        again.Load(loaded.RegionOffices);
        Assert.Equal(region.SelectedOffices, again.SelectedOffices);
        Assert.Equal(true, again.Find("Sydney")!.IsChecked);
        Assert.Equal(false, again.Find("Boston")!.IsChecked);
        Assert.Null(again.Find("Americas")!.IsChecked);

        var connection = new ConnectionViewModel();
        connection.Load(loaded);
        var built = connection.BuildSettings();
        Assert.Equal(loaded.RegionOffices, built.RegionOffices);

        var missing = DeskSettingsFile.Deserialize("{}", text => text ?? "");
        Assert.Null(missing.RegionOffices);
    }

    [Fact]
    public void LoadMatchedChecksTheOfficeAndNotALongerCity()
    {
        var picker = new RegionSettingsViewModel();
        picker.LoadMatched(["York", "Brisbane Office"]);
        Assert.True(picker.Find("York")!.IsChecked);
        Assert.NotEqual(true, picker.Find("New York")!.IsChecked);
        Assert.True(picker.Find("Brisbane")!.IsChecked);
        Assert.NotEqual(true, picker.Find("Sydney")!.IsChecked);
    }

    [Fact]
    public async Task HardwareRegionOverridesThisWindowOnly()
    {
        using var client = new SampleServiceNowClient();
        client.AddComputer(Computer("hw-cph", "CPH0001", "HP ZBook", "Copenhagen", "In use", ""));
        var store = new MemorySettingsStore();
        store.Save(new DeskSettings { RegionOffices = ["York"] });
        var workspace = new HardwareWorkspaceViewModel(store);
        workspace.Attach(client);
        workspace.RememberViewer(new CurrentUser("sample-user", "Alex Rivera", "alex.rivera", "alex.rivera@example.com")
        {
            Location = "Brisbane"
        });
        await workspace.RefreshAsync();

        Assert.Equal(["York"], store.Load().RegionOffices);
        Assert.False(store.Load().HardwareOfficeOverride);
        Assert.Contains("Showing your office: Brisbane.", workspace.OfficeStatus);

        workspace.RegionPicker.SetChecked("Denmark", true);
        await workspace.OfficeLoad;

        Assert.Equal(["York"], store.Load().RegionOffices);
        Assert.True(store.Load().HardwareOfficeOverride);
        Assert.Equal(["Copenhagen"], store.Load().HardwareOfficeLocations);
        Assert.Contains("Showing override: Copenhagen.", workspace.OfficeStatus);
        Assert.Equal("CPH0001", Assert.Single(workspace.Items).SerialNumber);
        Assert.DoesNotContain(workspace.Items, asset => asset.Location.Display.Contains("Hong Kong", StringComparison.OrdinalIgnoreCase));
        Assert.True(workspace.RegionPicker.Find("Copenhagen")!.IsChecked);
        Assert.NotEqual(true, workspace.RegionPicker.Find("New York")!.IsChecked);

        workspace.RegionPicker.SetChecked("Denmark", false);
        await workspace.OfficeLoad;

        Assert.Equal(["York"], store.Load().RegionOffices);
        Assert.False(store.Load().HardwareOfficeOverride);
        Assert.Null(store.Load().HardwareOfficeLocations);
        Assert.Contains("Showing your office: Brisbane.", workspace.OfficeStatus);
        Assert.NotEqual(true, workspace.RegionPicker.Find("Copenhagen")!.IsChecked);
    }

    private static HardwareAsset Computer(
        string sysId,
        string serial,
        string model,
        string location,
        string state,
        string substate) =>
        new()
        {
            SysId = sysId,
            SerialNumber = serial,
            Model = model,
            ModelCategory = HardwareCatalog.Computer,
            Location = new ReferenceValue(sysId + "-loc", location),
            InstallStatus = state,
            InstallStatusLabel = state,
            Substatus = substate,
            SubstatusLabel = substate
        };

    private static async Task<HardwareWorkspaceViewModel> OpenAllLocationsAsync(SampleServiceNowClient client)
    {
        var workspace = new HardwareWorkspaceViewModel(new MemorySettingsStore());
        workspace.Attach(client);
        await workspace.RefreshAsync();
        await workspace.SearchAllLocationsCommand.ExecuteAsync(null);
        return workspace;
    }
}
