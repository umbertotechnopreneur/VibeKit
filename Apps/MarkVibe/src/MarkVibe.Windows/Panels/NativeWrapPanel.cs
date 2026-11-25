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
    private double _measuredLineWidth;

    public double Spacing { get; init; } = 4;

    /// <summary>Measures each item before projecting its row and total desired extent.</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        }
        var measured = Position(availableSize.Width, availableSize.Width, arrange: false);
        _measuredLineWidth = measured.Width;
        var scale = XamlRoot?.RasterizationScale ?? 1;
        var desiredWidth = Math.Min(availableSize.Width, Math.Ceiling(measured.Width * scale) / scale);
        return new Size(desiredWidth, measured.Height);
    }

    /// <summary>Arranges rows using the final allocated width, including live window resizing.</summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = finalSize.Width;
        var pixel = 1 / (XamlRoot?.RasterizationScale ?? 1);
        // A centered layout slot can round slightly below its content-sized desired width.
        var wrapWidth = _measuredLineWidth > width && _measuredLineWidth <= width + pixel + 1e-6
            ? _measuredLineWidth
            : width;
        Position(wrapWidth, width, arrange: true);
        return finalSize;
    }

    /// <summary>Shares row-breaking math between desired extent and arrangement.</summary>
    private Size Position(double wrapWidth, double availableWidth, bool arrange)
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
            if (x > 0 && x + gap + size.Width > wrapWidth)
            {
                y += rowHeight + Spacing;
                x = 0;
                gap = 0;
                rowHeight = 0;
            }

            x += gap;
            if (arrange)
            {
                var childWidth = Math.Clamp(availableWidth - x, 0, size.Width);
                child.Arrange(new Rect(x, y, childWidth, size.Height));
            }
            x += size.Width;
            measuredWidth = Math.Max(measuredWidth, x);
            rowHeight = Math.Max(rowHeight, size.Height);
        }
        return new Size(Math.Min(measuredWidth, wrapWidth), y + rowHeight);
    }
}
