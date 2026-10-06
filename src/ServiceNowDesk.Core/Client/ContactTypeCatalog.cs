using ServiceNowDesk.Models;

namespace ServiceNowDesk.Client;

/// <summary>
/// Channel on the incident form is the contact_type field.
/// Direct is the default for a new incident. An instance value is kept when ServiceNow returns it.
/// </summary>
public static class ContactTypeCatalog
{
    public const string DirectLabel = "Direct";
    public const string DirectValue = "direct";

    public static bool IsDirect(Choice? choice)
    {
        if (choice is null)
            return false;
        if (string.Equals(choice.Label?.Trim(), DirectLabel, StringComparison.OrdinalIgnoreCase))
            return true;
        return string.Equals(choice.Value?.Trim(), DirectValue, StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<Choice> Merge(IReadOnlyList<Choice>? choices)
    {
        var list = new List<Choice>();
        if (choices is not null)
        {
            foreach (var choice in choices)
            {
                if (choice?.Value is null)
                    continue;
                if (list.Any(existing => existing.Value == choice.Value))
                    continue;
                list.Add(choice);
            }
        }

        if (!list.Any(IsDirect))
            list.Insert(0, new Choice(DirectValue, DirectLabel));
        return list;
    }

    public static string DefaultValue(IEnumerable<Choice>? choices)
    {
        var labeled = choices?.FirstOrDefault(choice =>
            string.Equals(choice.Label?.Trim(), DirectLabel, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(labeled?.Value))
            return labeled.Value;

        var valued = choices?.FirstOrDefault(choice =>
            string.Equals(choice.Value?.Trim(), DirectValue, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(valued?.Value))
            return valued.Value;

        return DirectValue;
    }
}
