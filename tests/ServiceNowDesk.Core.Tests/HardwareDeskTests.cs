using ServiceNowDesk.Client;
using ServiceNowDesk.Mapping;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class HardwareDeskTests
{
    [Fact]
    public async Task NewIncidentDefaultsChannelToDirectAndLeavesSavedContactType()
    {
        using var client = new SampleServiceNowClient();
        var workspace = new IncidentWorkspaceViewModel(new RecordingDesktopServices());
        workspace.Attach(client);
        await workspace.EnsureChoicesAsync();
        await workspace.OpenFromSearchAsync("inc-printer");

        Assert.Equal("phone", workspace.ContactType);
        Assert.Contains(workspace.ContactChoices, choice => choice.Value == "direct" && choice.Label == "Direct");

        workspace.NewRecordCommand.Execute(null);
        Assert.Equal("direct", workspace.ContactType);
        Assert.False(workspace.IsDirty);

        workspace.Caller.Set("user-jordan", "Jordan Lee");
        workspace.ShortDescription = "Headset for the desk";
        workspace.Priority = "1";
        await workspace.SaveCommand.ExecuteAsync(null);

        Assert.Equal("direct", workspace.ContactType);
        Assert.Equal("", workspace.Priority);
        Assert.Equal("Headset for the desk", workspace.ShortDescription);
    }

    [Fact]
    public async Task DirectStaysWhenTheInstanceUsesItsOwnValueAndIsAddedWhenMissing()
    {
        var kept = ContactTypeCatalog.Merge([new Choice("channel_direct", "Direct"), new Choice("phone", "Phone")]);
        Assert.Equal("channel_direct", ContactTypeCatalog.DefaultValue(kept));
        Assert.Single(kept, choice => choice.Label == "Direct");

        var added = ContactTypeCatalog.Merge([new Choice("phone", "Phone")]);
        Assert.Equal("direct", ContactTypeCatalog.DefaultValue(added));
        Assert.Contains(added, choice => choice.Value == "direct" && choice.Label == "Direct");

        using var client = new SampleServiceNowClient();
        client.ContactTypeChoices = [new Choice("phone", "Phone"), new Choice("email", "Email")];
        var workspace = new IncidentWorkspaceViewModel(new RecordingDesktopServices());
        workspace.Attach(client);
        await workspace.EnsureChoicesAsync();
        workspace.NewRecordCommand.Execute(null);

        Assert.Equal("direct", workspace.ContactType);
        Assert.Contains(workspace.ContactChoices, choice => choice.Label == "Direct");
    }

    [Fact]
    public async Task PriorityOnTheIncidentFormIsNotPosted()
    {
        using var client = new SampleServiceNowClient();
        var workspace = new IncidentWorkspaceViewModel(new RecordingDesktopServices());
        workspace.Attach(client);
        await workspace.OpenFromSearchAsync("inc-printer");
        workspace.Priority = "1";
        workspace.ShortDescription = "Printer jam changed";
        await workspace.SaveCommand.ExecuteAsync(null);

        var saved = await client.GetIncidentAsync("inc-printer", CancellationToken.None);
        Assert.Equal("3", saved.Priority);
        Assert.Equal("Printer jam changed", saved.ShortDescription);
    }

    [Fact]
    public void HpPrefixHintDoesNotRewriteTheSerial()
    {
        Assert.Equal(HardwareSerial.HpPrefixHint, HardwareSerial.HintFor("35CG6245F8S"));
        Assert.Equal(HardwareSerial.HpPrefixHint, HardwareSerial.HintFor("35CG62720ZR"));
        Assert.Equal(HardwareSerial.HpPrefixHint, HardwareSerial.HintFor("SN:5cd6220GYW"));
        Assert.Equal(HardwareSerial.HpPrefixHint, HardwareSerial.HintFor("SN:5c"));
        Assert.Equal("", HardwareSerial.HintFor("5CG6245F8S"));
        Assert.Equal("", HardwareSerial.HintFor("5cd6220GYW"));
        Assert.Equal("", HardwareSerial.HintFor("ABCDEFG"));
    }

    [Fact]
    public async Task EnterAddsTheExactScanAndALeadingCharacterDoesNotMatchUntilEdited()
    {
        using var client = new SampleServiceNowClient();
        var workspace = await OpenHardwareAsync(client);
        workspace.ReceiveStockroom.Set("stock-bne", "Brisbane");

        workspace.ScanText = "5CG6245F8S";
        await workspace.ReceiveScanCommand.ExecuteAsync(null);
        Assert.Equal("", workspace.ScanText);
        var exact = Assert.Single(workspace.Batch);
        Assert.Equal("5CG6245F8S", exact.Text);
        Assert.Equal("", exact.Status);
        Assert.Equal(0, HardwarePatches(client));
        Assert.Equal(HardwareCatalog.InTransit, (await client.GetHardwareAsync("hw-transit", CancellationToken.None)).InstallStatus);

        await workspace.LookupRowCommand.ExecuteAsync(exact);
        var received = await client.GetHardwareAsync("hw-transit", CancellationToken.None);
        Assert.Equal(HardwareCatalog.InStock, received.InstallStatus);
        Assert.Equal(HardwareCatalog.Available, received.Substatus);
        Assert.Equal("stock-bne", received.Stockroom.SysId);
        Assert.Equal("For testing by Mark Lindsay", received.Comments);
        Assert.Equal("5CG6245F8S", exact.Text);
        Assert.Equal("Received", exact.Status);
        Assert.Equal(HardwareCatalog.InTransit, exact.OldState);
        Assert.Equal(HardwareCatalog.InStock, exact.NewState);

        var patches = HardwarePatches(client);
        workspace.ScanText = "35CG6245F8S";
        await workspace.ReceiveScanCommand.ExecuteAsync(null);

        Assert.Equal("", workspace.ScanText);
        Assert.Equal(patches, HardwarePatches(client));
        var junk = workspace.Batch[0];
        Assert.Equal("35CG6245F8S", junk.Text);
        Assert.Equal("", junk.Status);
        Assert.Equal(HardwareSerial.HpPrefixHint, junk.Hint);

        await workspace.LookupRowCommand.ExecuteAsync(junk);
        Assert.Equal("35CG6245F8S", junk.Text);
        Assert.Equal("Unmatched", junk.Status);
        Assert.Equal(patches, HardwarePatches(client));
        Assert.Equal(5, client.HardwareCount);

        junk.Text = "5CG6245F8S";
        await workspace.LookupRowCommand.ExecuteAsync(junk);

        Assert.Equal("5CG6245F8S", junk.Text);
        Assert.Equal("Already in stock", junk.Status);
        Assert.Equal(patches, HardwarePatches(client));
        var again = await client.GetHardwareAsync("hw-transit", CancellationToken.None);
        Assert.Equal(HardwareCatalog.InStock, again.InstallStatus);
        Assert.Equal("For testing by Mark Lindsay", again.Comments);
    }

    [Fact]
    public async Task CaseInsensitiveHpSerialAndTypedDellSerialMatchAsEdited()
    {
        using var client = new SampleServiceNowClient();
        var workspace = await OpenHardwareAsync(client);
        workspace.ReceiveStockroom.Set("stock-bne", "Brisbane");

        workspace.ScanText = "5cd6220GYW";
        await workspace.ReceiveScanCommand.ExecuteAsync(null);
        var hp = Assert.Single(workspace.Batch);
        Assert.Equal("5cd6220GYW", hp.Text);
        Assert.Equal("", hp.Status);
        Assert.Equal(0, HardwarePatches(client));
        await workspace.LookupRowCommand.ExecuteAsync(hp);
        Assert.Equal("5cd6220GYW", hp.Text);
        Assert.Equal("Received", hp.Status);
        Assert.Equal("5CD6220GYW", hp.MatchedSerial);
        var stored = await client.GetHardwareAsync("hw-hp-case", CancellationToken.None);
        Assert.Equal(HardwareCatalog.InStock, stored.InstallStatus);
        Assert.Equal("HP serial stored in uppercase", stored.Comments);

        workspace.ScanText = "ABCDEFG";
        await workspace.ReceiveScanCommand.ExecuteAsync(null);
        var dell = workspace.Batch[0];
        Assert.Equal("ABCDEFG", dell.Text);
        Assert.Equal("", dell.Hint);
        Assert.Equal("", dell.Status);
        await workspace.LookupRowCommand.ExecuteAsync(dell);
        Assert.Equal("ABCDEFG", dell.Text);
        Assert.Equal("", dell.Hint);
        Assert.Equal("Received", dell.Status);
        Assert.Equal("ABCDEFG", dell.MatchedSerial);
        var tag = await client.GetHardwareAsync("hw-dell", CancellationToken.None);
        Assert.Equal(HardwareCatalog.InStock, tag.InstallStatus);
        Assert.Equal("Dell service tag", tag.Comments);
    }

    [Fact]
    public async Task PrefixedHpSerialDoesNotMatchUntilTheRowIsEdited()
    {
        using var client = new SampleServiceNowClient();
        var workspace = await OpenHardwareAsync(client);
        workspace.ReceiveStockroom.Set("stock-bne", "Brisbane");
        workspace.ScanText = "SN:5cd6220GYW";
        await workspace.ReceiveScanCommand.ExecuteAsync(null);

        var row = Assert.Single(workspace.Batch);
        Assert.Equal("SN:5cd6220GYW", row.Text);
        Assert.Equal("", row.Status);
        Assert.Equal(HardwareSerial.HpPrefixHint, row.Hint);
        Assert.Equal(0, HardwarePatches(client));
        await workspace.LookupRowCommand.ExecuteAsync(row);
        Assert.Equal("SN:5cd6220GYW", row.Text);
        Assert.Equal("Unmatched", row.Status);
        Assert.Equal(0, HardwarePatches(client));
        Assert.Equal(HardwareCatalog.InTransit, (await client.GetHardwareAsync("hw-hp-case", CancellationToken.None)).InstallStatus);

        row.Text = "5cd6220GYW";
        await workspace.LookupRowCommand.ExecuteAsync(row);

        Assert.Equal("5cd6220GYW", row.Text);
        Assert.Equal("Received", row.Status);
        Assert.Equal("5CD6220GYW", row.MatchedSerial);
        Assert.Equal(HardwareCatalog.InStock, (await client.GetHardwareAsync("hw-hp-case", CancellationToken.None)).InstallStatus);
    }

    [Fact]
    public async Task UnknownScanStaysUnmatchedAndCreatesNothing()
    {
        using var client = new SampleServiceNowClient();
        var workspace = await OpenHardwareAsync(client);
        workspace.ReceiveStockroom.Set("stock-bne", "Brisbane");
        workspace.ScanText = "ZZZNOTREAL1";
        await workspace.ReceiveScanCommand.ExecuteAsync(null);

        var row = Assert.Single(workspace.Batch);
        Assert.Equal("ZZZNOTREAL1", row.Text);
        Assert.Equal("", row.Status);
        Assert.Equal(0, HardwarePatches(client));
        await workspace.LookupRowCommand.ExecuteAsync(row);
        Assert.Equal("ZZZNOTREAL1", row.Text);
        Assert.Equal("Unmatched", row.Status);
        Assert.Equal("", row.Hint);
        Assert.Equal(5, client.HardwareCount);
        Assert.Equal(0, HardwarePatches(client));
    }

    [Fact]
    public async Task ReceivingWithoutAStockroomDoesNotWrite()
    {
        using var client = new SampleServiceNowClient();
        var workspace = await OpenHardwareAsync(client);
        workspace.ScanText = "5CG6245F8S";
        await workspace.ReceiveScanCommand.ExecuteAsync(null);

        var row = Assert.Single(workspace.Batch);
        Assert.Equal("5CG6245F8S", row.Text);
        Assert.Equal("", row.Status);
        Assert.Equal(0, HardwarePatches(client));
        await workspace.LookupRowCommand.ExecuteAsync(row);
        Assert.Equal("Needs stockroom", row.Status);
        Assert.Contains("stockroom", workspace.ReceiveMessage, StringComparison.OrdinalIgnoreCase);
        var asset = await client.GetHardwareAsync("hw-transit", CancellationToken.None);
        Assert.Equal(HardwareCatalog.InTransit, asset.InstallStatus);
        Assert.Equal("", asset.Substatus);
        Assert.Equal("For testing by Mark Lindsay", asset.Comments);
        Assert.Equal(0, HardwarePatches(client));
    }

    [Fact]
    public async Task RetiredUpdateDoesNotSendDisposalFields()
    {
        using var client = new SampleServiceNowClient();
        var workspace = await OpenHardwareAsync(client);
        await workspace.OpenAsync("hw-inuse");
        workspace.InstallStatus = HardwareCatalog.Retired;
        await workspace.SubstateLoad;
        Assert.Equal("", workspace.Substatus);
        await workspace.SaveCommand.ExecuteAsync(null);

        var payload = client.LastHardwarePayload;
        Assert.Contains("\"install_status\":\"Retired\"", payload);
        Assert.DoesNotContain("disposal_reason", payload);
        Assert.DoesNotContain("beneficiary", payload);
        Assert.DoesNotContain("resale_price", payload);
        Assert.DoesNotContain("scheduled_retirement", payload);
        Assert.DoesNotContain("disposal_date", payload);
        var saved = await client.GetHardwareAsync("hw-inuse", CancellationToken.None);
        Assert.Equal(HardwareCatalog.Retired, saved.InstallStatus);
        Assert.Equal("Assigned laptop", saved.Comments);
        Assert.Equal("loc-syd", saved.Location.SysId);
    }

    [Fact]
    public async Task CommentsRoundTripOnUpdate()
    {
        using var client = new SampleServiceNowClient();
        var workspace = await OpenHardwareAsync(client);
        await workspace.OpenAsync("hw-inuse");
        workspace.Comments = "Shelf note for the batch";
        await workspace.SaveCommand.ExecuteAsync(null);

        var saved = await client.GetHardwareAsync("hw-inuse", CancellationToken.None);
        Assert.Equal("Shelf note for the batch", saved.Comments);
        Assert.Equal(HardwareCatalog.InUse, saved.InstallStatus);
        Assert.Equal("user-jordan", saved.AssignedTo.SysId);
        Assert.Contains("\"comments\":\"Shelf note for the batch\"", client.LastHardwarePayload);
        Assert.DoesNotContain("install_status", client.LastHardwarePayload);
        Assert.DoesNotContain("disposal", client.LastHardwarePayload);
    }

    [Fact]
    public async Task ChangingStateClearsASubstateThatDoesNotBelong()
    {
        using var client = new SampleServiceNowClient();
        var workspace = await OpenHardwareAsync(client);
        await workspace.OpenAsync("hw-stock");
        Assert.True(workspace.ShowSubstate);
        Assert.Equal(HardwareCatalog.Available, workspace.Substatus);

        workspace.InstallStatus = HardwareCatalog.InTransit;
        await workspace.SubstateLoad;

        Assert.Equal("", workspace.Substatus);
        Assert.False(workspace.ShowSubstate);
    }

    [Fact]
    public void HardwareChangeJsonSendsOnlyTheEditedAssetFields()
    {
        var json = ChangeJson.FromHardware(new HardwareChanges
        {
            InstallStatus = HardwareCatalog.Retired,
            Comments = "Kept on the asset"
        });

        Assert.Contains("\"install_status\":\"Retired\"", json);
        Assert.Contains("\"comments\":\"Kept on the asset\"", json);
        Assert.DoesNotContain("disposal_reason", json);
        Assert.DoesNotContain("beneficiary", json);
        Assert.DoesNotContain("resale_price", json);
        Assert.DoesNotContain("scheduled_retirement", json);
        Assert.DoesNotContain("serial_number", json);
        Assert.DoesNotContain("display_name", json);
    }

    [Fact]
    public async Task HardwareSearchMatchesSerialModelOrAssignee()
    {
        using var client = new SampleServiceNowClient();
        var workspace = await OpenHardwareAsync(client);
        Assert.Equal(5, workspace.Items.Count);

        workspace.SearchText = "5CG6245F8S";
        await workspace.RefreshAsync();
        Assert.Equal("5CG6245F8S", Assert.Single(workspace.Items).SerialNumber);

        workspace.SearchText = "Fury";
        await workspace.RefreshAsync();
        Assert.Equal("5CG30710BR", Assert.Single(workspace.Items).SerialNumber);

        workspace.SearchText = "Jordan";
        await workspace.RefreshAsync();
        var assigned = Assert.Single(workspace.Items);
        Assert.Equal("5CG0000DBR", assigned.SerialNumber);
        Assert.Equal("Jordan Lee", assigned.AssignedTo.Display);
    }

    [Fact]
    public async Task DefaultOfficeFallsBackToTheSignedInUsersLocation()
    {
        using var client = new SampleServiceNowClient();
        client.SignedInUser = ViewerAt("Sydney Office");
        var workspace = new HardwareWorkspaceViewModel(new MemorySettingsStore());
        workspace.Attach(client);
        workspace.RememberViewer(await client.GetCurrentUserAsync(CancellationToken.None));
        await workspace.RefreshAsync();

        Assert.Contains("location.nameLIKE", client.LastHardwareQuery);
        Assert.Contains("Sydney Office", client.LastHardwareQuery);
        Assert.DoesNotContain("Hong Kong", client.LastHardwareQuery);
        Assert.DoesNotContain("Maroochydore", client.LastHardwareQuery);
        Assert.Equal(2, workspace.Items.Count);
        Assert.All(workspace.Items, asset => Assert.Contains("Sydney", asset.Location.Display, StringComparison.OrdinalIgnoreCase));
        Assert.True(workspace.Offices.Single(office => office.Name == "Sydney Office").IsSelected);
        foreach (var city in HardwareCatalog.KnownOfficeNames)
            Assert.Contains(workspace.Offices, office => office.Name == city);
        Assert.Equal(1, HardwareGets(client));

        using var open = new SampleServiceNowClient();
        var anywhere = new HardwareWorkspaceViewModel(new MemorySettingsStore());
        anywhere.Attach(open);
        anywhere.RememberViewer(await open.GetCurrentUserAsync(CancellationToken.None));
        await anywhere.RefreshAsync();

        Assert.Contains("No location on the signed-in account", anywhere.OfficeStatus);
        Assert.DoesNotContain("location.name", open.LastHardwareQuery);
        Assert.Equal(5, anywhere.Items.Count);
        foreach (var city in HardwareCatalog.KnownOfficeNames)
            Assert.Contains(anywhere.Offices, office => office.Name == city);
    }

    [Fact]
    public async Task SavedMultiOfficeDefaultIsWhatTheInitialHardwareQueryUses()
    {
        using var client = new SampleServiceNowClient();
        client.SignedInUser = ViewerAt("Hong Kong Office");
        var store = new MemorySettingsStore();
        store.Save(new DeskSettings
        {
            HardwareOfficeLocations = ["Brisbane", "Maroochydore", "Gold Coast", "Townsville", "Cairns"]
        });
        var workspace = new HardwareWorkspaceViewModel(store);
        workspace.Attach(client);
        workspace.RememberViewer(await client.GetCurrentUserAsync(CancellationToken.None));
        await workspace.RefreshAsync();

        Assert.Contains("location.nameLIKE\"Brisbane\"", client.LastHardwareQuery);
        Assert.Contains("Maroochydore", client.LastHardwareQuery);
        Assert.Contains("Gold Coast", client.LastHardwareQuery);
        Assert.Contains("Townsville", client.LastHardwareQuery);
        Assert.Contains("Cairns", client.LastHardwareQuery);
        Assert.DoesNotContain("Hong Kong", client.LastHardwareQuery);
        Assert.Equal(2, workspace.Items.Count);
        Assert.All(workspace.Items, asset => Assert.Contains("Brisbane", asset.Location.Display, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, HardwareGets(client));
        Assert.False(workspace.Offices.Single(office => office.Name == "Hong Kong Office").IsSelected);
    }

    [Fact]
    public async Task FullListSearchDoesNotSendTheOfficeRestriction()
    {
        using var client = new SampleServiceNowClient();
        client.SignedInUser = ViewerAt("Sydney Office");
        var workspace = new HardwareWorkspaceViewModel(new MemorySettingsStore());
        workspace.Attach(client);
        workspace.RememberViewer(await client.GetCurrentUserAsync(CancellationToken.None));
        await workspace.RefreshAsync();
        Assert.Contains("location.name", client.LastHardwareQuery);

        await workspace.SearchAllLocationsCommand.ExecuteAsync(null);

        Assert.DoesNotContain("location.name", client.LastHardwareQuery);
        Assert.Contains("model_category.name=Computer", client.LastHardwareQuery);
        Assert.Equal(5, workspace.Items.Count);
        Assert.Contains("Showing all locations.", workspace.OfficeStatus);

        var sydney = workspace.Offices.Single(office => office.Name == "Sydney Office");
        sydney.IsSelected = true;
        await workspace.OfficeLoad;
        Assert.Contains("Sydney Office", client.LastHardwareQuery);
        Assert.Equal(2, workspace.Items.Count);

        sydney.IsSelected = false;
        await workspace.OfficeLoad;
        Assert.DoesNotContain("location.name", client.LastHardwareQuery);
        Assert.Equal(5, workspace.Items.Count);
    }

    [Fact]
    public async Task ColumnFiltersNarrowTheVisibleRowsAndCombine()
    {
        using var client = new SampleServiceNowClient();
        var workspace = await OpenHardwareAsync(client);
        Assert.Equal(5, workspace.Items.Count);
        var gets = HardwareGets(client);

        workspace.LocationFilter = "Brisbane";
        workspace.StateFilter = "In transit";
        Assert.Equal(2, workspace.Items.Count);
        Assert.All(workspace.Items, asset =>
        {
            Assert.Contains("Brisbane", asset.Location.Display, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("transit", asset.InstallStatusLabel, StringComparison.OrdinalIgnoreCase);
        });

        workspace.SerialFilter = "5CD";
        Assert.Equal("5CD6220GYW", Assert.Single(workspace.Items).SerialNumber);

        workspace.CommentsFilter = "shelf";
        Assert.Empty(workspace.Items);

        workspace.SerialFilter = "";
        workspace.CommentsFilter = "";
        Assert.Equal(2, workspace.Items.Count);
        Assert.Equal(gets, HardwareGets(client));
    }

    [Fact]
    public async Task SetAsDefaultPersistsAndIsReadBack()
    {
        using var client = new SampleServiceNowClient();
        var store = new MemorySettingsStore();
        var workspace = new HardwareWorkspaceViewModel(store);
        workspace.Attach(client);
        await workspace.RefreshAsync();

        workspace.Offices.Single(office => office.Name == "Brisbane").IsSelected = true;
        workspace.Offices.Single(office => office.Name == "Cairns").IsSelected = true;
        await workspace.OfficeLoad;
        workspace.SetDefaultOfficesCommand.Execute(null);

        Assert.Equal(["Brisbane", "Cairns"], store.Load().HardwareOfficeLocations);
        Assert.Contains("Default saved: Brisbane, Cairns.", workspace.OfficeStatus);

        var json = DeskSettingsFile.Serialize(store.Load(), value => value ?? "");
        var roundTrip = DeskSettingsFile.Deserialize(json, value => value ?? "");
        Assert.Equal(["Brisbane", "Cairns"], roundTrip.HardwareOfficeLocations);
        Assert.Equal(["Brisbane", "Maroochydore", "Gold Coast", "Townsville", "Cairns"], roundTrip.OfficeLocations);

        var connection = new ConnectionViewModel();
        connection.Load(roundTrip);
        var built = connection.BuildSettings();
        Assert.Equal(["Brisbane", "Cairns"], built.HardwareOfficeLocations);
        Assert.Equal(["Brisbane", "Maroochydore", "Gold Coast", "Townsville", "Cairns"], built.OfficeLocations);

        client.SignedInUser = ViewerAt("Hong Kong Office");
        var again = new HardwareWorkspaceViewModel(store);
        again.Attach(client);
        again.RememberViewer(await client.GetCurrentUserAsync(CancellationToken.None));
        await again.RefreshAsync();

        Assert.Contains("location.nameLIKE\"Brisbane\"", client.LastHardwareQuery);
        Assert.Contains("Cairns", client.LastHardwareQuery);
        Assert.DoesNotContain("Hong Kong", client.LastHardwareQuery);
        Assert.DoesNotContain("Maroochydore", client.LastHardwareQuery);
        Assert.True(again.Offices.Single(office => office.Name == "Brisbane").IsSelected);
        Assert.True(again.Offices.Single(office => office.Name == "Cairns").IsSelected);
        Assert.False(again.Offices.Single(office => office.Name == "Gold Coast").IsSelected);
        Assert.Equal(2, again.Items.Count);
    }

    private static CurrentUser ViewerAt(string location) =>
        new("sample-user", "Alex Rivera", "alex.rivera", "alex.rivera@example.com") { Location = location };

    private static int HardwareGets(SampleServiceNowClient client) =>
        client.RecentActivity.Count(activity => activity.Method == "GET" && activity.Path.Contains("alm_hardware", StringComparison.Ordinal));

    private static async Task<HardwareWorkspaceViewModel> OpenHardwareAsync(SampleServiceNowClient client)
    {
        var workspace = new HardwareWorkspaceViewModel();
        workspace.Attach(client);
        await workspace.RefreshAsync();
        return workspace;
    }

    private static int HardwarePatches(SampleServiceNowClient client) =>
        client.RecentActivity.Count(activity => activity.Method == "PATCH" && activity.Path.Contains("alm_hardware", StringComparison.Ordinal));
}
