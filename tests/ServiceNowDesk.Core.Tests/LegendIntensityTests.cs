using ServiceNowDesk.Alerts;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class LegendIntensityTests
{
    private static readonly Dictionary<string, int> Measured =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [HighlightCatalog.SlaBreaching] = 74,
            [HighlightCatalog.SlaAssignedToYou] = 79,
            [HighlightCatalog.OnHoldPastFollowUp] = 75,
            [HighlightCatalog.UpdatedByCaller] = 58,
            [HighlightCatalog.ReturnedWithNotes] = 57,
            [HighlightCatalog.Unattended] = 80,
            [HighlightCatalog.Unassigned] = 42,
            [HighlightCatalog.AssignedToMe] = 79,
            [HighlightCatalog.WatchedGroup] = 68
        };

    [Fact]
    public void EachColourDefaultsToItsMeasuredWhiteBlackLightness()
    {
        Assert.Equal(0, LegendColorIntensity.Measure("#FFFFFF"));
        Assert.Equal(100, LegendColorIntensity.Measure("#000000"));
        Assert.Equal(50, LegendColorIntensity.Measure("#FF0000"));
        Assert.Equal("#FFFFFF", LegendColorIntensity.Swatch("#C56A62", 0));
        Assert.Equal("#000000", LegendColorIntensity.Swatch("#C56A62", 100));
        Assert.Equal("#C56A62", LegendColorIntensity.Swatch(HighlightCatalog.UnassignedSwatchHex, 42));

        var legend = new LegendSettingsViewModel();
        Assert.False(legend.AdjustIndividually);
        Assert.Equal(68, legend.SliderValue);
        Assert.NotEqual(100, legend.SliderValue);
        Assert.Equal("All colours", legend.IntensityCaption);
        AssertMatchesCatalog(legend);

        legend.AdjustIndividually = true;
        foreach (var entry in legend.Entries)
        {
            var catalog = HighlightCatalog.Find(entry.Key)!;
            var measured = LegendColorIntensity.Measure(catalog.SwatchHex);
            Assert.Equal(Measured[entry.Key], measured);
            Assert.InRange(measured, 1, 99);
            legend.Select(entry.Key);
            Assert.Equal(measured, legend.SliderValue);
            Assert.Equal(catalog.SwatchHex, entry.SwatchHex);
            Assert.Equal(catalog.RowHex, entry.RowHex);
        }

        legend.AdjustIndividually = false;
        Assert.Equal(68, legend.SliderValue);
        AssertMatchesCatalog(legend);

        var preferences = HighlightPreferences.From(new DeskSettings());
        Assert.Null(preferences.SharedIntensity);
        Assert.Equal(42, preferences.IntensityOf(HighlightCatalog.Unassigned));
        Assert.Equal(HighlightCatalog.UnassignedRowHex, preferences.ChooseRowHex(true, []));
        Assert.Equal(
            HighlightCatalog.Find(HighlightCatalog.SlaAssignedToYou)!.RowHex,
            preferences.ChooseSlaAssigneeHex(true));

        var changes = 0;
        legend.Changed += (_, _) => changes++;
        legend.Load(preferences);
        Assert.Equal(0, changes);
        Assert.Equal(68, legend.SliderValue);
        AssertMatchesCatalog(legend);
        Assert.Null(Saved(legend).LegendIntensity);
        Assert.Null(Saved(legend).LegendColorIntensities);
    }

    [Fact]
    public void AllColoursStartsAtTheAverageAndRecoloursOnlyWhenMoved()
    {
        var store = new MemorySettingsStore();
        store.Save(new DeskSettings { Password = "secret", UseSampleData = true });
        var main = new MainViewModel(store, new RecordingDesktopServices());
        main.Connection.Load(store.Load());
        main.Legend.Load(main.Connection.Highlights);
        Assert.Equal(68, main.Legend.SliderValue);
        AssertMatchesCatalog(main.Legend);

        var row = Row("inc9", unassigned: true);
        main.Incidents.ShowCachedRows([row], 1);
        main.Notifications.RememberViewer("sample-user", main.Legend.Current());
        main.Notifications.Apply(SlaSnapshot(), new AlertWatchState());
        main.Notifications.SelectQueueCommand.Execute(AlertKind.SlaBreaching);
        var mine = main.Notifications.DashboardRows.Single(item => item.Number == "INC9");
        var slaRow = HighlightCatalog.Find(HighlightCatalog.SlaAssignedToYou)!.RowHex;
        Assert.Equal(HighlightCatalog.UnassignedRowHex, main.Incidents.Items[0].HighlightHex);
        Assert.Equal(slaRow, mine.HighlightHex);
        Assert.Null(store.Current.LegendIntensity);

        main.Legend.SliderValue = 35;

        Assert.All(main.Legend.Entries, entry => AssertAtLightness(entry, 35));
        var unassigned = main.Legend.Entries.Single(entry => entry.Key == HighlightCatalog.Unassigned);
        var sla = main.Legend.Entries.Single(entry => entry.Key == HighlightCatalog.SlaBreaching);
        Assert.NotEqual(unassigned.SwatchHex, sla.SwatchHex);
        Assert.Equal(35, LegendColorIntensity.Measure(unassigned.SwatchHex));
        Assert.Equal(35, LegendColorIntensity.Measure(sla.SwatchHex));
        Assert.Equal(
            LegendColorIntensity.Row(HighlightCatalog.UnassignedSwatchHex, HighlightCatalog.UnassignedRowHex, 35),
            main.Incidents.Items[0].HighlightHex);
        var slaTint = LegendColorIntensity.Row(
            HighlightCatalog.Find(HighlightCatalog.SlaAssignedToYou)!.SwatchHex,
            slaRow,
            35);
        Assert.Equal(slaTint, main.Connection.Highlights.ChooseSlaAssigneeHex(true));
        main.Notifications.RememberViewer("sample-user", main.Connection.Highlights);
        Assert.Equal(slaTint, mine.HighlightHex);
        Assert.Equal(35, store.Current.LegendIntensity);
        Assert.Null(store.Current.LegendColorIntensities);
        Assert.Equal("secret", store.Current.Password);
        Assert.True(store.Current.UseSampleData);
        Assert.All(HighlightCatalog.Entries, entry =>
            Assert.Equal(35, main.Legend.Current().IntensityOf(entry.Key)));
    }

    [Fact]
    public void IndividualSetChangesOnlyTheSelectedColourLightness()
    {
        var legend = new LegendSettingsViewModel();
        Assert.Equal(68, legend.SliderValue);
        legend.AdjustIndividually = true;
        Assert.Equal("Click a colour swatch", legend.IntensityCaption);
        Assert.False(legend.SetIntensityCommand.CanExecute(null));
        AssertMatchesCatalog(legend);

        var unassigned = legend.Entries.Single(entry => entry.Key == HighlightCatalog.Unassigned);
        var sla = legend.Entries.Single(entry => entry.Key == HighlightCatalog.SlaBreaching);
        legend.Select(HighlightCatalog.Unassigned);
        Assert.True(unassigned.IsSelected);
        Assert.False(sla.IsSelected);
        Assert.Equal(42, legend.SliderValue);
        Assert.Equal("Unassigned", legend.IntensityCaption);
        Assert.True(legend.SetIntensityCommand.CanExecute(null));
        Assert.Equal(HighlightCatalog.UnassignedSwatchHex, unassigned.SwatchHex);

        var changes = 0;
        legend.Changed += (_, _) => changes++;
        legend.SliderValue = 20;
        Assert.Equal(0, changes);
        AssertMatchesCatalog(legend);

        legend.Select(HighlightCatalog.SlaBreaching);
        Assert.Equal(74, legend.SliderValue);
        Assert.True(sla.IsSelected);
        Assert.False(unassigned.IsSelected);
        Assert.Equal(HighlightCatalog.Find(HighlightCatalog.SlaBreaching)!.SwatchHex, sla.SwatchHex);

        legend.Select(HighlightCatalog.Unassigned);
        Assert.Equal(42, legend.SliderValue);
        legend.SliderValue = 20;
        legend.SetIntensityCommand.Execute(null);

        Assert.Equal(1, changes);
        AssertAtLightness(unassigned, 20);
        Assert.All(
            legend.Entries.Where(entry => entry.Key != HighlightCatalog.Unassigned),
            entry =>
            {
                var catalog = HighlightCatalog.Find(entry.Key)!;
                Assert.Equal(catalog.SwatchHex, entry.SwatchHex);
                Assert.Equal(catalog.RowHex, entry.RowHex);
            });
        Assert.Equal(
            LegendColorIntensity.Row(HighlightCatalog.UnassignedSwatchHex, HighlightCatalog.UnassignedRowHex, 20),
            legend.Current().ChooseRowHex(true, []));
        Assert.Equal(
            HighlightCatalog.Find(HighlightCatalog.SlaBreaching)!.RowHex,
            legend.Current().ChooseRowHex(false, [AlertKind.SlaBreaching]));
        Assert.Null(legend.Current().SharedIntensity);
        Assert.Equal(20, legend.Current().IntensityOf(HighlightCatalog.Unassigned));
        Assert.Equal(74, legend.Current().IntensityOf(HighlightCatalog.SlaBreaching));

        var saved = Saved(legend);
        Assert.Null(saved.LegendIntensity);
        Assert.NotNull(saved.LegendColorIntensities);
        Assert.Equal(20, saved.LegendColorIntensities[HighlightCatalog.Unassigned]);
        Assert.Single(saved.LegendColorIntensities);

        legend.AdjustIndividually = false;
        Assert.Equal("All colours", legend.IntensityCaption);
        Assert.Equal(66, legend.SliderValue);
        Assert.False(unassigned.IsSelected);
        AssertAtLightness(unassigned, 20);
        Assert.Equal(HighlightCatalog.Find(HighlightCatalog.SlaBreaching)!.SwatchHex, sla.SwatchHex);

        legend.SliderValue = 35;
        Assert.All(legend.Entries, entry => AssertAtLightness(entry, 35));
        Assert.NotEqual(
            legend.Entries.Single(entry => entry.Key == HighlightCatalog.Unassigned).SwatchHex,
            legend.Entries.Single(entry => entry.Key == HighlightCatalog.WatchedGroup).SwatchHex);
        Assert.Equal(35, Saved(legend).LegendIntensity);
        Assert.Null(Saved(legend).LegendColorIntensities);
    }

    [Fact]
    public void ASavedLightnessLoadsBackAndAMissingValueUsesTheMeasuredDefault()
    {
        var store = new MemorySettingsStore();
        store.Save(new DeskSettings { Password = "secret", UseSampleData = true, InstanceUrl = "https://example.service-now.com" });
        var main = new MainViewModel(store, new RecordingDesktopServices());
        main.Connection.Load(store.Load());
        main.Legend.Load(main.Connection.Highlights);
        main.Legend.AdjustIndividually = true;
        main.Legend.Select(HighlightCatalog.Unassigned);
        main.Legend.SliderValue = 20;
        main.Legend.SetIntensityCommand.Execute(null);

        var json = DeskSettingsFile.Serialize(store.Current, text => text ?? "");
        var loaded = DeskSettingsFile.Deserialize(json, text => text ?? "");
        Assert.Null(loaded.LegendIntensity);
        Assert.Equal(20, loaded.LegendColorIntensities![HighlightCatalog.Unassigned]);
        Assert.Equal("secret", loaded.Password);
        Assert.True(loaded.UseSampleData);

        var again = new LegendSettingsViewModel();
        again.Load(HighlightPreferences.From(loaded));
        Assert.False(again.AdjustIndividually);
        Assert.Equal(66, again.SliderValue);
        var unassigned = again.Entries.Single(entry => entry.Key == HighlightCatalog.Unassigned);
        AssertAtLightness(unassigned, 20);
        Assert.All(
            again.Entries.Where(entry => entry.Key != HighlightCatalog.Unassigned),
            entry =>
            {
                var catalog = HighlightCatalog.Find(entry.Key)!;
                Assert.Equal(catalog.SwatchHex, entry.SwatchHex);
                Assert.Equal(catalog.RowHex, entry.RowHex);
            });

        var connection = new ConnectionViewModel();
        connection.Load(loaded);
        var built = connection.BuildSettings();
        Assert.Null(built.LegendIntensity);
        Assert.Equal(20, built.LegendColorIntensities![HighlightCatalog.Unassigned]);
        Assert.Equal("secret", built.Password);

        var legacy = DeskSettingsFile.Deserialize("{\"InstanceUrl\":\"https://example.service-now.com\"}", text => text ?? "");
        var fresh = new LegendSettingsViewModel();
        fresh.Load(HighlightPreferences.From(legacy));
        Assert.Equal(68, fresh.SliderValue);
        AssertMatchesCatalog(fresh);

        var shared = new DeskSettings { LegendIntensity = 35 };
        var restored = new LegendSettingsViewModel();
        restored.Load(HighlightPreferences.From(shared));
        Assert.Equal(35, restored.SliderValue);
        Assert.All(restored.Entries, entry => AssertAtLightness(entry, 35));

        var clamped = HighlightPreferences.From(new DeskSettings
        {
            LegendIntensity = 140,
            LegendColorIntensities = new Dictionary<string, int>
            {
                ["UNASSIGNED"] = -20,
                ["nope"] = 10
            }
        });
        Assert.Equal(100, clamped.SharedIntensity);
        Assert.Equal(0, clamped.IntensityOf(HighlightCatalog.Unassigned));
        Assert.Equal(100, clamped.IntensityOf(HighlightCatalog.SlaBreaching));
        Assert.Equal("#FFFFFF", LegendColorIntensity.Swatch(HighlightCatalog.UnassignedSwatchHex, clamped.IntensityOf(HighlightCatalog.Unassigned)));
        Assert.Equal("#000000", LegendColorIntensity.Swatch(HighlightCatalog.Find(HighlightCatalog.SlaBreaching)!.SwatchHex, 100));
    }

    private static void AssertMatchesCatalog(LegendSettingsViewModel legend)
    {
        Assert.All(legend.Entries, entry =>
        {
            var catalog = HighlightCatalog.Find(entry.Key);
            Assert.NotNull(catalog);
            Assert.Equal(catalog.SwatchHex, entry.SwatchHex);
            Assert.Equal(catalog.RowHex, entry.RowHex);
            Assert.Equal(Measured[entry.Key], LegendColorIntensity.Measure(entry.SwatchHex));
        });
    }

    private static void AssertAtLightness(LegendEntryModel entry, int percent)
    {
        var catalog = HighlightCatalog.Find(entry.Key);
        Assert.NotNull(catalog);
        Assert.Equal(LegendColorIntensity.Swatch(catalog.SwatchHex, percent), entry.SwatchHex);
        Assert.Equal(LegendColorIntensity.Row(catalog.SwatchHex, catalog.RowHex, percent), entry.RowHex);
        Assert.Equal(percent, LegendColorIntensity.Measure(entry.SwatchHex));
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
