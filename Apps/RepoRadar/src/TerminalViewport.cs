namespace WorkspaceRepo;

internal readonly record struct ViewportBounds(int Left, int Top, int Width, int Height);

internal readonly record struct ViewportCell(int Index, int X, int Y, int Width, int Height);

/// <summary>
/// Plans a scrollable grid of terminal cells. A list uses the same plan with one column.
/// Rendering and navigation consume the same item positions, including gaps at group boundaries.
/// </summary>
internal sealed class TerminalViewport
{
    private readonly int itemCount, columns, firstGroupCount, padding, rowCount, visibleRows;

    public int FirstVisibleIndex { get; }
    public IReadOnlyList<ViewportCell> Cells { get; }

    public TerminalViewport(ViewportBounds bounds, int columns, int itemHeight, int columnGap, int rowGap,
        int itemCount, int selected, int firstVisibleIndex, int firstGroupCount = 0)
    {
        this.columns = columns;
        this.itemCount = itemCount;
        this.firstGroupCount = firstGroupCount;
        padding = firstGroupCount > 0 && firstGroupCount < itemCount ? (columns - firstGroupCount % columns) % columns : 0;
        rowCount = (itemCount + padding + columns - 1) / columns;
        visibleRows = (bounds.Height + rowGap) / (itemHeight + rowGap);

        if (visibleRows == 0 || itemCount == 0)
        {
            FirstVisibleIndex = firstVisibleIndex;
            Cells = [];
            return;
        }

        int selectedRow = Slot(Math.Clamp(selected, 0, itemCount - 1)) / columns;
        int firstRow = Slot(Math.Clamp(firstVisibleIndex, 0, itemCount - 1)) / columns;
        // Keep focus in the viewport and use available rows when scrolling near the end.
        firstRow = Math.Clamp(firstRow, Math.Max(0, selectedRow - visibleRows + 1), selectedRow);
        firstRow = Math.Min(firstRow, Math.Max(0, rowCount - visibleRows));
        int lastRow = Math.Min(rowCount, firstRow + visibleRows) - 1;
        FirstVisibleIndex = IndexAt(firstRow, 0);
        int lastIndex = IndexAt(lastRow, columns - 1);

        var cells = new ViewportCell[lastIndex - FirstVisibleIndex + 1];
        int columnSpan = bounds.Width + columnGap;
        for (int i = 0; i < cells.Length; i++)
        {
            int index = FirstVisibleIndex + i;
            int slot = Slot(index);
            int column = slot % columns;
            int x = column * columnSpan / columns;
            int width = (column + 1) * columnSpan / columns - x - columnGap;
            int y = bounds.Top + (slot / columns - firstRow) * (itemHeight + rowGap);
            cells[i] = new ViewportCell(index, bounds.Left + x, y, width, itemHeight);
        }
        Cells = cells;
    }

    public int Move(int selected, ConsoleKey key)
    {
        if (itemCount == 0) return 0;
        return key switch
        {
            ConsoleKey.Home => 0,
            ConsoleKey.End => itemCount - 1,
            ConsoleKey.LeftArrow => Math.Max(0, selected - 1),
            ConsoleKey.RightArrow => Math.Min(itemCount - 1, selected + 1),
            ConsoleKey.UpArrow => MoveRows(selected, -1),
            ConsoleKey.DownArrow => MoveRows(selected, 1),
            ConsoleKey.PageUp => MoveRows(selected, -Math.Max(1, visibleRows)),
            ConsoleKey.PageDown => MoveRows(selected, Math.Max(1, visibleRows)),
            _ => selected
        };
    }

    private int MoveRows(int selected, int delta)
    {
        int slot = Slot(selected);
        int row = slot / columns;
        int next = Math.Clamp(row + delta, 0, rowCount - 1);
        return next == row ? selected : IndexAt(next, slot % columns);
    }

    private int Slot(int index) => index < firstGroupCount ? index : index + padding;

    private int IndexAt(int row, int column)
    {
        int slot = row * columns + column;
        // A partial row ends at its last item. An arrow into a vacant column stays on that row.
        if (slot < firstGroupCount + padding) return Math.Min(slot, firstGroupCount - 1);
        return Math.Min(slot - padding, itemCount - 1);
    }
}
