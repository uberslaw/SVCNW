using System.Reflection;
using System.Runtime.ExceptionServices;
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

        Assert.DoesNotContain("location.name", client.LastHardwareQuery);
        Assert.Contains("model_category.name=Computer", client.LastHardwareQuery);
        Assert.Equal(2, workspace.Items.Count);
        Assert.All(workspace.Items, asset => Assert.Contains("Sydney", asset.Location.Display, StringComparison.OrdinalIgnoreCase));
        Assert.True(workspace.Offices.Single(office => office.Name == "Sydney Office").IsSelected);
        Assert.Contains("Showing your office: Sydney Office.", workspace.OfficeStatus);
        AssertStoredOffice(workspace, "Brisbane Office");
        foreach (var city in HardwareCatalog.KnownOfficeNames.Where(city => city != "Brisbane"))
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
        AssertStoredOffice(anywhere, "Brisbane Office");
        foreach (var city in HardwareCatalog.KnownOfficeNames.Where(city => city != "Brisbane"))
            Assert.Contains(anywhere.Offices, office => office.Name == city);
    }

    [Fact]
    public async Task SavedMultiOfficeListDoesNotReplaceTheAccountOffice()
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

        Assert.Equal("Your office: Hong Kong Office", workspace.AccountOfficeText);
        Assert.Contains("Showing your office: Hong Kong Office.", workspace.OfficeStatus);
        Assert.DoesNotContain("location.name", client.LastHardwareQuery);
        Assert.Equal("Hong Kong Office", Assert.Single(workspace.Items).Location.Display);
        Assert.True(workspace.Offices.Single(office => office.Name == "Hong Kong Office").IsSelected);
        Assert.False(workspace.Offices.Single(office => office.Name == "Brisbane Office").IsSelected);
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
        Assert.DoesNotContain("location.name", client.LastHardwareQuery);
        var gets = HardwareGets(client);

        await workspace.SearchAllLocationsCommand.ExecuteAsync(null);

        Assert.Equal(gets, HardwareGets(client));
        Assert.Equal(5, workspace.Items.Count);
        Assert.Contains("Showing all locations.", workspace.OfficeStatus);
        Assert.Contains(workspace.Items, asset => asset.Location.Display == "Hong Kong Office");
        Assert.DoesNotContain(workspace.Offices, office => office.Name == "Hong Kong Office");
        Assert.Equal("All locations", workspace.OfficeSelectionSummary);

        var sydney = workspace.Offices.Single(office => office.Name == "Sydney Office");
        sydney.IsSelected = true;
        await workspace.OfficeLoad;
        Assert.Equal(gets, HardwareGets(client));
        Assert.Equal(2, workspace.Items.Count);

        sydney.IsSelected = false;
        await workspace.OfficeLoad;
        Assert.Equal(gets, HardwareGets(client));
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
        await workspace.ColumnFiltersReady;
        Assert.Equal(2, workspace.Items.Count);
        Assert.Equal(2, workspace.ShownCount);
        Assert.All(workspace.Items, asset =>
        {
            Assert.Contains("Brisbane", asset.Location.Display, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("transit", asset.InstallStatusLabel, StringComparison.OrdinalIgnoreCase);
        });

        workspace.SerialFilter = "5CD";
        await workspace.ColumnFiltersReady;
        Assert.Equal("5CD6220GYW", Assert.Single(workspace.Items).SerialNumber);

        workspace.CommentsFilter = "shelf";
        await workspace.ColumnFiltersReady;
        Assert.Empty(workspace.Items);

        workspace.SerialFilter = "";
        workspace.CommentsFilter = "";
        await workspace.ColumnFiltersReady;
        Assert.Equal(2, workspace.Items.Count);
        Assert.Equal(gets, HardwareGets(client));
    }

    [Fact]
    public async Task PlainModelFilterMatchesContainsAndClears()
    {
        using var client = new SampleServiceNowClient();
        var workspace = new HardwareWorkspaceViewModel(new MemorySettingsStore());
        workspace.Attach(client);
        await workspace.RefreshAsync();
        await workspace.SearchAllLocationsCommand.ExecuteAsync(null);
        var all = workspace.Items.Count;
        Assert.True(all > 1);

        workspace.ModelFilter = "fury";
        await workspace.ColumnFiltersReady;
        var fury = Assert.Single(workspace.Items);
        Assert.Contains("Fury", fury.Model, StringComparison.OrdinalIgnoreCase);

        workspace.AssignedFilter = "nobody-matches-this";
        await workspace.ColumnFiltersReady;
        Assert.Empty(workspace.Items);

        workspace.AssignedFilter = "";
        await workspace.ColumnFiltersReady;
        Assert.Equal("HP ZBook Fury 16 G9", Assert.Single(workspace.Items).Model);

        workspace.ModelFilter = "";
        await workspace.ColumnFiltersReady;
        Assert.Equal(all, workspace.Items.Count);
    }

    [Fact]
    public async Task ColumnFilterShowsFilteringUntilShownCountUpdates()
    {
        using var client = new SampleServiceNowClient();
        var workspace = new HardwareWorkspaceViewModel(new MemorySettingsStore());
        workspace.Attach(client);
        await workspace.RefreshAsync();
        await workspace.SearchAllLocationsCommand.ExecuteAsync(null);
        var all = workspace.ShownCount;
        Assert.True(all > 1);
        Assert.False(workspace.IsFiltering);

        workspace.ModelFilter = "fury";
        Assert.True(workspace.IsFiltering);
        Assert.Equal(all, workspace.ShownCount);

        await workspace.ColumnFiltersReady;
        Assert.False(workspace.IsFiltering);
        Assert.Equal(1, workspace.ShownCount);
        Assert.Contains("Fury", Assert.Single(workspace.Items).Model, StringComparison.OrdinalIgnoreCase);

        workspace.ModelFilter = "";
        Assert.True(workspace.IsFiltering);
        await workspace.ColumnFiltersReady;
        Assert.False(workspace.IsFiltering);
        Assert.Equal(all, workspace.ShownCount);
    }

    [Fact]
    public async Task EveryColumnFilterNarrowsShownCount()
    {
        using var client = new SampleServiceNowClient();
        var workspace = await OpenHardwareAsync(client);
        var all = workspace.ShownCount;
        Assert.Equal(5, all);

        await AssertFilterNarrowsAsync(workspace, () => workspace.SerialFilter = "5CD", all);
        workspace.SerialFilter = "";
        await workspace.ColumnFiltersReady;

        await AssertFilterNarrowsAsync(workspace, () => workspace.ModelFilter = "Fury", all);
        workspace.ModelFilter = "";
        await workspace.ColumnFiltersReady;

        await AssertFilterNarrowsAsync(workspace, () => workspace.AssignedFilter = "nobody-matches-this", all, expectEmpty: true);
        workspace.AssignedFilter = "";
        await workspace.ColumnFiltersReady;

        await AssertFilterNarrowsAsync(workspace, () => workspace.LocationFilter = "Brisbane", all);
        workspace.LocationFilter = "";
        await workspace.ColumnFiltersReady;

        await AssertFilterNarrowsAsync(workspace, () => workspace.StateFilter = "In transit", all);
        workspace.StateFilter = "";
        await workspace.ColumnFiltersReady;

        workspace.SubstatusFilter = "zzzz-no-such-substate";
        await workspace.ColumnFiltersReady;
        Assert.Equal(0, workspace.ShownCount);
        workspace.SubstatusFilter = "";
        await workspace.ColumnFiltersReady;

        workspace.CommentsFilter = "zzzz-no-such-comment";
        await workspace.ColumnFiltersReady;
        Assert.Equal(0, workspace.ShownCount);
        workspace.CommentsFilter = "";
        await workspace.ColumnFiltersReady;
        Assert.Equal(all, workspace.ShownCount);
    }

    [Fact]
    public async Task SavedCatalogIsSearchableWhileRefreshAllDownloads()
    {
        using var inner = new SampleServiceNowClient();
        var catalog = new MemoryHardwareCatalogStore();
        var settings = new MemorySettingsStore();
        settings.Save(new DeskSettings { HardwareOfficeLocations = ["Brisbane"], HardwareOfficeOverride = true });

        var seed = new HardwareWorkspaceViewModel(settings, catalog);
        seed.Attach(inner);
        await seed.RefreshAsync();
        Assert.Contains(seed.Items, asset => asset.Location.Display == "Brisbane Office");
        var savedSerial = seed.Items.First(asset => asset.Location.Display == "Brisbane Office").SerialNumber;

        inner.AddComputer(new HardwareAsset
        {
            SysId = "hw-bne-during-download",
            SerialNumber = "BNEDURING",
            Model = "HP EliteBook During Download",
            ModelCategory = HardwareCatalog.Computer,
            Location = new ReferenceValue("loc-bne", "Brisbane Office"),
            InstallStatus = HardwareCatalog.InUse,
            InstallStatusLabel = HardwareCatalog.InUse
        });

        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proxy = DispatchProxy.Create<IServiceNowClient, HardwareDownloadHold>();
        var gate = (HardwareDownloadHold)proxy;
        gate.Inner = inner;
        gate.Ready = hold;

        var workspace = new HardwareWorkspaceViewModel(settings, catalog);
        workspace.Attach(proxy);
        // Disk first — no await on network — so the tab is searchable before refresh.
        workspace.ShowSavedCatalog();
        Assert.Contains(workspace.Items, asset => asset.SerialNumber == savedSerial);
        Assert.DoesNotContain(workspace.Items, asset => asset.SerialNumber == "BNEDURING");
        Assert.Contains("computers saved", workspace.CatalogStatus, StringComparison.OrdinalIgnoreCase);

        workspace.SerialFilter = savedSerial[..Math.Min(4, savedSerial.Length)];
        await workspace.ColumnFiltersReady;
        Assert.Contains(workspace.Items, asset => asset.SerialNumber == savedSerial);

        workspace.SerialFilter = "";
        await workspace.ColumnFiltersReady;
        var download = workspace.RefreshAllCatalogCommand.ExecuteAsync(null);
        var started = await Task.WhenAny(gate.Started.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(gate.Started.Task, started);
        Assert.True(workspace.IsDownloading);
        Assert.Contains(workspace.Items, asset => asset.SerialNumber == savedSerial);

        workspace.SerialFilter = savedSerial;
        await workspace.ColumnFiltersReady;
        Assert.Equal(savedSerial, Assert.Single(workspace.Items).SerialNumber);
        Assert.DoesNotContain(workspace.Items, asset => asset.SerialNumber == "BNEDURING");

        workspace.SerialFilter = "";
        await workspace.ColumnFiltersReady;
        hold.SetResult();
        await download;

        Assert.False(workspace.IsDownloading);
        Assert.Contains(workspace.Items, asset => asset.SerialNumber == "BNEDURING");
        Assert.Contains(workspace.Items, asset => asset.SerialNumber == savedSerial);
    }

    [Fact]
    public async Task EmptyFullRefreshKeepsTheSavedCatalog()
    {
        using var client = new SampleServiceNowClient();
        var catalog = new MemoryHardwareCatalogStore();
        var settings = new MemorySettingsStore();
        settings.Save(new DeskSettings { HardwareOfficeLocations = ["Brisbane"], HardwareOfficeOverride = true });

        var seed = new HardwareWorkspaceViewModel(settings, catalog);
        seed.Attach(client);
        await seed.RefreshAsync();
        var savedCount = seed.Items.Count;
        Assert.True(savedCount > 0);
        var savedSerial = seed.Items[0].SerialNumber;

        client.RemoveHardware(_ => true);
        Assert.Equal(0, client.HardwareCount);

        var workspace = new HardwareWorkspaceViewModel(settings, catalog);
        workspace.Attach(client);
        workspace.ShowSavedCatalog();
        Assert.Equal(savedCount, workspace.Items.Count);
        Assert.Contains(workspace.Items, asset => asset.SerialNumber == savedSerial);

        await workspace.RefreshAllCatalogCommand.ExecuteAsync(null);

        Assert.False(workspace.IsDownloading);
        Assert.Equal(savedCount, workspace.Items.Count);
        Assert.Contains(workspace.Items, asset => asset.SerialNumber == savedSerial);
        Assert.Contains("kept the", workspace.CatalogStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("saved on this PC", workspace.CatalogStatus, StringComparison.OrdinalIgnoreCase);

        var reloaded = catalog.Load(DeskListScope.Practice);
        Assert.NotNull(reloaded);
        Assert.True(reloaded.Items.Count >= savedCount);
    }

    [Fact]
    public async Task SetAsDefaultPersistsAndIsReadBack()
    {
        using var client = new SampleServiceNowClient();
        var store = new MemorySettingsStore();
        var workspace = new HardwareWorkspaceViewModel(store);
        workspace.Attach(client);
        await workspace.RefreshAsync();

        workspace.Offices.Single(office => office.Name == "Brisbane Office").IsSelected = true;
        workspace.Offices.Single(office => office.Name == "Cairns").IsSelected = true;
        await workspace.OfficeLoad;
        await workspace.OverrideOfficeCommand.ExecuteAsync(null);

        Assert.Equal(["Brisbane Office", "Cairns"], store.Load().HardwareOfficeLocations);
        Assert.True(store.Load().HardwareOfficeOverride);
        Assert.Contains("Showing override: Brisbane Office, Cairns.", workspace.OfficeStatus);

        var json = DeskSettingsFile.Serialize(store.Load(), value => value ?? "");
        var roundTrip = DeskSettingsFile.Deserialize(json, value => value ?? "");
        Assert.Equal(["Brisbane Office", "Cairns"], roundTrip.HardwareOfficeLocations);
        Assert.True(roundTrip.HardwareOfficeOverride);
        Assert.Equal(["Brisbane", "Maroochydore", "Gold Coast", "Townsville", "Cairns"], roundTrip.OfficeLocations);

        var connection = new ConnectionViewModel();
        connection.Load(roundTrip);
        var built = connection.BuildSettings();
        Assert.Equal(["Brisbane Office", "Cairns"], built.HardwareOfficeLocations);
        Assert.True(built.HardwareOfficeOverride);
        Assert.Equal(["Brisbane", "Maroochydore", "Gold Coast", "Townsville", "Cairns"], built.OfficeLocations);

        client.SignedInUser = ViewerAt("Hong Kong Office");
        var again = new HardwareWorkspaceViewModel(store);
        again.Attach(client);
        again.RememberViewer(await client.GetCurrentUserAsync(CancellationToken.None));
        await again.RefreshAsync();

        Assert.DoesNotContain("location.name", client.LastHardwareQuery);
        Assert.True(again.Offices.Single(office => office.Name == "Brisbane Office").IsSelected);
        Assert.True(again.Offices.Single(office => office.Name == "Cairns").IsSelected);
        Assert.False(again.Offices.Single(office => office.Name == "Gold Coast").IsSelected);
        Assert.Equal(2, again.Items.Count);
        Assert.All(again.Items, asset =>
            Assert.True(
                HardwareOfficeNames.SamePlace(asset.Location.Display, "Brisbane")
                || HardwareOfficeNames.SamePlace(asset.Location.Display, "Cairns")));
    }

    [Fact]
    public void CityAndOfficeSuffixAreTheSamePlaceAndOtherLocationsAreNot()
    {
        Assert.True(HardwareOfficeNames.SamePlace("Brisbane", "Brisbane Office"));
        Assert.True(HardwareOfficeNames.SamePlace("  brisbane ", "BRISBANE OFFICE"));
        Assert.True(HardwareOfficeNames.SamePlace("Gold Coast", "Gold Coast Office"));
        Assert.True(HardwareOfficeNames.SamePlace("Townsville Office", "townsville"));
        Assert.False(HardwareOfficeNames.SamePlace("Brisbane", "Brisbane CBD"));
        Assert.False(HardwareOfficeNames.SamePlace("Brisbane", "Sydney"));
        Assert.False(HardwareOfficeNames.SamePlace("Cairns", "Cairns Depot"));
        Assert.False(HardwareOfficeNames.SamePlace("Gold Coast", "Gold Coast Campus"));
        Assert.False(HardwareOfficeNames.SamePlace("Brisbane Office", "Brisbane Office Tower"));

        Assert.True(HardwareOfficeNames.UseIncomingLabel(
            "Brisbane", HardwareOfficeLabelKind.Seed,
            "Brisbane Office", HardwareOfficeLabelKind.Reference));
        Assert.False(HardwareOfficeNames.UseIncomingLabel(
            "Brisbane Office", HardwareOfficeLabelKind.Reference,
            "Brisbane", HardwareOfficeLabelKind.Seed));
        Assert.True(HardwareOfficeNames.UseIncomingLabel(
            "Brisbane", HardwareOfficeLabelKind.Reference,
            "Brisbane Office", HardwareOfficeLabelKind.Reference));
        Assert.True(HardwareOfficeNames.UseIncomingLabel(
            "Brisbane Office", HardwareOfficeLabelKind.Reference,
            "Brisbane", HardwareOfficeLabelKind.Asset));
        Assert.False(HardwareOfficeNames.UseIncomingLabel(
            "Brisbane", HardwareOfficeLabelKind.Asset,
            "Brisbane Office", HardwareOfficeLabelKind.Reference));
    }

    [Fact]
    public async Task SavedShortOfficeNameSelectsTheLabelStoredOnTheHardwareAsset()
    {
        using var client = new SampleServiceNowClient();
        client.AddLocation("loc-tsv", "Townsville Office");
        client.AddLocation("loc-cns-depot", "Cairns Depot");
        client.AddComputer(new HardwareAsset
        {
            SysId = "hw-tsv",
            SerialNumber = "TSV0001",
            Model = "HP ZBook",
            ModelCategory = HardwareCatalog.Computer,
            Location = new ReferenceValue("loc-tsv", "Townsville Office"),
            InstallStatus = HardwareCatalog.InUse,
            InstallStatusLabel = HardwareCatalog.InUse
        });
        var store = new MemorySettingsStore();
        store.Save(new DeskSettings
        {
            HardwareOfficeLocations = ["  brisbane ", "Townsville", "Cairns"],
            HardwareOfficeOverride = true
        });
        var workspace = new HardwareWorkspaceViewModel(store);
        workspace.Attach(client);
        await workspace.RefreshAsync();

        AssertStoredOffice(workspace, "Brisbane Office");
        AssertStoredOffice(workspace, "Townsville Office");
        AssertStoredOffice(workspace, "Cairns");
        AssertStoredOffice(workspace, "Cairns Depot");
        AssertStoredOffice(workspace, "Maroochydore");
        AssertStoredOffice(workspace, "Gold Coast");
        Assert.True(workspace.Offices.Single(office => office.Name == "Brisbane Office").IsSelected);
        Assert.True(workspace.Offices.Single(office => office.Name == "Townsville Office").IsSelected);
        Assert.True(workspace.Offices.Single(office => office.Name == "Cairns").IsSelected);
        Assert.False(workspace.Offices.Single(office => office.Name == "Cairns Depot").IsSelected);
        Assert.False(workspace.Offices.Single(office => office.Name == "Maroochydore").IsSelected);
        Assert.False(workspace.Offices.Single(office => office.Name == "Gold Coast").IsSelected);
        Assert.DoesNotContain("location.name", client.LastHardwareQuery);
        Assert.Equal(3, workspace.Items.Count);
        Assert.Contains(workspace.Items, asset => asset.Location.Display == "Brisbane Office");
        Assert.Contains(workspace.Items, asset => asset.SerialNumber == "TSV0001");
        Assert.False(HardwareCatalog.MatchesLocation(new HardwareAsset
        {
            Location = new ReferenceValue("loc-cns-depot", "Cairns Depot"),
            ModelCategory = HardwareCatalog.Computer
        }, ["Cairns"]));
        Assert.True(HardwareOfficeNames.LoosePlace("AU Brisbane Office", "Brisbane"));
    }

    [Fact]
    public void MultipleOfficesAreSeparateClausesSoTheLimitAppliesAfterTheLocationFilter()
    {
        var query = HardwareCatalog.ListQuery(null, ["Brisbane Office", "Maroochydore Office", "Gold Coast Office", "Townsville Office"]);

        Assert.Equal(
            "model_category.name=Computer^location.name=\"Brisbane Office\""
            + "^NQmodel_category.name=Computer^location.name=\"Brisbane\""
            + "^NQmodel_category.name=Computer^location.name=\"Maroochydore Office\""
            + "^NQmodel_category.name=Computer^location.name=\"Maroochydore\""
            + "^NQmodel_category.name=Computer^location.name=\"Gold Coast Office\""
            + "^NQmodel_category.name=Computer^location.name=\"Gold Coast\""
            + "^NQmodel_category.name=Computer^location.name=\"Townsville Office\""
            + "^NQmodel_category.name=Computer^location.name=\"Townsville\""
            + "^ORDERBYserial_number",
            query);
        Assert.DoesNotContain("(", query);
        Assert.DoesNotContain("Hong Kong", query);
        Assert.Equal(
            "model_category.name=Computer^location.name=\"Brisbane\"^NQmodel_category.name=Computer^location.name=\"Brisbane Office\"^ORDERBYserial_number",
            HardwareCatalog.ListQuery(null, ["Brisbane"]));
        Assert.Equal(
            "model_category.name=Computer^location.name=\"Brisbane Office\"^NQmodel_category.name=Computer^location.name=\"Brisbane\"^ORDERBYserial_number",
            HardwareCatalog.ListQuery(null, ["Brisbane Office"]));
        Assert.Equal("model_category.name=Computer^ORDERBYserial_number", HardwareCatalog.ListQuery(null, null));
    }

    [Fact]
    public async Task LocationFilterRunsBeforeTheHardwareRowCap()
    {
        using var client = new SampleServiceNowClient();
        client.AddComputer(HongKongVm("0000-0000-0353"));

        var page = await client.SearchHardwareAsync(new TicketQuery
        {
            Locations = ["Brisbane Office"],
            Limit = 1,
            Activity = ActivityFilter.Any
        }, CancellationToken.None);

        var only = Assert.Single(page.Items);
        Assert.Equal("Brisbane Office", only.Location.Display);
        Assert.DoesNotContain("(", client.LastHardwareQuery);
        Assert.Contains("location.name=\"Brisbane Office\"", client.LastHardwareQuery);
        Assert.Contains("location.name=\"Brisbane\"", client.LastHardwareQuery);
    }

    [Fact]
    public async Task ReturnedRowsDoNotAddOfficeChoices()
    {
        using var client = new SampleServiceNowClient();
        client.AddComputer(HongKongVm("0000-0000-0353"));
        var store = new MemorySettingsStore();
        store.Save(new DeskSettings { HardwareOfficeLocations = ["Brisbane"], HardwareOfficeOverride = true });
        var workspace = new HardwareWorkspaceViewModel(store);
        workspace.Attach(client);
        await workspace.RefreshAsync();

        AssertStoredOffice(workspace, "Brisbane Office");
        Assert.Equal("Brisbane Office", workspace.OfficeSelectionSummary);
        Assert.DoesNotContain(workspace.Offices, office => office.Name.Contains("Hong Kong", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(workspace.Items, asset => asset.Location.Display.Contains("Hong Kong", StringComparison.OrdinalIgnoreCase));
        Assert.All(workspace.Items, asset => Assert.Equal("Brisbane Office", asset.Location.Display));
        Assert.DoesNotContain("location.name", client.LastHardwareQuery);

        await workspace.SearchAllLocationsCommand.ExecuteAsync(null);

        Assert.Contains(workspace.Items, asset => asset.SerialNumber == "0000-0000-0353");
        Assert.DoesNotContain(workspace.Offices, office => office.Name == "Hong Kong Office");
        Assert.Equal("All locations", workspace.OfficeSelectionSummary);
    }

    [Fact]
    public async Task DefaultBrisbaneShowsAnAssetStoredAsBrisbaneOffice()
    {
        using var client = new SampleServiceNowClient();
        client.RemoveLocation("Brisbane Office");
        client.SignedInUser = ViewerAt("Brisbane");
        var workspace = new HardwareWorkspaceViewModel(new MemorySettingsStore());
        workspace.Attach(client);
        workspace.RememberViewer(await client.GetCurrentUserAsync(CancellationToken.None));
        await workspace.RefreshAsync();

        var selected = Assert.Single(workspace.Offices, office => office.IsSelected);
        Assert.Equal("Brisbane", selected.Name);
        Assert.Contains(workspace.Items, asset => asset.Location.Display == "Brisbane Office");
        Assert.DoesNotContain(workspace.Items, asset => asset.Location.Display.Contains("Hong Kong", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("location.name", client.LastHardwareQuery);
        Assert.Equal("Your office: Brisbane", workspace.AccountOfficeText);
        Assert.Contains("Showing your office: Brisbane.", workspace.OfficeStatus);
        Assert.True(HardwareCatalog.MatchesLocation(new HardwareAsset
        {
            Location = new ReferenceValue("loc", "Brisbane Office"),
            ModelCategory = HardwareCatalog.Computer
        }, ["Brisbane"]));
        Assert.True(HardwareOfficeNames.SamePlace("Brisbane", "Brisbane Office"));
        Assert.False(HardwareCatalog.MatchesLocation(new HardwareAsset
        {
            Location = new ReferenceValue("loc-hkg", "Hong Kong Office"),
            ModelCategory = HardwareCatalog.Computer
        }, ["Brisbane"]));
    }

    [Fact]
    public async Task BrisbaneOfficeSelectionKeepsARowStoredAsBrisbane()
    {
        using var client = new SampleServiceNowClient();
        client.AddComputer(new HardwareAsset
        {
            SysId = "hw-bne-city",
            SerialNumber = "BNECITY1",
            Model = "HP ZBook",
            ModelCategory = HardwareCatalog.Computer,
            Location = new ReferenceValue("loc-city", "Brisbane"),
            InstallStatus = HardwareCatalog.InUse,
            InstallStatusLabel = HardwareCatalog.InUse
        });
        var store = new MemorySettingsStore();
        store.Save(new DeskSettings { HardwareOfficeLocations = ["Brisbane Office"], HardwareOfficeOverride = true });
        var workspace = new HardwareWorkspaceViewModel(store);
        workspace.Attach(client);
        await workspace.RefreshAsync();

        Assert.Contains(workspace.Items, asset => asset.SerialNumber == "BNECITY1");
        Assert.Contains(workspace.Items, asset => asset.Location.Display == "Brisbane Office");
        Assert.DoesNotContain(workspace.Items, asset => asset.Location.Display.Contains("Hong Kong", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LocalOfficeFilterKeepsHongKongOutEvenWhenTheDownloadIsUnscoped()
    {
        using var client = new SampleServiceNowClient();
        client.ApplyHardwareLocationFilter = false;
        client.RemoveHardware(asset => HardwareOfficeNames.SamePlace(asset.Location.Display, "Brisbane"));
        client.AddComputer(HongKongVm("0000-0000-0999"));
        var store = new MemorySettingsStore();
        store.Save(new DeskSettings { HardwareOfficeLocations = ["Brisbane"], HardwareOfficeOverride = true });
        var workspace = new HardwareWorkspaceViewModel(store);
        workspace.Attach(client);
        await workspace.RefreshAsync();

        Assert.Empty(workspace.Items);
        Assert.Contains("Showing override: Brisbane", workspace.OfficeStatus);
        Assert.Contains("No computers for the selected office", workspace.CatalogStatus);
        Assert.DoesNotContain(workspace.Offices, office => office.Name == "Hong Kong Office");
    }

    [Fact]
    public async Task SavedCatalogIsReusedAndRefreshAllReportsNewComputers()
    {
        using var client = new SampleServiceNowClient();
        var catalog = new MemoryHardwareCatalogStore();
        var settings = new MemorySettingsStore();
        settings.Save(new DeskSettings { HardwareOfficeLocations = ["Brisbane"], HardwareOfficeOverride = true });
        var first = new HardwareWorkspaceViewModel(settings, catalog);
        first.Attach(client);
        await first.RefreshAsync();

        Assert.Contains(first.Items, asset => asset.Location.Display == "Brisbane Office");
        Assert.DoesNotContain("location.name", client.LastHardwareQuery);
        var gets = HardwareGets(client);
        Assert.True(gets >= 1);

        client.AddComputer(new HardwareAsset
        {
            SysId = "hw-bne-new",
            SerialNumber = "BNENEW01",
            Model = "HP EliteBook",
            ModelCategory = HardwareCatalog.Computer,
            Location = new ReferenceValue("loc-bne", "Brisbane Office"),
            InstallStatus = HardwareCatalog.InUse,
            InstallStatusLabel = HardwareCatalog.InUse
        });

        var second = new HardwareWorkspaceViewModel(settings, catalog);
        second.Attach(client);
        await second.RefreshAsync();

        Assert.Equal(gets, HardwareGets(client));
        Assert.DoesNotContain(second.Items, asset => asset.SerialNumber == "BNENEW01");
        Assert.Contains("computers saved", second.CatalogStatus);

        await second.RefreshAllCatalogCommand.ExecuteAsync(null);

        Assert.True(HardwareGets(client) > gets);
        Assert.Contains(second.Items, asset => asset.SerialNumber == "BNENEW01");
        Assert.Contains("new", second.CatalogStatus, StringComparison.OrdinalIgnoreCase);
        Assert.False(second.IsDownloading);
        Assert.Equal(100, second.DownloadPercent);
    }

    [Fact]
    public async Task OfficeSearchNarrowsChoicesWithoutReloadingAssets()
    {
        using var client = new SampleServiceNowClient();
        var workspace = await OpenHardwareAsync(client);
        var gets = HardwareGets(client);
        var shown = workspace.Items.Count;
        Assert.Contains(workspace.VisibleOffices, office => office.Name == "Cairns");
        Assert.Contains(workspace.VisibleOffices, office => office.Name == "Brisbane Office");

        workspace.OfficeSearchText = "br";

        Assert.Equal(gets, HardwareGets(client));
        Assert.Equal(shown, workspace.Items.Count);
        Assert.Contains(workspace.VisibleOffices, office => office.Name == "Brisbane Office");
        Assert.DoesNotContain(workspace.VisibleOffices, office => office.Name == "Cairns");
        Assert.DoesNotContain(workspace.VisibleOffices, office => office.Name == "Maroochydore");
        Assert.True(workspace.VisibleOffices.Count < workspace.Offices.Count);

        workspace.OfficeSearchText = "cair";
        Assert.Equal(gets, HardwareGets(client));
        var cairns = Assert.Single(workspace.VisibleOffices);
        Assert.Equal("Cairns", cairns.Name);

        cairns.IsSelected = true;
        await workspace.OfficeLoad;
        Assert.Equal(gets, HardwareGets(client));
        Assert.Equal("Cairns", workspace.OfficeSelectionSummary);
        Assert.DoesNotContain(workspace.Items, asset => asset.Location.Display.Contains("Hong Kong", StringComparison.OrdinalIgnoreCase));

        cairns.IsSelected = false;
        await workspace.OfficeLoad;
        Assert.Equal(gets, HardwareGets(client));
        Assert.Equal("All locations", workspace.OfficeSelectionSummary);
        Assert.Contains(workspace.Items, asset => asset.Location.Display == "Hong Kong Office");
    }

    [Fact]
    public async Task AccountOfficeIsTheDefaultAndOverrideCanReplaceIt()
    {
        using var client = new SampleServiceNowClient();
        client.AddComputer(new HardwareAsset
        {
            SysId = "hw-cns",
            SerialNumber = "CNS0001",
            Model = "HP ZBook",
            ModelCategory = HardwareCatalog.Computer,
            Location = new ReferenceValue("loc-cns", "Cairns"),
            InstallStatus = HardwareCatalog.InUse,
            InstallStatusLabel = HardwareCatalog.InUse
        });
        client.SignedInUser = ViewerAt("Brisbane");
        var store = new MemorySettingsStore();
        var workspace = new HardwareWorkspaceViewModel(store);
        workspace.Attach(client);
        workspace.RememberViewer(await client.GetCurrentUserAsync(CancellationToken.None));
        await workspace.RefreshAsync();

        Assert.Equal("Your office: Brisbane", workspace.AccountOfficeText);
        Assert.Contains("Showing your office: Brisbane.", workspace.OfficeStatus);
        Assert.DoesNotContain("location.name", client.LastHardwareQuery);
        Assert.Contains(workspace.Items, asset => asset.Location.Display == "Brisbane Office");
        Assert.DoesNotContain(workspace.Items, asset => asset.Location.Display.Contains("Hong Kong", StringComparison.OrdinalIgnoreCase));

        foreach (var office in workspace.Offices.Where(office => office.IsSelected).ToArray())
            office.IsSelected = false;
        await workspace.OfficeLoad;
        workspace.Offices.Single(office => office.Name == "Cairns").IsSelected = true;
        await workspace.OfficeLoad;
        await workspace.OverrideOfficeCommand.ExecuteAsync(null);

        Assert.Contains("Showing override: Cairns.", workspace.OfficeStatus);
        Assert.Equal("CNS0001", Assert.Single(workspace.Items).SerialNumber);
        Assert.Equal(["Cairns"], store.Load().HardwareOfficeLocations);
        Assert.True(store.Load().HardwareOfficeOverride);

        await workspace.ClearOverrideCommand.ExecuteAsync(null);

        Assert.Contains("Showing your office: Brisbane.", workspace.OfficeStatus);
        Assert.Contains(workspace.Items, asset => asset.Location.Display == "Brisbane Office");
        Assert.DoesNotContain(workspace.Items, asset => asset.SerialNumber == "CNS0001");
        Assert.DoesNotContain(workspace.Items, asset => asset.Location.Display.Contains("Hong Kong", StringComparison.OrdinalIgnoreCase));
        Assert.False(store.Load().HardwareOfficeOverride);
        Assert.Null(store.Load().HardwareOfficeLocations);

        using var open = new SampleServiceNowClient();
        var anywhere = new HardwareWorkspaceViewModel(new MemorySettingsStore());
        anywhere.Attach(open);
        anywhere.RememberViewer(await open.GetCurrentUserAsync(CancellationToken.None));
        await anywhere.RefreshAsync();

        Assert.Equal("No location on the signed-in account.", anywhere.AccountOfficeText);
        Assert.Contains("No location on the signed-in account", anywhere.OfficeStatus);
        Assert.DoesNotContain("location.name", open.LastHardwareQuery);
        Assert.Contains(anywhere.Items, asset => asset.Location.Display.Contains("Hong Kong", StringComparison.OrdinalIgnoreCase));

        anywhere.Offices.Single(office => office.Name == "Cairns").IsSelected = true;
        await anywhere.OfficeLoad;
        await anywhere.OverrideOfficeCommand.ExecuteAsync(null);
        Assert.Contains("Showing override: Cairns.", anywhere.OfficeStatus);
        Assert.Empty(anywhere.Items);
    }

    [Fact]
    public void BlankInstanceUrlBecomesArupAndASavedUrlIsKept()
    {
        var connection = new ConnectionViewModel();
        connection.Load(new DeskSettings { InstanceUrl = "   " });
        Assert.Equal("https://arup.service-now.com", connection.InstanceUrl);

        connection.Load(DeskSettingsFile.Deserialize("{\"InstanceUrl\":\"\"}", value => value ?? ""));
        Assert.Equal(DeskSettings.DefaultInstanceUrl, connection.InstanceUrl);
        Assert.Equal(
            ServiceNowSession.NormalizeInstance("https://arup.service-now.com/"),
            ServiceNowSession.NormalizeInstance(connection.InstanceUrl));

        connection.InstanceUrl = "https://kept.service-now.com/";
        var saved = connection.BuildSettings();
        Assert.Equal("https://kept.service-now.com/", saved.InstanceUrl);

        var again = new ConnectionViewModel();
        again.Load(saved);
        Assert.Equal("https://kept.service-now.com/", again.InstanceUrl);
        Assert.Equal(
            ServiceNowSession.NormalizeInstance("https://kept.service-now.com/"),
            ServiceNowSession.NormalizeInstance("https://kept.service-now.com"));
        Assert.NotEqual(DeskSettings.DefaultInstanceUrl, again.BuildSettings().InstanceUrl);
    }

    private static HardwareAsset HongKongVm(string serial) => new()
    {
        SysId = "hw-hk-vm",
        SerialNumber = serial,
        Model = "Microsoft Corporation Virtual Machine",
        ModelCategory = HardwareCatalog.Computer,
        Location = new ReferenceValue("loc-hkg", "Hong Kong Office"),
        InstallStatus = HardwareCatalog.InUse,
        InstallStatusLabel = HardwareCatalog.InUse
    };

    private static void AssertStoredOffice(HardwareWorkspaceViewModel workspace, string name)
    {
        var office = Assert.Single(workspace.Offices, candidate => HardwareOfficeNames.SamePlace(candidate.Name, name));
        Assert.Equal(name, office.Name);
    }

    private static CurrentUser ViewerAt(string location) =>
        new("sample-user", "Alex Rivera", "alex.rivera", "alex.rivera@example.com") { Location = location };

    private static int HardwareGets(SampleServiceNowClient client) =>
        client.RecentActivity.Count(activity => activity.Method == "GET" && activity.Path.Contains("alm_hardware", StringComparison.Ordinal));

    private static async Task AssertFilterNarrowsAsync(
        HardwareWorkspaceViewModel workspace,
        Action setFilter,
        int all,
        bool expectEmpty = false)
    {
        setFilter();
        Assert.True(workspace.IsFiltering);
        await workspace.ColumnFiltersReady;
        Assert.False(workspace.IsFiltering);
        if (expectEmpty)
            Assert.Equal(0, workspace.ShownCount);
        else
            Assert.True(workspace.ShownCount < all);
    }

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

public class HardwareDownloadHold : DispatchProxy
{
    public SampleServiceNowClient Inner { get; set; } = null!;
    public TaskCompletionSource Ready { get; set; } = null!;
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null)
            throw new InvalidOperationException("Missing ServiceNow method.");
        if (targetMethod.Name == nameof(IServiceNowClient.DownloadHardwareAsync))
            return HoldDownloadAsync(args ?? []);

        try
        {
            return targetMethod.Invoke(Inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private async Task<HardwareCatalogDownload> HoldDownloadAsync(object?[] args)
    {
        Started.TrySetResult();
        await Ready.Task.ConfigureAwait(false);
        var locations = args[0] as IReadOnlyList<string>;
        var ids = args[1] as IReadOnlyList<string>;
        var progress = args[2] as IProgress<DownloadTick>;
        var token = args.Length > 3 && args[3] is CancellationToken ct ? ct : CancellationToken.None;
        return await Inner.DownloadHardwareAsync(locations, ids, progress, token).ConfigureAwait(false);
    }
}
