namespace WorkspaceRepo;

internal static class TerminalViewportTests
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("Terminal viewport: " + description);
            checks++;
        }

        var grid = new TerminalViewport(new(2, 10, 162, 26), 2, 8, 2, 1, 5, 1, 0, firstGroupCount: 3);
        Check(grid.Cells.Select(cell => (cell.Index, cell.X, cell.Y)).SequenceEqual(new[]
        {
            (0, 2, 10), (1, 84, 10), (2, 2, 19), (3, 2, 28), (4, 84, 28)
        }), "the second group starts on a new row after an odd number of aligned tiles");
        Check(grid.Move(1, ConsoleKey.DownArrow) == 2, "Down lands on the nearest tile of a partial row");
        Check(grid.Move(2, ConsoleKey.DownArrow) == 3, "Down crosses the group boundary in the same column");
        Check(grid.Move(4, ConsoleKey.UpArrow) == 2, "Up finds the nearest tile before the group boundary");

        var scrolled = new TerminalViewport(new(2, 10, 162, 8), 2, 8, 2, 1, 5, 2, 0, firstGroupCount: 3);
        Check(scrolled.FirstVisibleIndex == 2 && scrolled.Cells.Count == 1, "scrolling keeps the partial focused row visible");
        Check(scrolled.Move(2, ConsoleKey.PageDown) == 3, "paging uses the actual number of visible rows");

        var list = new TerminalViewport(new(1, 12, 68, 3), 1, 1, 0, 0, 5, 3, 0);
        Check(list.FirstVisibleIndex == 1 && list.Cells[^1] == new ViewportCell(3, 1, 14, 68, 1),
            "the same viewport keeps the selected row visible in a single-column list");
        Check(list.Move(3, ConsoleKey.DownArrow) == 4 && list.Move(3, ConsoleKey.Home) == 0,
            "list navigation uses its own visible row geometry");

        var compact = new TerminalViewport(new(1, 12, 98, 1), 1, 1, 0, 0, 5, 3, 0);
        Check(compact.Cells.Count == 1 && compact.Cells[0].Index == 3, "a one-row viewport always displays its selection");
        return checks;
    }
}
