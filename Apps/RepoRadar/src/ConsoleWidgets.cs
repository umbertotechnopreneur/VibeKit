namespace WorkspaceRepo;

internal readonly record struct DialogBounds(int X, int Y, int Width, int Height);
internal readonly record struct ConsoleLine(string Text, string Style, bool SpaceBefore = false);
internal readonly record struct KeyHint(string Before, string Key, string After);
internal readonly record struct ShortcutHint(string Key, string Label);

internal sealed record AboutContent(
    string Heading,
    IReadOnlyList<ConsoleLine> Lines,
    KeyHint Dismiss,
    string TextStyle,
    string RuleStyle,
    string KeyStyle);

/// <summary>Draws widgets from text, styles and terminal bounds supplied by the host screen.</summary>
internal static class ConsoleWidgets
{
    // The host supplies the usable rows; the widget never paints over its footer.
    public static DialogBounds PlaceDialog(DashboardGrid grid, int preferredWidth, int preferredHeight,
        int top, int bottom)
    {
        top = Math.Clamp(top, 0, grid.Height);
        bottom = Math.Clamp(bottom, top, grid.Height);
        int width = Math.Clamp(preferredWidth, 0, Math.Max(0, grid.Width - 4));
        int height = Math.Clamp(preferredHeight, 0, bottom - top);
        return new((grid.Width - width) / 2, top + (bottom - top - height) / 2, width, height);
    }

    public static string GradientStyle(IReadOnlyList<string> colors, int position, int width)
    {
        int index = Math.Clamp(position, 0, Math.Max(0, width - 1)) * colors.Count / Math.Max(1, width);
        return colors[index];
    }

    public static void Frame(DashboardGrid grid, int x, int y, int width, int height,
        string borderStyle, IReadOnlyList<string>? palette = null)
    {
        if (width < 2 || height < 2) return;
        bool gradient = palette is { Count: > 0 };
        grid.Box(x, y, width, height, gradient ? palette![0] : borderStyle, opaque: true);
        if (!gradient) return;
        for (int column = 1; column < width - 1; column++)
        {
            string style = GradientStyle(palette!, column, width);
            grid.Write(x + column, y, "─", style);
            grid.Write(x + column, y + height - 1, "─", style);
        }
        int right = x + width - 1;
        string last = palette![^1];
        grid.Write(right, y, "╮", last);
        for (int row = 1; row < height - 1; row++) grid.Write(right, y + row, "│", last);
        grid.Write(right, y + height - 1, "╯", last);
    }

    public static void Center(DashboardGrid grid, int x, int y, int width, string text, string style)
    {
        string fitted = DashboardGrid.Fit(text, width);
        int start = x + Math.Max(0, (width - DashboardGrid.Measure(fitted)) / 2);
        grid.Write(start, y, fitted, style, width);
    }

    public static void Badge(DashboardGrid grid, int x, int y, int width, string text, string style, bool framed)
    {
        if (framed)
        {
            Frame(grid, x, y, width, 3, style);
            grid.Write(x + 2, y + 1, text, "bold " + style, width - 4);
        }
        else grid.Write(x, y, text, "bold " + style, width);
    }

    public static void StatusLine(DashboardGrid grid, int x, int y, int width, string text, string style,
        string? right = null, string? rightStyle = null)
    {
        int rightWidth = right is null ? 0 : TerminalText.Measure(right);
        if (right is not null && rightWidth + 5 < width)
        {
            grid.Write(x + width - rightWidth, y, right, rightStyle, rightWidth);
            width -= rightWidth + 3;
        }
        grid.Write(x, y, TerminalText.Abbreviate(text, width), style, width);
    }

    public static void Shortcuts(DashboardGrid grid, int x, int y, int width, IReadOnlyList<ShortcutHint> hints,
        string keyStyle, string labelStyle)
    {
        int end = x + width;
        foreach (var hint in hints)
        {
            string key = "[" + hint.Key + "]";
            int keyWidth = TerminalText.Measure(key);
            int total = keyWidth + 1 + TerminalText.Measure(hint.Label);
            if (x + total > end) break;
            grid.Write(x, y, key, keyStyle);
            grid.Write(x + keyWidth + 1, y, hint.Label, labelStyle);
            x += total + 2;
        }
    }

    /// <summary>Shows progress within the given width. An unknown fraction draws an indeterminate trail.</summary>
    public static void Progress(DashboardGrid grid, int x, int y, int width, int frame,
        double? fraction, IReadOnlyList<string> palette, string track)
    {
        if (width < 7 || palette.Count == 0) return;
        int count = Math.Min((width + 1) / 2, 17);
        int start = x + (width - (count * 2 - 1)) / 2;
        if (fraction is { } completed)
        {
            int filled = (int)Math.Round(Math.Clamp(completed, 0, 1) * count);
            for (int i = 0; i < count; i++)
                grid.Write(start + i * 2, y, "■", i < filled ? GradientStyle(palette, i, count) : track);
            return;
        }

        const int trail = 6;
        int head = frame % (count + trail) - (trail - 1);
        for (int i = 0; i < count; i++)
        {
            bool lit = i >= head && i < head + trail;
            grid.Write(start + i * 2, y, "●", lit ? GradientStyle(palette, i - head, trail) : track);
        }
    }

    public static void About(DashboardGrid grid, DialogBounds bounds, AboutContent content,
        IReadOnlyList<string> palette, Action<DashboardGrid, int, int, int> drawLogo)
    {
        var (x, y, width, height) = bounds;
        Frame(grid, x, y, width, height, content.RuleStyle, palette);
        if (width < 6 || height < 4) return;
        int left = x + 2;
        int span = width - 4;
        grid.Fill(left, y + 1, span, '─', content.RuleStyle);
        Center(grid, left, y + 1, span, "  " + content.Heading + "  ", "bold " + content.TextStyle);

        int row = y + 2;
        if (height >= 15 && height - 11 >= content.Lines.Count)
        {
            drawLogo(grid, x + 5, y + 3, width - 10);
            row = y + 8;
        }

        int lastContentRow = y + height - 4;
        int extraRows = Math.Max(0, lastContentRow - row + 1 - content.Lines.Count);
        foreach (var line in content.Lines)
        {
            if (row > lastContentRow) break;
            if (line.SpaceBefore && extraRows > 0) { row++; extraRows--; }
            Center(grid, left, row++, span, line.Text, line.Style);
        }

        if (height >= 7) grid.Fill(left, y + height - 3, span, '─', content.RuleStyle);
        DrawKeyHint(grid, left, y + height - 2, span, content.Dismiss, content.TextStyle, content.KeyStyle);
    }

    private static void DrawKeyHint(DashboardGrid grid, int x, int y, int width, KeyHint hint,
        string textStyle, string keyStyle)
    {
        int before = DashboardGrid.Measure(hint.Before);
        int key = DashboardGrid.Measure(hint.Key);
        int total = before + key + DashboardGrid.Measure(hint.After);
        if (total > width) { Center(grid, x, y, width, hint.Key, keyStyle); return; }
        int start = x + (width - total) / 2;
        grid.Write(start, y, hint.Before, textStyle);
        grid.Write(start + before, y, hint.Key, keyStyle);
        grid.Write(start + before + key, y, hint.After, textStyle);
    }
}
