using ServiceNowDesk.Alerts;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class HighlightTests
{
    [Fact]
    public void DefaultsExplainTheRedRowAndLeaveTheBroadQueuesOff()
    {
        Assert.Equal(
            [
                HighlightCatalog.SlaBreaching,
                HighlightCatalog.OnHoldPastFollowUp,
                HighlightCatalog.UpdatedByCaller,
                HighlightCatalog.ReturnedWithNotes,
                HighlightCatalog.Unassigned,
                HighlightCatalog.AssignedToMe,
                HighlightCatalog.WatchedGroup
            ],
            HighlightCatalog.Entries.Select(entry => entry.Key).ToArray());

        var unassigned = HighlightCatalog.Find(HighlightCatalog.Unassigned);
        Assert.NotNull(unassigned);
        Assert.Equal(HighlightCatalog.UnassignedRowHex, unassigned.RowHex);
        Assert.Contains("nobody is assigned", unassigned.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.True(unassigned.EnabledByDefault);

        foreach (var entry in HighlightCatalog.Entries.Where(entry => entry.Kind is not null))
        {
            Assert.Equal(AlertCatalog.Swatch(entry.Kind!.Value).Hex, entry.SwatchHex);
            Assert.Equal(HighlightCatalog.Lighten(entry.SwatchHex), entry.RowHex);
        }

        var defaults = HighlightPreferences.Default;
        Assert.True(defaults.IsEnabled(HighlightCatalog.Unassigned));
        Assert.True(defaults.IsEnabled(HighlightCatalog.SlaBreaching));
        Assert.False(defaults.IsEnabled(HighlightCatalog.AssignedToMe));
        Assert.False(defaults.IsEnabled(HighlightCatalog.WatchedGroup));
        Assert.Equal(HighlightCatalog.DefaultKeys, HighlightPreferences.From(new DeskSettings()).EnabledKeys);
    }

    [Fact]
    public void LightTintsStayReadableAgainstAWhiteRow()
    {
        Assert.Equal("#DDEBEA", HighlightCatalog.Lighten("#0F6E6B"));
        Assert.Equal("#F7EDDD", HighlightCatalog.Lighten("#C47E09"));
        Assert.Equal("#F5E0DF", HighlightCatalog.Lighten("#B42318"));
        Assert.Equal("#DFE6EE", HighlightCatalog.Lighten("#1D4E89"));
        Assert.Equal("#EBE1FA", HighlightCatalog.Lighten("#6D28D9"));
        Assert.Equal("#DDECEF", HighlightCatalog.Lighten("#0E7490"));
    }

    [Fact]
    public void AMissingSavedListUsesDefaultsAndAnEmptyListTurnsEveryHighlightOff()
    {
        var settings = new DeskSettings();
        Assert.Null(settings.EnabledHighlights);
        HighlightPreferences.From(settings).ApplyTo(settings);
        Assert.Contains(HighlightCatalog.Unassigned, settings.EnabledHighlights!);
        Assert.DoesNotContain(HighlightCatalog.AssignedToMe, settings.EnabledHighlights!);

        var cleared = new DeskSettings();
        HighlightPreferences.FromKeys([]).ApplyTo(cleared);
        Assert.NotNull(cleared.EnabledHighlights);
        Assert.Empty(cleared.EnabledHighlights);
        Assert.Equal("", HighlightPreferences.From(cleared).ChooseRowHex(true, [AlertKind.SlaBreaching]));

        var kept = HighlightPreferences.FromKeys(["nope", HighlightCatalog.AssignedToMe, HighlightCatalog.Unassigned]);
        Assert.Equal([HighlightCatalog.Unassigned, HighlightCatalog.AssignedToMe], kept.EnabledKeys);
    }

    [Fact]
    public void TheHigherLegendEntryWinsAndATurnedOffColorStaysClear()
    {
        var prefs = HighlightPreferences.Default;
        var sla = HighlightCatalog.Find(HighlightCatalog.SlaBreaching)!.RowHex;
        Assert.Equal(sla, prefs.ChooseRowHex(true, [AlertKind.SlaBreaching, AlertKind.UpdatedByCaller]));
        Assert.Equal(HighlightCatalog.UnassignedRowHex, prefs.ChooseRowHex(true, []));
        Assert.Equal("", prefs.ChooseRowHex(false, [AlertKind.AssignedToMe, AlertKind.WatchedGroup]));

        var withMine = HighlightPreferences.FromKeys(
            [.. HighlightCatalog.DefaultKeys, HighlightCatalog.AssignedToMe]);
        Assert.Equal(HighlightCatalog.UnassignedRowHex, withMine.ChooseRowHex(true, [AlertKind.AssignedToMe]));
        Assert.Equal(
            HighlightCatalog.Find(HighlightCatalog.AssignedToMe)!.RowHex,
            withMine.ChooseRowHex(false, [AlertKind.AssignedToMe]));
    }

    [Fact]
    public void RowsTakeTheSnapshotColorAndDropItWhenTheLegendTurnsThatEntryOff()
    {
        var highlighter = new RowHighlighter();
        var row = Row("inc1", unassigned: true);
        highlighter.Paint(row);
        Assert.Equal(HighlightCatalog.UnassignedRowHex, row.HighlightHex);

        highlighter.Use(new AlertSnapshot(new Dictionary<AlertKind, AlertBucket>
        {
            [AlertKind.SlaBreaching] = new([Alert("INC1", AlertKind.SlaBreaching)], 1),
            [AlertKind.UpdatedByCaller] = new([Alert("INC1", AlertKind.UpdatedByCaller)], 1)
        }));
        highlighter.Paint(row);
        Assert.Equal(HighlightCatalog.Find(HighlightCatalog.SlaBreaching)!.RowHex, row.HighlightHex);

        var changes = 0;
        row.PropertyChanged += (_, args) =>
        {
            Assert.Equal(nameof(TicketRow.HighlightHex), args.PropertyName);
            changes++;
        };
        highlighter.Use(HighlightPreferences.FromKeys([]));
        highlighter.Paint(row);
        highlighter.Paint(row);
        Assert.Equal("", row.HighlightHex);
        Assert.Equal(1, changes);

        var hit = new SearchHit
        {
            Section = DeskSection.WalkUps,
            TableLabel = "Walk-up",
            SysId = "ims1",
            Number = "IMS1",
            Title = "Lobby",
            StateLabel = "Open",
            Tone = "",
            Meta = "",
            When = "",
            SortKey = "",
            Unassigned = true
        };
        highlighter.Use(HighlightPreferences.Default);
        highlighter.Paint(hit);
        Assert.Equal(HighlightCatalog.UnassignedRowHex, hit.HighlightHex);
    }

    [Fact]
    public void TheLegendSavesTheChoiceAndRepaintsAnOpenList()
    {
        var store = new MemorySettingsStore();
        store.Save(new DeskSettings { Password = "secret", UseSampleData = true });
        var main = new MainViewModel(store, new RecordingDesktopServices());
        main.Connection.Load(store.Load());
        main.Legend.Load(main.Connection.Highlights);

        var row = Row("inc9", unassigned: true);
        main.Incidents.ShowCachedRows([row], 1);
        Assert.Equal(HighlightCatalog.UnassignedRowHex, main.Incidents.Items[0].HighlightHex);

        var events = 0;
        main.Legend.Changed += (_, _) => events++;
        main.Legend.Load(HighlightPreferences.FromKeys([]));
        Assert.Equal(0, events);

        main.Legend.Entries.Single(entry => entry.Key == HighlightCatalog.Unassigned).IsEnabled = true;
        Assert.Equal(HighlightCatalog.UnassignedRowHex, main.Incidents.Items[0].HighlightHex);
        Assert.Equal("secret", store.Current.Password);
        Assert.True(store.Current.UseSampleData);
        Assert.Equal([HighlightCatalog.Unassigned], store.Current.EnabledHighlights);
    }

    [Fact]
    public void ConnectionKeepsHighlightChoicesWhenOtherSettingsAreSaved()
    {
        var connection = new ConnectionViewModel();
        connection.Load(new DeskSettings
        {
            Password = "secret",
            EnabledHighlights = [HighlightCatalog.WatchedGroup]
        });
        var built = connection.BuildSettings();
        Assert.Equal([HighlightCatalog.WatchedGroup], built.EnabledHighlights);
        Assert.Equal("secret", built.Password);
        Assert.Equal("00:01:00", built.JiggleFrequency);
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

    private static AlertRecord Alert(string sysId, AlertKind kind) =>
        new(kind, DeskSection.Incidents, sysId, sysId, "Printer", "Open", "Client Services", "Brisbane", "2026-10-01");
}
