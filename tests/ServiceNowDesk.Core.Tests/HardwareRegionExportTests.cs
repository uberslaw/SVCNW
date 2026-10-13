using System.Text.Json;
using ServiceNowDesk.Client;
using ServiceNowDesk.Mapping;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class HardwareRegionExportTests
{
    [Fact]
    public void CsvKeepsModelConfigurationItemAndAssignee()
    {
        var monitor = Computer("mon", "Monitor", "Dock", "Sydney Office", HardwareCatalog.InUse, "DOCK-01", "Alex Rivera");
        var numbered = Computer("num", HardwareCatalog.Computer, "HP EliteBook", "Sydney Office", "1", "SYD-EB-01", "Alex Rivera");
        Assert.False(HardwareRegionExport.IsInUseComputer(monitor));
        Assert.False(HardwareRegionExport.IsInUseComputer(numbered));
        Assert.True(HardwareRegionExport.IsInUseComputer(numbered, [new Choice("1", HardwareCatalog.InUse)]));
        Assert.False(HardwareRegionExport.InRegion(numbered, []));
        Assert.False(HardwareRegionExport.InRegion(
            Computer("ny", HardwareCatalog.Computer, "NY Laptop", "New York", HardwareCatalog.InUse, "NY-01", "Alex Rivera"),
            ["York"]));

        var csv = HardwareRegionExport.Format(
        [
            Computer("a", HardwareCatalog.Computer, "Dell Latitude 5450", "York Office", HardwareCatalog.InUse, "YORK-LAT-02", "Lee, Jordan"),
            Computer("b", HardwareCatalog.Computer, "HP EliteBook", "Sydney Office", HardwareCatalog.InUse, "SYD-EB-03", ""),
            numbered
        ]);

        Assert.Equal(
            """
            Model,Configuration item,Assigned to
            HP EliteBook,SYD-EB-03,
            HP EliteBook,SYD-EB-01,Alex Rivera
            Dell Latitude 5450,YORK-LAT-02,"Lee, Jordan"
            """ + "\n",
            csv);
    }

    [Fact]
    public void MapperReadsTheConfigurationItem()
    {
        using var document = JsonDocument.Parse("""
            {
              "sys_id": {"value":"hw-1","display_value":"hw-1"},
              "serial_number": {"value":"ABC","display_value":"ABC"},
              "display_name": {"value":"HP ZBook","display_value":"HP ZBook"},
              "model": {"value":"model-1","display_value":"HP ZBook Ultra"},
              "model_category": {"value":"cat-computer","display_value":"Computer"},
              "ci": {"value":"ci-1","display_value":"SYD-ZB16-01"},
              "assigned_to": {"value":"user-jordan","display_value":"Jordan Lee"},
              "location": {"value":"loc-syd","display_value":"Sydney Office"},
              "install_status": {"value":"1","display_value":"In use"},
              "substatus": {"value":"","display_value":""},
              "stockroom": {"value":"","display_value":""},
              "comments": {"value":"","display_value":""}
            }
            """);

        var asset = RecordMapper.Hardware(document.RootElement);
        Assert.Equal("SYD-ZB16-01", asset.ConfigurationItem.Display);
        Assert.Equal("ci-1", asset.ConfigurationItem.SysId);
        Assert.Equal("HP ZBook Ultra", asset.Model);
        Assert.Equal("Jordan Lee", asset.AssignedTo.Display);
        Assert.Equal(HardwareCatalog.Computer, asset.ModelCategory);
        Assert.True(HardwareRegionExport.IsInUseComputer(asset));
    }

    [Fact]
    public async Task DownloadUsesThePageRegionAndKeepsInUseComputers()
    {
        using var client = new SampleServiceNowClient();
        client.AddComputer(Computer("hw-york", HardwareCatalog.Computer, "Dell Latitude 5450", "York Office", HardwareCatalog.InUse, "YORK-LAT-02", "Lee, Jordan"));
        client.AddComputer(Computer("hw-active", HardwareCatalog.Computer, "Active Laptop", "Sydney Office", "Active", "SYD-ACT-01", "Alex Rivera"));
        client.AddComputer(Computer("hw-ny", HardwareCatalog.Computer, "NY Laptop", "New York", HardwareCatalog.InUse, "NY-01", "Alex Rivera"));
        client.AddComputer(Computer("hw-open", HardwareCatalog.Computer, "HP EliteBook", "Sydney Office", HardwareCatalog.InUse, "SYD-EB-03", ""));

        var store = new MemorySettingsStore();
        store.Save(new DeskSettings { RegionOffices = ["Hong Kong"] });
        var desktop = new RecordingDesktopServices();
        var workspace = new HardwareWorkspaceViewModel(store);
        workspace.RegionPicker.SetChecked("Sydney", true);
        workspace.RegionPicker.SetChecked("York", true);
        workspace.UseDesktop(desktop);
        workspace.Attach(client);

        await workspace.DownloadRegionLaptopsCommand.ExecuteAsync(null);

        var path = Assert.Single(desktop.SavedFiles);
        Assert.Equal("region-laptops.csv", Path.GetFileName(path));
        var csv = File.ReadAllText(path);
        Assert.Equal(
            """
            Model,Configuration item,Assigned to
            HP EliteBook,SYD-EB-03,
            HP ZBook Ultra 16 inch G1i Mobile Workstation,SYD-ZB16-01,Jordan Lee
            Dell Latitude 5450,YORK-LAT-02,"Lee, Jordan"
            """ + "\n",
            csv);
        Assert.DoesNotContain("Active Laptop", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("NY Laptop", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("HP ZBook Fury 16 G9", csv, StringComparison.Ordinal);
        Assert.Contains("Sydney", client.LastHardwareQuery, StringComparison.Ordinal);
        Assert.Contains("York", client.LastHardwareQuery, StringComparison.Ordinal);
        Assert.StartsWith("Saved 3 computers to ", workspace.ExportNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadUsesSettingsWhenThePageHasNoRegion()
    {
        using var client = new SampleServiceNowClient();
        var store = new MemorySettingsStore();
        store.Save(new DeskSettings { RegionOffices = ["Sydney"] });
        var desktop = new RecordingDesktopServices();
        var workspace = new HardwareWorkspaceViewModel(store);
        workspace.RememberViewer(new CurrentUser("sample-user", "Alex Rivera", "alex", "alex@example.com") { Location = "Brisbane Office" });
        workspace.UseDesktop(desktop);
        workspace.Attach(client);
        await workspace.RefreshAsync();

        Assert.DoesNotContain(workspace.Items, asset => asset.SerialNumber == "5CG0000DBR");

        await workspace.DownloadRegionLaptopsCommand.ExecuteAsync(null);

        var csv = File.ReadAllText(Assert.Single(desktop.SavedFiles));
        Assert.Contains("SYD-ZB16-01", csv, StringComparison.Ordinal);
        Assert.Contains("Jordan Lee", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("HP ZBook Ultra G1a", csv, StringComparison.Ordinal);
        Assert.Contains(workspace.Items, asset => asset.Location.Display == "Brisbane Office");
        Assert.DoesNotContain(workspace.Items, asset => asset.SerialNumber == "5CG0000DBR");
    }

    [Fact]
    public async Task DownloadWithoutARegionOrDesktopDoesNotWriteAFile()
    {
        using var client = new SampleServiceNowClient();
        var desktop = new RecordingDesktopServices();
        var missing = new HardwareWorkspaceViewModel(new MemorySettingsStore());
        missing.UseDesktop(desktop);
        missing.Attach(client);
        await missing.DownloadRegionLaptopsCommand.ExecuteAsync(null);
        Assert.Equal(HardwareWorkspaceViewModel.RegionExportMissingMessage, missing.ExportNote);
        Assert.Empty(desktop.SavedFiles);

        var store = new MemorySettingsStore();
        store.Save(new DeskSettings { RegionOffices = ["Sydney"] });
        var unavailable = new HardwareWorkspaceViewModel(store);
        unavailable.Attach(client);
        await unavailable.DownloadRegionLaptopsCommand.ExecuteAsync(null);
        Assert.Equal(HardwareWorkspaceViewModel.RegionExportUnavailableMessage, unavailable.ExportNote);
        Assert.Empty(desktop.SavedFiles);

        var busy = new HardwareWorkspaceViewModel(store);
        busy.UseDesktop(desktop);
        busy.Attach(client);
        busy.IsDownloading = true;
        await busy.DownloadRegionLaptopsCommand.ExecuteAsync(null);
        Assert.Equal(HardwareWorkspaceViewModel.RegionExportBusyMessage, busy.ExportNote);
        Assert.Empty(desktop.SavedFiles);
    }

    [Fact]
    public void HardwarePageOffersTheRegionDownload()
    {
        var view = RepoFile("src/ServiceNowDesk.App/Views/HardwareView.xaml");
        Assert.Contains("DownloadRegionLaptopsCommand", view, StringComparison.Ordinal);
        Assert.Contains("ExportNote", view, StringComparison.Ordinal);
        Assert.Contains("Download region laptops", view, StringComparison.Ordinal);
    }

    private static HardwareAsset Computer(
        string sysId,
        string category,
        string model,
        string location,
        string state,
        string configurationItem,
        string assignedTo) =>
        new()
        {
            SysId = sysId,
            SerialNumber = sysId,
            Model = model,
            ModelCategory = category,
            Location = new ReferenceValue(sysId + "-loc", location),
            InstallStatus = state,
            InstallStatusLabel = state,
            ConfigurationItem = new ReferenceValue(sysId + "-ci", configurationItem),
            AssignedTo = string.IsNullOrEmpty(assignedTo)
                ? ReferenceValue.Empty
                : new ReferenceValue(sysId + "-user", assignedTo)
        };

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ServiceNowDesk.sln")))
                return File.ReadAllText(Path.Combine(dir.FullName, relative));
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not find ServiceNowDesk.sln from " + AppContext.BaseDirectory);
    }
}
