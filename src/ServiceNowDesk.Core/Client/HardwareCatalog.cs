using ServiceNowDesk.Models;
using ServiceNowDesk.Query;

namespace ServiceNowDesk.Client;

/// <summary>
/// Confirmed alm_hardware install_status labels. Sample data stores the label as the value.
/// Live updates send the sys_choice value once those choices have loaded.
/// </summary>
public static class HardwareCatalog
{
    public const string InStock = "In stock";
    public const string InTransit = "In transit";
    public const string InUse = "In use";
    public const string Retired = "Retired";
    public const string Missing = "Missing";
    public const string InMaintenance = "In maintenance";
    public const string Available = "Available";
    public const string Computer = "Computer";

    /// <summary>
    /// City seeds offered before ServiceNow has been asked. They are not a saved default.
    /// A seed that names the same place as a location record ("Brisbane" and "Brisbane Office")
    /// is collapsed to the name stored on that location.
    /// </summary>
    public static readonly string[] KnownOfficeNames =
    [
        "Brisbane",
        "Maroochydore",
        "Gold Coast",
        "Townsville",
        "Cairns"
    ];

    public static IReadOnlyList<Choice> InstallStatuses { get; } =
    [
        new(InStock, InStock),
        new(InTransit, InTransit),
        new(InUse, InUse),
        new(Retired, Retired),
        new(Missing, Missing),
        new(InMaintenance, InMaintenance)
    ];

    public static bool SameLabel(string? left, string? right) =>
        string.Equals((left ?? "").Trim(), (right ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    public static string LabelOf(IEnumerable<Choice> choices, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var match = choices.FirstOrDefault(choice => choice.Value == value);
        if (match is not null && !string.IsNullOrWhiteSpace(match.Label))
            return match.Label;

        match = choices.FirstOrDefault(choice => SameLabel(choice.Label, value));
        return match?.Label ?? value;
    }

    public static string ValueForLabel(IEnumerable<Choice> choices, string label, string fallback)
    {
        var match = choices.FirstOrDefault(choice => SameLabel(choice.Label, label) && !string.IsNullOrEmpty(choice.Value));
        return match?.Value ?? fallback;
    }

    public static IReadOnlyList<Choice> SubstatesForLabel(string? stateLabel)
    {
        if (SameLabel(stateLabel, InStock))
        {
            return
            [
                new("Available", "Available"),
                new("Pending transfer", "Pending transfer"),
                new("Legal hold", "Legal hold")
            ];
        }

        if (SameLabel(stateLabel, Retired))
        {
            return
            [
                new("", "None"),
                new("Disposed", "Disposed"),
                new("Donated", "Donated"),
                new("Pending disposal", "Pending disposal"),
                new("RMA", "RMA")
            ];
        }

        if (SameLabel(stateLabel, Missing))
        {
            return
            [
                new("", "None"),
                new("Lost", "Lost"),
                new("Stolen", "Stolen"),
                new("Not returned by leaver", "Not returned by leaver")
            ];
        }

        return [];
    }

    public static IReadOnlyList<Choice> RealSubstates(string? stateLabel) =>
        SubstatesForLabel(stateLabel).Where(choice => !string.IsNullOrEmpty(choice.Value)).ToArray();

    public static bool RequiresStockroom(string? stateLabel) => SameLabel(stateLabel, InStock);

    public static bool RequiresLocation(string? stateLabel) =>
        SameLabel(stateLabel, InStock) || SameLabel(stateLabel, Missing) || SameLabel(stateLabel, Retired);

    public static bool IsInStock(string? label, string? value, IEnumerable<Choice> statuses)
    {
        if (SameLabel(label, InStock))
            return true;
        return SameLabel(LabelOf(statuses, value), InStock);
    }

    public static bool IsComputer(HardwareAsset asset) =>
        string.IsNullOrWhiteSpace(asset.ModelCategory) || SameLabel(asset.ModelCategory, Computer);

    public static bool MatchesSearch(HardwareAsset asset, string? text)
    {
        if (!IsComputer(asset))
            return false;

        var term = (text ?? "").Trim();
        if (term.Length == 0)
            return true;

        return Contains(asset.SerialNumber, term)
            || Contains(asset.Model, term)
            || Contains(asset.AssignedTo.Display, term);
    }

    private static bool Contains(string? value, string term) =>
        !string.IsNullOrEmpty(value) && value.Contains(term, StringComparison.OrdinalIgnoreCase);

    public static bool MatchesLocation(HardwareAsset asset, IReadOnlyList<string>? locations)
    {
        var names = OfficeNames(locations);
        if (names.Length == 0)
            return true;

        var display = asset.Location.Display ?? "";
        return names.Any(name => display.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Computer rows, restricted to the selected location names, ordered by serial.
    /// Each office is its own complete clause joined with ^NQ. A parenthesized OR is not
    /// used: ServiceNow drops that group, then sysparm_limit returns the first computers
    /// in serial order from every city.
    /// </summary>
    public static string ListQuery(string? text, IReadOnlyList<string>? locations = null)
    {
        var term = EncodedQuery.Sanitize(text);
        var places = OfficeNames(locations);
        var branches = new List<string>();
        if (places.Length == 0)
            branches.AddRange(TextBranches("model_category.name=Computer", term));
        else
        {
            foreach (var place in places)
                branches.AddRange(TextBranches("model_category.name=Computer^location.nameLIKE" + Quote(place), term));
        }

        return string.Join("^NQ", branches) + "^ORDERBYserial_number";
    }

    private static IEnumerable<string> TextBranches(string scope, string term)
    {
        if (term.Length == 0)
        {
            yield return scope;
            yield break;
        }

        yield return scope + "^serial_numberLIKE" + term;
        yield return scope + "^model.nameLIKE" + term;
        yield return scope + "^assigned_to.nameLIKE" + term;
    }

    private static string[] OfficeNames(IReadOnlyList<string>? locations) =>
        (locations ?? [])
            .Select(EncodedQuery.Sanitize)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string Quote(string value)
    {
        var escaped = value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        return "\"" + escaped + "\"";
    }
}

/// <summary>
/// Where a hardware office label was first seen. Asset locations outrank the location
/// table, which outranks the hardcoded city seeds.
/// </summary>
public enum HardwareOfficeLabelKind
{
    Seed = 0,
    Account = 1,
    Reference = 2,
    Asset = 3
}

/// <summary>
/// One checkbox per place. "Brisbane" and "Brisbane Office" are the same office.
/// </summary>
public static class HardwareOfficeNames
{
    public static string Normalize(string? name) => (name ?? "").Trim();

    public static bool SamePlace(string? left, string? right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        if (a.Length == 0 || b.Length == 0)
            return false;

        if (a.Equals(b, StringComparison.OrdinalIgnoreCase))
            return true;

        return IsLongerOfficeName(a, b) || IsLongerOfficeName(b, a);
    }

    /// <summary>
    /// True when <paramref name="longer"/> is <paramref name="shorter"/> plus " Office".
    /// </summary>
    public static bool IsLongerOfficeName(string? longer, string? shorter)
    {
        var full = Normalize(longer);
        var stem = Normalize(shorter);
        if (full.Length == 0 || stem.Length == 0 || full.Length <= stem.Length)
            return false;

        const string suffix = " Office";
        if (!full.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return false;

        var without = full[..^suffix.Length].TrimEnd();
        return without.Equals(stem, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether <paramref name="incoming"/> should replace the label already on the checkbox.
    /// The name stored on a hardware asset wins. Otherwise a ServiceNow location name wins
    /// over a hardcoded seed, and within that tier the "Office" form wins over the bare city.
    /// </summary>
    public static bool UseIncomingLabel(
        string current,
        HardwareOfficeLabelKind currentKind,
        string incoming,
        HardwareOfficeLabelKind incomingKind)
    {
        if (Normalize(current).Equals(Normalize(incoming), StringComparison.Ordinal))
            return false;

        var currentTier = Tier(currentKind);
        var incomingTier = Tier(incomingKind);
        if (incomingTier > currentTier)
            return true;
        if (incomingTier < currentTier)
            return false;

        return incomingTier > 0 && IsLongerOfficeName(incoming, current);
    }

    public static int Tier(HardwareOfficeLabelKind kind) => kind switch
    {
        HardwareOfficeLabelKind.Asset => 2,
        HardwareOfficeLabelKind.Seed => 0,
        _ => 1
    };
}
