using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ServiceNowDesk.Alerts;
using ServiceNowDesk.ViewModels;

namespace ServiceNowDesk.Converters;

public sealed class BoolVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value is true;
        if (Invert)
            visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class StringVisibilityConverter : IValueConverter
{
    public bool WhenEmpty { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var empty = string.IsNullOrWhiteSpace(value as string);
        var visible = WhenEmpty ? empty : !empty;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class SectionVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true && parameter is string name && targetType.IsEnum)
            return Enum.Parse(targetType, name);
        return Binding.DoNothing;
    }
}

public sealed class AlertKindBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var kind = value is AlertKind alert ? alert : AlertKind.AssignedToMe;
        var swatch = AlertCatalog.Swatch(kind);
        if (Application.Current?.TryFindResource(swatch.ResourceKey) is Brush brush)
            return brush;

        var color = (Color)ColorConverter.ConvertFromString(swatch.Hex);
        var created = new SolidColorBrush(color);
        created.Freeze();
        return created;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class AlertRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? StandardTemplate { get; set; }

    public DataTemplate? AssignedTemplate { get; set; }

    public DataTemplate? SlaTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is not AlertRow row)
            return StandardTemplate;
        if (row.Kind == AlertKind.SlaBreaching)
            return SlaTemplate;
        if (row.Kind == AlertKind.AssignedToMe)
            return AssignedTemplate ?? StandardTemplate;
        return StandardTemplate;
    }
}

public sealed class HexBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, SolidColorBrush> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hex = value as string;
        if (string.IsNullOrWhiteSpace(hex))
            return Brushes.Transparent;

        if (Cache.TryGetValue(hex, out var cached))
            return cached;

        try
        {
            if (ColorConverter.ConvertFromString(hex) is not Color color)
                return Brushes.Transparent;
            var created = new SolidColorBrush(color);
            created.Freeze();
            Cache[hex] = created;
            return created;
        }
        catch (FormatException)
        {
            return Brushes.Transparent;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class LegendTrackBrushConverter : IMultiValueConverter
{
    private static readonly LinearGradientBrush Neutral = Build(
        LegendColorIntensity.NeutralTrackStart,
        null,
        LegendColorIntensity.NeutralTrackEnd);

    private static readonly Dictionary<string, LinearGradientBrush> Hues = new(StringComparer.OrdinalIgnoreCase);

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var individual = values.Length > 0 && values[0] is true;
        var hex = values.Length > 1 ? values[1] as string : null;
        if (!individual || string.IsNullOrWhiteSpace(hex))
            return Neutral;

        if (Hues.TryGetValue(hex, out var cached))
            return cached;

        try
        {
            var brush = Build(
                LegendColorIntensity.Curve(hex, LegendColorIntensity.Minimum),
                LegendColorIntensity.Curve(hex, LegendColorIntensity.Vivid),
                LegendColorIntensity.Curve(hex, LegendColorIntensity.Maximum));
            Hues[hex] = brush;
            return brush;
        }
        catch (ArgumentException)
        {
            return Neutral;
        }
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static LinearGradientBrush Build(string start, string? vivid, string end)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0)
        };
        brush.GradientStops.Add(new GradientStop(Stop(start), 0));
        if (vivid is not null)
            brush.GradientStops.Add(new GradientStop(Stop(vivid), LegendColorIntensity.Vivid / 100d));
        brush.GradientStops.Add(new GradientStop(Stop(end), 1));
        brush.Freeze();
        return brush;
    }

    private static Color Stop(string hex) =>
        (Color)ColorConverter.ConvertFromString(hex);
}

public sealed class EqualsMultiConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length == 2 && Equals(values[0], values[1]);

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
