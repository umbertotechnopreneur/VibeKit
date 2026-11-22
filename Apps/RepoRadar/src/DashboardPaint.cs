using static WorkspaceRepo.DashboardTerminal;

namespace WorkspaceRepo;

public sealed partial class Dashboard
{
    private static readonly string[] Palette = ["#21F0EE", "#36D6F3", "#78B6FF", "#B59BFF", "#E398EF", "#FFB6BA", "#FFDF79"];
    private static readonly string[] RadarPalette = ["#35DAEE", "#3CF8FF", "#56EED2", "#94F2AF"];
    private const string Good = "#86E890";
    private const string FocusCyan = "#3CF8FF", FocusBackground = " on #16464F", FocusTitle = "#ECFEFF";
    private const string FocusMark = "▶";
    private static string Tone(string health) => health switch
    {
        "Green" => Good, "Gone" or "Amber" => "#FFCF61", "Red" => "#E5AF9C",
        "Both" => "#FF8D98", "Push" => "#F6AB7E", "Purple" => "#D79CFF", _ => Muted
    };
    private static string Label(string health) => health switch
    { "Green" => "Aligned", "Amber" => "Branches", "Gone" => "Upstream gone", "Red" => "Files", "Push" => "Push", "Both" => "Push + Pull", "Purple" => "Pull", _ => "Verify" };

    private void Paint(int w, int h)
    {
        var grid = new DashboardGrid(w, h);
        if (w < 38 || h < 12)
        {
            viewport = null;
            grid.Write(0, 0, ProductBrand.Name, Cyan);
            grid.Write(0, 2, "Resize to at least 39 × 12.", Foreground);
            grid.Write(0, h - 1, status, Muted);
            terminal.Paint(grid); return;
        }

        viewport = PaintWorkspace(grid);
        if (viewport != null) offset = viewport.FirstVisibleIndex;
        terminal.Paint(grid);
    }

    // The wordmark is set in a 5 × 7 pixel alphabet. Two pixel rows share one terminal
    // row via upper/lower block characters, keeping the letter proportions on a monospace grid.
    private static readonly Dictionary<char, string[]> Alphabet = new()
    {
        ['R'] = ["11110","11011","11011","11110","11100","11010","11011"],
        ['E'] = ["11111","11000","11000","11110","11000","11000","11111"],
        ['P'] = ["11110","11011","11011","11110","11000","11000","11000"],
        ['O'] = ["01110","11011","11011","11011","11011","11011","01110"],
        ['A'] = ["01110","11011","11011","11111","11011","11011","11011"],
        ['D'] = ["11110","11011","11011","11011","11011","11011","11110"]
    };

    private static readonly string[] RadarPixels =
        ["0001111111000", "0110000000210", "1100011122011", "1000102200001",
         "1100011100011", "0110000000110", "0001111111000"];

    private static int DrawBrand(DashboardGrid g)
    {
        if (g.Width < 65 || g.Height < 24)
        {
            g.Write(2, 0, "◉  " + ProductBrand.Name, "bold " + Cyan);
            g.Write(7 + DashboardGrid.Measure(ProductBrand.Name), 0, ProductBrand.AuthorCredit, Muted);
            return 1;
        }

        int logoWidth = Math.Min(74, g.Width - 4);
        DrawWordmark(g, 2, 1, logoWidth);
        int textX = logoWidth + 8;
        if (g.Width >= textX + DashboardGrid.Measure(ProductBrand.AuthorCredit) + 2)
        {
            for (int y = 1; y <= 4; y++) g.Write(textX - 3, y, "│", Border);
            g.Write(textX, 1, ProductBrand.Name, "bold " + Foreground);
            g.Write(textX, 2, ProductBrand.AuthorCredit, Foreground);
            g.Write(textX, 3, ProductBrand.Description, Muted);
        }
        else g.Write(2, 5, ProductBrand.AuthorCredit, Muted);
        return 6;
    }

    private static void DrawWordmark(DashboardGrid g, int x, int y, int width)
    {
        const string name = "REPO RADAR";
        const int lettersWidth = 59;
        const int radarSpace = 15;
        if (width < lettersWidth)
        {
            Center(g, x, y + 1, width, "◉  " + name, "bold " + Cyan);
            return;
        }

        bool withRadar = width >= lettersWidth + radarSpace;
        int start = x + (width - lettersWidth - (withRadar ? radarSpace : 0)) / 2;
        if (withRadar)
        {
            DrawPixels(g, start, y, RadarPixels, 0, RadarPixels[0].Length);
            start += radarSpace;
        }
        for (int letter = 0; letter < name.Length; letter++)
        {
            if (name[letter] != ' ')
                DrawPixels(g, start + letter * 6, y, Alphabet[name[letter]], letter * 6, lettersWidth);
        }
    }

    private static void DrawPixels(DashboardGrid g, int x, int y, string[] pixels, int colorOffset, int colorWidth)
    {
        for (int col = 0; col < pixels[0].Length; col++)
        {
            string ink = ConsoleWidgets.GradientStyle(RadarPalette, colorOffset + col, colorWidth);
            for (int row = 0; row < 4; row++)
            {
                char top = pixels[row * 2][col];
                char bottom = row < 3 ? pixels[row * 2 + 1][col] : '0';
                if (top != '0' || bottom != '0')
                    g.Write(x + col, y + row, top == '0' ? "▄" : bottom == '0' ? "▀" : "█",
                        top == '2' || bottom == '2' ? "#DCFFD8" : ink);
            }
        }
    }

    private static string Abbreviate(string text, int width) => TerminalText.Abbreviate(text, width);

    private WorkspaceTileViewModel ProjectTile(IndexedRepo repo) =>
        WorkspaceTileViewModel.Create(repo, RemoteName(repo), options.Pinned.Contains(repo.Path, paths),
            options.ShowCounts, options.ShowFiles, CheckedAt(repo));

    private const string CardInfoHint = "[I] Info";
    private const string MarkedStyle = "bold #07242A on #3CF8FF";

    private void DrawSignalCard(DashboardGrid g, WorkspaceTileViewModel tile, int x, int y, int width, int height, bool focused,
        bool infoAvailable = true, bool marked = false)
    {
        if (height < 4) return;
        string ink = Tone(tile.Category);
        Frame(g, x, y, width, height, focused ? FocusCyan : Muted);
        int end = x + width - 2;
        int bottom = y + height - 1;
        int left = x + 3;
        int titleX = left + (infoAvailable ? 4 : 2);
        string badge = StatusMark(tile.Category) + " " + tile.Badge;
        bool boxed = width >= 60 && height >= 7;
        if (!boxed)
            badge = "[" + (width >= 48 ? tile.Badge : Label(tile.Category).ToUpperInvariant()) + "]";
        int badgeWidth = DashboardGrid.Measure(badge) + (boxed ? 4 : 0);
        int badgeX = end - badgeWidth;
        ConsoleWidgets.Badge(g, badgeX, y + 1, badgeWidth, badge, ink, boxed);
        int infoRow = y + (boxed ? 4 : 2);
        int infoX = end - CardInfoHint.Length;
        bool showInfo = infoAvailable && width >= 38 && infoRow < bottom;
        int LineEnd(int line) => showInfo && line == infoRow ? infoX - 2 : end;
        if (showInfo) g.Write(infoX, infoRow, CardInfoHint, focused ? "bold " + FocusCyan : Muted);

        string titleStyle = "bold " + (focused ? FocusTitle + FocusBackground : Foreground);
        if (focused)
        {
            g.Fill(x + 1, y + 1, badgeX - x - 1, ' ', FocusCyan + FocusBackground);
            g.Write(x + 1, y + 1, FocusMark, "bold " + FocusCyan + FocusBackground);
        }
        g.Write(left, y + 1, infoAvailable ? (marked ? "[X]" : "[ ]") : "◇",
            marked ? MarkedStyle : focused ? FocusTitle + FocusBackground : Muted);
        g.Write(titleX, y + 1, Abbreviate(tile.Title, badgeX - titleX - 1), titleStyle, badgeX - titleX - 1);
        if (options.ShowPath)
        {
            int pathWidth = (boxed ? badgeX - 1 : LineEnd(y + 2)) - titleX;
            g.Write(titleX, y + 2, Abbreviate(tile.Path, pathWidth), Muted, pathWidth);
        }

        int row = y + (boxed ? 4 : options.ShowPath ? 3 : 2);
        void Line(string content, string style)
        {
            if (row >= bottom) return;
            int span = LineEnd(row) - left;
            g.Write(left, row++, DashboardGrid.Fit(content, span), style, span);
        }

        if (height >= 8 && tile.Category != "Green" && (tile.Category != "Red" || options.ShowFiles))
            Line(tile.Category == "Amber" ? "Clean working tree · branch histories aligned" : tile.Condition, Foreground);

        if (row < bottom)
        {
            g.Write(left, row, "↳", Muted);
            int branchX = left + 2;
            string counts = options.ShowCounts ? $"  │  ↑{Value(tile.Ahead)} ↓{Value(tile.Behind)}" : "";
            string branches = options.ShowBranches ? $"  ·  {tile.Branches}" : "";
            int lineEnd = LineEnd(row);
            int branchWidth = Math.Max(7, lineEnd - branchX - DashboardGrid.Measure(counts + branches));
            string branch = DashboardGrid.Fit(tile.Branch, branchWidth);
            g.Write(branchX, row, branch, "bold " + (tile.Ahead == 0 && tile.Behind == 0 ? Good : ink), lineEnd - branchX);
            int detailsX = branchX + DashboardGrid.Measure(branch);
            g.Write(detailsX, row++, DashboardGrid.Fit(counts + branches, lineEnd - detailsX), Foreground, lineEnd - detailsX);
        }

        string footer = options.ShowChecked ? $"▣ Last checked {tile.LastChecked}" : "";
        if (options.ShowFiles)
            footer += (footer.Length > 0 ? "  ·  " : "") + tile.Files;
        if (footer.Length > 0) Line(footer, Muted);
        if (options.ShowBranches && tile.Tracking.Length > 0) Line(tile.Tracking, Tone("Gone"));
    }

    private TerminalViewport? DrawCards(DashboardGrid g, int startY, bool flat)
    {
        int width = g.Width - 4;
        int minWidth = flat ? 40 : TileMinimumWidth(options.Size);
        int maxColumns = options.Size == 2 ? 3 : 8;
        int columns = Math.Clamp((width + 2) / minWidth, 1, maxColumns);
        int space = FooterTop(g) - startY - 1;
        if (space < 4)
        {
            g.Write(3, startY, "Increase terminal height to see the tiles.", Muted);
            return null;
        }
        int cardHeight = Math.Min(flat ? 6 : TileHeight(options.Size), space);
        int aligned = filter == "Overview" ? visible.TakeWhile(r => DisplayStatus(r) == "Green").Count() : 0;
        var plan = new TerminalViewport(new ViewportBounds(2, startY, width, space), columns, cardHeight,
            columnGap: 2, rowGap: 1, visible.Count, selected, offset, firstGroupCount: aligned);

        if (visible.Count == 0)
        {
            g.Write(3, startY + 1, bulkSelectedOnly ? "No repositories are marked. Press Y to see the workspace." :
                all.Count == 0 ? "Scan a workspace to discover its repositories." :
                filter == "Sync" && search.Length == 0 ? "No verified commit differences" : "No repositories match this view.", "bold " + Foreground);
            g.Write(3, startY + 2, all.Count == 0 ? "[S] Scan  [O] Choose folder" :
                $"[Esc] Overview  [Tab] Filter  [F5] Check remotes  ·  {scoped.Count(r => InFilter(r, "Unknown"))} unverified", Muted);
            return plan;
        }

        foreach (var cell in plan.Cells)
        {
            int x = cell.X, y = cell.Y, span = cell.Width;
            var r = visible[cell.Index];
            bool focused = cell.Index == selected;
            bool marked = bulkMarked.Contains(r.Path);
            var tile = ProjectTile(r);
            if (!flat)
            {
                DrawSignalCard(g, tile, x, y, span, cell.Height, focused, marked: marked);
                continue;
            }

            string ink = Tone(tile.Category);
            if (focused)
            {
                g.Fill(x, y, span, ' ', FocusCyan + FocusBackground);
                g.Write(x, y, FocusMark, "bold " + FocusCyan + FocusBackground);
            }
            g.Write(x + 2, y, marked ? "[X]" : "[ ]", marked ? MarkedStyle : focused ? Muted + FocusBackground : Muted);
            g.Write(x + 6, y, Abbreviate(tile.Title, span - 6),
                "bold " + (focused ? FocusTitle + FocusBackground : Foreground), span - 6);
            if (options.ShowPath) g.Write(x + 2, y + 1, Abbreviate(tile.Path, span - 2), Muted);
            if (options.ShowCounts) g.Write(x + 2, y + 2, "Ahead " + Value(tile.Ahead) + "   Behind " + Value(tile.Behind), Foreground);
            string condition = tile.Category == "Red" && !options.ShowFiles ? tile.Badge : tile.Condition;
            bool showInfo = cell.Height >= 4 && span >= 38;
            int conditionWidth = span - (showInfo ? CardInfoHint.Length + 4 : 2);
            g.Write(x + 2, y + 3, Abbreviate(condition, conditionWidth), ink, conditionWidth);
            if (showInfo)
                g.Write(x + span - CardInfoHint.Length, y + 3, CardInfoHint, focused ? "bold " + FocusCyan : Muted);

        }

        DrawPage(g, FooterTop(g) - 1, plan.Cells.Count, plan.FirstVisibleIndex, visible.Count);
        return plan;
    }

    private static int TileMinimumWidth(int size) => size switch { 0 => 41, 2 => 74, _ => 66 };
    private static int TileHeight(int size) => size switch { 0 => 7, 2 => 11, _ => 8 };
    private static string Value(int count) => count < 0 ? "?" : count.ToString();

    private static IEnumerable<string> Wrap(string? value, int max) => TerminalText.Wrap(value, max);

    private void DrawDialog(DashboardGrid g)
    {
        if (view == View.Browser) { DrawPicker(g); return; }
        if (view == View.About) { DrawAbout(g); return; }
        if (view == View.Layout) { DrawTileSettings(g); return; }
        if (view == View.Info) { DrawRepositoryInfo(g); return; }
        if (view == View.Operations) { DrawBulkOperations(g); return; }
        string title = view switch
        {
            View.Refresh => "Refresh remote status",
            View.Details => "Repository Detail · " + visible[selected].Name,
            View.PathEntry => "Go to path", View.Search => "Find repositories", _ => "Attention"
        };
        bool details = view == View.Details;
        var (x, y, width, height) = Dialog(g, details ? Math.Max(104, g.Width * 4 / 5) : 74,
            details ? Math.Max(28, g.Height * 3 / 4) : 10);
        int left = x + 3, inner = width - 6, row = y + 3, foot = y + height - 3;
        Frame(g, x, y, width, height);
        g.Write(left, y + 1, title, "bold " + Cyan, inner);
        g.Fill(left, y + 2, inner, '─', Border);
        void Line(string text, string style = Foreground)
        {
            if (row < foot) g.Write(left, row++, text, style, inner);
        }
        void Wrapped(string text, string style = Foreground)
        {
            foreach (var line in Wrap(text, inner)) Line(line, style);
        }

        string keys;
        switch (view)
        {
            case View.Refresh:
                Wrapped($"Fetch tracked remotes for {all.Count} indexed repositories?");
                Wrapped(root, Cyan);
                Wrapped("Download Git objects and refresh tracking references.", Muted);
                keys = "[Enter] Fetch    [Esc] Back";
                break;
            case View.Details:
                DrawRepositoryDetails(g, left, row, inner, foot);
                keys = "[↑↓] Scroll details   [Esc] Return";
                break;
            case View.PathEntry:
            case View.Search:
                Wrapped(view == View.PathEntry ? "Absolute path to a file or folder" : "Repository name, remote, path or branch issue");
                Line("› " + Abbreviate(input, inner - 3) + "▏", Cyan);
                if (view == View.PathEntry && browseError.Length > 0) Wrapped(browseError, Gold);
                keys = "[Enter] Apply    [Esc] Cancel";
                break;
            default:
                Wrapped(notice, Gold);
                keys = "[Any key] Back";
                break;
        }
        g.Fill(left, foot, inner, '─', Border);
        g.Write(left, foot + 1, keys, "bold " + Foreground, inner);
    }

}
