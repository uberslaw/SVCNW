using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.Services;

public interface IPersonalTaskStore
{
    IReadOnlyList<PersonalTask> Load();

    void Replace(IReadOnlyList<PersonalTask> tasks);
}

public sealed class MemoryPersonalTaskStore : IPersonalTaskStore
{
    private List<PersonalTask> _tasks = [];

    public IReadOnlyList<PersonalTask> Load() => _tasks.ToArray();

    public void Replace(IReadOnlyList<PersonalTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        _tasks = tasks.ToList();
    }
}

/// <summary>
/// Plain JSON in %AppData%\ServiceNowDesk\daily-tasks.json. Not DPAPI and not ServiceNow.
/// </summary>
public sealed class FilePersonalTaskStore : IPersonalTaskStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();

    public FilePersonalTaskStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Enter a path for the personal task file.", nameof(path));
        Path = path;
    }

    public string Path { get; }

    public static FilePersonalTaskStore InApplicationData() => new(DeskAppData.DailyTasksPath);

    public IReadOnlyList<PersonalTask> Load()
    {
        lock (_gate)
            return Read();
    }

    public void Replace(IReadOnlyList<PersonalTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        lock (_gate)
        {
            var folder = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrWhiteSpace(folder))
                Directory.CreateDirectory(folder);

            var file = new PersonalTaskFile
            {
                Tasks = tasks.Select(task => new PersonalTaskStored
                {
                    Id = task.Id.ToString("D"),
                    Text = task.Text,
                    TrafficLight = task.Light.ToString(),
                    CreatedUtc = task.CreatedUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)
                }).ToList()
            };
            File.WriteAllText(Path, JsonSerializer.Serialize(file, JsonOptions));
        }
    }

    private IReadOnlyList<PersonalTask> Read()
    {
        try
        {
            if (!File.Exists(Path))
                return [];

            var file = JsonSerializer.Deserialize<PersonalTaskFile>(File.ReadAllText(Path), JsonOptions);
            if (file?.Tasks is null)
                return [];

            var seen = new HashSet<Guid>();
            var tasks = new List<PersonalTask>();
            foreach (var stored in file.Tasks)
            {
                if (stored is null || !Guid.TryParse(stored.Id, out var id) || !seen.Add(id))
                    continue;
                var text = stored.Text?.Trim() ?? "";
                if (text.Length == 0)
                    continue;
                if (text.Length > PersonalTask.MaxTextLength)
                    text = text[..PersonalTask.MaxTextLength];

                var light = Enum.TryParse<TrafficLight>(stored.TrafficLight, ignoreCase: true, out var parsed)
                    ? PersonalTask.Normalize(parsed)
                    : TrafficLight.Amber;
                var created = DateTime.TryParse(
                    stored.CreatedUtc,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsedUtc)
                    ? DateTime.SpecifyKind(parsedUtc.ToUniversalTime(), DateTimeKind.Utc)
                    : DateTime.UnixEpoch;
                tasks.Add(new PersonalTask(id, text, light, created));
            }

            return tasks;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private sealed class PersonalTaskFile
    {
        [JsonPropertyName("tasks")]
        public List<PersonalTaskStored> Tasks { get; set; } = [];
    }

    private sealed class PersonalTaskStored
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("text")]
        public string Text { get; set; } = "";

        [JsonPropertyName("trafficLight")]
        public string TrafficLight { get; set; } = "";

        [JsonPropertyName("createdUtc")]
        public string CreatedUtc { get; set; } = "";
    }
}
