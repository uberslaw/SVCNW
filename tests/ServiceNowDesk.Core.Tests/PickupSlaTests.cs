using ServiceNowDesk.Alerts;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class PickupSlaTests
{
    private static readonly DateTime Now = new(2026, 10, 9, 12, 0, 0);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(12, 10)]
    [InlineData(60, 50)]
    [InlineData(120, 100)]
    [InlineData(150, 125)]
    public void PercentIsElapsedOverTwoHours(int minutesOpen, double expected)
    {
        var opened = Now.AddMinutes(-minutesOpen);
        Assert.Equal(expected, PickupSla.Percent(opened, Now), precision: 6);
        Assert.Equal(PickupSla.BarFillPercent(expected), Math.Min(100, expected), precision: 6);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(9.9, 0)]
    [InlineData(10, 1)]
    [InlineData(19.9, 1)]
    [InlineData(20, 2)]
    [InlineData(100, 10)]
    [InlineData(110, 11)]
    public void BandStepsEveryTenPercent(double percent, int band)
    {
        Assert.Equal(band, PickupSla.Band(percent));
    }

    [Fact]
    public void CrossedBandsListsEachNewTenth()
    {
        Assert.Equal([1, 2, 3], PickupSla.CrossedBands(0, 3));
        Assert.Equal([5], PickupSla.CrossedBands(4, 5));
        Assert.Empty(PickupSla.CrossedBands(5, 5));
        Assert.Empty(PickupSla.CrossedBands(6, 4));
        Assert.Equal(PickupSla.TickMarks(), new[] { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 });
    }

    [Fact]
    public void WatchJigglesOnlyWhenATenPercentBandIsNewlyCrossed()
    {
        var watch = new PickupSlaWatch();
        var first = Snapshot("inc-a", "INC-A", minutesOpen: 25);
        Assert.False(watch.Observe(first).Due);
        Assert.Equal(2, watch.CurrentBand);

        Assert.False(watch.Observe(Snapshot("inc-a", "INC-A", minutesOpen: 29)).Due);

        var crossed = watch.Observe(Snapshot("inc-a", "INC-A", minutesOpen: 36));
        Assert.True(crossed.Due);
        Assert.Equal([3], crossed.CrossedBands);

        Assert.False(watch.Observe(null).Due);
        Assert.Equal(-1, watch.CurrentBand);
    }

    [Fact]
    public void ScopeRequiresUnassignedOpenOfficeAndOpenedAt()
    {
        var offices = NotificationPreferences.DefaultLocations;
        var ok = Record("inc-ok", assigned: "", location: "Brisbane Office", opened: "2026-10-09 11:00:00");
        Assert.True(PickupSla.IsInScope(ok, offices));

        Assert.False(PickupSla.IsInScope(
            ok with { AssignedToSysId = "user-other" },
            offices));
        Assert.False(PickupSla.IsInScope(
            ok with { Location = "Melbourne" },
            offices));
        Assert.False(PickupSla.IsInScope(
            ok with { Location = "" },
            offices));
        Assert.False(PickupSla.IsInScope(
            ok with { Opened = "" },
            offices));
        Assert.False(PickupSla.IsInScope(
            ok with { StateValue = "6", State = "Resolved" },
            offices));
        Assert.True(PickupSla.IsInScope(ok, null));
    }

    [Fact]
    public void SelectWorstPicksHighestPercentInsideOfficeScope()
    {
        var offices = new[] { "Brisbane" };
        var younger = Record("inc-young", assigned: "", location: "Brisbane", opened: "2026-10-09 11:30:00");
        var older = Record("inc-old", assigned: "", location: "Brisbane Office", opened: "2026-10-09 10:30:00");
        var melbourne = Record("inc-mel", assigned: "", location: "Melbourne", opened: "2026-10-09 08:00:00");
        var assigned = Record("inc-mine", assigned: "user-alex", location: "Brisbane", opened: "2026-10-09 08:00:00");

        var worst = PickupSla.SelectWorst([younger, older, melbourne, assigned], Now, offices);
        Assert.NotNull(worst);
        Assert.Equal("inc-old", worst.SysId);
        Assert.Equal(75, worst.Percent, precision: 6);
        Assert.Equal(2, worst.ScopedCount);
        Assert.Contains("INC", PickupSla.FormatLabel(worst));
        Assert.Contains("2 unassigned", PickupSla.FormatLabel(worst));
    }

    [Fact]
    public async Task PracticeUnassignedQueueFeedsTheWidgetPickupBar()
    {
        using var client = new SampleServiceNowClient();
        var offices = NotificationPreferences.DefaultLocations;
        var queue = await client.ListUnassignedGroupQueueAsync(
            "Aus DT - Client Services",
            offices,
            CancellationToken.None);
        Assert.NotEmpty(queue);
        Assert.All(queue, row =>
        {
            Assert.True(string.IsNullOrWhiteSpace(row.AssignedToSysId));
            Assert.True(OfficeQueue.Matches(row.Location, offices));
        });

        var notifications = new NotificationWorkspaceViewModel();
        AlertAttention? attention = null;
        notifications.Attention += (_, args) => attention = args;

        var oldest = queue[0];
        var opened = Now.AddMinutes(-72);
        var aged = queue
            .Select(row => row with
            {
                Opened = row.SysId == oldest.SysId
                    ? opened.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
                    : Now.AddMinutes(-5).ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
            })
            .ToArray();

        notifications.ApplyPickupQueue(aged, Now, offices);
        Assert.True(notifications.HasPickupSla);
        Assert.Equal(oldest.Number, notifications.PickupNumber);
        Assert.Equal(60, notifications.PickupPercent, precision: 6);
        Assert.Equal(60, notifications.PickupBarPercent, precision: 6);
        Assert.Null(attention);

        notifications.TickPickupSla(Now.AddMinutes(12));
        Assert.NotNull(attention);
        Assert.True(attention!.PickupSlaThreshold);
        Assert.Equal(70, notifications.PickupPercent, precision: 6);
        Assert.Equal(new[] { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 }, notifications.PickupTicks);
    }

    [Fact]
    public void OpenPickupRaisesTheWorstTicket()
    {
        var notifications = new NotificationWorkspaceViewModel();
        AlertRow? opened = null;
        notifications.OpenRequested += (_, row) => opened = row;
        notifications.ApplyPickupQueue(
            [Record("inc-open", assigned: "", location: "Brisbane", opened: "2026-10-09 11:00:00")],
            Now,
            ["Brisbane"]);

        Assert.True(notifications.OpenPickupCommand.CanExecute(null));
        notifications.OpenPickupCommand.Execute(null);
        Assert.NotNull(opened);
        Assert.Equal("inc-open", opened!.SysId);
        Assert.Equal(AlertKind.WatchedGroup, opened.Kind);
    }

    private static PickupSlaSnapshot Snapshot(string sysId, string number, int minutesOpen)
    {
        var opened = Now.AddMinutes(-minutesOpen);
        var percent = PickupSla.Percent(opened, Now);
        return new PickupSlaSnapshot(
            sysId,
            number,
            "Title",
            DeskSection.Incidents,
            "Client Services",
            "Brisbane",
            opened,
            percent,
            PickupSla.Band(percent),
            1);
    }

    private static WatchedRecord Record(string sysId, string assigned, string location, string opened) => new()
    {
        Section = DeskSection.Incidents,
        SysId = sysId,
        Number = "INC-" + sysId,
        Title = "Pickup candidate",
        State = "New",
        StateValue = "1",
        Group = "Client Services",
        Location = location,
        Updated = opened,
        AssignedToSysId = assigned,
        Opened = opened
    };
}
