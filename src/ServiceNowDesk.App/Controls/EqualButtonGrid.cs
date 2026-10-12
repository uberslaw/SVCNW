using System.Windows;
using System.Windows.Controls;

namespace ServiceNowDesk.Controls;

/// <summary>
/// Equal-size button cells, left-aligned. The cell is at least
/// <see cref="MinCellWidth"/> by <see cref="MinCellHeight"/> and grows when a label
/// measures wider, so text stays on one line. Every live grid shares that size, so a
/// list with fewer buttons still uses the same cell. Gap is the same in both directions.
/// </summary>
public sealed class EqualButtonGrid : Panel
{
    public const double Gap = 8;
    public const double MinCellWidth = 128;
    public const double MinCellHeight = 32;

    private static readonly List<WeakReference<EqualButtonGrid>> Live = [];
    private static double _sharedWidth = MinCellWidth;
    private static double _sharedHeight = MinCellHeight;

    private double _cellWidth = MinCellWidth;
    private double _cellHeight = MinCellHeight;

    public EqualButtonGrid()
    {
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        Live.Add(new WeakReference<EqualButtonGrid>(this));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var visible = VisibleChildren();
        var cellWidth = Math.Max(MinCellWidth, _sharedWidth);
        var cellHeight = Math.Max(MinCellHeight, _sharedHeight);
        foreach (var child in visible)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            cellWidth = Math.Max(cellWidth, Finite(child.DesiredSize.Width));
            cellHeight = Math.Max(cellHeight, Finite(child.DesiredSize.Height));
        }

        NoteCell(cellWidth, cellHeight);
        cellWidth = Math.Max(cellWidth, _sharedWidth);
        cellHeight = Math.Max(cellHeight, _sharedHeight);
        _cellWidth = cellWidth;
        _cellHeight = cellHeight;

        if (visible.Count == 0)
            return new Size(0, 0);

        var columns = ColumnCount(availableSize.Width, cellWidth, visible.Count);
        var rows = (visible.Count + columns - 1) / columns;
        var width = columns * cellWidth + (columns - 1) * Gap;
        var height = rows * cellHeight + (rows - 1) * Gap;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var visible = VisibleChildren();
        if (visible.Count == 0)
            return finalSize;

        var columns = ColumnCount(finalSize.Width, _cellWidth, visible.Count);
        for (var index = 0; index < visible.Count; index++)
        {
            var column = index % columns;
            var row = index / columns;
            var x = column * (_cellWidth + Gap);
            var y = row * (_cellHeight + Gap);
            visible[index].Arrange(new Rect(x, y, _cellWidth, _cellHeight));
        }

        return finalSize;
    }

    private static int ColumnCount(double availableWidth, double cellWidth, int visible)
    {
        if (visible <= 1 || cellWidth <= 0)
            return Math.Max(1, visible);

        var width = availableWidth;
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0)
            width = cellWidth;

        var columns = 1;
        while (columns < visible)
        {
            var next = columns + 1;
            var needed = next * cellWidth + (next - 1) * Gap;
            if (needed > width + 0.5)
                break;
            columns = next;
        }

        return columns;
    }

    private List<UIElement> VisibleChildren()
    {
        var visible = new List<UIElement>();
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
                continue;
            visible.Add(child);
        }

        return visible;
    }

    private void NoteCell(double width, double height)
    {
        var wider = width > _sharedWidth + 0.5;
        var taller = height > _sharedHeight + 0.5;
        if (!wider && !taller)
            return;
        if (wider)
            _sharedWidth = width;
        if (taller)
            _sharedHeight = height;

        for (var index = Live.Count - 1; index >= 0; index--)
        {
            if (!Live[index].TryGetTarget(out var panel))
            {
                Live.RemoveAt(index);
                continue;
            }

            if (!ReferenceEquals(panel, this))
                panel.InvalidateMeasure();
        }
    }

    private static double Finite(double value) =>
        double.IsNaN(value) || double.IsInfinity(value) ? 0 : value;
}
