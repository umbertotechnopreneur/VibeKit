using static WorkspaceRepo.DashboardTerminal;

namespace WorkspaceRepo;

public sealed partial class Dashboard
{
    private const string Panel = "#0D1218", Selected = "#082126";
    private static readonly string[] OverviewOrder = ["Green", "Push", "Purple", "Gone", "Red", "Unknown"];
    private static readonly string[] TableOrder = ["Green", "ExtraBranches", "LocalChanges", "RemoteChanges", "Unknown"];
    private static readonly string[] Spinner = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    private TerminalViewport? PaintWorkspace(DashboardGrid g)
    {
        if (view == View.Help)
        {
            int brandBottom = DrawBrand(g);
            DrawToolbar(g, brandBottom, tab);
            DrawHelpPage(g, brandBottom + 3);
            DrawHelpFooter(g);
            return null;
        }
        var scan = scanSession?.Capture();
        TerminalViewport? plan = null;
        if (view == View.Consent) DrawWelcome(g);
        else
        {
            int top = DrawBrand(g);
            var shown = view is View.Browser or View.Busy ? WorkspaceTab.Repositories : tab;
            bool table = shown != WorkspaceTab.Dashboard;
            var items = table ? model.Rows(shown, scan) : null;
            bool panels = (view is View.About or View.Browser) && g.Height >= 37 && g.Width >= 95;
            int tableBottom = FooterTop(g) - (panels ? 13 : g.Height < 13 ? 1 : 2);
            int first = DrawWorkspaceHeader(g, top, shown, items?.Count ?? visible.Count, scan, tableBottom);
            plan = items != null
                ? DrawRepositoryTable(g, first, tableBottom, shown, items, panels)
                : DrawCards(g, first, presentation == 2);
            if (view == View.Busy && scan != null) DrawScanPopup(g, first, scan);
            else if (view != View.Grid) DrawDialog(g);
        }
        DrawFooter(g, scan);
        // A scan or picker can draw a different set of rows behind its overlay.
        return view == View.Grid ? plan : null;
    }

    private static string StatusText(string health) => health switch
    {
        "Green" => "Clean and aligned", "Amber" => "Branch inventory", "Red" => "File changes",
        "Push" => "Commits to push", "Both" => "Push and pull", "Purple" => "Commits to pull",
        "Gone" => "Upstream gone", "Discovered" => "Awaiting inspection", _ => "Unverified"
    };

    private static string StatusMark(string health) => !global::Ui.UseEmoji ? health switch
    {
        "Green" => "+", "Amber" or "Gone" => "!", "Red" => "*", "Both" => "!", "Push" => "^", "Purple" => "v", _ => "?"
    } : health switch
    {
        "Green" => "✅", "Amber" or "Gone" => "△", "Red" => "✎", "Both" => "❌", "Push" => "↑", "Purple" => "↓", _ => "?"
    };

    private static int MarkWidth(string health) => DashboardGrid.Measure(StatusMark(health));

    private static string CardTone(IndexedRepo repo) => RepositoryAlignment.Of(repo).Status;

    private static string Counts(IndexedRepo r) => $"↑{(r.Ahead < 0 ? "?" : r.Ahead)}  ↓{(r.Behind < 0 ? "?" : r.Behind)}";

    private static string CheckedAt(IndexedRepo r, bool remote = false)
    {
        if (!DateTimeOffset.TryParse(remote ? r.RemoteVerifiedUtc : r.LastCheckedUtc, out var time)) return "—";
        var age = DateTimeOffset.UtcNow - time;
        if (age.TotalMinutes < 1) return "just now";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes} min ago";
        if (age.TotalDays < 1) return $"{(int)age.TotalHours} h ago";
        return time.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    private int DrawWorkspaceHeader(DashboardGrid g, int top, WorkspaceTab shown, int count, WorkspaceScanSnapshot? scan, int tableBottom)
    {
        DrawToolbar(g, top, shown);
        if (shown != WorkspaceTab.Dashboard)
            return DrawTableHeader(g, top, count, scan, tableBottom);
        string heading = bulkSelectedOnly ? "Selected only" :
            filter switch { "Overview" => "Aligned first", "Sync" => "Out of sync", _ => StatusText(filter) };
        if (g.Width >= 112 && g.Height >= 45)
        {
            DrawLargeHeader(g, top, heading);
            DrawCounters(g, top + 7, separate: false, described: true);
            return top + 11;
        }
        string scope = "Scope  " + root;
        int scopeWidth = Math.Min(g.Width / 3, Math.Max(14, scope.Length + 2));
        g.Write(2, top + 3, "Dashboard", "bold " + Foreground);
        const int titleWidth = 10;
        g.Write(titleWidth + 2, top + 3, "— " + heading, "bold " + (filter == "Overview" ? Good : Cyan), g.Width - scopeWidth - titleWidth - 5);
        g.Write(g.Width - scopeWidth - 1, top + 3, Abbreviate(scope, scopeWidth), Cyan, scopeWidth);
        if (g.Height < 26)
        {
            int contentStartLimit = FooterTop(g) - 5; // A four-row card and its pagination.
            if (top + 5 > contentStartLimit) return top + 4;
            if (g.Height < 18 || top + 7 > contentStartLimit) return top + 5;
            DrawCounters(g, top + 5, separate: false, compact: true);
            return top + 7;
        }
        string meta = $"Repositories  {scoped.Count}    ·    Showing  {visible.Count}    ·    Last scan  {(scoped.Count == 0 ? "—" : CheckedAt(scoped.MaxBy(r => r.LastCheckedUtc)!))}";
        g.Write(2, top + 4, search.Length == 0 ? meta : meta + "    ·    Find  " + search, Muted);
        DrawCounters(g, top + 6, separate: false);
        return top + (g.Width >= 94 ? 10 : 8);
    }

    private void DrawToolbar(DashboardGrid g, int top, WorkspaceTab shown)
    {
        g.Fill(1, top, g.Width - 2, '─', Border);
        g.Fill(1, top + 2, g.Width - 2, '─', Border);
        var sections = new (string Name, string Short, string Key, WorkspaceTab? Tab)[]
        {
            ("Dashboard", "Dash", "1", WorkspaceTab.Dashboard),
            ("Repositories", "Repos", "2", WorkspaceTab.Repositories),
            ("Branches", "Branch", "B", WorkspaceTab.Branches),
            ("Remotes", "Rem", "M", WorkspaceTab.Remotes),
            ("Operations", "Ops", "3", null),
            ("Settings", "Set", "4", null)
        };
        bool compact = g.Width < 111;
        bool small = g.Width < 57;
        const string help = "[F1] Help";
        int helpX = g.Width - help.Length - 2;
        int x = 2;
        foreach (var section in sections)
        {
            bool active = view switch
            {
                View.Help => false,
                View.Layout => section.Key == "4",
                View.Operations => section.Key == "3",
                _ => section.Tab == shown
            };
            string label = small ? section.Short : section.Key + " " + (compact ? section.Short : section.Name);
            if (active) label = "[" + label + "]";
            if (x + label.Length >= helpX) break;
            g.Write(x, top + 1, label, active ? "bold " + Cyan + " on " + Selected : Foreground);
            x += label.Length + (compact ? 1 : 2);
            if (!compact && x < helpX - 1)
            {
                g.Write(x, top + 1, "│", Border);
                x += 3;
            }
        }
        g.Write(helpX, top + 1, help, view == View.Help ? "bold " + FocusTitle + FocusBackground : Cyan);
    }

    private int DrawTableHeader(DashboardGrid g, int top, int count, WorkspaceScanSnapshot? scan, int tableBottom)
    {
        string countLabel = $"Repositories  {count:N0}";
        string scanLabel = "Last scan  " + (model.Latest is { } latest ? CheckedAt(latest) : "—");
        string suffix = g.Width >= 112 ? $"  │  {countLabel}  │  {scanLabel}" : $"  │  {countLabel}";
        int rootWidth = Math.Max(0, g.Width - suffix.Length - 9);
        string location = DashboardGrid.Fit(root, rootWidth);
        g.Write(2, top + 3, "Root", Foreground);
        g.Write(8, top + 3, location, "bold " + Cyan);
        g.Write(8 + DashboardGrid.Measure(location), top + 3, suffix, Foreground);
        // Leave room for the table's heading, border and at least one data row before adding counters.
        int headerRows = tableBottom - top - 4;
        if (g.Height < 18 || headerRows < 7) return top + 4;
        bool expanded = headerRows >= 9 && g.Width >= 94 && g.Height >= 24;
        DrawCounters(g, top + 5, separate: true, compact: !expanded, scan: scan);
        return top + (expanded ? 9 : 7);
    }

    private void DrawLargeHeader(DashboardGrid g, int top, string heading)
    {
        int scopeWidth = Math.Clamp(g.Width / 3, 36, 65);
        int scopeX = g.Width - scopeWidth - 1;
        g.Write(2, top + 3, "Dashboard — ", "bold " + Foreground);
        g.Write(14, top + 3, heading, "bold " + (filter == "Overview" ? Good : Cyan));
        string explanation = filter switch
        {
            "Overview" => "Clean and aligned first. Then commit differences, missing upstreams and file changes.",
            "Sync" => "Verified commits ahead or behind, including on secondary branches.",
            "Gone" => "Local branches whose upstream disappeared from the remote.",
            "Green" => "Clean working copies with an aligned branch and no detected drift.",
            "Unknown" => "Repositories whose remote status still needs verification.",
            "Purple" => "Repositories with commits available from their upstream.",
            "Push" => "Repositories with commits to publish to their tracked upstream.",
            "Amber" => "Additional branches and branch observations.",
            _ => "Repositories with modified or untracked files."
        };
        g.Write(2, top + 4, explanation, Foreground, scopeX - 5);
        string lastScan = scoped.Count == 0 ? "—" : CheckedAt(scoped.MaxBy(r => r.LastCheckedUtc)!);
        string totals = $"{visible.Count} shown of {scoped.Count}    ·    Checked {lastScan}";
        g.Write(2, top + 5, search.Length > 0 ? totals + "    ·    Find " + search : totals, Muted, scopeX - 5);
        g.Box(scopeX, top + 3, scopeWidth, 4, Border);
        g.Write(scopeX + 2, top + 4, "Scope", "bold " + Foreground);
        g.Write(scopeX + 9, top + 4, Abbreviate(root, scopeWidth - 13), "bold " + Cyan);
        string hint = scopeWidth >= 46 ? $"{heading}   [Ctrl+L] Clear   [Esc] Overview" : heading + "   [Esc] Overview";
        g.Write(scopeX + 2, top + 5, hint, Muted, scopeWidth - 4);
    }

    private void DrawCounters(DashboardGrid g, int row, bool separate, bool described = false, bool compact = false, WorkspaceScanSnapshot? scan = null)
    {
        int width = g.Width - 2;
        var order = separate ? TableOrder : OverviewOrder;
        int countColumns = order.Length;
        if (g.Width < 94 || compact)
        {
            for (int i = 0; i < order.Length; i++)
            {
                string key = order[i], mark = CounterMark(key), ink = CounterTone(key);
                int left = 2 + (width - 2) * i / countColumns;
                int span = 2 + (width - 2) * (i + 1) / countColumns - left;
                string count = model.Count(key, scan).ToString();
                string label = key switch
                {
                    "Green" => "OK", "Gone" => "Gone", "Push" => "Push", "Purple" => "Pull", "Red" => "Files",
                    "ExtraBranches" => "Branches", "LocalChanges" => "Local", "RemoteChanges" => "Remote", _ => "Check"
                };
                g.Write(left, row, mark, ink);
                int textX = left + DashboardGrid.Measure(mark) + 1;
                string summary = label.Length + count.Length + 1 <= left + span - textX - 1 ? label + " " + count : count;
                g.Write(textX, row, summary, "bold " + ink);
            }
            return;
        }

        if (!separate) g.Box(1, row, width, described ? 4 : 3, Foreground);
        for (int i = 0; i < order.Length; i++)
        {
            string key = order[i], mark = CounterMark(key), ink = CounterTone(key);
            int left = 1 + width * i / countColumns, span = 1 + width * (i + 1) / countColumns - left;
            if (separate) g.Box(left, row, span - 1, 3, ink);
            else if (i > 0)
            {
                g.Write(left, row + 1, "│", Border);
                if (described) g.Write(left, row + 2, "│", Border);
            }
            string label = separate ? TableStatusText(key) : span >= 25 ? StatusText(key) : Label(key);
            string count = model.Count(key, scan).ToString();
            g.Write(left + 2, row + 1, mark, "bold " + ink);
            int labelX = left + 3 + DashboardGrid.Measure(mark);
            int countX = left + span - count.Length - (separate ? 3 : 2);
            int labelSpace = countX - labelX - 1;
            if (separate && DashboardGrid.Measure(label) > labelSpace) label = ShortTableStatus(key);
            g.Write(labelX, row + 1, DashboardGrid.Fit(label, labelSpace), "bold " + ink);
            g.Write(countX, row + 1, count, "bold " + ink);
            if (described)
            {
                var (full, shortText) = key switch
                {
                    "Green" => ("Clean files and matching branch histories", "No changes"),
                    "Red" => ("Modified or untracked files in the working tree", "Local file edits"),
                    "Push" => ("Local commits waiting to be published", "Local commits"),
                    "Purple" => ("Remote commits ready to be integrated", "Remote commits"),
                    "Gone" => ("A local branch lost its remote counterpart", "Remote branch gone"),
                    _ => ("The remote or tracked branch could not be verified", "Needs a remote check")
                };
                int available = left + span - labelX - 2;
                if (full.Length <= available) g.Write(labelX, row + 2, full, Muted);
                else if (shortText.Length <= available) g.Write(labelX, row + 2, shortText, Muted);
            }
        }
    }

    private static string CounterTone(string key) => key switch
    {
        "ExtraBranches" => Tone("Amber"),
        "LocalChanges" => "#F58B96",
        "RemoteChanges" => Tone("Purple"),
        _ => Tone(key)
    };

    private static string CounterMark(string key) => key switch
    {
        "Green" => global::Ui.UseEmoji ? "✓" : "+",
        "ExtraBranches" => global::Ui.UseEmoji ? "△" : "!",
        "LocalChanges" => global::Ui.UseEmoji ? "✎" : "*",
        "RemoteChanges" => global::Ui.UseEmoji ? "↓" : "v",
        _ => StatusMark(key)
    };

    private static string TableStatusText(string key) => key switch
    {
        "Green" => "Synchronized",
        "Amber" or "ExtraBranches" => "Extra branches",
        "Red" or "Push" or "LocalChanges" => "Local changes",
        "Purple" or "RemoteChanges" => "Remote changes",
        _ => StatusText(key)
    };

    private static string ShortTableStatus(string key) => key switch
    {
        "Green" => "Synced", "ExtraBranches" or "Amber" => "Branches",
        "LocalChanges" or "Red" or "Push" => "Local", "RemoteChanges" or "Purple" => "Remote",
        "Discovered" => "Found", _ => Label(key)
    };

    private static string TableRowTone(string key) => key switch
    {
        "Red" => CounterTone("LocalChanges"),
        _ => CounterTone(key)
    };

    private TerminalViewport? DrawRepositoryTable(DashboardGrid g, int top, int bottom, WorkspaceTab shown,
        IReadOnlyList<WorkspaceTableRow> items, bool panels)
    {
        if (bottom - top < 4) return null;

        const int left = 1;
        int right = g.Width - 2;
        int width = right - left + 1;
        bool repositories = shown == WorkspaceTab.Repositories;
        bool showLocation = !repositories || g.Width >= 95;
        bool showBranch = repositories || g.Width >= 95;
        bool showCounts = repositories || g.Width >= 105;
        int indexWidth = g.Width >= 70 ? 10 : 8;
        int countsWidth = !showCounts ? 0 : g.Width >= 105 ? 15 : 8;
        int statusWidth = g.Width switch { >= 105 => 22, >= 65 => 15, _ => 9 };
        int flexible = right - left - indexWidth - countsWidth - statusWidth;
        int nameWidth = flexible * (showLocation ? (showBranch ? 34 : 40) : 63) / 100;
        int locationWidth = !showLocation ? 0 : showBranch ? flexible * 42 / 100 : flexible - nameWidth;

        int name = left + indexWidth;
        int location = name + nameWidth;
        int branch = location + locationWidth;
        int condition = right - countsWidth - statusWidth;
        int drift = right - countsWidth;
        int[] separators = new[] { name, location, branch, condition, drift }.Distinct().Where(x => x < right).ToArray();

        string locationTitle = shown switch
        {
            WorkspaceTab.Remotes => "Remote URL",
            WorkspaceTab.Branches => "Local / Remote",
            _ => "Path"
        };

        g.Box(left, top, width, bottom - top + 1, Border);
        void Cell(int x, int end, int y, string value, string style, bool middle = false)
        {
            int available = end - x - 2;
            string text = middle ? Abbreviate(value, available) : DashboardGrid.Fit(value, available);
            g.Write(x + 1, y, text, style, available);
        }

        void Rule(int y, bool focused = false)
        {
            string ink = focused ? FocusCyan : Border;
            g.Fill(left + 1, y, width - 2, '─', ink);
            foreach (int separator in separators) g.Write(separator, y, "┼", ink);
            g.Write(left, y, "├", ink);
            g.Write(right, y, "┤", ink);
        }

        const string heading = "bold " + Foreground;
        Cell(left, name, top + 1, "[ ] #", heading);
        Cell(name, location, top + 1, "Repository", heading);
        if (showLocation) Cell(location, branch, top + 1, locationTitle, heading);
        if (showBranch) Cell(branch, condition, top + 1, "Branch", heading);
        Cell(condition, drift, top + 1, "Status", heading);
        if (showCounts) Cell(drift, right, top + 1, g.Width >= 105 ? "Ahead/Behind" : "↑ / ↓", heading);
        foreach (int separator in separators)
        {
            g.Write(separator, top, "┬", Border);
            g.Write(separator, top + 1, "│", Border);
        }
        Rule(top + 2);

        int stride = g.Height >= 37 ? 2 : 1;
        int firstRow = top + 3;
        var plan = new TerminalViewport(new ViewportBounds(left, firstRow, width, bottom - firstRow),
            columns: 1, itemHeight: 1, columnGap: 0, rowGap: stride - 1, items.Count, selected, offset);

        if (items.Count == 0)
            g.Write(name + 1, firstRow, view == View.Busy
                ? "Repositories appear as the scan finds them."
                : "No repositories match this view.", Muted, right - name - 2);

        foreach (var cell in plan.Cells)
        {
            var item = items[cell.Index];
            int y = cell.Y;
            bool focused = selected == cell.Index;
            string background = focused ? FocusBackground : "";
            if (focused) g.Fill(left + 1, y, width - 2, ' ', FocusCyan + background);

            // The row number and data share a single highlighted band.
            string rowNumber = (focused ? FocusMark : " ") + "    " + (cell.Index + 1).ToString(g.Width >= 70 ? "D2" : "0");
            Cell(left, name, y, rowNumber, (focused ? "bold " + FocusCyan : Muted) + background);
            bool marked = bulkMarked.Contains(item.Path);
            g.Write(left + 2, y, marked ? "[X]" : "[ ]", marked ? MarkedStyle : Muted + background, 3);
            Cell(name, location, y, item.Name, "bold " + (focused ? FocusTitle : Cyan) + background, middle: true);
            if (showLocation) Cell(location, branch, y, item.Location, Foreground + background, middle: true);
            if (showBranch) Cell(branch, condition, y, item.Branch, Foreground + background, middle: true);
            string status = TableStatusText(item.Status);
            if (DashboardGrid.Measure(status) > statusWidth - 5) status = ShortTableStatus(item.Status);
            Cell(condition, drift, y, CounterMark(item.Status) + " " + status, TableRowTone(item.Status) + background);
            if (showCounts) Cell(drift, right, y, item.Counts, Foreground + background);
            foreach (int separator in separators) g.Write(separator, y, "│", Border + background);
            if (focused)
            {
                g.Write(left, y, "│", FocusCyan);
                g.Write(right, y, "│", FocusCyan);
            }
            if (stride == 2 && y + 1 < bottom) Rule(y + 1, focused);
            if (stride == 2 && focused) Rule(y - 1, focused: true);
        }

        int emptyStart = firstRow + Math.Max(1, plan.Cells.Count * stride);
        foreach (int separator in separators)
        {
            for (int y = emptyStart; y < bottom; y++) g.Write(separator, y, "│", Border);
            g.Write(separator, bottom, "┴", Border);
        }
        if (bottom + 1 < FooterTop(g)) DrawPage(g, bottom + 1, plan.Cells.Count, plan.FirstVisibleIndex, items.Count);
        if (panels) DrawTablePanels(g, bottom + 3);
        return plan;
    }

    private void DrawTablePanels(DashboardGrid g, int y)
    {
        int width = Math.Min(34, g.Width / 3);
        Frame(g, 1, y, width, 9, Border);
        g.Write(3, y + 1, "Legend (Git status)", "bold " + Cyan);
        for (int i = 0; i < TableOrder.Length; i++)
        {
            string key = TableOrder[i];
            g.Write(3, y + 3 + i, $"{CounterMark(key)} {TableStatusText(key)}  {model.Count(key)}", CounterTone(key), width - 4);
        }
        int right = g.Width - width - 1;
        Frame(g, right, y, width, 9, Border);
        g.Write(right + 2, y + 1, view == View.Browser ? "Shortcuts" : "Last checked", "bold " + Cyan);
        var lines = view == View.Browser ? new[] { "[↑↓] Move selection", "[↵] Open", "[Space] Choose folder", "[Tab] Switch pane", "[G] Go to path" } :
            scoped.OrderByDescending(r => r.LastCheckedUtc).Take(4).Select(r => $"{RemoteName(r)} · {CheckedAt(r)}").ToArray();
        for (int i = 0; i < lines.Length; i++)
            g.Write(right + 2, y + 3 + i, Abbreviate(lines[i], width - 4), Muted);
    }

    private static void DrawPage(DashboardGrid g, int row, int displayed, int start, int count)
    {
        string page = count == 0 ? "0 repositories" : $"{start + 1}–{Math.Min(count, start + displayed)} of {count}  ↑ ↓";
        int x = g.Width - page.Length - 3;
        g.Fill(2, row, x - 4, '─', Border);
        g.Write(x, row, page, Muted);
    }

    private static readonly ShortcutHint[] WorkspaceShortcuts =
        [new("Space", "Mark"), new("3", "Operations"), new("Y", "Selected only"), new("Ctrl+A", "Mark visible"),
         new("Ctrl+X", "Clear"), new("I", "Info"), new("S", "Scan"), new("F5", "Fetch"), new("O", "Folder"),
         new("Enter", "Details"), new("V", "Out of sync"), new("G", "Gone"), new("D", "Files"), new("Tab", "Filter"),
         new("W", "View"), new("A", "About"), new("Q", "Quit")];
    private static readonly ShortcutHint[] CompactShortcuts =
        [new("Space", "Mark"), new("3", "Operations"), new("Y", "Selected only"), new("I", "Info"), new("S", "Scan"),
         new("F5", "Fetch"), new("O", "Folder"), new("Enter", "Details")];

    private void DrawFooter(DashboardGrid g, WorkspaceScanSnapshot? scan)
    {
        int top = FooterTop(g);
        g.Fill(1, top, g.Width - 2, '─', Border);
        if (g.Height >= 20)
        {
            if (view == View.Grid)
                ConsoleWidgets.Shortcuts(g, 2, top + 1, g.Width - 4,
                    g.Width >= 112 ? WorkspaceShortcuts : CompactShortcuts, "bold " + Cyan, Foreground);
            else if (view == View.Busy) g.Write(2, top + 1, scan?.Cancelling == true ? "Cancelling…" : "[Esc] Cancel", Cyan);
            Frame(g, 1, top + 2, g.Width - 2, 3, view == View.Busy ? null : Cyan);
        }
        int y = g.Height - 2;
        string label = view switch
        {
            View.Busy when scan != null => $"{Spinner[scan.Frame % Spinner.Length]}  {scan.Footer}",
            View.Consent => $"Ready  ·  Awaiting consent to scan {root}",
            _ when status == "Ready" => $"Ready  ·  {visible.ElementAtOrDefault(selected)?.Path ?? root}",
            _ => status
        };
        if (view == View.Grid)
        {
            int outside = bulkMarked.Count - visible.Count(repo => bulkMarked.Contains(repo.Path));
            string marks = $"{bulkMarked.Count} selected" + (outside > 0 ? $" · {outside} outside view" : "");
            if (bulkSelectedOnly) marks += " · Selected only";
            if (bulkQueue.Count > 0) marks += $" · {bulkQueue.Count} queued";
            label = marks + "  │  " + label;
        }
        const string credit = "[A] About " + ProductBrand.Name;
        bool showCredit = view == View.Grid && g.Width >= 110 && g.Height >= 20;
        ConsoleWidgets.StatusLine(g, 3, y, g.Width - 7, label, view == View.Busy ? Cyan : Foreground,
            showCredit ? credit : null, Muted);
    }

    private static int FooterTop(DashboardGrid g) => g.Height - (g.Height >= 20 ? 5 : 2);

    private static void Frame(DashboardGrid g, int x, int y, int width, int height, string? ink = null)
        => ConsoleWidgets.Frame(g, x, y, width, height, ink ?? Cyan, ink is null ? Palette : null);

    private static void Center(DashboardGrid g, int x, int y, int width, string text, string style = Foreground)
        => ConsoleWidgets.Center(g, x, y, width, text, style);
}
