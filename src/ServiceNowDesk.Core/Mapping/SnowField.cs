using System.Text.Json;

namespace ServiceNowDesk.Mapping;

public readonly record struct SnowField(string Value, string Display)
{
    public static SnowField Empty { get; } = new("", "");

    public static SnowField Read(JsonElement record, string name)
    {
        if (record.ValueKind != JsonValueKind.Object || !record.TryGetProperty(name, out var element))
            return Empty;

        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return Empty;

        if (element.ValueKind == JsonValueKind.Object)
        {
            var value = element.TryGetProperty("value", out var raw) ? AsString(raw) : "";
            var display = element.TryGetProperty("display_value", out var shown) ? AsString(shown) : value;
            if (string.IsNullOrWhiteSpace(display))
                display = value;
            return new SnowField(value, display);
        }

        var text = AsString(element);
        return new SnowField(text, text);
    }

    public static string AsString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? "",
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => ""
    };

    public static bool IsTrue(SnowField field) =>
        field.Value.Equals("true", StringComparison.OrdinalIgnoreCase)
        || field.Value == "1";
}
