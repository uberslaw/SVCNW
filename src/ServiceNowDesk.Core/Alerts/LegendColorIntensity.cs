namespace ServiceNowDesk.Alerts;

/// <summary>
/// Legend slider curve. Position 0 is a light wash, 70 is fully saturated, and 100 is a step darker.
/// Hue stays with the original swatch. Saved positions use <see cref="Version"/>; older lightness numbers are ignored.
/// </summary>
public static class LegendColorIntensity
{
    public const int Version = 2;
    public const int Minimum = 0;
    public const int Maximum = 100;
    public const int Vivid = 70;

    public const string NeutralTrackStart = "#E6E6E6";
    public const string NeutralTrackEnd = "#1A1A1A";

    public readonly record struct Hsl(double Hue, double Saturation, double Lightness);

    public static int Clamp(int value) => Math.Clamp(value, Minimum, Maximum);

    public static int Average(IEnumerable<int> positions)
    {
        ArgumentNullException.ThrowIfNull(positions);
        var count = 0;
        var total = 0;
        foreach (var position in positions)
        {
            total += Clamp(position);
            count++;
        }

        if (count == 0)
            return Minimum;

        return Clamp((int)Math.Round(total / (double)count, MidpointRounding.AwayFromZero));
    }

    /// <summary>Saturation and lightness at <paramref name="position"/>, before the swatch hue is applied.</summary>
    public static Hsl Sample(int position)
    {
        position = Clamp(position);
        if (position <= Vivid)
        {
            var span = position / (double)Vivid;
            return new Hsl(0d, Lerp(0.20d, 1.00d, span), Lerp(0.90d, 0.46d, span));
        }

        var tail = (position - Vivid) / (double)(Maximum - Vivid);
        return new Hsl(0d, 1.00d, Lerp(0.46d, 0.32d, tail));
    }

    public static Hsl Describe(string hex)
    {
        var (hue, saturation, lightness) = ToHsl(Parse(Normalize(hex)));
        return new Hsl(hue, saturation, lightness);
    }

    /// <summary>Paints <paramref name="originalHex"/> at <paramref name="position"/> on the curve.</summary>
    public static string Curve(string originalHex, int position)
    {
        var (hue, _, _) = ToHsl(Parse(Normalize(originalHex)));
        var sample = Sample(position);
        return FromHsl(hue, sample.Saturation, sample.Lightness);
    }

    /// <summary>Whole-number position whose curve colour is nearest the original swatch in RGB.</summary>
    public static int Closest(string originalHex)
    {
        var text = Normalize(originalHex);
        var target = Parse(text);
        var best = Minimum;
        var bestDistance = long.MaxValue;
        for (var position = Minimum; position <= Maximum; position++)
        {
            var sample = Parse(Curve(text, position));
            var distance = Square(sample.Red - target.Red)
                + Square(sample.Green - target.Green)
                + Square(sample.Blue - target.Blue);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = position;
            }
        }

        return best;
    }

    /// <summary>Light row tint for an applied curve colour. An unapplied position keeps today's row hex.</summary>
    public static string Row(string originalSwatch, string originalRow, int position, bool applied)
    {
        if (!applied)
            return Normalize(originalRow);

        return HighlightCatalog.Lighten(Curve(originalSwatch, position));
    }

    private static double Lerp(double start, double end, double span) => start + (end - start) * span;

    private static long Square(int value) => (long)value * value;

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
