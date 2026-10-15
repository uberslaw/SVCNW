using System.Text;
using System.Text.Json;

namespace ServiceNowDesk.Client;

public interface ILaptopRefreshStore
{
    LaptopRefreshSnapshot? Load(string scope);
    void Save(string scope, LaptopRefreshSnapshot snapshot);
}

public sealed class MemoryLaptopRefreshStore : ILaptopRefreshStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly Dictionary<string, string> _json = new(StringComparer.OrdinalIgnoreCase);

    public LaptopRefreshSnapshot? Load(string scope)
    {
        if (!_json.TryGetValue(scope, out var json))
            return null;
        return JsonSerializer.Deserialize<LaptopRefreshSnapshot>(json, JsonOptions);
    }

    public void Save(string scope, LaptopRefreshSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _json[scope] = JsonSerializer.Serialize(snapshot, JsonOptions);
    }
}

public sealed class FileLaptopRefreshStore : ILaptopRefreshStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _folder;

    public FileLaptopRefreshStore(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            throw new ArgumentException("Enter a folder for the laptop refresh list.", nameof(folder));
        _folder = folder;
    }

    public static FileLaptopRefreshStore InApplicationData() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ServiceNowDesk"));

    public LaptopRefreshSnapshot? Load(string scope)
    {
        try
        {
            var path = PathFor(scope);
            if (!File.Exists(path))
                return null;
            return JsonSerializer.Deserialize<LaptopRefreshSnapshot>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(string scope, LaptopRefreshSnapshot snapshot)
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
        return Path.Combine(_folder, "laptop-refresh." + name + ".json");
    }
}
