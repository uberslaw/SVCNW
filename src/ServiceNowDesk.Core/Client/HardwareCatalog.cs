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

    public static string ListQuery(string? text)
    {
        var term = EncodedQuery.Sanitize(text);
        const string computers = "model_category.name=Computer";
        if (term.Length == 0)
            return computers + "^ORDERBYserial_number";

        return string.Join("^NQ",
        [
            computers + "^serial_numberLIKE" + term,
            computers + "^model.nameLIKE" + term,
            computers + "^assigned_to.nameLIKE" + term
        ]);
    }
}
