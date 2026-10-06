using System.Text.Json;
using ServiceNowDesk.Models;

namespace ServiceNowDesk.Client;

public static class FormCatalogPolicy
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
    public const int MaxDependentCategories = 80;
    public const int MaxCatalogItems = 30;
    public const int MaxAssignmentGroups = 500;
    public const int MaxGroupMembers = 8000;

    public static bool IsStale(DateTimeOffset capturedAt, DateTimeOffset now) =>
        capturedAt == default || now - capturedAt >= MaxAge;
}

public static class FormCatalogFields
{
    public static readonly (string Table, string Element)[] Independent =
    [
        ("incident", "state"),
        ("incident", "impact"),
        ("incident", "urgency"),
        ("incident", "priority"),
        ("incident", "category"),
        ("incident", "contact_type"),
        ("incident", "hold_reason"),
        ("incident", "close_code"),
        ("sc_request", "request_state"),
        ("sc_request", "priority"),
        ("sc_req_item", "state"),
        ("sc_req_item", "priority"),
        ("interaction", "state"),
        ("interaction", "type")
    ];
}

public sealed class CachedChoiceList
{
    public string Table { get; set; } = "";
    public string Element { get; set; } = "";
    public string DependentValue { get; set; } = "";
    public List<Choice> Choices { get; set; } = [];
}

public sealed class CachedCatalogForm
{
    public string SysId { get; set; } = "";
    public DateTimeOffset CapturedAt { get; set; }
    public List<CatalogVariableDefinition> Variables { get; set; } = [];
}

public sealed class CachedAssignmentGroup
{
    public string SysId { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class CachedGroupMember
{
    public string GroupSysId { get; set; } = "";
    public string UserSysId { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class FormCatalogSnapshot
{
    public DateTimeOffset CapturedAt { get; set; }
    public DateTimeOffset DirectoryCapturedAt { get; set; }
    public bool DirectoryComplete { get; set; }
    public bool MembersVerified { get; set; }
    public List<string> VerifiedMemberGroups { get; set; } = [];
    public List<CachedChoiceList> Choices { get; set; } = [];
    public List<CachedCatalogForm> CatalogItems { get; set; } = [];
    public List<CachedAssignmentGroup> Groups { get; set; } = [];
    public List<CachedGroupMember> Members { get; set; } = [];
}

public interface IFormCatalogStore
{
    FormCatalogSnapshot? Load(Uri instanceUri);
    void Save(Uri instanceUri, FormCatalogSnapshot snapshot);
}

public sealed class FileFormCatalogStore : IFormCatalogStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _folder;

    public FileFormCatalogStore(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            throw new ArgumentException("Enter a folder for the form catalog.", nameof(folder));
        _folder = folder;
    }

    public static FileFormCatalogStore InApplicationData() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ServiceNowDesk"));

    public FormCatalogSnapshot? Load(Uri instanceUri)
    {
        try
        {
            var path = PathFor(instanceUri);
            if (!File.Exists(path))
                return null;
            return JsonSerializer.Deserialize<FormCatalogSnapshot>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(Uri instanceUri, FormCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Directory.CreateDirectory(_folder);
        var path = PathFor(instanceUri);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    private string PathFor(Uri instanceUri)
    {
        var host = instanceUri.Host.ToLowerInvariant();
        foreach (var ch in Path.GetInvalidFileNameChars())
            host = host.Replace(ch, '_');
        return Path.Combine(_folder, "form-catalog." + host + ".json");
    }
}
