using System.Globalization;
using System.Text.Json;

namespace ServiceNowDesk.Services;

/// <summary>
/// How far back Settings → Cache sums uploaded / downloaded ServiceNow bytes.
/// Session is in-memory for this run; other periods use persisted daily buckets.
/// </summary>
public enum TransferPeriod
{
    Session,
    Today,
    ThisWeek,
    Month,
    ThreeMonths,
    SixMonths,
    TwelveMonths,
    AllTime
}

public sealed class TransferDayBucket
{
    public string Day { get; set; } = "";
    public long UploadedBytes { get; set; }
    public long DownloadedBytes { get; set; }
}

public sealed class TransferStatsSnapshot
{
    public List<TransferDayBucket> Days { get; set; } = [];
}

public interface ITransferStatsStore
{
    TransferStatsSnapshot Load();
    void Save(TransferStatsSnapshot snapshot);
}

public sealed class MemoryTransferStatsStore : ITransferStatsStore
{
    private TransferStatsSnapshot _snapshot = new();

    public TransferStatsSnapshot Load() =>
        new()
        {
            Days = _snapshot.Days
                .Select(day => new TransferDayBucket
                {
                    Day = day.Day,
                    UploadedBytes = day.UploadedBytes,
                    DownloadedBytes = day.DownloadedBytes
                })
                .ToList()
        };

    public void Save(TransferStatsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = new TransferStatsSnapshot
        {
            Days = snapshot.Days
                .Select(day => new TransferDayBucket
                {
                    Day = day.Day,
                    UploadedBytes = day.UploadedBytes,
                    DownloadedBytes = day.DownloadedBytes
                })
                .ToList()
        };
    }
}

public sealed class FileTransferStatsStore : ITransferStatsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public FileTransferStatsStore(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            throw new ArgumentException("Enter a folder for transfer stats.", nameof(folder));
        _path = Path.Combine(folder, DeskAppData.TransferStatsFileName);
    }

    public static FileTransferStatsStore InApplicationData() => new(DeskAppData.Folder);

    public TransferStatsSnapshot Load()
    {
        try
        {
            if (!File.Exists(_path))
                return new TransferStatsSnapshot();
            return JsonSerializer.Deserialize<TransferStatsSnapshot>(File.ReadAllText(_path), JsonOptions)
                ?? new TransferStatsSnapshot();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new TransferStatsSnapshot();
        }
    }

    public void Save(TransferStatsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var folder = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, JsonOptions));
        File.Move(temp, _path, overwrite: true);
    }
}

/// <summary>
/// Counts ServiceNow HTTP upload / download bytes for this session and rolling daily buckets.
/// </summary>
public sealed class TransferMeter
{
    /// <summary>Keep enough daily rows for the longest named period (12 months) plus margin.</summary>
    public const int MaxDayBuckets = 400;

    private readonly ITransferStatsStore _store;
    private readonly object _gate = new();
    private readonly List<TransferDayBucket> _days;
    private long _sessionUploaded;
    private long _sessionDownloaded;
    private bool _dirty;

    public TransferMeter(ITransferStatsStore? store = null)
    {
        _store = store ?? new MemoryTransferStatsStore();
        _days = _store.Load().Days ?? [];
        PruneUnlocked(DateTimeOffset.Now);
    }

    public long SessionUploadedBytes
    {
        get { lock (_gate) return _sessionUploaded; }
    }

    public long SessionDownloadedBytes
    {
        get { lock (_gate) return _sessionDownloaded; }
    }

    public void Add(long uploadedBytes, long downloadedBytes)
    {
        if (uploadedBytes <= 0 && downloadedBytes <= 0)
            return;

        lock (_gate)
        {
            if (uploadedBytes > 0)
                _sessionUploaded += uploadedBytes;
            if (downloadedBytes > 0)
                _sessionDownloaded += downloadedBytes;

            var day = DayKey(DateTimeOffset.Now);
            var bucket = _days.FirstOrDefault(row => string.Equals(row.Day, day, StringComparison.Ordinal));
            if (bucket is null)
            {
                bucket = new TransferDayBucket { Day = day };
                _days.Add(bucket);
            }

            if (uploadedBytes > 0)
                bucket.UploadedBytes += uploadedBytes;
            if (downloadedBytes > 0)
                bucket.DownloadedBytes += downloadedBytes;
            _dirty = true;
            PruneUnlocked(DateTimeOffset.Now);
        }
    }

    public (long Uploaded, long Downloaded) Totals(TransferPeriod period, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (period == TransferPeriod.Session)
                return (_sessionUploaded, _sessionDownloaded);

            var start = PeriodStart(period, now);
            long uploaded = 0;
            long downloaded = 0;
            foreach (var bucket in _days)
            {
                if (!TryParseDay(bucket.Day, out var day))
                    continue;
                if (start is { } from && day < from)
                    continue;
                uploaded += bucket.UploadedBytes;
                downloaded += bucket.DownloadedBytes;
            }

            return (uploaded, downloaded);
        }
    }

    public void Flush()
    {
        TransferStatsSnapshot snapshot;
        lock (_gate)
        {
            if (!_dirty)
                return;
            snapshot = new TransferStatsSnapshot
            {
                Days = _days
                    .Select(day => new TransferDayBucket
                    {
                        Day = day.Day,
                        UploadedBytes = day.UploadedBytes,
                        DownloadedBytes = day.DownloadedBytes
                    })
                    .OrderBy(day => day.Day, StringComparer.Ordinal)
                    .ToList()
            };
            _dirty = false;
        }

        _store.Save(snapshot);
    }

    public static DateOnly? PeriodStart(TransferPeriod period, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        return period switch
        {
            TransferPeriod.Session => null,
            TransferPeriod.Today => today,
            TransferPeriod.ThisWeek => today.AddDays(-(int)today.DayOfWeek),
            TransferPeriod.Month => today.AddDays(1 - today.Day),
            TransferPeriod.ThreeMonths => today.AddMonths(-3),
            TransferPeriod.SixMonths => today.AddMonths(-6),
            TransferPeriod.TwelveMonths => today.AddMonths(-12),
            TransferPeriod.AllTime => null,
            _ => today
        };
    }

    public static string LabelOf(TransferPeriod period) => period switch
    {
        TransferPeriod.Session => "This session",
        TransferPeriod.Today => "Today",
        TransferPeriod.ThisWeek => "This week",
        TransferPeriod.Month => "Month",
        TransferPeriod.ThreeMonths => "3 months",
        TransferPeriod.SixMonths => "6 months",
        TransferPeriod.TwelveMonths => "12 months",
        TransferPeriod.AllTime => "All time",
        _ => period.ToString()
    };

    private void PruneUnlocked(DateTimeOffset now)
    {
        if (_days.Count <= MaxDayBuckets)
            return;

        var cutoff = DateOnly.FromDateTime(now.LocalDateTime).AddDays(-(MaxDayBuckets - 1));
        _days.RemoveAll(bucket => TryParseDay(bucket.Day, out var day) && day < cutoff);
        if (_days.Count > MaxDayBuckets)
        {
            foreach (var extra in _days.OrderBy(day => day.Day, StringComparer.Ordinal).Take(_days.Count - MaxDayBuckets).ToArray())
                _days.Remove(extra);
        }
    }

    private static string DayKey(DateTimeOffset when) =>
        DateOnly.FromDateTime(when.LocalDateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static bool TryParseDay(string? value, out DateOnly day) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
}

/// <summary>Human-readable byte sizes for Settings cache rows and transfer totals.</summary>
public static class ByteSizeFormat
{
    public static string Format(long bytes)
    {
        if (bytes < 0)
            bytes = 0;

        double value = bytes;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        if (unit == 0)
            return bytes.ToString("N0", CultureInfo.CurrentCulture) + " B";

        var decimals = value >= 100 || unit == 1 ? 0 : 1;
        return value.ToString("N" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture)
            + " "
            + units[unit];
    }
}
