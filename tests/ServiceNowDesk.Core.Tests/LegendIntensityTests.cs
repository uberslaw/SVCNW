using ServiceNowDesk.Alerts;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class LegendIntensityTests
{
    private static readonly Dictionary<string, int> ClosestPosition =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [HighlightCatalog.SlaBreaching] = 100,
            [HighlightCatalog.SlaAssignedToYou] = 100,
            [HighlightCatalog.OnHoldPastFollowUp] = 100,
            [HighlightCatalog.UpdatedByCaller] = 89,
            [HighlightCatalog.ReturnedWithNotes] = 87,
            [HighlightCatalog.Unattended] = 100,
            [HighlightCatalog.Unassigned] = 42,
            [HighlightCatalog.AssignedToMe] = 100,
            [HighlightCatalog.WatchedGroup] = 100
        };

    [Fact]
    public void TheCurveWashesThenSaturatesAndStopsShortOfBlack()
    {
        var washed = LegendColorIntensity.Sample(0);
        var vivid = LegendColorIntensity.Sample(LegendColorIntensity.Vivid);
        var dark = LegendColorIntensity.Sample(100);
        Assert.Equal(0.20, washed.Saturation);
        Assert.Equal(0.90, washed.Lightness);
        Assert.Equal(1.00, vivid.Saturation);
        Assert.Equal(0.46, vivid.Lightness);
        Assert.Equal(1.00, dark.Saturation);
        Assert.Equal(0.32, dark.Lightness);
        Assert.True(dark.Lightness > 0.25);
        Assert.True(dark.Lightness < vivid.Lightness);

        var swatch = HighlightCatalog.UnassignedSwatchHex;
        var atZero = LegendColorIntensity.Curve(swatch, 0);
        var atVivid = LegendColorIntensity.Curve(swatch, LegendColorIntensity.Vivid);
        var atEnd = LegendColorIntensity.Curve(swatch, 100);
        Assert.NotEqual("#FFFFFF", atZero);
        Assert.NotEqual("#000000", atEnd);
        Assert.InRange(LegendColorIntensity.Describe(atZero).Lightness, 0.85, 0.95);
        Assert.InRange(LegendColorIntensity.Describe(atZero).Saturation, 0.10, 0.35);
        Assert.True(LegendColorIntensity.Describe(atVivid).Saturation > 0.98);
        Assert.True(LegendColorIntensity.Describe(atEnd).Lightness > 0.25);
        Assert.True(LegendColorIntensity.Describe(atEnd).Lightness < LegendColorIntensity.Describe(atVivid).Lightness);
        Assert.Equal("#E6E6E6", LegendColorIntensity.NeutralTrackStart);
        Assert.Equal("#1A1A1A", LegendColorIntensity.NeutralTrackEnd);

        var origin = LegendColorIntensity.Describe(swatch).Hue;
        Assert.True(HueDistance(origin, LegendColorIntensity.Describe(atZero).Hue) < 0.02);
        Assert.True(HueDistance(origin, LegendColorIntensity.Describe(atVivid).Hue) < 0.02);
        Assert.True(HueDistance(origin, LegendColorIntensity.Describe(atEnd).Hue) < 0.02);

        var breaching = HighlightCatalog.Find(HighlightCatalog.SlaBreaching)!.SwatchHex;
        var breachingHue = LegendColorIntensity.Describe(breaching).Hue;
        Assert.True(HueDistance(breachingHue, LegendColorIntensity.Describe(LegendColorIntensity.Curve(breaching, 0)).Hue) < 0.02);
        Assert.True(HueDistance(breachingHue, LegendColorIntensity.Describe(LegendColorIntensity.Curve(breaching, 100)).Hue) < 0.02);
    }

    [Fact]
    public void EachColourDefaultsToTheClosestCurvePositionAndFirstLoadKeepsTheSwatch()
    {
        var legend = new LegendSettingsViewModel();
        Assert.False(legend.AdjustIndividually);
        Assert.Equal(91, legend.SliderValue);
        Assert.NotEqual(0, legend.SliderValue);
        Assert.NotEqual(100, legend.SliderValue);
        AssertMatchesCatalog(legend);

        legend.AdjustIndividually = true;
        foreach (var entry in legend.Entries)
        {
            var catalog = HighlightCatalog.Find(entry.Key)!;
            var closest = LegendColorIntensity.Closest(catalog.SwatchHex);
            Assert.Equal(ClosestPosition[entry.Key], closest);
            legend.Select(entry.Key);
            Assert.Equal(closest, legend.SliderValue);
            Assert.Equal(catalog.SwatchHex, entry.SwatchHex);
            Assert.Equal(catalog.RowHex, entry.RowHex);
            Assert.Equal(catalog.SwatchHex, legend.SelectedOriginalHex);
        }

        legend.AdjustIndividually = false;
        Assert.Equal("", legend.SelectedOriginalHex);
        Assert.Equal(91, legend.SliderValue);
        AssertMatchesCatalog(legend);

        var preferences = HighlightPreferences.From(new DeskSettings());
        Assert.Null(preferences.SharedIntensity);
        Assert.Equal(42, preferences.IntensityOf(HighlightCatalog.Unassigned));
        Assert.Equal(HighlightCatalog.UnassignedRowHex, preferences.ChooseRowHex(true, []));

        var changes = 0;
        legend.Changed += (_, _) => changes++;
        legend.Load(preferences);
        Assert.Equal(0, changes);
        Assert.Equal(91, legend.SliderValue);
        AssertMatchesCatalog(legend);
        var saved = Saved(legend);
        Assert.Equal(LegendColorIntensity.Version, saved.LegendIntensityVersion);
        Assert.Null(saved.LegendIntensity);
        Assert.Null(saved.LegendColorIntensities);
    }

    [Fact]
    public void AllColoursStartsAtTheAverageAndRecoloursOnlyWhenMoved()
    {
        var store = new MemorySettingsStore();
        store.Save(new DeskSettings { Password = "secret", UseSampleData = true });
        var main = new MainViewModel(store, new RecordingDesktopServices());
        main.Connection.Load(store.Load());
        main.Legend.Load(main.Connection.Highlights);
        Assert.Equal(91, main.Legend.SliderValue);
        AssertMatchesCatalog(main.Legend);

        var row = Row("inc9", unassigned: true);
        main.Incidents.ShowCachedRows([row], 1);
        main.Notifications.RememberViewer("sample-user", main.Legend.Current());
        main.Notifications.Apply(SlaSnapshot(), new AlertWatchState());
        main.Notifications.SelectQueueCommand.Execute(AlertKind.SlaBreaching);
        var mine = main.Notifications.DashboardRows.Single(item => item.Number == "INC9");
        var slaCatalog = HighlightCatalog.Find(HighlightCatalog.SlaAssignedToYou)!;
        Assert.Equal(HighlightCatalog.UnassignedRowHex, main.Incidents.Items[0].HighlightHex);
        Assert.Equal(slaCatalog.RowHex, mine.HighlightHex);
        Assert.Null(store.Current.LegendIntensity);

        main.Legend.SliderValue = 40;

        Assert.All(main.Legend.Entries, entry => AssertOnCurve(entry, 40));
        var unassigned = main.Legend.Entries.Single(entry => entry.Key == HighlightCatalog.Unassigned);
        var sla = main.Legend.Entries.Single(entry => entry.Key == HighlightCatalog.SlaBreaching);
        Assert.NotEqual(unassigned.SwatchHex, sla.SwatchHex);
        Assert.True(HueDistance(
            LegendColorIntensity.Describe(HighlightCatalog.UnassignedSwatchHex).Hue,
            LegendColorIntensity.Describe(unassigned.SwatchHex).Hue) < 0.02);
        Assert.Equal(
            LegendColorIntensity.Row(HighlightCatalog.UnassignedSwatchHex, HighlightCatalog.UnassignedRowHex, 40, applied: true),
            main.Incidents.Items[0].HighlightHex);
        var slaTint = LegendColorIntensity.Row(slaCatalog.SwatchHex, slaCatalog.RowHex, 40, applied: true);
        Assert.Equal(slaTint, main.Connection.Highlights.ChooseSlaAssigneeHex(true));
        main.Notifications.RememberViewer("sample-user", main.Connection.Highlights);
        Assert.Equal(slaTint, mine.HighlightHex);
        Assert.Equal(LegendColorIntensity.Version, store.Current.LegendIntensityVersion);
        Assert.Equal(40, store.Current.LegendIntensity);
        Assert.Null(store.Current.LegendColorIntensities);
        Assert.Equal("secret", store.Current.Password);
        Assert.True(store.Current.UseSampleData);
    }

    [Fact]
    public void IndividualSetChangesOnlyTheSelectedColour()
    {
        var legend = new LegendSettingsViewModel();
        Assert.Equal(91, legend.SliderValue);
        legend.AdjustIndividually = true;
        Assert.False(legend.SetIntensityCommand.CanExecute(null));
        AssertMatchesCatalog(legend);

        var unassigned = legend.Entries.Single(entry => entry.Key == HighlightCatalog.Unassigned);
        var sla = legend.Entries.Single(entry => entry.Key == HighlightCatalog.SlaBreaching);
        legend.Select(HighlightCatalog.Unassigned);
        Assert.Equal(42, legend.SliderValue);
        Assert.Equal(HighlightCatalog.UnassignedSwatchHex, unassigned.SwatchHex);

        var changes = 0;
        legend.Changed += (_, _) => changes++;
        legend.SliderValue = 15;
        Assert.Equal(0, changes);
        AssertMatchesCatalog(legend);

        legend.Select(HighlightCatalog.SlaBreaching);
        Assert.Equal(100, legend.SliderValue);
        Assert.Equal(HighlightCatalog.Find(HighlightCatalog.SlaBreaching)!.SwatchHex, sla.SwatchHex);

        legend.Select(HighlightCatalog.Unassigned);
        Assert.Equal(42, legend.SliderValue);
        legend.SliderValue = 15;
        legend.SetIntensityCommand.Execute(null);

        Assert.Equal(1, changes);
        AssertOnCurve(unassigned, 15);
        Assert.All(
            legend.Entries.Where(entry => entry.Key != HighlightCatalog.Unassigned),
            entry =>
            {
                var catalog = HighlightCatalog.Find(entry.Key)!;
                Assert.Equal(catalog.SwatchHex, entry.SwatchHex);
                Assert.Equal(catalog.RowHex, entry.RowHex);
            });
        Assert.Equal(
            LegendColorIntensity.Row(HighlightCatalog.UnassignedSwatchHex, HighlightCatalog.UnassignedRowHex, 15, applied: true),
            legend.Current().ChooseRowHex(true, []));
        Assert.Equal(
            HighlightCatalog.Find(HighlightCatalog.SlaBreaching)!.RowHex,
            legend.Current().ChooseRowHex(false, [AlertKind.SlaBreaching]));
        Assert.Null(legend.Current().SharedIntensity);
        Assert.Equal(15, legend.Current().IntensityOf(HighlightCatalog.Unassigned));
        Assert.Equal(100, legend.Current().IntensityOf(HighlightCatalog.SlaBreaching));

        var saved = Saved(legend);
        Assert.Equal(LegendColorIntensity.Version, saved.LegendIntensityVersion);
        Assert.Null(saved.LegendIntensity);
        Assert.Equal(15, saved.LegendColorIntensities![HighlightCatalog.Unassigned]);
        Assert.Single(saved.LegendColorIntensities);

        legend.AdjustIndividually = false;
        Assert.Equal(88, legend.SliderValue);
        AssertOnCurve(unassigned, 15);
        Assert.Equal(HighlightCatalog.Find(HighlightCatalog.SlaBreaching)!.SwatchHex, sla.SwatchHex);
    }

    [Fact]
    public void AnOldLightnessValueIsNotAppliedAsACurvePosition()
    {
        var old = new DeskSettings
        {
            LegendIntensity = 68,
            LegendColorIntensities = new Dictionary<string, int>
            {
                [HighlightCatalog.Unassigned] = 42
            }
        };
        Assert.Equal(0, old.LegendIntensityVersion);
        var ignored = HighlightPreferences.From(old);
        Assert.Null(ignored.SharedIntensity);
        Assert.Equal(42, ignored.IntensityOf(HighlightCatalog.Unassigned));
        Assert.Equal(100, ignored.IntensityOf(HighlightCatalog.SlaBreaching));

        var legend = new LegendSettingsViewModel();
        legend.Load(ignored);
        Assert.Equal(91, legend.SliderValue);
        Assert.NotEqual(68, legend.SliderValue);
        AssertMatchesCatalog(legend);

        var json = DeskSettingsFile.Serialize(old, text => text ?? "");
        var roundTrip = DeskSettingsFile.Deserialize(json, text => text ?? "");
        Assert.Equal(68, roundTrip.LegendIntensity);
        Assert.Equal(0, roundTrip.LegendIntensityVersion);
        var fromFile = new LegendSettingsViewModel();
        fromFile.Load(HighlightPreferences.From(roundTrip));
        AssertMatchesCatalog(fromFile);
        Assert.Equal(91, fromFile.SliderValue);

        var legacy = DeskSettingsFile.Deserialize("{\"LegendIntensity\":68}", text => text ?? "");
        var fresh = new LegendSettingsViewModel();
        fresh.Load(HighlightPreferences.From(legacy));
        AssertMatchesCatalog(fresh);

        var current = new DeskSettings
        {
            LegendIntensityVersion = LegendColorIntensity.Version,
            LegendIntensity = 40
        };
        var restored = new LegendSettingsViewModel();
        restored.Load(HighlightPreferences.From(current));
        Assert.Equal(40, restored.SliderValue);
        Assert.All(restored.Entries, entry => AssertOnCurve(entry, 40));
    }

    private static double HueDistance(double left, double right)
    {
        var gap = Math.Abs(left - right);
        return Math.Min(gap, 1d - gap);
    }

    private static void AssertMatchesCatalog(LegendSettingsViewModel legend)
    {
        Assert.All(legend.Entries, entry =>
        {
            var catalog = HighlightCatalog.Find(entry.Key);
            Assert.NotNull(catalog);
            Assert.Equal(catalog.SwatchHex, entry.SwatchHex);
            Assert.Equal(catalog.RowHex, entry.RowHex);
            Assert.Equal(ClosestPosition[entry.Key], LegendColorIntensity.Closest(entry.SwatchHex));
        });
    }

    private static void AssertOnCurve(LegendEntryModel entry, int position)
    {
        var catalog = HighlightCatalog.Find(entry.Key);
        Assert.NotNull(catalog);
        Assert.Equal(LegendColorIntensity.Curve(catalog.SwatchHex, position), entry.SwatchHex);
        Assert.Equal(LegendColorIntensity.Row(catalog.SwatchHex, catalog.RowHex, position, applied: true), entry.RowHex);
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
