using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ServiceNowDesk.Services;

/// <summary>
/// A Daily Work row the user marked "Not part of my queue". Local only — not written to ServiceNow.
/// </summary>
public sealed record QueueDismissal(
    string SysId,
    string Number,
    string Office,
    string List,
    string Reason,
    DateTime DismissedAtLocal,
    DateOnly LocalDay);

public interface IQueueDismissalStore
{
    IReadOnlyList<QueueDismissal> Load();

    void Save(IReadOnlyList<QueueDismissal> dismissals);
}

public sealed class MemoryQueueDismissalStore : IQueueDismissalStore
{
    private List<QueueDismissal> _items = [];

    public IReadOnlyList<QueueDismissal> Load() => _items.ToArray();

    public void Save(IReadOnlyList<QueueDismissal> dismissals)
    {
        ArgumentNullException.ThrowIfNull(dismissals);
        _items = dismissals.ToList();
    }
}

/// <summary>
/// Plain JSON in %AppData%\ServiceNowDesk\queue-dismissals.json. Older local days are dropped on write.
/// </summary>
public sealed class FileQueueDismissalStore : IQueueDismissalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();

    public FileQueueDismissalStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Enter a path for the queue dismissal file.", nameof(path));
        Path = path;
    }

    public string Path { get; }

    public static FileQueueDismissalStore InApplicationData() =>
        new(System.IO.Path.Combine(DeskAppData.Folder, DeskAppData.QueueDismissalsFileName));

    public IReadOnlyList<QueueDismissal> Load()
    {
        lock (_gate)
            return Read();
    }

    public void Save(IReadOnlyList<QueueDismissal> dismissals)
    {
        ArgumentNullException.ThrowIfNull(dismissals);
        lock (_gate)
        {
            var folder = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrWhiteSpace(folder))
                Directory.CreateDirectory(folder);

            var keepAfter = DateOnly.FromDateTime(DateTime.Now).AddDays(-1);
            var file = new QueueDismissalFile
            {
                Dismissals = dismissals
                    .Where(item => item.LocalDay >= keepAfter)
                    .Select(ToStored)
                    .ToList()
            };
            File.WriteAllText(Path, JsonSerializer.Serialize(file, JsonOptions));
        }
    }

    private IReadOnlyList<QueueDismissal> Read()
    {
        try
        {
            if (!File.Exists(Path))
                return [];

            var file = JsonSerializer.Deserialize<QueueDismissalFile>(File.ReadAllText(Path), JsonOptions);
            if (file?.Dismissals is null)
                return [];

            var items = new List<QueueDismissal>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var stored in file.Dismissals)
            {
                if (stored is null)
                    continue;
                var sysId = stored.SysId?.Trim() ?? "";
                var number = stored.Number?.Trim() ?? "";
                if (sysId.Length == 0 && number.Length == 0)
                    continue;
                var key = sysId.Length > 0 ? sysId : number;
                if (!seen.Add(key))
                    continue;
                if (ParseDate(stored.LocalDay) is not DateOnly day)
                    continue;
                var when = DateTime.TryParse(
                    stored.DismissedAtLocal,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsed)
                    ? parsed
                    : day.ToDateTime(TimeOnly.MinValue);
                items.Add(new QueueDismissal(
                    sysId,
                    number,
                    stored.Office?.Trim() ?? "",
                    stored.List?.Trim() ?? "",
                    stored.Reason?.Trim() ?? "",
                    when,
                    day));
            }

            return items;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static QueueDismissalStored ToStored(QueueDismissal item) => new()
    {
        SysId = item.SysId,
        Number = item.Number,
        Office = item.Office,
        List = item.List,
        Reason = item.Reason,
        DismissedAtLocal = item.DismissedAtLocal.ToString("o", CultureInfo.InvariantCulture),
        LocalDay = item.LocalDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
    };

    private static DateOnly? ParseDate(string? text) =>
        DateOnly.TryParseExact(text?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : null;

    private sealed class QueueDismissalFile
    {
        [JsonPropertyName("dismissals")]
        public List<QueueDismissalStored> Dismissals { get; set; } = [];
    }

    private sealed class QueueDismissalStored
    {
        [JsonPropertyName("sysId")]
        public string? SysId { get; set; }

        [JsonPropertyName("number")]
        public string? Number { get; set; }

        [JsonPropertyName("office")]
        public string? Office { get; set; }

        [JsonPropertyName("list")]
        public string? List { get; set; }

        [JsonPropertyName("reason")]
        public string? Reason { get; set; }

        [JsonPropertyName("dismissedAtLocal")]
        public string? DismissedAtLocal { get; set; }

        [JsonPropertyName("localDay")]
        public string? LocalDay { get; set; }
    }
}
