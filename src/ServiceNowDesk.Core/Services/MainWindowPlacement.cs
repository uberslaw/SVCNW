using ServiceNowDesk.Models;

namespace ServiceNowDesk.Services;

/// <summary>
/// Validates and restores the main window size and position from <see cref="DeskSettings"/>.
/// Keeps WPF out of Core: callers pass a work-area rectangle and apply the result.
/// </summary>
public static class MainWindowPlacement
{
    public const double DefaultWidth = 1280;
    public const double DefaultHeight = 800;
    public const double MinWidth = 1024;
    public const double MinHeight = 640;

    /// <summary>How many pixels of the window must stay on the work area when nudging back on screen.</summary>
    public const double VisibleEdge = 48;

    public const string StateNormal = "Normal";
    public const string StateMaximized = "Maximized";

    public readonly record struct Rect(double Left, double Top, double Width, double Height);

    public readonly record struct Placement(double Left, double Top, double Width, double Height, bool Maximized);

    /// <summary>
    /// Builds a placement for the current work area from saved settings.
    /// Missing or unusable width/height fall back to the default size, centred.
    /// Off-screen coordinates are nudged so at least <see cref="VisibleEdge"/> pixels remain visible.
    /// </summary>
    public static Placement Resolve(DeskSettings? settings, Rect workArea)
    {
        var area = SanitizeWorkArea(workArea);
        var width = ClampSize(settings?.WindowWidth, DefaultWidth, MinWidth, area.Width);
        var height = ClampSize(settings?.WindowHeight, DefaultHeight, MinHeight, area.Height);
        var maximized = IsMaximized(settings?.WindowState);

        double left;
        double top;
        if (TryCoordinate(settings?.WindowLeft, out var savedLeft) && TryCoordinate(settings?.WindowTop, out var savedTop))
        {
            left = savedLeft;
            top = savedTop;
        }
        else
        {
            left = area.Left + Math.Max(0, (area.Width - width) / 2);
            top = area.Top + Math.Max(0, (area.Height - height) / 2);
        }

        (left, top) = KeepOnScreen(left, top, width, height, area);
        return new Placement(left, top, width, height, maximized);
    }

    /// <summary>Writes a captured placement onto settings. Minimized is stored as Normal.</summary>
    public static void Capture(DeskSettings settings, double left, double top, double width, double height, string? windowState)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!TrySize(width) || !TrySize(height))
            return;

        settings.WindowWidth = width;
        settings.WindowHeight = height;
        if (TryCoordinate(left, out var safeLeft) && TryCoordinate(top, out var safeTop))
        {
            settings.WindowLeft = safeLeft;
            settings.WindowTop = safeTop;
        }

        settings.WindowState = IsMaximized(windowState) ? StateMaximized : StateNormal;
    }

    public static bool IsMaximized(string? state) =>
        string.Equals(state?.Trim(), StateMaximized, StringComparison.OrdinalIgnoreCase);

    private static Rect SanitizeWorkArea(Rect workArea)
    {
        var width = workArea.Width;
        var height = workArea.Height;
        if (!TrySize(width) || width < MinWidth)
            width = MinWidth;
        if (!TrySize(height) || height < MinHeight)
            height = MinHeight;
        var left = TryCoordinate(workArea.Left, out var l) ? l : 0;
        var top = TryCoordinate(workArea.Top, out var t) ? t : 0;
        return new Rect(left, top, width, height);
    }

    private static double ClampSize(double? saved, double fallback, double minimum, double workSize)
    {
        var size = TrySize(saved) ? saved!.Value : fallback;
        if (size < minimum)
            size = minimum;
        var max = Math.Max(minimum, workSize);
        if (size > max)
            size = max;
        return size;
    }

    private static (double Left, double Top) KeepOnScreen(double left, double top, double width, double height, Rect area)
    {
        var minLeft = area.Left + VisibleEdge - width;
        var maxLeft = area.Left + area.Width - VisibleEdge;
        var minTop = area.Top;
        var maxTop = area.Top + Math.Max(0, area.Height - VisibleEdge);

        if (left < minLeft)
            left = minLeft;
        if (left > maxLeft)
            left = maxLeft;
        if (top < minTop)
            top = minTop;
        if (top > maxTop)
            top = maxTop;
        return (left, top);
    }

    private static bool TrySize(double? value) =>
        value is { } number && !double.IsNaN(number) && !double.IsInfinity(number) && number > 0;

    private static bool TryCoordinate(double? value, out double number)
    {
        if (value is { } candidate && !double.IsNaN(candidate) && !double.IsInfinity(candidate))
        {
            number = candidate;
            return true;
        }

        number = 0;
        return false;
    }
}
