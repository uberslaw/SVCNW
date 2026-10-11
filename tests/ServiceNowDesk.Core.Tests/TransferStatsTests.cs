using System.Net;
using System.Text;
using ServiceNowDesk.Client;
using ServiceNowDesk.Models;
using ServiceNowDesk.Services;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Tests;

public class TransferStatsTests
{
    [Fact]
    public void PeriodAggregationSumsDailyBucketsAndSessionIsSeparate()
    {
        var now = DateTimeOffset.Now;
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var store = new MemoryTransferStatsStore();
        store.Save(new TransferStatsSnapshot
        {
            Days =
            [
                new TransferDayBucket { Day = "2020-01-01", UploadedBytes = 100, DownloadedBytes = 1_000 },
                new TransferDayBucket
                {
                    Day = today.AddDays(-1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    UploadedBytes = 50,
                    DownloadedBytes = 500
                },
                new TransferDayBucket
                {
                    Day = today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    UploadedBytes = 25,
                    DownloadedBytes = 250
                }
            ]
        });

        var meter = new TransferMeter(store);
        meter.Add(10, 20);

        Assert.Equal((10, 20), meter.Totals(TransferPeriod.Session, now));
        Assert.Equal((35, 270), meter.Totals(TransferPeriod.Today, now));
        Assert.Equal((85, 770), meter.Totals(TransferPeriod.Month, now));
        Assert.Equal((185, 1_770), meter.Totals(TransferPeriod.AllTime, now));
        Assert.True(meter.Totals(TransferPeriod.TwelveMonths, now).Downloaded >= 770);
    }

    [Fact]
    public void PersistedBucketsSurviveANewMeter()
    {
        var store = new MemoryTransferStatsStore();
        var first = new TransferMeter(store);
        first.Add(40, 400);
        first.Flush();

        var second = new TransferMeter(store);
        var today = second.Totals(TransferPeriod.Today, DateTimeOffset.Now);
        Assert.Equal(40, today.Uploaded);
        Assert.Equal(400, today.Downloaded);
        Assert.Equal((0L, 0L), second.Totals(TransferPeriod.Session, DateTimeOffset.Now));
    }

    [Fact]
    public void ByteSizeFormatUsesReadableUnits()
    {
        Assert.Equal("0 B", ByteSizeFormat.Format(0));
        Assert.Equal("512 B", ByteSizeFormat.Format(512));
        Assert.Contains("KB", ByteSizeFormat.Format(2048), StringComparison.Ordinal);
        Assert.Contains("MB", ByteSizeFormat.Format(2 * 1024 * 1024), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LiveClientRecordsUploadAndDownloadBytes()
    {
        var meter = new TransferMeter(new MemoryTransferStatsStore());
        var handler = new StubHandler((request, _) =>
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            if (path.Contains("/sys_user", StringComparison.Ordinal) && !path.Contains("sys_user_group", StringComparison.Ordinal))
                return Api.Json("""{"result":[{"sys_id":"sample-user","name":"Alex Rivera","user_name":"alex.rivera","email":"alex@example.com"}]}""");
            if (request.Method == HttpMethod.Patch)
                return Api.Json("""{"result":{"sys_id":"inc-1","number":"INC1","short_description":"Updated","state":"2","sys_updated_on":"2026-10-01 10:00:00"}}""");
            return Api.Json("""{"result":[]}""");
        });
        using var client = ServiceNowClient.Create(Api.BasicSession(), handler, transfer: meter);

        await client.GetCurrentUserAsync(CancellationToken.None);
        Assert.True(meter.SessionDownloadedBytes > 0);
        Assert.Equal(0, meter.SessionUploadedBytes);

        var body = """{"short_description":"hello"}""";
        // Patch through a known write path.
        await client.UpdateIncidentAsync("inc-1", new IncidentChanges { ShortDescription = "hello" }, CancellationToken.None);

        Assert.True(meter.SessionUploadedBytes >= Encoding.UTF8.GetByteCount(body) - 20);
        Assert.True(meter.SessionDownloadedBytes > 0);
        meter.Flush();
        var session = meter.Totals(TransferPeriod.Session, DateTimeOffset.Now);
        Assert.Equal(meter.SessionUploadedBytes, session.Uploaded);
        Assert.Equal(meter.SessionDownloadedBytes, session.Downloaded);
    }

    [Fact]
    public void CacheFootprintAndRowSizeTextReportStoredBytes()
    {
        var list = new CachedTicketList
        {
            CapturedAt = DateTimeOffset.UtcNow,
            Items = [new CachedTicketRow { SysId = "1", Number = "INC1", Title = "Printer" }]
        };
        Assert.True(CacheFootprint.ListSectionBytes(list) > 0);

        var hardware = new MemoryHardwareCatalogStore();
        hardware.Save(DeskListScope.Practice, new HardwareCatalogSnapshot
        {
            CapturedAt = DateTimeOffset.UtcNow,
            Items =
            [
                new HardwareAsset
                {
                    SysId = "hw-1",
                    SerialNumber = "ABC",
                    Model = "Laptop",
                    ModelCategory = HardwareCatalog.Computer,
                    Location = new ReferenceValue("loc", "Brisbane Office")
                }
            ]
        });
        Assert.True(hardware.StoredBytes(DeskListScope.Practice) > 0);

        var row = new CacheRowModel("hardware", "Hardware");
        row.SetStoredBytes(hardware.StoredBytes(DeskListScope.Practice));
        Assert.True(row.StoredBytes > 0);
        Assert.Contains("on disk", row.SizeText, StringComparison.OrdinalIgnoreCase);

        var main = new MainViewModel(
            new MemorySettingsStore(),
            new RecordingDesktopServices(),
            transfer: new TransferMeter(new MemoryTransferStatsStore()));
        Assert.Equal("This session", main.SelectedTransferPeriod?.Label);
        Assert.StartsWith("Up to server:", main.TransferUploadedText, StringComparison.Ordinal);
        Assert.StartsWith("Down from server:", main.TransferDownloadedText, StringComparison.Ordinal);

        main.SelectedTransferPeriod = main.TransferPeriods.Single(option => option.Period == TransferPeriod.AllTime);
        Assert.Equal("All time", main.SelectedTransferPeriod.Label);
    }
}
