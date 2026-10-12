using ServiceNowDesk.Alerts;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class LegendIntensityTests
{
    private const int DefaultSlider = 94;

    private static readonly Dictionary<string, int> ClosestPosition =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [HighlightCatalog.SlaBreaching] = 100,
            [HighlightCatalog.SlaAssignedToYou] = 100,
            [HighlightCatalog.OnHoldPastFollowUp] = 100,
            [HighlightCatalog.UpdatedByCaller] = 100,
            [HighlightCatalog.ReturnedWithNotes] = 100,
            [HighlightCatalog.Unattended] = 100,
            [HighlightCatalog.Unassigned] = 50,
            [HighlightCatalog.AssignedToMe] = 100,
            [HighlightCatalog.WatchedGroup] = 100
        };

    [Fact]
    public void PastelRisesToFullColourAndDarkensOnlyInTheLastTenth()
    {
        const int oldPlateau = 61;
        var pastel = LegendColorIntensity.Sample(0);
        var low = LegendColorIntensity.Sample(8);
        var mid = LegendColorIntensity.Sample(45);
        var plateau = LegendColorIntensity.Sample(oldPlateau);
        var later = LegendColorIntensity.Sample(85);
        var full = LegendColorIntensity.Sample(LegendColorIntensity.FullColour);
        var end = LegendColorIntensity.Sample(LegendColorIntensity.Maximum);

        Assert.Equal(90, LegendColorIntensity.FullColour);
        Assert.Equal(100, LegendColorIntensity.Maximum);
        Assert.Equal(0.28, pastel.Saturation);
        Assert.Equal(0.90, pastel.Lightness);
        Assert.Equal(1.00, full.Saturation);
        Assert.Equal(0.50, full.Lightness);
        Assert.Equal(1.00, end.Saturation);
        Assert.Equal(0.46, end.Lightness);

        Assert.True(low.Lightness > mid.Lightness);
        Assert.True(low.Saturation < mid.Saturation);
        Assert.True(plateau.Saturation < 0.98);
        Assert.True(later.Saturation > plateau.Saturation);
        Assert.True(later.Lightness >= full.Lightness);
        Assert.True(full.Lightness > 0.45);

        for (var position = LegendColorIntensity.Minimum; position < LegendColorIntensity.FullColour; position++)
        {
            var sample = LegendColorIntensity.Sample(position);
            Assert.True(sample.Lightness + 1e-9 >= full.Lightness, $"position {position} is darker than full colour");
            Assert.True(sample.Saturation <= full.Saturation + 1e-9);
        }

        Assert.True(end.Lightness < full.Lightness);
        Assert.InRange(full.Lightness - end.Lightness, 0.02, 0.08);
        Assert.Equal(full.Hue, end.Hue);
        Assert.Equal(full.Saturation, end.Saturation);

        var swatch = HighlightCatalog.UnassignedSwatchHex;
        var atZero = LegendColorIntensity.Curve(swatch, 0);
        var atLow = LegendColorIntensity.Curve(swatch, 8);
        var atMid = LegendColorIntensity.Curve(swatch, 45);
        var atPlateau = LegendColorIntensity.Curve(swatch, oldPlateau);
        var atLater = LegendColorIntensity.Curve(swatch, 85);
        var atFull = LegendColorIntensity.Curve(swatch, LegendColorIntensity.FullColour);
        var atEnd = LegendColorIntensity.Curve(swatch, LegendColorIntensity.Maximum);
        Assert.NotEqual("#FFFFFF", atZero);
        Assert.NotEqual("#000000", atEnd);
        Assert.True(LegendColorIntensity.Describe(atLow).Lightness > LegendColorIntensity.Describe(atMid).Lightness);
        Assert.True(LegendColorIntensity.Describe(atLow).Saturation < LegendColorIntensity.Describe(atMid).Saturation);
        Assert.True(LegendColorIntensity.Describe(atLater).Saturation > LegendColorIntensity.Describe(atPlateau).Saturation);
        Assert.True(LegendColorIntensity.Describe(atLater).Lightness >= LegendColorIntensity.Describe(atFull).Lightness - 0.01);
        Assert.True(LegendColorIntensity.Describe(atEnd).Lightness < LegendColorIntensity.Describe(atFull).Lightness);
        Assert.InRange(
            LegendColorIntensity.Describe(atFull).Lightness - LegendColorIntensity.Describe(atEnd).Lightness,
            0.01,
            0.08);

        var breaching = HighlightCatalog.Find(HighlightCatalog.SlaBreaching)!;
        Assert.NotEqual(
            LegendColorIntensity.Row(breaching.SwatchHex, breaching.RowHex, LegendColorIntensity.FullColour, applied: true),
            LegendColorIntensity.Row(breaching.SwatchHex, breaching.RowHex, LegendColorIntensity.Maximum, applied: true));
        Assert.Equal("#E6E6E6", LegendColorIntensity.NeutralTrackStart);
        Assert.Equal("#808080", LegendColorIntensity.NeutralTrackFull);
        Assert.Equal("#757575", LegendColorIntensity.NeutralTrackEnd);

        var origin = LegendColorIntensity.Describe(swatch).Hue;
        foreach (var painted in new[] { atZero, atLow, atMid, atPlateau, atLater, atFull, atEnd })
            Assert.True(HueDistance(origin, LegendColorIntensity.Describe(painted).Hue) < 0.02);

        var breachingHue = LegendColorIntensity.Describe(breaching.SwatchHex).Hue;
        foreach (var position in new[] { 0, oldPlateau, 85, LegendColorIntensity.FullColour, LegendColorIntensity.Maximum })
        {
            var painted = LegendColorIntensity.Curve(breaching.SwatchHex, position);
            Assert.True(HueDistance(breachingHue, LegendColorIntensity.Describe(painted).Hue) < 0.02);
        }

        Assert.All(HighlightCatalog.Entries, entry =>
        {
            var hue = LegendColorIntensity.Describe(entry.SwatchHex).Hue;
            var start = LegendColorIntensity.Curve(entry.SwatchHex, 0);
            var finish = LegendColorIntensity.Curve(entry.SwatchHex, LegendColorIntensity.Maximum);
            Assert.True(HueDistance(hue, LegendColorIntensity.Describe(start).Hue) < 0.02);
            Assert.True(HueDistance(hue, LegendColorIntensity.Describe(finish).Hue) < 0.02);
            Assert.True(HueDistance(
                LegendColorIntensity.Describe(LegendColorIntensity.Curve(entry.SwatchHex, LegendColorIntensity.FullColour)).Hue,
                LegendColorIntensity.Describe(finish).Hue) < 0.02);
            Assert.True(RowContrast(LegendColorIntensity.Row(entry.SwatchHex, entry.RowHex, LegendColorIntensity.Maximum, applied: true)) >= 4.5);
        });
    }

    [Fact]
    public void EachColourDefaultsToTheClosestCurvePositionAndFirstLoadKeepsTheSwatch()
    {
        var legend = new LegendSettingsViewModel();
        Assert.False(legend.AdjustIndividually);
        Assert.Equal(DefaultSlider, legend.SliderValue);
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
        Assert.Equal(DefaultSlider, legend.SliderValue);
        AssertMatchesCatalog(legend);

        var preferences = HighlightPreferences.From(new DeskSettings());
        Assert.Null(preferences.SharedIntensity);
        Assert.Equal(ClosestPosition[HighlightCatalog.Unassigned], preferences.IntensityOf(HighlightCatalog.Unassigned));
        Assert.Equal(HighlightCatalog.UnassignedRowHex, preferences.ChooseRowHex(true, []));

        var changes = 0;
        legend.Changed += (_, _) => changes++;
        legend.Load(preferences);
        Assert.Equal(0, changes);
        Assert.Equal(DefaultSlider, legend.SliderValue);
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
        Assert.Equal(DefaultSlider, main.Legend.SliderValue);
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
        Assert.Equal(DefaultSlider, legend.SliderValue);
        legend.AdjustIndividually = true;
        Assert.False(legend.SetIntensityCommand.CanExecute(null));
        AssertMatchesCatalog(legend);

        var unassigned = legend.Entries.Single(entry => entry.Key == HighlightCatalog.Unassigned);
        var sla = legend.Entries.Single(entry => entry.Key == HighlightCatalog.SlaBreaching);
        legend.Select(HighlightCatalog.Unassigned);
        Assert.Equal(ClosestPosition[HighlightCatalog.Unassigned], legend.SliderValue);
        Assert.Equal(HighlightCatalog.UnassignedSwatchHex, unassigned.SwatchHex);

        var changes = 0;
        legend.Changed += (_, _) => changes++;
        legend.SliderValue = 15;
        Assert.Equal(0, changes);
        AssertMatchesCatalog(legend);

        legend.Select(HighlightCatalog.SlaBreaching);
        Assert.Equal(ClosestPosition[HighlightCatalog.SlaBreaching], legend.SliderValue);
        Assert.Equal(HighlightCatalog.Find(HighlightCatalog.SlaBreaching)!.SwatchHex, sla.SwatchHex);

        legend.Select(HighlightCatalog.Unassigned);
        Assert.Equal(ClosestPosition[HighlightCatalog.Unassigned], legend.SliderValue);
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
        Assert.Equal(ClosestPosition[HighlightCatalog.SlaBreaching], legend.Current().IntensityOf(HighlightCatalog.SlaBreaching));

        var saved = Saved(legend);
        Assert.Equal(LegendColorIntensity.Version, saved.LegendIntensityVersion);
        Assert.Null(saved.LegendIntensity);
        Assert.Equal(15, saved.LegendColorIntensities![HighlightCatalog.Unassigned]);
        Assert.Single(saved.LegendColorIntensities);

        legend.AdjustIndividually = false;
        Assert.Equal(
            LegendColorIntensity.Average(legend.Entries.Select(entry =>
                entry.Key == HighlightCatalog.Unassigned ? 15 : ClosestPosition[entry.Key])),
            legend.SliderValue);
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
        Assert.Equal(ClosestPosition[HighlightCatalog.Unassigned], ignored.IntensityOf(HighlightCatalog.Unassigned));
        Assert.Equal(ClosestPosition[HighlightCatalog.SlaBreaching], ignored.IntensityOf(HighlightCatalog.SlaBreaching));

        var legend = new LegendSettingsViewModel();
        legend.Load(ignored);
        Assert.Equal(DefaultSlider, legend.SliderValue);
        Assert.NotEqual(68, legend.SliderValue);
        AssertMatchesCatalog(legend);

        var json = DeskSettingsFile.Serialize(old, text => text ?? "");
        var roundTrip = DeskSettingsFile.Deserialize(json, text => text ?? "");
        Assert.Equal(68, roundTrip.LegendIntensity);
        Assert.Equal(0, roundTrip.LegendIntensityVersion);
        var fromFile = new LegendSettingsViewModel();
        fromFile.Load(HighlightPreferences.From(roundTrip));
        AssertMatchesCatalog(fromFile);
        Assert.Equal(DefaultSlider, fromFile.SliderValue);

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

    private static double RowContrast(string hex)
    {
        var background = Parse(hex);
        var text = (Red: (byte)0x1C, Green: (byte)0x25, Blue: (byte)0x29);
        var lighter = Math.Max(Luminance(background), Luminance(text));
        var darker = Math.Min(Luminance(background), Luminance(text));
        return (lighter + 0.05d) / (darker + 0.05d);
    }

    private static double Luminance((byte Red, byte Green, byte Blue) color) =>
        0.2126d * Linearize(color.Red) + 0.7152d * Linearize(color.Green) + 0.0722d * Linearize(color.Blue);

    private static double Linearize(byte channel)
    {
        var unit = channel / 255d;
        return unit <= 0.04045d
            ? unit / 12.92d
            : Math.Pow((unit + 0.055d) / 1.055d, 2.4d);
    }

    private static (byte Red, byte Green, byte Blue) Parse(string text) =>
        (
            Convert.ToByte(text.Substring(1, 2), 16),
            Convert.ToByte(text.Substring(3, 2), 16),
            Convert.ToByte(text.Substring(5, 2), 16));

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
