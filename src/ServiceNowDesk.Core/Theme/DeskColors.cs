namespace ServiceNowDesk.Theme;

/// <summary>
/// Shared desk chrome colours. Keep <c>DeskTheme.xaml</c> in sync with these hex values.
/// </summary>
public static class DeskColors
{
    /// <summary>Primary accent (dark teal/green).</summary>
    public const string Accent = "#0A635C";

    /// <summary>
    /// Selected preset chip: <see cref="Accent"/> blended 30% toward white.
    /// </summary>
    public const string PresetActive = "#54928D";

    /// <summary>Inactive preset / Create new chip fill (card white).</summary>
    public const string PresetIdle = "#FFFFFF";

    /// <summary>Blend each RGB channel toward white by <paramref name="amount"/> (0–1).</summary>
    public static string BlendTowardWhite(string hex, double amount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(amount, 1);

        var text = hex.Trim();
        if (text.StartsWith('#'))
            text = text[1..];
        if (text.Length != 6)
            throw new ArgumentException("Color must be a 6-digit hex value.", nameof(hex));

        var red = Convert.ToByte(text[..2], 16);
        var green = Convert.ToByte(text.Substring(2, 2), 16);
        var blue = Convert.ToByte(text.Substring(4, 2), 16);
        return $"#{Mix(red, amount):X2}{Mix(green, amount):X2}{Mix(blue, amount):X2}";
    }

    private static byte Mix(byte channel, double amount) =>
        (byte)Math.Round(channel + (255 - channel) * amount, MidpointRounding.AwayFromZero);
}
