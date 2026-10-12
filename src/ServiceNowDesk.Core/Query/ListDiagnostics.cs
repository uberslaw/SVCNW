using System.Globalization;
using System.Text;

namespace ServiceNowDesk.Query;

/// <summary>
/// Plain-text handoff for a list that looks wrong.
/// Ticket descriptions, journal text, and auth material are not included.
/// </summary>
public sealed record ListDiagnosticSnapshot(
    string Section,
    string Preset,
    bool TeamSelected,
    int TeamCount,
    IReadOnlyList<string> TeamLabels,
    int RegionOfficeCount,
    bool FilteredByRegion,
    string CachePopulation,
    int CacheRowsStored,
    int RowsShown,
    string? EncodedQuery,
    string? QuerySkippedReason,
    string? Error);

public static class ListDiagnostics
{
    public static string Format(ListDiagnosticSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var text = new StringBuilder();
        text.Append("Section: ").AppendLine(Clean(snapshot.Section));
        text.Append("Preset: ").AppendLine(Clean(snapshot.Preset));
        text.Append("Team selected: ").AppendLine(snapshot.TeamSelected ? "yes" : "no");
        text.Append("Team count: ").AppendLine(Math.Max(0, snapshot.TeamCount).ToString(CultureInfo.InvariantCulture));
        text.Append("Team: ").AppendLine(Labels(snapshot.TeamLabels));
        text.Append("Region offices: ").AppendLine(Math.Max(0, snapshot.RegionOfficeCount).ToString(CultureInfo.InvariantCulture));
        text.Append("Region filter: ").AppendLine(snapshot.FilteredByRegion ? "yes" : "no");
        text.Append("Cache population: ").AppendLine(string.IsNullOrWhiteSpace(snapshot.CachePopulation) ? "none" : Clean(snapshot.CachePopulation));
        text.Append("Cache rows stored: ").AppendLine(Math.Max(0, snapshot.CacheRowsStored).ToString(CultureInfo.InvariantCulture));
        text.Append("Rows shown: ").AppendLine(Math.Max(0, snapshot.RowsShown).ToString(CultureInfo.InvariantCulture));
        text.Append("Query: ").AppendLine(QueryLine(snapshot.EncodedQuery, snapshot.QuerySkippedReason));
        if (!string.IsNullOrWhiteSpace(snapshot.Error))
            text.Append("Error: ").AppendLine(Clean(snapshot.Error));
        return text.ToString().TrimEnd();
    }

    private static string Labels(IReadOnlyList<string>? labels)
    {
        if (labels is null || labels.Count == 0)
            return "none";
        var names = new List<string>();
        foreach (var label in labels)
        {
            var cleaned = Clean(label);
            if (cleaned.Length > 0)
                names.Add(cleaned);
        }

        return names.Count == 0 ? "none" : string.Join(", ", names);
    }

    private static string QueryLine(string? encodedQuery, string? skippedReason)
    {
        if (!string.IsNullOrWhiteSpace(skippedReason))
            return "not sent — " + Clean(skippedReason);
        var query = Clean(encodedQuery);
        return query.Length == 0 ? "not sent" : query;
    }

    private static string Clean(string? value) =>
        (value ?? "").Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
}
