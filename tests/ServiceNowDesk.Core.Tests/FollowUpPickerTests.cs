using ServiceNowDesk.Client;
using ServiceNowDesk.Mapping;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class FollowUpPickerTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 15, 4, 23);

    [Fact]
    public void SevenDaysFromKeepsTheTimeOfDay()
    {
        Assert.Equal("2026-10-14 15:04:23", FollowUpValue.SevenDaysFrom(Now));
    }

    [Fact]
    public void PickerSelectionFormatsTheSaveString()
    {
        var text = FollowUpValue.FormatSelection(new DateTime(2026, 11, 20), 10, 10, 23);
        Assert.Equal("2026-11-20 10:10:23", text);
        Assert.Equal(text, FollowUpValue.Format(new DateTime(2026, 11, 20, 10, 10, 23)));
    }

    [Fact]
    public void PickerOpensOnAStoredValue()
    {
        var seed = FollowUpValue.PickerSeed("2026-11-20 10:10:23", Now);
        Assert.Equal(new DateTime(2026, 11, 20, 10, 10, 23), seed);
    }

    [Fact]
    public void BlankHoldDefaultLeavesAnExistingValue()
    {
        Assert.Null(FollowUpValue.BlankHoldDefault(true, "2026-11-20 10:10:23", Now));
        Assert.Null(FollowUpValue.BlankHoldDefault(false, "", Now));
        Assert.Equal("2026-10-14 15:04:23", FollowUpValue.BlankHoldDefault(true, "  ", Now));
    }

    [Fact]
    public void UnparseableFollowUpFailsHoldValidation()
    {
        Assert.False(FollowUpValue.TryValidateHold("awaiting_vendor", "not-a-date", out var message));
        Assert.Contains("date and time", message, StringComparison.OrdinalIgnoreCase);

        Assert.True(FollowUpValue.TryValidateHold("awaiting_vendor", "2026-11-20 10:10", out message));
        Assert.Equal("", message);
    }

    [Fact]
    public async Task RequestItemBlankFollowUpBecomesSevenDaysWhenPutOnHold()
    {
        using var client = new SampleServiceNowClient();
        var items = await OpenItemsAsync(client, "ritm-dock");
        items.FollowUpNow = () => Now;

        Assert.Equal("", items.FollowUp);
        items.State = "on_hold";

        Assert.True(items.ShowHoldReason);
        Assert.Equal("2026-10-14 15:04:23", items.FollowUp);
    }

    [Fact]
    public async Task RequestItemKeepsAnExistingFollowUp()
    {
        using var client = new SampleServiceNowClient();
        var items = await OpenItemsAsync(client, "ritm-hold");
        items.FollowUpNow = () => Now;

        Assert.Equal("2026-10-01 11:00:00", items.FollowUp);
        items.State = "2";
        items.State = "on_hold";
        Assert.Equal("2026-10-01 11:00:00", items.FollowUp);
    }

    [Fact]
    public async Task RequestItemSavesAPickerValueAndATypedValue()
    {
        using var client = new SampleServiceNowClient();
        var items = await OpenItemsAsync(client, "ritm-dock");
        items.FollowUpNow = () => Now;
        items.State = "on_hold";
        items.HoldReason = "awaiting_vendor";
        items.FollowUp = FollowUpValue.FormatSelection(new DateTime(2026, 11, 20), 10, 10, 23);
        await items.SaveCommand.ExecuteAsync(null);

        Assert.Equal("", items.ErrorMessage);
        var picked = await client.GetRequestedItemAsync("ritm-dock", CancellationToken.None);
        Assert.Equal("2026-11-20 10:10:23", picked.FollowUp);

        items.FollowUp = "2026-12-01 08:05";
        await items.SaveCommand.ExecuteAsync(null);

        Assert.Equal("", items.ErrorMessage);
        var typed = await client.GetRequestedItemAsync("ritm-dock", CancellationToken.None);
        Assert.Equal("2026-12-01 08:05", typed.FollowUp);
    }

    [Fact]
    public async Task RequestItemRejectsAnUnparseableFollowUp()
    {
        using var client = new SampleServiceNowClient();
        var items = await OpenItemsAsync(client, "ritm-dock");
        items.FollowUpNow = () => Now;
        items.State = "on_hold";
        items.HoldReason = "awaiting_vendor";
        items.FollowUp = "not-a-date";
        var patches = ItemPatches(client);
        await items.SaveCommand.ExecuteAsync(null);

        Assert.Equal(patches, ItemPatches(client));
        Assert.Contains("date and time", items.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        var unchanged = await client.GetRequestedItemAsync("ritm-dock", CancellationToken.None);
        Assert.Equal("1", unchanged.State);
        Assert.Equal("", unchanged.FollowUp);
    }

    [Fact]
    public async Task IncidentBlankFollowUpBecomesSevenDaysWhenPutOnHold()
    {
        using var client = new SampleServiceNowClient();
        var incidents = await OpenIncidentsAsync(client, "inc-printer");
        incidents.FollowUpNow = () => Now;

        Assert.Equal("", incidents.FollowUp);
        incidents.State = "3";

        Assert.True(incidents.ShowHoldReason);
        Assert.Equal("2026-10-14 15:04:23", incidents.FollowUp);
    }

    [Fact]
    public async Task IncidentKeepsAnExistingFollowUp()
    {
        using var client = new SampleServiceNowClient();
        var incidents = await OpenIncidentsAsync(client, "inc-hold");
        incidents.FollowUpNow = () => Now;

        Assert.Equal("2026-10-01 09:00:00", incidents.FollowUp);
        incidents.State = "2";
        incidents.State = "3";
        Assert.Equal("2026-10-01 09:00:00", incidents.FollowUp);
    }

    [Fact]
    public async Task IncidentSavesAPickerValueAndATypedValue()
    {
        using var client = new SampleServiceNowClient();
        var incidents = await OpenIncidentsAsync(client, "inc-printer");
        incidents.FollowUpNow = () => Now;
        incidents.State = "3";
        incidents.HoldReason = "awaiting_caller";
        incidents.FollowUp = FollowUpValue.FormatSelection(new DateTime(2026, 11, 20), 10, 10, 23);
        await incidents.SaveCommand.ExecuteAsync(null);

        Assert.Equal("", incidents.ErrorMessage);
        var picked = await client.GetIncidentAsync("inc-printer", CancellationToken.None);
        Assert.Equal("2026-11-20 10:10:23", picked.FollowUp);
        Assert.Equal("3", picked.State);

        incidents.FollowUp = "2026-12-01 08:05";
        await incidents.SaveCommand.ExecuteAsync(null);

        Assert.Equal("", incidents.ErrorMessage);
        var typed = await client.GetIncidentAsync("inc-printer", CancellationToken.None);
        Assert.Equal("2026-12-01 08:05", typed.FollowUp);
    }

    [Fact]
    public async Task IncidentRejectsAnUnparseableFollowUp()
    {
        using var client = new SampleServiceNowClient();
        var incidents = await OpenIncidentsAsync(client, "inc-printer");
        incidents.FollowUpNow = () => Now;
        incidents.State = "3";
        incidents.HoldReason = "awaiting_caller";
        incidents.FollowUp = "not-a-date";
        var patches = IncidentPatches(client);
        await incidents.SaveCommand.ExecuteAsync(null);

        Assert.Equal(patches, IncidentPatches(client));
        Assert.Contains("date and time", incidents.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        var unchanged = await client.GetIncidentAsync("inc-printer", CancellationToken.None);
        Assert.Equal("2", unchanged.State);
        Assert.Equal("", unchanged.FollowUp);
    }

    [Fact]
    public void IncidentChangesSendFollowUpInTheSaveFormat()
    {
        var json = ChangeJson.FromIncident(new IncidentChanges
        {
            State = "3",
            HoldReason = "awaiting_caller",
            FollowUp = FollowUpValue.FormatSelection(new DateTime(2026, 11, 20), 10, 10, 23)
        });

        Assert.Contains("\"follow_up\":\"2026-11-20 10:10:23\"", json, StringComparison.Ordinal);
        Assert.Contains("\"hold_reason\":\"awaiting_caller\"", json, StringComparison.Ordinal);
    }

    private static async Task<RequestedItemWorkspaceViewModel> OpenItemsAsync(SampleServiceNowClient client, string sysId)
    {
        var items = new RequestedItemWorkspaceViewModel(new RecordingDesktopServices());
        items.Attach(client);
        await items.EnsureChoicesAsync();
        await items.OpenFromSearchAsync(sysId);
        return items;
    }

    private static async Task<IncidentWorkspaceViewModel> OpenIncidentsAsync(SampleServiceNowClient client, string sysId)
    {
        var incidents = new IncidentWorkspaceViewModel(new RecordingDesktopServices());
        incidents.Attach(client);
        await incidents.EnsureChoicesAsync();
        await incidents.OpenFromSearchAsync(sysId);
        return incidents;
    }

    private static int ItemPatches(SampleServiceNowClient client) =>
        client.RecentActivity.Count(entry => entry.Method == "PATCH" && entry.Path.Contains("sc_req_item", StringComparison.Ordinal));

    private static int IncidentPatches(SampleServiceNowClient client) =>
        client.RecentActivity.Count(entry => entry.Method == "PATCH" && entry.Path.Contains("incident", StringComparison.Ordinal));
}
