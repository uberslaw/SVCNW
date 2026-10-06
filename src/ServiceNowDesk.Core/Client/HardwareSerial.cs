namespace ServiceNowDesk.Client;

/// <summary>
/// Barcode scans are matched to alm_hardware.serial_number.
/// A single extra leading character is ignored only when the exact scan misses and the remainder starts with 5.
/// </summary>
public static class HardwareSerial
{
    public static string? LeadingFiveRemainder(string? scan)
    {
        var text = (scan ?? "").Trim();
        if (text.Length < 2)
            return null;

        var remainder = text[1..];
        if (remainder[0] != '5')
            return null;

        return remainder;
    }

    public static async Task<T?> FindAsync<T>(string? scan, Func<string, bool, Task<T?>> findExact) where T : class
    {
        ArgumentNullException.ThrowIfNull(findExact);
        var text = (scan ?? "").Trim();
        if (text.Length == 0)
            return null;

        var exact = await findExact(text, false).ConfigureAwait(false);
        if (exact is not null)
            return exact;

        exact = await findExact(text, true).ConfigureAwait(false);
        if (exact is not null)
            return exact;

        var remainder = LeadingFiveRemainder(text);
        if (remainder is null)
            return null;

        var stripped = await findExact(remainder, false).ConfigureAwait(false);
        if (stripped is not null)
            return stripped;

        return await findExact(remainder, true).ConfigureAwait(false);
    }
}
