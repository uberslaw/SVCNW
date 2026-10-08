using System.Text.Json;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.Client;

public sealed class HardwareCatalogSnapshot
{
    public DateTimeOffset CapturedAt { get; set; }
    public bool AllLocations { get; set; }
    public List<string> Offices { get; set; } = [];
    public List<HardwareAsset> Items { get; set; } = [];
}

public interface IHardwareCatalogStore
{
    HardwareCatalogSnapshot? Load(string scope);
    void Save(string scope, HardwareCatalogSnapshot snapshot);
}

public sealed class MemoryHardwareCatalogStore : IHardwareCatalogStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly Dictionary<string, string> _json = new(StringComparer.OrdinalIgnoreCase);

    public HardwareCatalogSnapshot? Load(string scope)
    {
        if (!_json.TryGetValue(scope, out var json))
            return null;
        return JsonSerializer.Deserialize<HardwareCatalogSnapshot>(json, JsonOptions);
    }

    public void Save(string scope, HardwareCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _json[scope] = JsonSerializer.Serialize(snapshot, JsonOptions);
    }
}

public sealed class FileHardwareCatalogStore : IHardwareCatalogStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _folder;

    public FileHardwareCatalogStore(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            throw new ArgumentException("Enter a folder for the saved hardware list.", nameof(folder));
        _folder = folder;
    }

    public static FileHardwareCatalogStore InApplicationData() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ServiceNowDesk"));

    public HardwareCatalogSnapshot? Load(string scope)
    {
        try
        {
            var path = PathFor(scope);
            if (!File.Exists(path))
                return null;
            return JsonSerializer.Deserialize<HardwareCatalogSnapshot>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(string scope, HardwareCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Directory.CreateDirectory(_folder);
        var path = PathFor(scope);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    private string PathFor(string scope)
    {
        var name = string.IsNullOrWhiteSpace(scope) ? "instance" : scope.Trim().ToLowerInvariant();
        foreach (var ch in Path.GetInvalidFileNameChars())
            name = name.Replace(ch, '_');
        return Path.Combine(_folder, "hardware." + name + ".json");
    }
}
