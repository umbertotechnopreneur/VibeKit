using static WorkspaceRepo.DashboardTerminal;

namespace WorkspaceRepo;

internal sealed partial class TerminalPathPicker
{
    const string SelectedInk = "bold #ECFFFF on #164148";
    const string PaneBorder = "#7298AC";

    static string FolderIcon => global::Ui.UseEmoji ? "📁" : "▱";
    static string DocumentIcon => global::Ui.UseEmoji ? "📄" : "·";
    static string DriveIcon => global::Ui.UseEmoji ? "🖴" : "▣";

    static int Scroll(int selected, int offset, int page, int count)
    {
        if (selected < offset) offset = selected;
        if (selected >= offset + page) offset = selected - page + 1;
        return Math.Clamp(offset, 0, Math.Max(0, count - page));
    }

    public void Draw(DashboardGrid g, int x, int y, int width, int height)
    {
        if (width < 12 || height < 7) return;
        g.Box(x, y, width, height, Cyan, opaque: true);
        int left = x + 2, inner = width - 4, bottom = y + height - 1;
        bool wide = width >= 70;
        bool detailed = height >= 17;
        bool paneHeaders = height >= 13;
        string title = mode == PathPickerMode.File ? "Choose File" : showFiles ? "Choose File or Folder" : "Choose Folder";
        g.Write(left + 1, y + 1, $"{FolderIcon}  {title}", "bold " + Foreground, inner - 2);
        if (detailed)
        {
            string intro = mode == PathPickerMode.File ? "Select a file or open a folder to browse." :
                showFiles ? "Open a folder or select a file as the repository location." :
                "Select a folder to open or choose as repository location.";
            g.Write(left + 1, y + 2, intro, Foreground, inner - 2);
            g.Fill(left, y + 3, inner, '─', PaneBorder);
        }
        int crumbRow = y + (detailed ? 4 : 2);
        DrawBreadcrumb(g, left + 1, crumbRow, inner - 2);
        if (detailed) g.Fill(left, crumbRow + 1, inner, '─', PaneBorder);

        int paneY = crumbRow + (detailed ? 2 : 1);
        int footer = bottom - (detailed ? 3 : 2);
        int paneHeight = footer - paneY;
        pageSize = Math.Max(1, paneHeight - (paneHeaders ? 4 : 2));
        int treeWidth = wide ? Math.Clamp(inner / 3, 20, 37) : inner;
        if (paneHeight >= 3)
        {
            if (wide || treeFocus) DrawTree(g, left, paneY, treeWidth, paneHeight, paneHeaders);
            if (wide || !treeFocus)
            {
                int offset = wide ? treeWidth + 1 : 0;
                DrawContents(g, left + offset, paneY, inner - offset, paneHeight, paneHeaders);
            }
        }
        DrawPickerFooter(g, left, footer, inner, bottom, detailed, wide);
    }

    void DrawBreadcrumb(DashboardGrid g, int x, int y, int width)
    {
        if (breadcrumb.Length == 0) return;
        string leading = FolderIcon + "  " + string.Join(" › ", breadcrumb[..^1]) + " › ";
        string current = breadcrumb[^1];
        if (DashboardGrid.Measure(leading + current) > width)
        {
            leading = FolderIcon + "  " + breadcrumb[0] + " › " + (breadcrumb.Length > 2 ? breadcrumb[1] + " › … › " : "");
            if (DashboardGrid.Measure(leading + current) > width) leading = FolderIcon + "  … › ";
        }
        int leadWidth = DashboardGrid.Measure(leading);
        if (leadWidth >= width) { g.Write(x, y, DashboardGrid.Fit(Folder, width), Cyan, width); return; }
        g.Write(x, y, leading, Muted, width);
        g.Write(x + leadWidth, y, DashboardGrid.Fit(current, width - leadWidth), "bold " + Cyan, width - leadWidth);
    }

    void DrawTree(DashboardGrid g, int x, int y, int width, int height, bool headers)
    {
        g.Box(x, y, width, height, treeFocus ? Cyan : PaneBorder);
        int row = y + 1;
        if (headers)
        {
            g.Write(x + 2, row++, $"▣  {locationsTitle}", "bold " + Foreground, width - 4);
            g.Fill(x + 1, row++, width - 2, '─', PaneBorder);
        }
        treeOffset = Scroll(treeRow, treeOffset, pageSize, tree.Count);
        for (int i = 0; i < pageSize && treeOffset + i < tree.Count; i++)
        {
            var node = tree[treeOffset + i];
            bool active = treeOffset + i == treeRow;
            string ink = active ? treeFocus ? SelectedInk : "bold #6FDAD5 on #102A30" : Foreground;
            if (active) g.Fill(x + 1, row + i, width - 2, ' ', ink);
            string padding = new(' ', Math.Min(node.Depth * 2, Math.Max(0, width / 2 - 3)));
            string marker = node.Expanded ? "▾" : "▸";
            string icon = node.Depth == 0 ? DriveIcon : FolderIcon;
            g.Write(x + 2, row + i, DashboardGrid.Fit($"{padding}{marker}  {icon}  {node.Label}", width - 4), ink, width - 4);
        }
    }

    void DrawContents(DashboardGrid g, int x, int y, int width, int height, bool headers)
    {
        g.Box(x, y, width, height, !treeFocus ? Cyan : PaneBorder);
        int first = y + 1, inside = width - 4;
        int modifiedWidth = inside >= 57 ? 17 : inside >= 39 ? 11 : 0;
        int typeWidth = inside >= 25 ? 9 : 0;
        int nameWidth = inside - typeWidth - modifiedWidth;
        void Row(int at, string name, string type, string modified, string ink)
        {
            g.Write(x + 2, at, DashboardGrid.Fit(name, nameWidth - 1), ink, nameWidth - 1);
            if (typeWidth > 0) g.Write(x + 2 + nameWidth, at, type, ink, typeWidth - 1);
            if (modifiedWidth > 0) g.Write(x + 2 + nameWidth + typeWidth, at, modified, ink, modifiedWidth);
        }
        if (headers)
        {
            Row(first++, "Name", "Type", "Modified", "bold " + Foreground);
            g.Fill(x + 1, first++, width - 2, '─', PaneBorder);
        }
        fileOffset = Scroll(fileRow, fileOffset, pageSize, contents.Count);
        if (contents.Count == 0) g.Write(x + 2, first, "No matching entries", Muted, inside);
        for (int i = 0; i < pageSize && fileOffset + i < contents.Count; i++)
        {
            var entry = contents[fileOffset + i];
            bool active = !treeFocus && fileOffset + i == fileRow;
            string ink = active ? SelectedInk : Foreground;
            if (active) g.Fill(x + 1, first + i, width - 2, ' ', ink);
            string icon = entry.IsParent ? "↑" : entry.IsFolder ? FolderIcon : DocumentIcon;
            string modified = entry.Modified?.ToString(modifiedWidth > 11 ? "yyyy-MM-dd HH:mm" : "yyyy-MM-dd") ?? "—";
            Row(first + i, $"{icon}  {entry.Name}", entry.IsFolder ? "<DIR>" : "File", modified, ink);
        }
    }

    void DrawPickerFooter(DashboardGrid g, int x, int y, int width, int bottom, bool detailed, bool wide)
    {
        if (editing)
        {
            string prefix = "Path › ";
            g.Write(x, y, prefix + DashboardGrid.Fit(input, width - prefix.Length - 1) + "▏", Foreground, width);
            g.Write(x, y + 1, Error.Length == 0 ? "Enter  Open path      Esc  Back" : Error, Error.Length == 0 ? Cyan : Gold, width);
            return;
        }
        if (detailed)
        {
            var path = treeFocus || contents.Count == 0 ? Folder : contents[fileRow].Path;
            g.Write(x, y++, DashboardGrid.Fit(Error.Length > 0 ? Error : path, width), Error.Length > 0 ? Gold : Muted, width);
        }
        if (wide)
        {
            ShortcutHint[] hints = [new("↑↓", "Navigate"), new("←", treeFocus ? "Collapse" : "Up"), new("Enter", "Open"),
                new("Space", "Choose"), new("Esc", "Cancel")];
            ConsoleWidgets.Shortcuts(g, x, y, width, hints, "bold " + Cyan, Foreground);
        }
        else g.Write(x, y, width >= 46 ? "[Tab] Pane  [Enter] Open  [Space] Choose  [Esc]" :
            "Esc · Tab pane · Enter · Space", Cyan, width);

        string toggles = mode == PathPickerMode.Either ? showFiles ? "    F Folders only" : "    F Include files" : "";
        if (y + 1 < bottom)
            g.Write(x, y + 1, !detailed && Error.Length > 0 ? Error : "Tab Switch pane    D Drives    G Enter path" +
                (mode == PathPickerMode.File ? "" : "    S Use folder") + toggles, Muted, width);
    }
}
