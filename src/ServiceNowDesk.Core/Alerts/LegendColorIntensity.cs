namespace ServiceNowDesk.Alerts;

/// <summary>
/// Legend lightness on a white–black scale. 0 is white and 100 is black.
/// Hue and saturation stay with the original swatch. A missing saved value uses the measured percent.
/// </summary>
public static class LegendColorIntensity
{
    public const int White = 0;
    public const int Black = 100;

    public static int Clamp(int value) => Math.Clamp(value, White, Black);

    /// <summary>How far <paramref name="hex"/> sits from white toward black, as a whole percent.</summary>
    public static int Measure(string hex)
    {
        var (red, green, blue) = Parse(Normalize(hex));
        var lightness = Lightness(red, green, blue);
        var towardBlack = (1d - lightness) * 100d;
        return Clamp((int)Math.Round(towardBlack, MidpointRounding.AwayFromZero));
    }

    public static int Average(IEnumerable<int> percents)
    {
        ArgumentNullException.ThrowIfNull(percents);
        var count = 0;
        var total = 0;
        foreach (var percent in percents)
        {
            total += Clamp(percent);
            count++;
        }

        if (count == 0)
            return White;

        return Clamp((int)Math.Round(total / (double)count, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// Sets the original swatch to <paramref name="percent"/> lightness. The measured percent returns the original hex.
    /// </summary>
    public static string Swatch(string originalHex, int percent)
    {
        var text = Normalize(originalHex);
        percent = Clamp(percent);
        if (Measure(text) == percent)
            return text;

        var (hue, saturation, _) = ToHsl(Parse(text));
        return FromHsl(hue, saturation, 1d - percent / 100d);
    }

    /// <summary>
    /// Row tint for a swatch at <paramref name="percent"/>. The measured percent keeps today's row hex.
    /// </summary>
    public static string Row(string originalSwatch, string originalRow, int percent)
    {
        var swatch = Swatch(originalSwatch, percent);
        if (string.Equals(swatch, Normalize(originalSwatch), StringComparison.OrdinalIgnoreCase))
            return Normalize(originalRow);

        return HighlightCatalog.Lighten(swatch);
    }

    private static double Lightness(byte red, byte green, byte blue)
    {
        var (_, _, lightness) = ToHsl((red, green, blue));
        return lightness;
    }

    private static (double Hue, double Saturation, double Lightness) ToHsl((byte Red, byte Green, byte Blue) color)
    {
        var red = color.Red / 255d;
        var green = color.Green / 255d;
        var blue = color.Blue / 255d;
        var max = Math.Max(red, Math.Max(green, blue));
        var min = Math.Min(red, Math.Min(green, blue));
        var lightness = (max + min) / 2d;
        if (max == min)
            return (0d, 0d, lightness);

        var delta = max - min;
        var saturation = lightness > 0.5d ? delta / (2d - max - min) : delta / (max + min);
        double hue;
        if (max == red)
            hue = (green - blue) / delta + (green < blue ? 6d : 0d);
        else if (max == green)
            hue = (blue - red) / delta + 2d;
        else
            hue = (red - green) / delta + 4d;

        return (hue / 6d, saturation, lightness);
    }

    private static string FromHsl(double hue, double saturation, double lightness)
    {
        if (saturation == 0d)
        {
            var grey = Channel(lightness);
            return $"#{grey:X2}{grey:X2}{grey:X2}";
        }

        var q = lightness < 0.5d
            ? lightness * (1d + saturation)
            : lightness + saturation - lightness * saturation;
        var p = 2d * lightness - q;
        var red = Channel(Hue(p, q, hue + 1d / 3d));
        var green = Channel(Hue(p, q, hue));
        var blue = Channel(Hue(p, q, hue - 1d / 3d));
        return $"#{red:X2}{green:X2}{blue:X2}";
    }

    private static double Hue(double p, double q, double t)
    {
        if (t < 0d)
            t += 1d;
        if (t > 1d)
            t -= 1d;
        if (t < 1d / 6d)
            return p + (q - p) * 6d * t;
        if (t < 0.5d)
            return q;
        if (t < 2d / 3d)
            return p + (q - p) * (2d / 3d - t) * 6d;
        return p;
    }

    private static byte Channel(double unit) =>
        (byte)Math.Clamp((int)Math.Round(unit * 255d, MidpointRounding.AwayFromZero), 0, 255);

    private static (byte Red, byte Green, byte Blue) Parse(string text) =>
        (
            Convert.ToByte(text.Substring(1, 2), 16),
            Convert.ToByte(text.Substring(3, 2), 16),
            Convert.ToByte(text.Substring(5, 2), 16));

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
