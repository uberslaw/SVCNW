using System.Text.Json;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.Services;

public interface IRecentAssignmentGroupStore
{
    event EventHandler? Changed;

    IReadOnlyList<Choice> Load();

    void Remember(string sysId, string label);
}

public sealed class MemoryRecentAssignmentGroupStore : IRecentAssignmentGroupStore
{
    private readonly object _gate = new();
    private readonly List<Choice> _items = [];

    public event EventHandler? Changed;

    public IReadOnlyList<Choice> Load()
    {
        lock (_gate)
            return _items.ToArray();
    }

    public void Remember(string sysId, string label)
    {
        if (!RecentAssignmentGroups.TryRemember(_items, sysId, label, _gate))
            return;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class FileRecentAssignmentGroupStore : IRecentAssignmentGroupStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly string _folder;

    public FileRecentAssignmentGroupStore(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            throw new ArgumentException("Enter a folder for recent assignment groups.", nameof(folder));
        _folder = folder;
    }

    public static FileRecentAssignmentGroupStore InApplicationData() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ServiceNowDesk"));

    public event EventHandler? Changed;

    public IReadOnlyList<Choice> Load()
    {
        lock (_gate)
            return Read();
    }

    public void Remember(string sysId, string label)
    {
        var changed = false;
        lock (_gate)
        {
            var items = Read().ToList();
            if (!RecentAssignmentGroups.TryRemember(items, sysId, label, gate: null))
                return;
            Write(items);
            changed = true;
        }

        if (changed)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    private IReadOnlyList<Choice> Read()
    {
        try
        {
            var path = Path.Combine(_folder, RecentAssignmentGroups.FileName);
            if (!File.Exists(path))
                return [];
            var rows = JsonSerializer.Deserialize<List<SavedGroup>>(File.ReadAllText(path)) ?? [];
            return rows
                .Where(row => !string.IsNullOrWhiteSpace(row.SysId))
                .Select(row => new Choice(row.SysId.Trim(), string.IsNullOrWhiteSpace(row.Label) ? row.SysId.Trim() : row.Label.Trim()))
                .Take(RecentAssignmentGroups.Limit)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void Write(IReadOnlyList<Choice> groups)
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, RecentAssignmentGroups.FileName);
        var temp = path + ".tmp";
        var rows = groups.Select(group => new SavedGroup { SysId = group.Value, Label = group.Label }).ToArray();
        File.WriteAllText(temp, JsonSerializer.Serialize(rows, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    private sealed class SavedGroup
    {
        public string SysId { get; set; } = "";
        public string Label { get; set; } = "";
    }
}

internal static class RecentAssignmentGroups
{
    public const int Limit = 5;
    public const string FileName = "recent-assignment-groups.json";

    public static bool TryRemember(List<Choice> items, string sysId, string label, object? gate)
    {
        var id = (sysId ?? "").Trim();
        if (id.Length == 0)
            return false;

        var name = (label ?? "").Trim();
        if (name.Length == 0)
            name = id;

        if (gate is null)
            return Apply(items, id, name);

        lock (gate)
            return Apply(items, id, name);
    }

    private static bool Apply(List<Choice> items, string id, string name)
    {
        var index = items.FindIndex(item => item.Value.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (index == 0 && items[0].Label.Equals(name, StringComparison.Ordinal))
            return false;

        if (index >= 0)
            items.RemoveAt(index);
        items.Insert(0, new Choice(id, name));
        if (items.Count > Limit)
            items.RemoveRange(Limit, items.Count - Limit);
        return true;
    }
}
