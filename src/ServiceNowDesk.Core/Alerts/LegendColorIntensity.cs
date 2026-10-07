namespace ServiceNowDesk.Alerts;

/// <summary>
/// Washes a legend swatch or row tint toward white. 100 keeps today's hex. 0 is white.
/// </summary>
public static class LegendColorIntensity
{
    public const int Full = 100;
    public const int Minimum = 0;

    public static int Clamp(int value) => Math.Clamp(value, Minimum, Full);

    public static string Apply(string hex, int intensity)
    {
        var text = Normalize(hex);
        intensity = Clamp(intensity);
        if (intensity == Full)
            return text;

        var red = Convert.ToByte(text.Substring(1, 2), 16);
        var green = Convert.ToByte(text.Substring(3, 2), 16);
        var blue = Convert.ToByte(text.Substring(5, 2), 16);
        var keep = intensity / 100d;
        var wash = 1d - keep;
        return $"#{Channel(red, keep, wash):X2}{Channel(green, keep, wash):X2}{Channel(blue, keep, wash):X2}";
    }

    private static byte Channel(byte channel, double keep, double wash) =>
        (byte)Math.Round(channel * keep + 255d * wash, MidpointRounding.AwayFromZero);

    private static string Normalize(string hex)
    {
        var text = (hex ?? "").Trim();
        if (text.StartsWith('#'))
            text = text[1..];
        if (text.Length != 6 || !text.All(Uri.IsHexDigit))
            throw new ArgumentException("Legend color must be a 6-digit hex value.", nameof(hex));
        return "#" + text.ToUpperInvariant();
    }
}
