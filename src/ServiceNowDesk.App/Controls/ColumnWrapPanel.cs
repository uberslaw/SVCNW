using System.Windows;
using System.Windows.Controls;

namespace ServiceNowDesk.Controls;

/// <summary>
/// Lays children out in equal columns. A row is only as tall as the tallest child in that row.
/// </summary>
public sealed class ColumnWrapPanel : Panel
{
    public static readonly DependencyProperty ColumnsProperty =
        DependencyProperty.Register(
            nameof(Columns),
            typeof(int),
            typeof(ColumnWrapPanel),
            new FrameworkPropertyMetadata(3, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = ColumnCount();
        var width = double.IsInfinity(availableSize.Width) ? 0d : Math.Max(0d, availableSize.Width);
        var cellWidth = width / columns;
        var column = 0;
        var rowHeight = 0d;
        var height = 0d;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(cellWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            column++;
            if (column != columns)
                continue;

            height += rowHeight;
            rowHeight = 0d;
            column = 0;
        }

        if (column != 0)
            height += rowHeight;

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = ColumnCount();
        var cellWidth = Math.Max(0d, finalSize.Width) / columns;
        var column = 0;
        var rowHeight = 0d;
        var y = 0d;
        var rowStart = 0;
        var count = InternalChildren.Count;
        for (var index = 0; index < count; index++)
        {
            InternalChildren[index].Measure(new Size(cellWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, InternalChildren[index].DesiredSize.Height);
            column++;
            if (column != columns && index != count - 1)
                continue;

            var x = 0d;
            for (var place = rowStart; place <= index; place++)
            {
                InternalChildren[place].Arrange(new Rect(x, y, cellWidth, rowHeight));
                x += cellWidth;
            }

            y += rowHeight;
            rowHeight = 0d;
            column = 0;
            rowStart = index + 1;
        }

        return finalSize;
    }

    private int ColumnCount() => Math.Max(1, Columns);
}
