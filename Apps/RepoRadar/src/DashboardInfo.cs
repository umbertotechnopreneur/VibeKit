using static WorkspaceRepo.DashboardTerminal;

namespace WorkspaceRepo;

public sealed partial class Dashboard
{
    private IndexedRepo? explainedRepository;
    private IReadOnlyList<RepositoryInfoSection> infoSections = [];
    private int infoOffset, infoLimit, infoPageSize = 1;

    private void OpenRepositoryInfo()
    {
        if (visible.ElementAtOrDefault(selected) is not { } repo) return;
        // Compose from one saved observation so resize and scrolling never change the facts.
        explainedRepository = repo;
        infoSections = RepositoryInfo.Create(repo);
        infoOffset = infoLimit = 0;
        view = View.Info;
    }

    private void HandleRepositoryInfo(ConsoleKey key)
    {
        if (key is ConsoleKey.Escape or ConsoleKey.Enter or ConsoleKey.I)
        {
            explainedRepository = null;
            infoSections = [];
            view = View.Grid;
            return;
        }
        infoOffset = Math.Clamp(key switch
        {
            ConsoleKey.UpArrow => infoOffset - 1,
            ConsoleKey.DownArrow => infoOffset + 1,
            ConsoleKey.PageUp => infoOffset - infoPageSize,
            ConsoleKey.PageDown => infoOffset + infoPageSize,
            ConsoleKey.Home => 0,
            ConsoleKey.End => infoLimit,
            _ => infoOffset
        }, 0, infoLimit);
    }

    private void DrawRepositoryInfo(DashboardGrid grid)
    {
        if (explainedRepository is not { } repo) return;
        var (x, y, width, height) = Dialog(grid, 108, 31);
        if (width < 18 || height < 5) return;
        Frame(grid, x, y, width, height, Cyan);
        int left = x + 3;
        int inner = width - 6;
        bool showPath = height >= 11;
        grid.Write(left, y + 1, "Repository Info", "bold " + Cyan, inner);
        if (height >= 6)
            grid.Write(left, y + 2, TerminalText.Abbreviate(RemoteName(repo), inner), "bold " + Foreground, inner);
        if (showPath) grid.Write(left, y + 3, TerminalText.Abbreviate(repo.Path, inner), Muted, inner);
        int top = y + (showPath ? 5 : height >= 6 ? 3 : 2);
        int footer = y + height - 2;
        if (showPath) grid.Fill(left, top - 1, inner, '─', Border);
        var lines = new List<ConsoleLine>();
        foreach (var section in infoSections)
        {
            if (lines.Count > 0 && height >= 16) lines.Add(new("", Foreground));
            lines.Add(new(section.Title, "bold " + Cyan));
            foreach (string text in section.Lines)
            {
                string indent = "• ";
                foreach (string line in TerminalText.Wrap(text, inner - 2))
                {
                    lines.Add(new(indent + line, Foreground));
                    indent = "  ";
                }
            }
        }
        infoPageSize = Math.Max(1, footer - top);
        infoLimit = Math.Max(0, lines.Count - infoPageSize);
        infoOffset = Math.Min(infoOffset, infoLimit);
        for (int index = 0; index < infoPageSize && infoOffset + index < lines.Count; index++)
        {
            var line = lines[infoOffset + index];
            grid.Write(left, top + index, line.Text, line.Style, inner);
        }
        string hint = infoLimit == 0 || inner < 23 ? "[Esc] Close" : "[Esc] Close    [↑↓] Scroll";
        string? position = infoLimit > 0 && inner >= 40
            ? $"{infoOffset + 1}–{Math.Min(lines.Count, infoOffset + infoPageSize)} of {lines.Count}" : null;
        ConsoleWidgets.StatusLine(grid, left, footer, inner, hint, Cyan, position, Muted);
    }
}
