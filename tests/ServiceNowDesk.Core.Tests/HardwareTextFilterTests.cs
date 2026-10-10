using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class HardwareTextFilterTests
{
    [Theory]
    [InlineData("HP EliteBook Z6 G5", "*Z6*G5*", true)]
    [InlineData("HP EliteBook Z6 G5", "*z6*g5*", true)]
    [InlineData("HP EliteBook ZBook G5", "*Z6*G5*", false)]
    [InlineData("HP EliteBook Z6 G9", "*Z6*G5*", false)]
    [InlineData("HP EliteBook Z6 G5", "Z6", true)]
    [InlineData("HP EliteBook Z6 G5", "HP Elite*", true)]
    [InlineData("HP EliteBook Z6 G5", "elite*", false)]
    [InlineData("HP EliteBook Z6 G5", "*G5", true)]
    [InlineData("HP EliteBook Z6 G5", "Z6*", false)]
    [InlineData("HP EliteBook Z6 G5", "*EliteBook Z? G5", true)]
    [InlineData("HP EliteBook Z6 G5", "*EliteBook Z?? G5", false)]
    [InlineData("HP EliteBook Z6 G5", "Z6 + G5", true)]
    [InlineData("HP EliteBook Z6 G5", "Z6 + G9", false)]
    [InlineData("HP EliteBook Z6 G5", "*Z6* + *G5*", true)]
    [InlineData("", "*", true)]
    [InlineData("anything", "", true)]
    [InlineData(null, "Z6", false)]
    public void GlobRulesMatchAsDocumented(string? value, string expression, bool expected) =>
        Assert.Equal(expected, HardwareTextFilter.Matches(value, expression));

    [Fact]
    public void ColumnFilterUsesTheSameWildcardRules()
    {
        Assert.True(HardwareCatalog.MatchesColumn("HP EliteBook Z6 G5", "*Z6*G5*"));
        Assert.False(HardwareCatalog.MatchesColumn("HP ZBook Fury 16 G9", "*Z6*G5*"));
        Assert.True(HardwareCatalog.MatchesColumn("5CD6220GYW", "5CD"));
    }

    [Fact]
    public async Task ModelColumnWildcardFiltersTheVisibleRows()
    {
        using var client = new SampleServiceNowClient();
        var workspace = new HardwareWorkspaceViewModel(new MemorySettingsStore());
        workspace.Attach(client);
        await workspace.RefreshAsync();
        await workspace.SearchAllLocationsCommand.ExecuteAsync(null);

        workspace.ModelFilter = "*Fury*G9*";
        await workspace.ColumnFiltersReady;
        var fury = Assert.Single(workspace.Items);
        Assert.Equal("HP ZBook Fury 16 G9", fury.Model);

        workspace.ModelFilter = "*Z6*G5*";
        await workspace.ColumnFiltersReady;
        Assert.Empty(workspace.Items);

        workspace.ModelFilter = "HP ZBook*";
        await workspace.ColumnFiltersReady;
        Assert.All(workspace.Items, asset =>
            Assert.StartsWith("HP ZBook", asset.Model, StringComparison.OrdinalIgnoreCase));
        Assert.True(workspace.Items.Count >= 2);

        workspace.ModelFilter = "*G1a* + *Workstation*";
        await workspace.ColumnFiltersReady;
        Assert.Equal(
            "HP ZBook Ultra G1a 14 inch Mobile Workstation PC",
            Assert.Single(workspace.Items).Model);
    }

    [Fact]
    public void SearchTipsDescribeWildcardsAndStayAvailableOnTheWorkspace()
    {
        var workspace = new HardwareWorkspaceViewModel(new MemorySettingsStore());
        Assert.Contains("*", workspace.SearchTipsBody, StringComparison.Ordinal);
        Assert.Contains("Z6", workspace.SearchTipsBody, StringComparison.Ordinal);
        Assert.False(workspace.SearchTipsOpen);

        workspace.ShowSearchTipsCommand.Execute(null);
        Assert.True(workspace.SearchTipsOpen);

        workspace.CloseSearchTipsCommand.Execute(null);
        Assert.False(workspace.SearchTipsOpen);
    }

    [Fact]
    public void MatchesSearchHonorsModelWildcards()
    {
        var asset = new HardwareAsset
        {
            SysId = "hw1",
            SerialNumber = "ABC",
            Model = "HP EliteBook Z6 G5",
            ModelCategory = HardwareCatalog.Computer,
            AssignedTo = new ReferenceValue("", "Sam"),
            Location = new ReferenceValue("", "Brisbane Office")
        };

        Assert.True(HardwareCatalog.MatchesSearch(asset, "*Z6*G5*"));
        Assert.False(HardwareCatalog.MatchesSearch(asset, "*Z6*G9*"));
        Assert.True(HardwareCatalog.MatchesSearch(asset, "HP Elite*"));
        Assert.True(HardwareCatalog.MatchesSearch(asset, "Z6"));
    }
}
