/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; native content-sized wrapping for action rows
 * Human guidance: keep publication and panel actions visible in narrow windows
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace MarkVibe.Windows;

/// <summary>Wraps individually sized native controls while retaining order and pointer targets.</summary>
internal sealed class NativeWrapPanel : Panel
{
    public double Spacing { get; init; } = 4;

    /// <summary>Measures each item before projecting its row and total desired extent.</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        }
        return Position(availableSize.Width, arrange: false);
    }

    /// <summary>Arranges rows using the final allocated width, including live window resizing.</summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        Position(finalSize.Width, arrange: true);
        return finalSize;
    }

    /// <summary>Shares row-breaking math between desired extent and arrangement.</summary>
    private Size Position(double width, bool arrange)
    {
        var x = 0d;
        var y = 0d;
        var rowHeight = 0d;
        var measuredWidth = 0d;
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (size.Width <= 0 || size.Height <= 0)
            {
                continue;
            }

            var gap = x == 0 ? 0 : Spacing;
            if (x > 0 && x + gap + size.Width > width)
            {
                y += rowHeight + Spacing;
                x = 0;
                gap = 0;
                rowHeight = 0;
            }

            x += gap;
            if (arrange)
            {
                child.Arrange(new Rect(x, y, Math.Min(width, size.Width), size.Height));
            }
            x += size.Width;
            measuredWidth = Math.Max(measuredWidth, x);
            rowHeight = Math.Max(rowHeight, size.Height);
        }
        return new Size(Math.Min(measuredWidth, width), y + rowHeight);
    }
}
