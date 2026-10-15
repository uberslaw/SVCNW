using System.Globalization;
using System.IO.Compression;
using System.Text;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class LaptopRefreshTests
{
    private static readonly DateOnly Today = new(2026, 10, 14);

    [Fact]
    public void CsvReadsDeliveryDatesAndSkipsTheFilterFooter()
    {
        var import = LaptopAgeSheet.ReadCsv("""
            Location,Serial #,Hostname,Model,Assigned To,State,Substate,Stockroom,Delivery Date,Age
            Melbourne,OLD1,MELLT1,Dell Precision 5560,"Lee, Jordan",In Use,,,6/10/2022,4y 0m
            Sydney,OLD2,SYDLT2,HP EliteBook,Alex Rivera,In Use,,,2018-03-01,8y 7m
            Applied filters:
            Age in Years is 4 or 5
            """);

        Assert.True(import.HasSerial);
        Assert.False(import.HasTracking);
        Assert.Equal(2, import.Devices.Count);
        Assert.Equal(new DateOnly(2022, 10, 6), import.Devices[0].DeliveryDate);
        Assert.Equal("Lee, Jordan", import.Devices[0].AssignedTo);
        Assert.Equal(new DateOnly(2018, 3, 1), import.Devices[1].DeliveryDate);
        Assert.Equal(new DateOnly(2022, 10, 6), LaptopAgeSheet.ParseDelivery("44840"));
    }

    [Fact]
    public void XlsxReadsSharedStringsAndExcelSerialDates()
    {
        var bytes = Workbook(
            ["Location", "Serial #", "Hostname", "Model", "Assigned To", "State", "Delivery Date", "Age"],
            [
                ["Melbourne", "OLD1", "MELLT1", "Dell Precision 5560", "Samira Fratanduono", "In Use", "44840", "4y 0m"]
            ],
            dateColumn: 7);
        using var stream = new MemoryStream(bytes);
        var import = LaptopAgeSheet.Read(stream, "ANZ All Devices 4 Years and older.xlsx");
        var device = Assert.Single(import.Devices);
        Assert.Equal("OLD1", device.SerialNumber);
        Assert.Equal("Samira Fratanduono", device.AssignedTo);
        Assert.Equal(new DateOnly(2022, 10, 6), device.DeliveryDate);
        Assert.Equal("4y 0m", LaptopRefreshLogic.FormatAge(device.DeliveryDate!.Value, Today));
    }

    [Fact]
    public void OldestEligiblePeopleMatchTheBoxesOnHand()
    {
        var snapshot = DueList();
        snapshot.Notes.Add(new LaptopCheckNote { SerialNumber = "OLD4", OnHold = true, Comment = "Waiting on leave dates" });
        snapshot.Notes.Add(new LaptopCheckNote { SerialNumber = "OLD5", Status = "Completed", Comment = "Returned the old laptop" });
        var catalog = new List<HardwareAsset>
        {
            Box("BOX1", "HP EliteBook", "Melbourne", "In stock", "Available"),
            Box("BOX2", "HP EliteBook", "Melbourne", "In box", ""),
            Box("OLD9", "Dell Precision 5560", "Melbourne", "In stock", "Available"),
            Box("SYDBOX", "HP EliteBook", "Sydney", "In stock", "Available"),
            Hold("HOLD1", "Melbourne")
        };
        catalog.Add(InUse("OLD1", "MELLT1", "Dell Precision 5560", "Melbourne", "Sam"));

        Assert.True(LaptopRefreshLogic.Observe(snapshot, catalog, catalogIsComplete: true, statuses: null, DateTimeOffset.Now));
        Assert.Equal(["BOX1", "BOX2", "SYDBOX"], snapshot.Watching.Select(item => item.SerialNumber).OrderBy(serial => serial, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(snapshot.Watching, item => item.SerialNumber == "OLD9");
        Assert.DoesNotContain(snapshot.Watching, item => item.SerialNumber == "HOLD1");

        var plan = LaptopRefreshLogic.Build(snapshot, catalog, ["Melbourne"], null, Today);
        Assert.Equal(["BOX1", "BOX2"], plan.InBox.Select(item => item.SerialNumber).ToArray());
        Assert.Equal(5, plan.DueCount);
        Assert.Equal(1, plan.CompletedCount);
        Assert.Equal(2, plan.NextSlotCount);
        Assert.Equal(["Sam", "Jo", "Alex"], plan.Next.Select(row => row.AssignedTo).ToArray());
        Assert.Equal("Next", plan.Next[0].QueueText);
        Assert.Equal("7y 9m", plan.Next[0].AgeText);
        Assert.Equal("Pending", plan.Next[0].StillHasLaptop ? "Confirmed" : "Pending");
        Assert.Equal("On hold", plan.Next[1].QueueText);
        Assert.Equal("Waiting on leave dates", plan.Next[1].Comment);
        Assert.Equal("Next", plan.Next[2].QueueText);
        Assert.DoesNotContain(plan.Next, row => row.AssignedTo == "Pat");
        Assert.DoesNotContain(plan.Next, row => row.AssignedTo == "Done");
    }

    [Fact]
    public void BoxedSerialAssignedLaterIsWhoActuallyReceivedIt()
    {
        var snapshot = DueList();
        var boxed = new List<HardwareAsset> { Box("BOX1", "HP EliteBook", "Melbourne", "In stock", "Available") };
        Assert.True(LaptopRefreshLogic.Observe(snapshot, boxed, true, null, DateTimeOffset.Parse("2026-10-01T00:00:00Z", CultureInfo.InvariantCulture)));
        Assert.Empty(snapshot.Received);

        var assigned = new List<HardwareAsset>
        {
            Box("BOX1", "HP EliteBook", "Melbourne", "In use", "") with
            {
                AssignedTo = new ReferenceValue("user-sam", "Sam"),
                InstallStatus = HardwareCatalog.InUse,
                InstallStatusLabel = HardwareCatalog.InUse
            }
        };
        Assert.True(LaptopRefreshLogic.Observe(snapshot, assigned, true, null, DateTimeOffset.Parse("2026-10-14T01:00:00Z", CultureInfo.InvariantCulture)));
        var received = Assert.Single(snapshot.Received);
        Assert.Equal("BOX1", received.SerialNumber);
        Assert.Equal("Sam", received.AssignedTo);
        Assert.Equal("In stock · Available", received.PreviousState);
        Assert.False(LaptopRefreshLogic.Observe(snapshot, assigned, true, null, DateTimeOffset.Now));

        received.Reason = "Starter, not the oldest laptop";
        var plan = LaptopRefreshLogic.Build(snapshot, assigned, ["Melbourne"], null, Today);
        var row = Assert.Single(plan.Received);
        Assert.Equal("Starter, not the oldest laptop", row.Reason);
        Assert.Contains("MELLT1", row.OldLaptop, StringComparison.Ordinal);
        Assert.Equal("Already received", Assert.Single(plan.Next, item => item.AssignedTo == "Sam").QueueText);
        Assert.Equal(0, plan.NextSlotCount);
        Assert.Contains("Who actually received a laptop", LaptopRefreshLogic.Format(plan), StringComparison.Ordinal);
        Assert.Contains("Starter, not the oldest laptop", LaptopRefreshLogic.Format(plan), StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceNowDisagreementAndSwappedNamesStayVisible()
    {
        Assert.True(LaptopRefreshLogic.SamePerson("Lee, Jordan", "Jordan Lee"));
        var snapshot = new LaptopRefreshSnapshot
        {
            Due =
            [
                Due("OLD1", "MELLT1", "Sam", "Melbourne", new DateOnly(2020, 1, 1)),
                Due("OLD2", "MELLT2", "Lee, Jordan", "Melbourne", new DateOnly(2021, 1, 1))
            ]
        };
        var catalog = new List<HardwareAsset>
        {
            InUse("OLD1", "MELLT1", "Dell Precision 5560", "Melbourne", "") with
            {
                InstallStatus = HardwareCatalog.InStock,
                InstallStatusLabel = HardwareCatalog.InStock,
                SubstatusLabel = HardwareCatalog.Available
            },
            InUse("OLD2", "MELLT2", "HP EliteBook", "Melbourne", "Jordan Lee"),
            Box("BOX1", "HP EliteBook", "Melbourne", "In stock", "Available")
        };
        var plan = LaptopRefreshLogic.Build(snapshot, catalog, [], null, Today);
        Assert.Equal("Check ServiceNow", plan.Next[0].QueueText);
        Assert.Contains("ServiceNow state is In stock", plan.Next[0].Detail, StringComparison.Ordinal);
        Assert.Equal("Next", plan.Next[1].QueueText);
        Assert.Equal("Lee, Jordan", plan.Next[1].AssignedTo);
    }

    [Fact]
    public void MelbourneSheetFillsStatusHoldVacationAndComment()
    {
        var snapshot = DueList();
        var tracking = LaptopAgeSheet.ReadCsv("""
            Computename,Model,Assigned to,Department,Warranty expiration,On Hold,Status,Vacation student,Comments
            MELLT1,Dell Precision 5560,Sam,5005-150 VIC Group 1,1/04/2024,,Completed,,Returned the old laptop
            MELLT4,HP ZBook,Jo,5005-67 Central DT,26/08/2023,,On Hold,Yes,Trevor has retired from Arup - has this been returned?
            NOHOST,HP EliteBook,Missing Person,,,,,,"Not on the list"
            """);
        Assert.False(tracking.HasSerial);
        Assert.True(tracking.HasTracking);
        var applied = LaptopRefreshLogic.ApplyNotes(snapshot, tracking);
        Assert.Equal(2, applied.Updated);
        Assert.Equal(["NOHOST"], applied.Unmatched.ToArray());
        Assert.Equal("Completed", snapshot.Notes.Single(note => note.SerialNumber == "OLD1").Status);
        var jo = snapshot.Notes.Single(note => note.SerialNumber == "OLD4");
        Assert.True(jo.OnHold);
        Assert.Contains("retired", jo.Comment, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Vacation student", jo.Comment, StringComparison.Ordinal);
        Assert.Equal("5005-67 Central DT", snapshot.Due.Single(device => device.SerialNumber == "OLD4").Department);

        var plan = LaptopRefreshLogic.Build(snapshot, [], ["Melbourne"], null, Today);
        Assert.Equal(1, plan.CompletedCount);
        Assert.DoesNotContain(plan.Next, row => row.AssignedTo == "Sam");
        Assert.Equal("On hold", plan.Next.Single(row => row.AssignedTo == "Jo").QueueText);
    }

    [Fact]
    public void ImportRemembersTheCheckAndALaterAssignment()
    {
        var store = new MemoryLaptopRefreshStore();
        var desktop = new RecordingDesktopServices();
        var refresh = Workspace(store, desktop);
        var path = Path.Combine(Path.GetTempPath(), "servicenow-desk-tests", "due-laptops.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            Location,Serial #,Hostname,Model,Assigned To,State,Delivery Date
            Melbourne,OLD1,MELLT1,Dell Precision 5560,Sam,In Use,2019-01-01
            Melbourne,OLD2,MELLT2,HP EliteBook,Alex,In Use,2021-06-01
            """);
        desktop.NextOpenFile = path;
        refresh.ImportDueListCommand.Execute(null);
        Assert.Contains("Imported 2 laptops", refresh.Status, StringComparison.Ordinal);
        Assert.Contains("1 Jan 2019", refresh.Status, StringComparison.Ordinal);
        Assert.Equal(["Sam", "Alex"], refresh.Next.Select(row => row.AssignedTo).ToArray());
        Assert.Equal("Later", refresh.Next[0].QueueText);
        Assert.Equal("Later", refresh.Next[1].QueueText);

        refresh.Next[0].StillHasLaptop = true;
        refresh.Next[0].Comment = "Seen on the desk";
        var again = Workspace(store, desktop);
        again.UseOffices([], null);
        Assert.True(again.Next.Single(row => row.AssignedTo == "Sam").StillHasLaptop);
        Assert.Equal("Seen on the desk", again.Next.Single(row => row.AssignedTo == "Sam").Comment);
        Assert.Equal("Confirmed", again.Next.Single(row => row.AssignedTo == "Sam").CheckText);

        again.NoteCatalog([Box("BOX9", "HP EliteBook", "Melbourne", "In box", "")], true, [], null);
        Assert.Equal(["BOX9"], again.InBox.Select(item => item.SerialNumber).ToArray());
        Assert.Equal("Next", again.Next.Single(row => row.AssignedTo == "Sam").QueueText);
        Assert.Equal("Later", again.Next.Single(row => row.AssignedTo == "Alex").QueueText);

        again.NoteCatalog(
            [InUse("BOX9", "BOX9", "HP EliteBook", "Melbourne", "Alex")],
            true,
            [],
            null);
        var received = Assert.Single(again.Received);
        Assert.Equal("Alex", received.AssignedTo);
        Assert.Equal("In box", received.PreviousState);
        received.Reason = "LTA to SIN office";
        var saved = Workspace(store, desktop);
        saved.UseOffices([], null);
        Assert.Equal("LTA to SIN office", Assert.Single(saved.Received).Reason);
        Assert.Equal("Already received", saved.Next.Single(row => row.AssignedTo == "Alex").QueueText);
    }

    [Fact]
    public async Task HardwareRefreshRemembersTheStockRoom()
    {
        using var client = new SampleServiceNowClient();
        var workspace = new HardwareWorkspaceViewModel(new MemorySettingsStore());
        workspace.Attach(client);
        await workspace.RefreshAllCatalogCommand.ExecuteAsync(null);

        Assert.Contains(workspace.LaptopRefresh.InBox, item => item.SerialNumber == "5CG30710BR");
        Assert.DoesNotContain(workspace.LaptopRefresh.InBox, item => item.SerialNumber == "5CG0000DBR");
        Assert.DoesNotContain(workspace.LaptopRefresh.InBox, item => item.SerialNumber == "5CG6245F8S");

        await client.UpdateHardwareAsync("hw-stock", new HardwareChanges { AssignedToId = "user-jordan" }, CancellationToken.None);
        await workspace.RefreshAllCatalogCommand.ExecuteAsync(null);
        var received = Assert.Single(workspace.LaptopRefresh.Received);
        Assert.Equal("5CG30710BR", received.SerialNumber);
        Assert.Equal("Jordan Lee", received.AssignedTo);
        Assert.Contains("In stock", received.PreviousState, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FileStoreRoundTripsTheDueList()
    {
        var folder = Path.Combine(Path.GetTempPath(), "servicenow-desk-tests", Guid.NewGuid().ToString("N"));
        var store = new FileLaptopRefreshStore(folder);
        store.Save("Practice", new LaptopRefreshSnapshot
        {
            SourceName = "due.xlsx",
            Due = [Due("OLD1", "MELLT1", "Sam", "Melbourne", new DateOnly(2022, 10, 6))]
        });
        var loaded = store.Load("practice");
        Assert.NotNull(loaded);
        Assert.Equal(new DateOnly(2022, 10, 6), Assert.Single(loaded.Due).DeliveryDate);
    }

    [Fact]
    public void SydneyListIsOneSheetAndStockIsTheAvailableLaptops()
    {
        var people = LaptopAgeSheet.ReadCsv("""
            Sydney,8G13TG3,SYDLT8G13TG3,Dell Latitude 5320,In use,4y 4m,Giles Thomas,Giles.Thomas,David,completed,Completed,5320
            Sydney,9LJQQG3,SYDLT9LJQQG3,Dell Latitude 5320,In use,4y 5m,Michael Roberts (X),Michael-X.Roberts,,Long Term Leave,Completed,5320
            Sydney,G40SPG3,SYDLTG40SPG3,Dell Latitude 5320,In use,4y 7m,Seamus Byrne,Seamus.Byrne,user finishes in Nov 2026,,Not completed,5320
            Sydney,540SPG3,SYDLT540SPG3,Dell Latitude 5320,In use,4y 7m,Arash Jafari,Arash.Jafari,,,Not completed,5320
            """);
        Assert.False(people.IsStockList);
        Assert.Equal(4, people.Devices.Count);
        var giles = people.Devices[0];
        Assert.Equal("Giles Thomas", giles.AssignedTo);
        Assert.Equal("Giles.Thomas", giles.UserName);
        Assert.Equal("David", giles.HandledBy);
        Assert.Equal("", giles.Comment);
        Assert.Equal("Completed", giles.Status);
        Assert.Equal("5320", giles.ListedModel);
        Assert.Equal("Long Term Leave", people.Devices[1].Comment);
        Assert.Equal("", people.Devices[1].HandledBy);
        Assert.Contains("user finishes in Nov 2026", people.Devices[2].Comment, StringComparison.Ordinal);
        Assert.Equal("Not completed", people.Devices[3].Status);

        var stock = LaptopAgeSheet.ReadCsv("""
            Location,Serial #,Hostname,Model,State,Delivery Date,Age,Cost
            Sydney,5CD6220H4F,HP ZBOOK,HP ZBook X G1i,In Stock,12/10/2026,0y 4m,2160.88
            Brisbane,5CD6220H03,HP ZBOOK,HP ZBook X G1i,In Transit,12/07/2026,0y 3m,2160.88
            """);
        Assert.True(stock.IsStockList);
        Assert.Equal(2, stock.Devices.Count);

        var snapshot = new LaptopRefreshSnapshot();
        LaptopRefreshLogic.ReplaceDueList(snapshot, people, "sydney.xlsx", DateTimeOffset.Now);
        LaptopRefreshLogic.ReplaceAvailable(snapshot, stock, "devices.xlsx");
        Assert.Equal(["5CD6220H4F"], snapshot.Available.Select(device => device.SerialNumber).ToArray());
        Assert.Equal(["5CD6220H03"], snapshot.InTransit.Select(device => device.SerialNumber).ToArray());
        var plan = LaptopRefreshLogic.Build(snapshot, [], ["Sydney"], null, Today);
        Assert.Equal(["5CD6220H4F"], plan.InBox.Select(row => row.SerialNumber).ToArray());
        Assert.Empty(plan.InTransit);
        Assert.Equal(1, plan.NextSlotCount);
        Assert.Equal(["Arash Jafari", "Seamus Byrne"], plan.Next.Select(row => row.AssignedTo).ToArray());
        Assert.Equal("Next", plan.Next[0].QueueText);
        Assert.Equal("Later", plan.Next[1].QueueText);
        Assert.Equal("David", people.Devices[0].HandledBy);
        Assert.Equal("5320", plan.Next[0].ListedModel);

        var open = LaptopRefreshLogic.Build(snapshot, [], [], null, Today);
        Assert.Equal(["5CD6220H4F"], open.InBox.Select(row => row.SerialNumber).ToArray());
        Assert.DoesNotContain(open.InBox, row => row.SerialNumber == "5CD6220H03");
        var transit = Assert.Single(open.InTransit);
        Assert.Equal("5CD6220H03", transit.SerialNumber);
        Assert.Equal("Brisbane", transit.Location);
        Assert.Equal("In Transit", transit.State);
        Assert.Equal(1, open.NextSlotCount);

        var sheet = LaptopRefreshLogic.Format(open);
        Assert.Contains("User name,Model code,", sheet, StringComparison.Ordinal);
        Assert.DoesNotContain("Done by", sheet, StringComparison.Ordinal);
        Assert.Contains("In transit", sheet, StringComparison.Ordinal);
        Assert.Contains("5CD6220H03", sheet, StringComparison.Ordinal);
    }

    [Fact]
    public void FullDeviceExportSplitsOldLaptopsFromYoungStock()
    {
        var sydney = LaptopAgeSheet.ReadCsv("""
            Melbourne,OLD1,MELLT1,HP EliteBook 840 G3,In use,9y 9m,Ceanu Geuder,Ceanu.Geuder,David,Long Term Leave,Not completed,840 G3
            """);
        var snapshot = new LaptopRefreshSnapshot();
        LaptopRefreshLogic.ReplaceDueList(snapshot, sydney, "sydney.xlsx", DateTimeOffset.Now);

        var all = LaptopAgeSheet.ReadCsv("""
            Location,Serial #,Hostname,Model,Assigned To,State,Substate,Stockroom,Delivery Date,Age
            Melbourne,OLD1,MELLT1,HP EliteBook 840 G3,Ceanu Geuder,In Use,,,2017-01-16,9y 9m
            Melbourne,OLD2,MELLT2,HP EliteBook 840 G4,Fiona Whitworth,In Use,,,2018-02-01,8y 8m
            Melbourne,YOUNG1,MELLT3,HP ZBook X,Duncan Roberts,In Use,,,2026-07-01,0y 3m
            Melbourne,MID1,MELLT4,Dell Latitude 5421,Three Year,In Use,,,2023-01-15,3y 8m
            Melbourne,STOCKNEW,ZBOOKHOST,HP ZBook X G1i,,In Stock,Available,Melbourne stock,2026-01-19,0y 8m
            Melbourne,STOCKOLD,OLDHOST,HP ZBook Studio G3,,In Stock,Available,Melbourne stock,2016-03-01,10y 7m
            Melbourne,HOLD1,HOLDHOST,HP EliteBook,,In Stock,Legal hold,Melbourne stock,2026-02-01,0y 8m
            Melbourne,PEND1,PENDHOST,HP EliteBook,,In Stock,Pending transfer,Melbourne stock,2026-03-01,0y 7m
            Brisbane,TRANSIT1,THOST,HP ZBook X,,In Transit,,,2026-06-01,0y 4m
            Melbourne,MAINT1,MAHOST,HP EliteBook,Fix Person,In Maintenance,,,2015-01-01,11y 9m
            Sydney,BARE1,BAREHOST,HP EliteBook,,In Use,,,2016-01-01,10y 9m
            """);
        Assert.True(LaptopRefreshLogic.IsFullInventory(all, Today));
        LaptopRefreshLogic.ReplaceInventory(snapshot, all, "All Devices.xlsx", DateTimeOffset.Now, Today);

        Assert.Equal(["OLD1", "OLD2"], snapshot.Due.Select(device => device.SerialNumber).ToArray());
        var kept = snapshot.Due.Single(device => device.SerialNumber == "OLD1");
        Assert.Equal("David", kept.HandledBy);
        Assert.Equal("Ceanu.Geuder", kept.UserName);
        Assert.Equal("840 G3", kept.ListedModel);
        Assert.Equal("Long Term Leave", snapshot.Notes.Single(note => note.SerialNumber == "OLD1").Comment);
        Assert.Equal(["STOCKNEW"], snapshot.Available.Select(device => device.SerialNumber).ToArray());
        Assert.Equal(["TRANSIT1"], snapshot.InTransit.Select(device => device.SerialNumber).ToArray());

        var plan = LaptopRefreshLogic.Build(snapshot, [], [], null, Today);
        Assert.Equal(["Ceanu Geuder", "Fiona Whitworth"], plan.Next.Select(row => row.AssignedTo).ToArray());
        Assert.Equal("Next", plan.Next[0].QueueText);
        Assert.Equal("David", plan.Next[0].HandledBy);
        Assert.Equal("840 G3", plan.Next[0].ListedModel);
        Assert.DoesNotContain("Done by", plan.Next[0].Detail, StringComparison.Ordinal);
        Assert.Equal("Long Term Leave", plan.Next[0].Comment);
        Assert.Equal("Later", plan.Next[1].QueueText);
        Assert.Equal(1, plan.NextSlotCount);
        Assert.Equal(["STOCKNEW"], plan.InBox.Select(row => row.SerialNumber).ToArray());
        Assert.DoesNotContain(plan.InBox, row => row.SerialNumber == "TRANSIT1");
        Assert.Equal("Melbourne stock", plan.InBox.Single(row => row.SerialNumber == "STOCKNEW").Stockroom);
        Assert.Equal("In Stock · Available", plan.InBox.Single(row => row.SerialNumber == "STOCKNEW").State);
        var transit = Assert.Single(plan.InTransit);
        Assert.Equal("TRANSIT1", transit.SerialNumber);
        Assert.Equal("In Transit", transit.State);

        var melbourne = LaptopRefreshLogic.Build(snapshot, [], ["Melbourne"], null, Today);
        Assert.Equal(["STOCKNEW"], melbourne.InBox.Select(row => row.SerialNumber).ToArray());
        Assert.Empty(melbourne.InTransit);
        Assert.Equal("Next", melbourne.Next[0].QueueText);
        Assert.Equal("Later", melbourne.Next[1].QueueText);

        LaptopRefreshLogic.ReplaceDueList(snapshot, sydney, "sydney-again.xlsx", DateTimeOffset.Now);
        Assert.Equal(["STOCKNEW"], snapshot.Available.Select(device => device.SerialNumber).ToArray());
        Assert.Equal(["TRANSIT1"], snapshot.InTransit.Select(device => device.SerialNumber).ToArray());

        snapshot.Due.Add(new DueLaptop
        {
            SerialNumber = "TRANSIT1",
            AssignedTo = "Transit Person",
            Location = "Brisbane",
            State = "In Use"
        });
        var hidden = LaptopRefreshLogic.Build(snapshot, [], [], null, Today);
        Assert.DoesNotContain(hidden.InTransit, row => row.SerialNumber == "TRANSIT1");
        Assert.DoesNotContain(hidden.InBox, row => row.SerialNumber == "TRANSIT1");
    }

    [Fact]
    public void FourYearExportStaysADueListIncludingOldStock()
    {
        var import = LaptopAgeSheet.ReadCsv("""
            Location,Serial #,Hostname,Model,Assigned To,State,Substate,Stockroom,Delivery Date,Age
            Melbourne,OLD1,MELLT1,Dell Precision 5560,Sam,In Use,,,2018-03-01,8y 7m
            Melbourne,OLDSTOCK,MELLT9,Dell Precision 5560,,In Stock,Available,Melbourne,2019-01-01,7y 9m
            """);
        Assert.False(LaptopRefreshLogic.IsFullInventory(import, Today));
        var snapshot = new LaptopRefreshSnapshot();
        LaptopRefreshLogic.ReplaceDueList(snapshot, import, "4-years.xlsx", DateTimeOffset.Now);
        Assert.Equal(["OLD1", "OLDSTOCK"], snapshot.Due.Select(device => device.SerialNumber).ToArray());
        Assert.Empty(snapshot.Available);

        var plan = LaptopRefreshLogic.Build(
            snapshot,
            [Box("OLDSTOCK", "Dell Precision 5560", "Melbourne", "In stock", "Available")],
            ["Melbourne"],
            null,
            Today);
        Assert.Empty(plan.InBox);
        Assert.Equal(["Sam"], plan.Next.Select(row => row.AssignedTo).ToArray());
    }

    [Fact]
    public void ImportReadsTheFullDeviceList()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var old = today.AddYears(-6).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var young = today.AddMonths(-8).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var mid = today.AddYears(-3).AddMonths(-6).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var store = new MemoryLaptopRefreshStore();
        var desktop = new RecordingDesktopServices();
        var refresh = Workspace(store, desktop);
        var path = Path.Combine(Path.GetTempPath(), "servicenow-desk-tests", "all-devices.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join('\n',
        [
            "Location,Serial #,Hostname,Model,Assigned To,State,Substate,Stockroom,Delivery Date,Age",
            "Melbourne,OLD1,MELLT1,HP EliteBook,Sam,In Use,,," + old + ",6y 0m",
            "Melbourne,YOUNG1,MELLT2,HP ZBook,Alex,In Use,,," + young + ",0y 8m",
            "Melbourne,MID1,MELLT3,Dell Latitude,Jo,In Use,,," + mid + ",3y 6m",
            "Melbourne,STOCKNEW,Z1,HP ZBook X,,In Stock,Available,Melbourne stock," + young + ",0y 8m",
            "Brisbane,TRANSIT1,T1,HP ZBook X,,In Transit,,," + young + ",0y 8m",
            "Melbourne,STOCKOLD,Z0,HP ZBook Studio,,In Stock,Available,Melbourne stock," + old + ",6y 0m"
        ]));
        desktop.NextOpenFile = path;
        refresh.ImportDueListCommand.Execute(null);

        Assert.Contains("in use for 4 years or more", refresh.Status, StringComparison.Ordinal);
        Assert.Contains("1 in stock is available to assign. 1 is in transit.", refresh.Status, StringComparison.Ordinal);
        Assert.Equal(["Sam"], refresh.Next.Select(row => row.AssignedTo).ToArray());
        Assert.Equal("Next", Assert.Single(refresh.Next).QueueText);
        Assert.Equal(["STOCKNEW"], refresh.InBox.Select(item => item.SerialNumber).ToArray());
        Assert.DoesNotContain(refresh.InBox, item => item.SerialNumber == "TRANSIT1");
        Assert.Equal("Melbourne stock", refresh.InBox.Single(item => item.SerialNumber == "STOCKNEW").Stockroom);
        var transit = Assert.Single(refresh.InTransit);
        Assert.Equal("TRANSIT1", transit.SerialNumber);
        Assert.Equal("In Transit", transit.State);
        Assert.True(refresh.HasInTransit);
    }

    [Fact]
    public void HardwarePageOffersLaptopRefresh()
    {
        var view = RepoFile("src/ServiceNowDesk.App/Views/HardwareView.xaml");
        Assert.Contains("OpenLaptopRefreshCommand", view, StringComparison.Ordinal);
        Assert.Contains("Import list", view, StringComparison.Ordinal);
        Assert.DoesNotContain("Vacation student", view, StringComparison.Ordinal);
        Assert.Contains("Who actually received a laptop", view, StringComparison.Ordinal);
        Assert.Contains("Still has the old laptop", view, StringComparison.Ordinal);
        Assert.Contains("Reason they received it", view, StringComparison.Ordinal);
        Assert.Contains("Model code", view, StringComparison.Ordinal);
        Assert.DoesNotContain("Done by", view, StringComparison.Ordinal);
        Assert.Contains("Laptops in transit", view, StringComparison.Ordinal);
        Assert.Contains("Laptops in stock under 3 years old are the ones in the box.", view, StringComparison.Ordinal);
        Assert.Contains("Laptops in transit stay in transit until they are changed to in stock.", view, StringComparison.Ordinal);
        Assert.DoesNotContain("ColumnDefinition Width=\"380\"", view, StringComparison.Ordinal);
        Assert.Contains("Width=\"*\"", view, StringComparison.Ordinal);
    }

    private static LaptopRefreshViewModel Workspace(ILaptopRefreshStore store, RecordingDesktopServices desktop)
    {
        var refresh = new LaptopRefreshViewModel(store, () => "practice");
        refresh.UseDesktop(desktop);
        return refresh;
    }

    private static LaptopRefreshSnapshot DueList() => new()
    {
        Due =
        [
            Due("OLD1", "MELLT1", "Sam", "Melbourne", new DateOnly(2019, 1, 1)),
            Due("OLD2", "MELLT2", "Alex", "Melbourne", new DateOnly(2021, 6, 1)),
            Due("OLD3", "SYDLT3", "Pat", "Sydney", new DateOnly(2018, 1, 1)),
            Due("OLD4", "MELLT4", "Jo", "Melbourne", new DateOnly(2020, 1, 1)),
            Due("OLD5", "MELLT5", "Done", "Melbourne", new DateOnly(2017, 1, 1)),
            Due("OLD9", "MELLT9", "", "Melbourne", new DateOnly(2016, 1, 1), "In Stock")
        ]
    };

    private static DueLaptop Due(string serial, string hostname, string person, string location, DateOnly delivered, string state = "In Use") =>
        new()
        {
            SerialNumber = serial,
            Hostname = hostname,
            Model = "Dell Precision 5560",
            AssignedTo = person,
            Location = location,
            State = state,
            DeliveryDate = delivered
        };

    private static HardwareAsset Box(string serial, string model, string location, string state, string substate) => new()
    {
        SysId = serial,
        SerialNumber = serial,
        Model = model,
        ModelCategory = HardwareCatalog.Computer,
        Location = new ReferenceValue(serial + "-loc", location),
        Stockroom = new ReferenceValue(serial + "-stock", location + " stock"),
        InstallStatus = state,
        InstallStatusLabel = state,
        Substatus = substate,
        SubstatusLabel = substate
    };

    private static HardwareAsset Hold(string serial, string location) => new()
    {
        SysId = serial,
        SerialNumber = serial,
        Model = "HP EliteBook",
        ModelCategory = HardwareCatalog.Computer,
        Location = new ReferenceValue(serial + "-loc", location),
        InstallStatus = HardwareCatalog.InStock,
        InstallStatusLabel = HardwareCatalog.InStock,
        Substatus = "Legal hold",
        SubstatusLabel = "Legal hold"
    };

    private static HardwareAsset InUse(string serial, string hostname, string model, string location, string person) => new()
    {
        SysId = serial,
        SerialNumber = serial,
        DisplayName = hostname,
        Model = model,
        ModelCategory = HardwareCatalog.Computer,
        Location = new ReferenceValue(serial + "-loc", location),
        ConfigurationItem = new ReferenceValue(serial + "-ci", hostname),
        AssignedTo = string.IsNullOrEmpty(person) ? ReferenceValue.Empty : new ReferenceValue(serial + "-user", person),
        InstallStatus = HardwareCatalog.InUse,
        InstallStatusLabel = HardwareCatalog.InUse
    };

    private static byte[] Workbook(string[] headers, string[][] rows, int dateColumn)
    {
        var strings = new List<string>();
        int Share(string value)
        {
            var index = strings.IndexOf(value);
            if (index >= 0)
                return index;
            strings.Add(value);
            return strings.Count - 1;
        }

        var sheet = new StringBuilder();
        sheet.Append("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");
        void WriteRow(int rowNumber, IReadOnlyList<string> values, bool dates)
        {
            sheet.Append("<row r=\"").Append(rowNumber).Append("\">");
            for (var column = 0; column < values.Count; column++)
            {
                var cell = ColumnName(column + 1) + rowNumber.ToString(CultureInfo.InvariantCulture);
                if (dates && column + 1 == dateColumn && double.TryParse(values[column], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    sheet.Append("<c r=\"").Append(cell).Append("\"><v>").Append(values[column]).Append("</v></c>");
                else
                    sheet.Append("<c r=\"").Append(cell).Append("\" t=\"s\"><v>").Append(Share(values[column])).Append("</v></c>");
            }

            sheet.Append("</row>");
        }

        WriteRow(1, headers, dates: false);
        for (var index = 0; index < rows.Length; index++)
            WriteRow(index + 2, rows[index], dates: true);
        sheet.Append("</sheetData></worksheet>");

        var shared = new StringBuilder();
        shared.Append("<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" count=\"")
            .Append(strings.Count)
            .Append("\" uniqueCount=\"")
            .Append(strings.Count)
            .Append("\">");
        foreach (var value in strings)
            shared.Append("<si><t>").Append(value).Append("</t></si>");
        shared.Append("</sst>");

        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "[Content_Types].xml",
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
                + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
                + "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
                + "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>"
                + "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>"
                + "<Override PartName=\"/xl/sharedStrings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml\"/>"
                + "</Types>");
            Write(zip, "_rels/.rels",
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>"
                + "</Relationships>");
            Write(zip, "xl/workbook.xml",
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">"
                + "<sheets><sheet name=\"Export\" sheetId=\"1\" r:id=\"rId1\"/></sheets>"
                + "</workbook>");
            Write(zip, "xl/_rels/workbook.xml.rels",
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>"
                + "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings\" Target=\"sharedStrings.xml\"/>"
                + "</Relationships>");
            Write(zip, "xl/worksheets/sheet1.xml", sheet.ToString());
            Write(zip, "xl/sharedStrings.xml", shared.ToString());
        }

        return output.ToArray();
    }

    private static void Write(ZipArchive zip, string path, string text)
    {
        var entry = zip.CreateEntry(path);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(text);
    }

    private static string ColumnName(int index)
    {
        var name = "";
        while (index > 0)
        {
            index--;
            name = (char)('A' + (index % 26)) + name;
            index /= 26;
        }

        return name;
    }

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ServiceNowDesk.sln")))
                return File.ReadAllText(Path.Combine(dir.FullName, relative));
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not find ServiceNowDesk.sln from " + AppContext.BaseDirectory);
    }
}
