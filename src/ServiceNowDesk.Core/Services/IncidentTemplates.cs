using System.Text.Json;

namespace ServiceNowDesk.Services;

public sealed class IncidentTemplate
{
    public string Name { get; set; } = "";
    public string ShortDescription { get; set; } = "";
    public string Description { get; set; } = "";
    public string State { get; set; } = "";
    public string Impact { get; set; } = "";
    public string Urgency { get; set; } = "";
    public string Priority { get; set; } = "";
    public string Category { get; set; } = "";
    public string Subcategory { get; set; } = "";
    public string SubcategoryLabel { get; set; } = "";
    public string ContactType { get; set; } = "";
    public string HoldReason { get; set; } = "";
    public string AssignmentGroupId { get; set; } = "";
    public string AssignmentGroupDisplay { get; set; } = "";
    public string AssignedToId { get; set; } = "";
    public string AssignedToDisplay { get; set; } = "";
    public string CallerId { get; set; } = "";
    public string CallerDisplay { get; set; } = "";
}

public interface IIncidentTemplateStore
{
    IReadOnlyList<IncidentTemplate> List();
    void Save(IncidentTemplate template);
    void Delete(string name);
}

public sealed class MemoryIncidentTemplateStore : IIncidentTemplateStore
{
    private readonly List<IncidentTemplate> _items = [];

    public IReadOnlyList<IncidentTemplate> List() =>
        _items.Select(Clone).OrderBy(template => template.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public void Save(IncidentTemplate template)
    {
        var copy = Normalize(template);
        var index = _items.FindIndex(item => item.Name.Equals(copy.Name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            _items[index] = copy;
        else
            _items.Add(copy);
    }

    public void Delete(string name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0)
            return;
        _items.RemoveAll(item => item.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
    }

    private static IncidentTemplate Normalize(IncidentTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var name = template.Name?.Trim() ?? "";
        if (name.Length == 0)
            throw new ArgumentException("Enter a template name.", nameof(template));
        var copy = Clone(template);
        copy.Name = name;
        return copy;
    }

    private static IncidentTemplate Clone(IncidentTemplate template) => new()
    {
        Name = template.Name ?? "",
        ShortDescription = template.ShortDescription ?? "",
        Description = template.Description ?? "",
        State = template.State ?? "",
        Impact = template.Impact ?? "",
        Urgency = template.Urgency ?? "",
        Priority = template.Priority ?? "",
        Category = template.Category ?? "",
        Subcategory = template.Subcategory ?? "",
        SubcategoryLabel = template.SubcategoryLabel ?? "",
        ContactType = template.ContactType ?? "",
        HoldReason = template.HoldReason ?? "",
        AssignmentGroupId = template.AssignmentGroupId ?? "",
        AssignmentGroupDisplay = template.AssignmentGroupDisplay ?? "",
        AssignedToId = template.AssignedToId ?? "",
        AssignedToDisplay = template.AssignedToDisplay ?? "",
        CallerId = template.CallerId ?? "",
        CallerDisplay = template.CallerDisplay ?? ""
    };
}

public sealed class FileIncidentTemplateStore : IIncidentTemplateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _folder;

    public FileIncidentTemplateStore(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            throw new ArgumentException("Enter a folder for incident templates.", nameof(folder));
        _folder = folder;
    }

    public static FileIncidentTemplateStore InApplicationData() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ServiceNowDesk"));

    public IReadOnlyList<IncidentTemplate> List()
    {
        try
        {
            var path = Path.Combine(_folder, "incident-templates.json");
            if (!File.Exists(path))
                return [];
            var templates = JsonSerializer.Deserialize<List<IncidentTemplate>>(File.ReadAllText(path)) ?? [];
            return templates
                .Where(template => !string.IsNullOrWhiteSpace(template.Name))
                .Select(template =>
                {
                    template.Name = template.Name.Trim();
                    return template;
                })
                .OrderBy(template => template.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public void Save(IncidentTemplate template)
    {
        var items = List().ToList();
        var store = new MemoryIncidentTemplateStore();
        foreach (var item in items)
            store.Save(item);
        store.Save(template);
        Write(store.List());
    }

    public void Delete(string name)
    {
        var store = new MemoryIncidentTemplateStore();
        foreach (var item in List())
            store.Save(item);
        store.Delete(name);
        Write(store.List());
    }

    private void Write(IReadOnlyList<IncidentTemplate> templates)
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "incident-templates.json");
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(templates, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }
}
