using System.Globalization;
using System.Text.Json;

namespace ServiceNowDesk.Mapping;

public static class KnowledgeStats
{
    public const string PublishedQuery = "workflow_state=published^active=true";

    public static string Caption(int count, bool practiceData)
    {
        var text = "Published articles on this instance: " + count.ToString(CultureInfo.InvariantCulture);
        return practiceData ? text + " (practice data)" : text;
    }

    public static int? ReadCount(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            return ReadCount(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static int? ReadCount(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("stats", out var stats)
            || stats.ValueKind != JsonValueKind.Object
            || !stats.TryGetProperty("count", out var count))
        {
            return null;
        }

        return count.ValueKind switch
        {
            JsonValueKind.Number when count.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(count.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }
}
