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

        return HardwareTextFilter.Matches(asset.SerialNumber, term)
            || HardwareTextFilter.Matches(asset.Model, term)
            || HardwareTextFilter.Matches(asset.AssignedTo.Display, term);
    }

    /// <summary>
    /// Local column filter. See <see cref="HardwareTextFilter"/> for wildcard rules.
    /// </summary>
    public static bool MatchesColumn(string? value, string? filter) =>
        HardwareTextFilter.Matches(value, filter);

    public static bool MatchesLocation(HardwareAsset asset, IReadOnlyList<string>? locations)
    {
        var names = OfficeNames(locations);
        if (names.Length == 0)
            return true;

        var display = asset.Location.Display ?? "";
        return names.Any(name =>
            HardwareOfficeNames.SamePlace(display, name) || HardwareOfficeNames.LoosePlace(display, name));
    }

    /// <summary>
    /// Computer rows, restricted to the selected offices, ordered by serial.
    /// Each checked office is queried as its own name and as the same place with or without
    /// a trailing " Office", one complete clause per label joined by ^NQ. A parenthesized
    /// OR is not used: ServiceNow drops that group and the row cap then returns other cities.
    /// A quoted <c>IN</c> list is not used either: ServiceNow keeps those quote characters,
    /// so <c>location.nameIN"Brisbane","Brisbane Office"</c> matches neither stored name.
    /// When location sys_ids are known, those are preferred so the live name spelling does not matter.
    /// </summary>
    public static string ListQuery(
        string? text,
        IReadOnlyList<string>? locations = null,
        IReadOnlyList<string>? locationSysIds = null)
    {
        var term = EncodedQuery.Sanitize(text);
        var branches = LocationBranches(locations, locationSysIds).ToArray();
        if (branches.Length == 0)
            return string.Join("^NQ", TextBranches("model_category.name=Computer", term)) + "^ORDERBYserial_number";

        var parts = new List<string>();
        foreach (var branch in branches)
            parts.AddRange(TextBranches("model_category.name=Computer^" + branch, term));
        return string.Join("^NQ", parts) + "^ORDERBYserial_number";
    }

    /// <summary>
    /// Full computer download. With offices, each place is matched by sys_id, exact name,
    /// and a <c>LIKE</c> on the city stem so "Brisbane" still finds "AU Brisbane Office".
    /// Local <see cref="MatchesLocation"/> then keeps the rows that belong to the place.
    /// </summary>
    public static string DownloadQuery(
        IReadOnlyList<string>? locations = null,
        IReadOnlyList<string>? locationSysIds = null)
    {
        var branches = LocationBranches(locations, locationSysIds, includeLike: true).ToArray();
        if (branches.Length == 0)
            return "model_category.name=Computer^ORDERBYserial_number";

        var parts = branches.Select(branch => "model_category.name=Computer^" + branch);
        return string.Join("^NQ", parts) + "^ORDERBYserial_number";
    }

    private static IEnumerable<string> LocationBranches(
        IReadOnlyList<string>? locations,
        IReadOnlyList<string>? locationSysIds,
        bool includeLike = false)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in locationSysIds ?? [])
        {
            var token = EncodedQuery.Sanitize(id);
            if (token.Length == 0 || !seen.Add("id:" + token))
                continue;
            yield return "location=" + token;
        }

        var places = OfficeNames(locations);
        var labels = places
            .SelectMany(HardwareOfficeNames.FilterLabels)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var label in labels)
        {
            if (!seen.Add("name:" + label))
                continue;
            yield return "location.name=" + Quote(label);
        }

        if (!includeLike)
            yield break;

        foreach (var place in places)
        {
            var stem = HardwareOfficeNames.CityStem(place);
            if (stem.Length < 3 || !seen.Add("like:" + stem))
                continue;
            yield return "location.nameLIKE" + EncodedQuery.Sanitize(stem);
        }
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

    /// <summary>
    /// City without a trailing " Office", used for LIKE downloads and loose matching.
    /// </summary>
    public static string CityStem(string? name)
    {
        var trimmed = Normalize(name);
        if (trimmed.Length == 0)
            return "";

        const string suffix = " Office";
        if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && trimmed.Length > suffix.Length)
        {
            var stem = trimmed[..^suffix.Length].TrimEnd();
            if (stem.Length > 0)
                return stem;
        }

        return trimmed;
    }

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
    /// True when the asset location names the office city as its own place, including a
    /// longer path such as "AU Brisbane Office". "Cairns Depot" does not match Cairns.
    /// </summary>
    public static bool LoosePlace(string? locationDisplay, string? office)
    {
        var place = Normalize(locationDisplay);
        var stem = CityStem(office);
        if (place.Length == 0 || stem.Length < 3)
            return false;

        if (SamePlace(place, stem))
            return true;

        foreach (var part in place.Split(['/', '\\', ',', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (SamePlace(part, stem))
                return true;

            var index = part.IndexOf(stem, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                var beforeOk = index == 0 || !char.IsLetterOrDigit(part[index - 1]);
                var after = index + stem.Length;
                var afterOk = after >= part.Length || !char.IsLetterOrDigit(part[after]);
                if (beforeOk && afterOk)
                {
                    var rest = after < part.Length ? part[after..].Trim() : "";
                    if (rest.Length == 0 || rest.Equals("Office", StringComparison.OrdinalIgnoreCase))
                        return true;
                }

                index = part.IndexOf(stem, index + 1, StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    /// <summary>
    /// The checkbox label and the same place written with or without a trailing " Office".
    /// </summary>
    public static IReadOnlyList<string> FilterLabels(string? name)
    {
        var trimmed = Normalize(name);
        if (trimmed.Length == 0)
            return [];

        const string suffix = " Office";
        if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && trimmed.Length > suffix.Length)
        {
            var stem = trimmed[..^suffix.Length].TrimEnd();
            if (stem.Length > 0 && IsLongerOfficeName(trimmed, stem))
                return [trimmed, stem];
        }

        return [trimmed, trimmed + suffix];
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
