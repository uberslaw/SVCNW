namespace ServiceNowDesk.Client;

/// <summary>
/// Receiving looks up the serial text exactly, case-insensitive.
/// A leading <c>5C</c> is only a hint that extra characters may be in front of an HP serial.
/// </summary>
public static class HardwareSerial
{
    public const string HpPrefixHint = "This looks like an HP serial with extra characters in front of 5C.";

    public static bool HasJunkBeforeHpPrefix(string? text)
    {
        var value = text ?? "";
        if (value.Length == 0)
            return false;
        if (value.StartsWith("5C", StringComparison.OrdinalIgnoreCase))
            return false;
        return value.IndexOf("5C", StringComparison.OrdinalIgnoreCase) > 0;
    }

    public static string HintFor(string? text) =>
        HasJunkBeforeHpPrefix(text) ? HpPrefixHint : "";
}
