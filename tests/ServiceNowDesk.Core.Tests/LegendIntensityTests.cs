using ServiceNowDesk.Alerts;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class LegendIntensityTests
{
    [Fact]
    public void DefaultIntensityLeavesTodaysLegendColoursUnchanged()
    {
        Assert.Equal("#C56A62", LegendColorIntensity.Apply(HighlightCatalog.UnassignedSwatchHex, 100));
        Assert.Equal("#F3E1E0", LegendColorIntensity.Apply(HighlightCatalog.UnassignedRowHex, 100));
        Assert.Equal("#FFFFFF", LegendColorIntensity.Apply("#C56A62", 0));
        Assert.Equal("#E2B5B1", LegendColorIntensity.Apply("#C56A62", 50));

        var legend = new LegendSettingsViewModel();
        Assert.False(legend.AdjustIndividually);
        Assert.Equal(LegendColorIntensity.Full, legend.SliderValue);
        Assert.Equal("All colours", legend.IntensityCaption);
        AssertMatchesCatalog(legend);

        var preferences = HighlightPreferences.From(new DeskSettings());
        Assert.Equal(LegendColorIntensity.Full, preferences.SharedIntensity);
        Assert.Equal(HighlightCatalog.UnassignedRowHex, preferences.ChooseRowHex(true, []));
        Assert.Equal(
            HighlightCatalog.Find(HighlightCatalog.SlaAssignedToYou)!.RowHex,
            preferences.ChooseSlaAssigneeHex(true));

        var changes = 0;
        legend.Changed += (_, _) => changes++;
        legend.Load(preferences);
        Assert.Equal(0, changes);
        AssertMatchesCatalog(legend);
        Assert.Null(Saved(legend).LegendIntensity);
        Assert.Null(Saved(legend).LegendColorIntensities);
    }

    [Fact]
    public void AllAtOnceAppliesOneIntensityToEveryColour()
    {
        var store = new MemorySettingsStore();
        store.Save(new DeskSettings { Password = "secret", UseSampleData = true });
        var main = new MainViewModel(store, new RecordingDesktopServices());
        main.Connection.Load(store.Load());
        main.Legend.Load(main.Connection.Highlights);

        var row = Row("inc9", unassigned: true);
        main.Incidents.ShowCachedRows([row], 1);
        main.Notifications.RememberViewer("sample-user", main.Legend.Current());
        main.Notifications.Apply(SlaSnapshot(), new AlertWatchState());
        main.Notifications.SelectQueueCommand.Execute(AlertKind.SlaBreaching);
        var mine = main.Notifications.DashboardRows.Single(item => item.Number == "INC9");
        var slaRow = HighlightCatalog.Find(HighlightCatalog.SlaAssignedToYou)!.RowHex;
        Assert.Equal(HighlightCatalog.UnassignedRowHex, main.Incidents.Items[0].HighlightHex);
        Assert.Equal(slaRow, mine.HighlightHex);

        main.Legend.SliderValue = 50;

        Assert.All(main.Legend.Entries, entry => AssertPainted(entry, 50));
        Assert.Equal(
            LegendColorIntensity.Apply(HighlightCatalog.UnassignedRowHex, 50),
            main.Incidents.Items[0].HighlightHex);
        Assert.Equal(LegendColorIntensity.Apply(slaRow, 50), mine.HighlightHex);
        Assert.Equal(50, store.Current.LegendIntensity);
        Assert.Null(store.Current.LegendColorIntensities);
        Assert.Equal("secret", store.Current.Password);
        Assert.True(store.Current.UseSampleData);
        Assert.All(HighlightCatalog.Entries, entry =>
            Assert.Equal(50, main.Legend.Current().IntensityOf(entry.Key)));
    }

    [Fact]
    public void IndividualSetChangesOnlyTheSelectedColour()
    {
        var legend = new LegendSettingsViewModel();
        legend.SliderValue = 60;
        legend.AdjustIndividually = true;
        Assert.Equal("Click a colour swatch", legend.IntensityCaption);
        Assert.False(legend.SetIntensityCommand.CanExecute(null));

        var unassigned = legend.Entries.Single(entry => entry.Key == HighlightCatalog.Unassigned);
        var sla = legend.Entries.Single(entry => entry.Key == HighlightCatalog.SlaBreaching);
        legend.Select(HighlightCatalog.Unassigned);
        Assert.True(unassigned.IsSelected);
        Assert.False(sla.IsSelected);
        Assert.Equal(60, legend.SliderValue);
        Assert.Equal("Unassigned", legend.IntensityCaption);
        Assert.True(legend.SetIntensityCommand.CanExecute(null));

        var changes = 0;
        legend.Changed += (_, _) => changes++;
        legend.SliderValue = 15;
        Assert.Equal(0, changes);
        AssertPainted(unassigned, 60);
        AssertPainted(sla, 60);

        legend.Select(HighlightCatalog.SlaBreaching);
        Assert.Equal(60, legend.SliderValue);
        Assert.True(sla.IsSelected);
        Assert.False(unassigned.IsSelected);
        AssertPainted(unassigned, 60);

        legend.Select(HighlightCatalog.Unassigned);
        legend.SliderValue = 15;
        legend.SetIntensityCommand.Execute(null);

        Assert.Equal(1, changes);
        AssertPainted(unassigned, 15);
        Assert.All(
            legend.Entries.Where(entry => entry.Key != HighlightCatalog.Unassigned),
            entry => AssertPainted(entry, 60));
        Assert.Equal(
            LegendColorIntensity.Apply(HighlightCatalog.UnassignedRowHex, 15),
            legend.Current().ChooseRowHex(true, []));
        Assert.Equal(
            LegendColorIntensity.Apply(HighlightCatalog.Find(HighlightCatalog.SlaBreaching)!.RowHex, 60),
            legend.Current().ChooseRowHex(false, [AlertKind.SlaBreaching]));
        Assert.Equal(60, legend.Current().SharedIntensity);
        Assert.Equal(15, legend.Current().IntensityOf(HighlightCatalog.Unassigned));
        Assert.Equal(60, legend.Current().IntensityOf(HighlightCatalog.SlaBreaching));

        var saved = Saved(legend);
        Assert.Equal(60, saved.LegendIntensity);
        Assert.NotNull(saved.LegendColorIntensities);
        Assert.Equal(15, saved.LegendColorIntensities[HighlightCatalog.Unassigned]);
        Assert.Single(saved.LegendColorIntensities);

        legend.AdjustIndividually = false;
        Assert.Equal("All colours", legend.IntensityCaption);
        Assert.Equal(60, legend.SliderValue);
        Assert.False(unassigned.IsSelected);
        AssertPainted(unassigned, 15);
        legend.SliderValue = 80;
        Assert.All(legend.Entries, entry => AssertPainted(entry, 80));
        Assert.Equal(80, Saved(legend).LegendIntensity);
        Assert.Null(Saved(legend).LegendColorIntensities);
    }

    [Fact]
    public void IntensitiesPersistAndLoadBack()
    {
        var store = new MemorySettingsStore();
        store.Save(new DeskSettings { Password = "secret", UseSampleData = true, InstanceUrl = "https://example.service-now.com" });
        var main = new MainViewModel(store, new RecordingDesktopServices());
        main.Connection.Load(store.Load());
        main.Legend.Load(main.Connection.Highlights);
        main.Legend.SliderValue = 60;
        main.Legend.AdjustIndividually = true;
        main.Legend.Select(HighlightCatalog.Unassigned);
        main.Legend.SliderValue = 20;
        main.Legend.SetIntensityCommand.Execute(null);

        var json = DeskSettingsFile.Serialize(store.Current, text => text ?? "");
        var loaded = DeskSettingsFile.Deserialize(json, text => text ?? "");
        Assert.Equal(60, loaded.LegendIntensity);
        Assert.Equal(20, loaded.LegendColorIntensities![HighlightCatalog.Unassigned]);
        Assert.Equal("secret", loaded.Password);
        Assert.True(loaded.UseSampleData);

        var again = new LegendSettingsViewModel();
        again.Load(HighlightPreferences.From(loaded));
        Assert.False(again.AdjustIndividually);
        Assert.Equal(60, again.SliderValue);
        Assert.Equal("All colours", again.IntensityCaption);
        var unassigned = again.Entries.Single(entry => entry.Key == HighlightCatalog.Unassigned);
        AssertPainted(unassigned, 20);
        Assert.All(
            again.Entries.Where(entry => entry.Key != HighlightCatalog.Unassigned),
            entry => AssertPainted(entry, 60));

        var connection = new ConnectionViewModel();
        connection.Load(loaded);
        var built = connection.BuildSettings();
        Assert.Equal(60, built.LegendIntensity);
        Assert.Equal(20, built.LegendColorIntensities![HighlightCatalog.Unassigned]);
        Assert.Equal("secret", built.Password);

        var legacy = DeskSettingsFile.Deserialize("{\"InstanceUrl\":\"https://example.service-now.com\"}", text => text ?? "");
        var fresh = new LegendSettingsViewModel();
        fresh.Load(HighlightPreferences.From(legacy));
        AssertMatchesCatalog(fresh);

        var clamped = HighlightPreferences.From(new DeskSettings
        {
            LegendIntensity = 140,
            LegendColorIntensities = new Dictionary<string, int>
            {
                ["UNASSIGNED"] = -20,
                ["nope"] = 10
            }
        });
        Assert.Equal(LegendColorIntensity.Full, clamped.SharedIntensity);
        Assert.Equal(LegendColorIntensity.Minimum, clamped.IntensityOf(HighlightCatalog.Unassigned));
        Assert.Equal(LegendColorIntensity.Full, clamped.IntensityOf(HighlightCatalog.SlaBreaching));
        Assert.Equal(
            LegendColorIntensity.Apply(HighlightCatalog.UnassignedRowHex, 0),
            clamped.ChooseRowHex(true, []));
    }

    private static void AssertMatchesCatalog(LegendSettingsViewModel legend)
    {
        Assert.All(legend.Entries, entry => AssertPainted(entry, LegendColorIntensity.Full));
    }

    private static void AssertPainted(LegendEntryModel entry, int intensity)
    {
        var catalog = HighlightCatalog.Find(entry.Key);
        Assert.NotNull(catalog);
        Assert.Equal(LegendColorIntensity.Apply(catalog.SwatchHex, intensity), entry.SwatchHex);
        Assert.Equal(LegendColorIntensity.Apply(catalog.RowHex, intensity), entry.RowHex);
        Assert.NotEqual(entry.SwatchHex, entry.RowHex);
    }

    private static DeskSettings Saved(LegendSettingsViewModel legend)
    {
        var settings = new DeskSettings();
        legend.Current().ApplyTo(settings);
        return settings;
    }

    private static TicketRow Row(string sysId, bool unassigned) => new()
    {
        SysId = sysId,
        Number = sysId,
        Title = "Printer",
        StateLabel = "Open",
        Tone = "",
        Meta = "",
        When = "",
        Unassigned = unassigned
    };

    private static AlertSnapshot SlaSnapshot() => new(new Dictionary<AlertKind, AlertBucket>
    {
        [AlertKind.SlaBreaching] = new(
        [
            new AlertRecord(
                AlertKind.SlaBreaching,
                DeskSection.Incidents,
                "inc-mine",
                "INC9",
                "Mine",
                "In Progress",
                "Client Services",
                "Brisbane",
                "2026-10-04",
                "Alex Rivera",
                "sample-user")
        ], 1)
    });
}
