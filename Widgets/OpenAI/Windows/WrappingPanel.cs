using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace OpenAIUsage.Windows;

internal sealed class WrappingPanel(double gap = 8, bool centerRows = false) : Panel
{
    private bool singleRow;

    public bool SingleRow
    {
        get => singleRow;
        set
        {
            if (singleRow == value) return;
            singleRow = value;
            InvalidateMeasure();
        }
    }

    protected override Size MeasureOverride(Size availableSize) => Layout(availableSize.Width, arrange: false);

    protected override Size ArrangeOverride(Size finalSize)
    {
        Layout(finalSize.Width, arrange: true);
        return finalSize;
    }

    private Size Layout(double width, bool arrange)
    {
        double x = 0, y = 0, rowHeight = 0, usedWidth = 0;
        var row = new List<(UIElement Child, double X, double Width, double Height)>();
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            if (!arrange) child.Measure(new Size(width, double.PositiveInfinity));
            var size = child.DesiredSize;
            double itemWidth = Math.Min(size.Width, width);
            if (!singleRow && x > 0 && x + gap + itemWidth > width + .01)
            {
                ArrangeRow();
                y += rowHeight + gap;
                x = rowHeight = 0;
            }
            if (x > 0) x += gap;
            if (arrange) row.Add((child, x, itemWidth, size.Height));
            x += itemWidth;
            usedWidth = Math.Max(usedWidth, x);
            rowHeight = Math.Max(rowHeight, size.Height);
        }
        ArrangeRow();
        return new Size(usedWidth, y + rowHeight);

        void ArrangeRow()
        {
            double offset = centerRows ? Math.Max(0, (width - x) / 2) : 0;
            foreach (var item in row)
                item.Child.Arrange(new Rect(item.X + offset, y, item.Width, item.Height));
            row.Clear();
        }
    }
}
