namespace ServiceNowDesk.Alerts;

/// <summary>
/// Legend slider curve, positions <see cref="Minimum"/>–<see cref="Maximum"/>.
/// 0 is a light pastel. Saturation keeps rising through <see cref="FullColour"/>,
/// where the colour is fully saturated and brighter than the old plateau near 61.
/// The last tenth of the bar only darkens that full colour slightly.
/// Hue stays with the original swatch. Saved positions use <see cref="Version"/>; older lightness numbers are ignored.
/// </summary>
public static class LegendColorIntensity
{
    public const int Version = 2;
    public const int Minimum = 0;
    public const int Maximum = 100;

    /// <summary>
    /// Full colour, nine tenths of the way along. Positions after this are the last tenth and only darken slightly.
    /// </summary>
    public const int FullColour = 90;

    private const double PastelSaturation = 0.28;
    private const double PastelLightness = 0.90;
    private const double FullSaturation = 1.00;
    private const double FullLightness = 0.50;
    private const double EndLightness = 0.46;

    public const string NeutralTrackStart = "#E6E6E6";
    public const string NeutralTrackFull = "#808080";
    public const string NeutralTrackEnd = "#757575";

    /// <summary>Normal list text, the same value as TextColor in the desk theme.</summary>
    private const double MinimumRowContrast = 4.5;

    private static readonly (byte Red, byte Green, byte Blue) RowText = (0x1C, 0x25, 0x29);

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

    /// <summary>
    /// Saturation and lightness at <paramref name="position"/>, before the swatch hue is applied.
    /// 0 is pastel. Through <see cref="FullColour"/> the colour grows stronger and stays at least as light as full colour.
    /// The last tenth keeps full saturation and darkens that colour slightly. It does not fade toward grey.
    /// </summary>
    public static Hsl Sample(int position)
    {
        position = Clamp(position);
        if (position >= FullColour)
        {
            var tail = (position - FullColour) / (double)(Maximum - FullColour);
            return new Hsl(0d, FullSaturation, Lerp(FullLightness, EndLightness, tail));
        }

        var rise = position / (double)FullColour;
        var saturationSpan = 1d - (1d - rise) * (1d - rise);
        return new Hsl(
            0d,
            Lerp(PastelSaturation, FullSaturation, saturationSpan),
            Lerp(PastelLightness, FullLightness, rise));
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
        var lightness = LightnessForReadableRow(hue, sample.Saturation, sample.Lightness);
        return FromHsl(hue, sample.Saturation, lightness);
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

    /// <summary>
    /// Keeps the lightened row at or above about 4.5:1 with normal text.
    /// Darkening stops just on the readable side of that line.
    /// </summary>
    private static double LightnessForReadableRow(double hue, double saturation, double lightness)
    {
        if (RowContrast(hue, saturation, lightness) >= MinimumRowContrast)
            return lightness;

        var tooDark = lightness;
        var readable = Math.Max(lightness, 0.90d);
        for (var step = 0; step < 24; step++)
        {
            var mid = (tooDark + readable) / 2d;
            if (RowContrast(hue, saturation, mid) >= MinimumRowContrast)
                readable = mid;
            else
                tooDark = mid;
        }

        return readable;
    }

    private static double RowContrast(double hue, double saturation, double lightness)
    {
        var row = Parse(HighlightCatalog.Lighten(FromHsl(hue, saturation, lightness)));
        return Contrast(row, RowText);
    }

    private static double Contrast(
        (byte Red, byte Green, byte Blue) background,
        (byte Red, byte Green, byte Blue) foreground)
    {
        var left = RelativeLuminance(background);
        var right = RelativeLuminance(foreground);
        var lighter = Math.Max(left, right);
        var darker = Math.Min(left, right);
        return (lighter + 0.05d) / (darker + 0.05d);
    }

    private static double RelativeLuminance((byte Red, byte Green, byte Blue) color) =>
        0.2126d * Linearize(color.Red) + 0.7152d * Linearize(color.Green) + 0.0722d * Linearize(color.Blue);

    private static double Linearize(byte channel)
    {
        var unit = channel / 255d;
        return unit <= 0.04045d
            ? unit / 12.92d
            : Math.Pow((unit + 0.055d) / 1.055d, 2.4d);
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
