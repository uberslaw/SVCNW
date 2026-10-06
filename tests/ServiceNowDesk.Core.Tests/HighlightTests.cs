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
                HighlightCatalog.SlaAssignedToYou,
                HighlightCatalog.OnHoldPastFollowUp,
                HighlightCatalog.UpdatedByCaller,
                HighlightCatalog.ReturnedWithNotes,
                HighlightCatalog.Unattended,
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

        var slaMine = HighlightCatalog.Find(HighlightCatalog.SlaAssignedToYou);
        Assert.NotNull(slaMine);
        Assert.False(slaMine.PaintsTicketLists);
        Assert.True(slaMine.EnabledByDefault);
        Assert.Contains("Assigned to", slaMine.Explanation, StringComparison.Ordinal);
        Assert.Equal(AlertCatalog.Swatch(AlertKind.AssignedToMe).Hex, slaMine.SwatchHex);

        var defaults = HighlightPreferences.Default;
        Assert.True(defaults.IsEnabled(HighlightCatalog.Unassigned));
        Assert.True(defaults.IsEnabled(HighlightCatalog.SlaBreaching));
        Assert.True(defaults.IsEnabled(HighlightCatalog.SlaAssignedToYou));
        Assert.False(defaults.IsEnabled(HighlightCatalog.AssignedToMe));
        Assert.False(defaults.IsEnabled(HighlightCatalog.WatchedGroup));
        Assert.Equal(HighlightCatalog.DefaultKeys, HighlightPreferences.From(new DeskSettings()).EnabledKeys);
    }

    [Fact]
    public void LightTintsStayReadableAgainstAWhiteRow()
    {
        Assert.Equal("#0A635C", AlertCatalog.Swatch(AlertKind.AssignedToMe).Hex);
        Assert.Equal("#9A6408", AlertCatalog.Swatch(AlertKind.WatchedGroup).Hex);
        Assert.Equal("#721612", AlertCatalog.Swatch(AlertKind.SlaBreaching).Hex);
        Assert.Equal("#14386C", AlertCatalog.Swatch(AlertKind.OnHoldPastFollowUp).Hex);
        Assert.Equal("#5B21B6", AlertCatalog.Swatch(AlertKind.UpdatedByCaller).Hex);
        Assert.Equal("#1A90C0", AlertCatalog.Swatch(AlertKind.ReturnedWithNotes).Hex);
        Assert.Equal("#2A333C", AlertCatalog.Swatch(AlertKind.Unattended).Hex);
        Assert.Equal("#C56A62", HighlightCatalog.UnassignedSwatchHex);
        Assert.Equal("#F3E1E0", HighlightCatalog.UnassignedRowHex);

        Assert.Equal("#CEE0DE", HighlightCatalog.Lighten("#0A635C"));
        Assert.Equal("#EBE0CE", HighlightCatalog.Lighten("#9A6408"));
        Assert.Equal("#E3D0D0", HighlightCatalog.Lighten("#721612"));
        Assert.Equal("#D0D7E2", HighlightCatalog.Lighten("#14386C"));
        Assert.Equal("#DED3F0", HighlightCatalog.Lighten("#5B21B6"));
        Assert.Equal("#D1E9F2", HighlightCatalog.Lighten("#1A90C0"));
        Assert.Equal("#D4D6D8", HighlightCatalog.Lighten("#2A333C"));
        Assert.Equal("#F3E1E0", HighlightCatalog.Lighten("#C56A62"));
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
        Assert.Equal(sla, prefs.ChooseRowHex(false, [AlertKind.SlaBreaching]));
        Assert.NotEqual(HighlightCatalog.Find(HighlightCatalog.SlaAssignedToYou)!.RowHex, sla);
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
    public void SlaDashboardNamesTheAssigneeAndHighlightsYours()
    {
        var notifications = new NotificationWorkspaceViewModel();
        notifications.RememberViewer("sample-user", HighlightPreferences.Default);
        notifications.Apply(new AlertSnapshot(new Dictionary<AlertKind, AlertBucket>
        {
            [AlertKind.SlaBreaching] = new(
            [
                new AlertRecord(AlertKind.SlaBreaching, DeskSection.Incidents, "inc-mine", "INC9", "Mine", "In Progress", "Client Services", "Brisbane", "2026-10-04", "Alex Rivera", "sample-user"),
                new AlertRecord(AlertKind.SlaBreaching, DeskSection.Incidents, "inc-other", "INC8", "Theirs", "In Progress", "Client Services", "Brisbane", "2026-10-04", "Jordan Lee", "user-jordan"),
                new AlertRecord(AlertKind.SlaBreaching, DeskSection.Incidents, "inc-none", "INC7", "Empty", "In Progress", "Client Services", "Brisbane", "2026-10-04")
            ], 3)
        }), new AlertWatchState());

        notifications.SelectQueueCommand.Execute(AlertKind.SlaBreaching);
        Assert.True(notifications.ShowAssigneeColumn);
        var mine = notifications.DashboardRows.Single(row => row.Number == "INC9");
        var other = notifications.DashboardRows.Single(row => row.Number == "INC8");
        var empty = notifications.DashboardRows.Single(row => row.Number == "INC7");
        Assert.Equal("Alex Rivera", mine.AssigneeLabel);
        Assert.Equal(HighlightCatalog.Find(HighlightCatalog.SlaAssignedToYou)!.RowHex, mine.HighlightHex);
        Assert.Equal("Jordan Lee", other.AssigneeLabel);
        Assert.Equal("", other.HighlightHex);
        Assert.Equal("Unassigned", empty.AssigneeLabel);
        Assert.Equal("", empty.HighlightHex);

        notifications.SelectQueueCommand.Execute(AlertKind.AssignedToMe);
        Assert.False(notifications.ShowAssigneeColumn);

        var off = HighlightPreferences.FromKeys(
            HighlightCatalog.DefaultKeys.Where(key => key != HighlightCatalog.SlaAssignedToYou).ToArray());
        notifications.SelectQueueCommand.Execute(AlertKind.SlaBreaching);
        notifications.RememberViewer("sample-user", off);
        Assert.Equal("", notifications.DashboardRows.Single(row => row.Number == "INC9").HighlightHex);
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
        Assert.Contains(main.Legend.Shown, entry => entry.Key == HighlightCatalog.Unassigned);
        Assert.True(main.Legend.AnyShown);
        Assert.Equal("", main.Legend.EmptyNote);
    }

    [Fact]
    public void TheLegendSitsAboveTicketListsAndNamesTheColorsThatAreOn()
    {
        var store = new MemorySettingsStore();
        store.Save(new DeskSettings { UseSampleData = true });
        var main = new MainViewModel(store, new RecordingDesktopServices());
        Assert.Equal(DeskSection.Connection, main.SelectedSection);
        Assert.False(main.ShowRowLegend);
        Assert.Contains(main.Legend.Shown, entry => entry.Key == HighlightCatalog.Unassigned);
        Assert.DoesNotContain(main.Legend.Shown, entry => entry.Key == HighlightCatalog.AssignedToMe);

        main.SelectedSection = DeskSection.Incidents;
        Assert.True(main.ShowRowLegend);
        main.SelectedSection = DeskSection.Search;
        Assert.True(main.ShowRowLegend);
        main.SelectedSection = DeskSection.Notifications;
        Assert.True(main.ShowRowLegend);
        main.SelectedSection = DeskSection.Leads;
        Assert.True(main.ShowRowLegend);
        main.SelectedSection = DeskSection.DailyWork;
        Assert.True(main.ShowRowLegend);
        main.SelectedSection = DeskSection.Legend;
        Assert.False(main.ShowRowLegend);
        main.SelectedSection = DeskSection.Settings;
        Assert.False(main.ShowRowLegend);

        main.Legend.Load(HighlightPreferences.FromKeys([]));
        Assert.Empty(main.Legend.Shown);
        Assert.False(main.Legend.AnyShown);
        Assert.Contains("Legend", main.Legend.EmptyNote, StringComparison.Ordinal);
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
