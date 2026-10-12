using System.Text;
using System.Text.Json;

namespace ServiceNowDesk.Client;

public static class DeskListScope
{
    public const string Practice = "practice";

    public static string ForInstance(Uri instanceUri)
    {
        var host = instanceUri.Host.ToLowerInvariant();
        foreach (var ch in Path.GetInvalidFileNameChars())
            host = host.Replace(ch, '_');
        return host.Length == 0 ? "instance" : host;
    }
}

public sealed class CachedTicketRow
{
    public string SysId { get; set; } = "";
    public string Number { get; set; } = "";
    public string Title { get; set; } = "";
    public string StateLabel { get; set; } = "";
    public string Tone { get; set; } = "";
    public string Meta { get; set; } = "";
    public string When { get; set; } = "";
    public string Badge { get; set; } = "";
    public string Location { get; set; } = "";
    public string SortKey { get; set; } = "";
    public bool Unassigned { get; set; }
    public string AssignmentGroupId { get; set; } = "";
    public string AssignmentGroupDisplay { get; set; } = "";
    public string AssigneeId { get; set; } = "";
    public string AssigneeDisplay { get; set; } = "";
    public string OpenedAtDisplay { get; set; } = "";
}

public sealed class CachedTicketList
{
    public DateTimeOffset CapturedAt { get; set; }
    public int TotalCount { get; set; }
    public List<CachedTicketRow> Items { get; set; } = [];

    /// <summary>Encoded query used for the last successful list download.</summary>
    public string? EncodedQuery { get; set; }

    /// <summary>Human-readable active filters at download time.</summary>
    public string? FilterSummary { get; set; }

    /// <summary>
    /// Leads people this download was for. Empty on My Tickets.
    /// A My Team file with a different set is not the current team.
    /// </summary>
    public List<string>? TeamMemberIds { get; set; }

    /// <summary>True when the encoded query included a Settings region location clause.</summary>
    public bool FilteredByRegion { get; set; }
}

public sealed class DeskListSnapshot
{
    public DateTimeOffset ChoicesCapturedAt { get; set; }
    public DateTimeOffset GroupsCapturedAt { get; set; }
    public DateTimeOffset MembersCapturedAt { get; set; }
    public DateTimeOffset ServiceOfferingsCapturedAt { get; set; }
    public DateTimeOffset ConfigurationItemsCapturedAt { get; set; }
    public CachedTicketList? Incidents { get; set; }
    public CachedTicketList? Requests { get; set; }
    public CachedTicketList? RequestItems { get; set; }
    public CachedTicketList? WalkUps { get; set; }
    public CachedTicketList? Knowledge { get; set; }

    /// <summary>Open incidents assigned to the selected Leads people. Null when that population was not downloaded.</summary>
    public CachedTicketList? IncidentTeam { get; set; }

    /// <summary>Open request items assigned to the selected Leads people. Null when that population was not downloaded.</summary>
    public CachedTicketList? RequestItemTeam { get; set; }

    /// <summary>Open walk-ups assigned to the selected Leads people. Null when that population was not downloaded.</summary>
    public CachedTicketList? WalkUpTeam { get; set; }
}

public interface IDeskListStore
{
    DeskListSnapshot? Load(string scope);
    void Save(string scope, DeskListSnapshot snapshot);
    long StoredBytes(string scope);
}

public sealed class MemoryDeskListStore : IDeskListStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly Dictionary<string, string> _json = new(StringComparer.OrdinalIgnoreCase);

    public DeskListSnapshot? Load(string scope)
    {
        if (!_json.TryGetValue(scope, out var json))
            return null;
        return JsonSerializer.Deserialize<DeskListSnapshot>(json);
    }

    public void Save(string scope, DeskListSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _json[scope] = JsonSerializer.Serialize(snapshot, JsonOptions);
    }

    public long StoredBytes(string scope) =>
        _json.TryGetValue(scope, out var json) ? Encoding.UTF8.GetByteCount(json) : 0;
}

public sealed class FileDeskListStore : IDeskListStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _folder;

    public FileDeskListStore(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            throw new ArgumentException("Enter a folder for the saved lists.", nameof(folder));
        _folder = folder;
    }

    public static FileDeskListStore InApplicationData() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ServiceNowDesk"));

    public DeskListSnapshot? Load(string scope)
    {
        try
        {
            var path = PathFor(scope);
            if (!File.Exists(path))
                return null;
            return JsonSerializer.Deserialize<DeskListSnapshot>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(string scope, DeskListSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Directory.CreateDirectory(_folder);
        var path = PathFor(scope);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    public long StoredBytes(string scope)
    {
        try
        {
            var path = PathFor(scope);
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private string PathFor(string scope)
    {
        var name = string.IsNullOrWhiteSpace(scope) ? "instance" : scope.Trim().ToLowerInvariant();
        foreach (var ch in Path.GetInvalidFileNameChars())
            name = name.Replace(ch, '_');
        return Path.Combine(_folder, "lists." + name + ".json");
    }
}
