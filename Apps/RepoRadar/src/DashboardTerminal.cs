using Spectre.Console;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace WorkspaceRepo;

/// <summary>Owns the alternate screen and flushes changed grid rows using Spectre.Console styles.</summary>
internal sealed class DashboardTerminal : IDisposable
{
    private readonly StringWriter buffer = new();
    private readonly IAnsiConsole renderer;
    private string[] previous = [];
    private int previousWidth;
    private bool active;
    private uint originalMode;
    private bool hasMode;

    internal const string Gold = "#EDBA73", Cyan = "#6FDAD5", Violet = "#B899FF",
        Foreground = "#E4E7EF", Muted = "#A5ACB9", Border = "#516074", Dark = "#151922";
    internal int Width => Math.Max(1, Console.WindowWidth - 1);
    internal int Height => Math.Max(1, Console.WindowHeight);

    public DashboardTerminal()
    {
        renderer = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes, ColorSystem = ColorSystemSupport.TrueColor,
            Out = new AnsiConsoleOutput(buffer)
        });
        renderer.Profile.Width = 8192;
        Enter();
    }

    public void Enter()
    {
        if (active) return;
        if (OperatingSystem.IsWindows() && GetConsoleMode(GetStdHandle(-11), out originalMode))
        {
            hasMode = SetConsoleMode(GetStdHandle(-11), originalMode | 4);
        }
        try { Console.Write("\x1b[?1049h\x1b[?25l\x1b[2J\x1b[H"); }
        catch { RestoreOutputMode(); throw; }
        active = true;
        previous = [];
    }

    /// <summary>Restores the host console mode even when the final terminal write fails.</summary>
    public void Leave()
    {
        if (!active) return;
        active = false;
        try { Console.Write("\x1b[0m\x1b[?25h\x1b[?1049l"); }
        finally { RestoreOutputMode(); }
    }

    private void RestoreOutputMode()
    {
        if (!hasMode) return;
        SetConsoleMode(GetStdHandle(-11), originalMode);
        hasMode = false;
    }

    public void Paint(DashboardGrid grid)
    {
        var lines = grid.Render();
        var diff = new StringBuilder();
        bool redraw = previous.Length != lines.Length || previousWidth != grid.Width;
        if (redraw) diff.Append("\x1b[2J");
        for (int row = 0; row < lines.Length; row++)
        {
            if (!redraw && lines[row] == previous[row]) continue;
            buffer.GetStringBuilder().Clear();
            if (lines[row].Length != 0) renderer.Write(new Markup(lines[row]));
            diff.Append("\x1b[").Append(row + 1).Append(";1H\x1b[0m").Append(buffer).Append("\x1b[0m\x1b[K");
        }
        if (diff.Length > 0) Console.Write(diff.ToString());
        previous = lines;
        previousWidth = grid.Width;
    }

    public void Dispose()
    {
        try { Leave(); }
        finally { buffer.Dispose(); }
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int id);
    [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(IntPtr handle, out uint mode);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleMode(IntPtr handle, uint mode);
}

/// <summary>Stores styled terminal cells and escapes text before formatting it with Spectre.Console.</summary>
internal sealed class DashboardGrid
{
    private record struct Cell(string? Text, string? Style);
    private readonly Cell[,] cells;
    public int Width { get; }
    public int Height { get; }

    public DashboardGrid(int width, int height)
    {
        Width = width; Height = height;
        cells = new Cell[Height, Width];
    }

    internal static int Measure(string text) => TerminalText.Measure(text);
    internal static string Fit(string text, int width) => TerminalText.Fit(text, width);

    public void Write(int x, int y, string? text, string? style = null, int max = int.MaxValue)
    {
        if (y < 0 || y >= Height || x < 0 || x >= Width || string.IsNullOrEmpty(text)) return;
        int end = x + Math.Min(Width - x, Math.Max(max, 0));
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (x < end && enumerator.MoveNext())
        {
            var element = TerminalText.Printable(enumerator.GetTextElement());
            int size = TerminalText.CellWidth(element);
            if (x + size > end) break;
            // Erase the other half of an existing wide glyph when a popup
            // boundary or a narrow replacement cuts through that glyph.
            if (x > 0 && cells[y, x].Text == "") cells[y, x - 1] = default;
            if (x + size < Width && cells[y, x + size].Text == "") cells[y, x + size] = default;
            cells[y, x++] = new(element, style);
            if (size == 2) cells[y, x++] = new("", style);
        }
    }

    public void Fill(int x, int y, int width, char c, string? style = null)
    {
        if (width > 0 && x < Width && y >= 0 && y < Height)
            Write(x, y, new string(c, Math.Min(Math.Max(0, Width - x), width)), style);
    }

    public void Box(int x, int y, int width, int height, string style, bool opaque = false)
    {
        if (width < 2 || height < 2) return;
        for (var row = 0; row < height; row++)
        {
            bool edge = row == 0 || row == height - 1;
            if (edge || opaque) Fill(x + 1, y + row, width - 2, edge ? '─' : ' ', edge ? style : null);
            Write(x, y + row, row == 0 ? "╭" : row == height - 1 ? "╰" : "│", style);
            Write(x + width - 1, y + row, row == 0 ? "╮" : row == height - 1 ? "╯" : "│", style);
        }
    }

    public string[] Render()
    {
        var lines = new string[Height];
        var result = new StringBuilder();
        for (int y = 0; y < Height; y++)
        {
            result.Clear();
            string? style = null;
            var last = Width - 1;
            while (last >= 0 && cells[y, last].Text == null) last--;
            for (int x = 0; x <= last; x++)
            {
                var cell = cells[y, x];
                if (cell.Style != style)
                {
                    if (style != null) result.Append("[/]");
                    style = cell.Style;
                    if (style != null) result.Append('[').Append(style).Append(']');
                }
                result.Append(Markup.Escape(cell.Text ?? " "));
            }
            if (style != null) result.Append("[/]");
            lines[y] = result.ToString();
        }
        return lines;
    }
}
