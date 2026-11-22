using static WorkspaceRepo.DashboardTerminal;

namespace WorkspaceRepo;

public sealed partial class Dashboard
{
    private static DialogBounds Dialog(DashboardGrid g, int preferredWidth, int preferredHeight)
        => ConsoleWidgets.PlaceDialog(g, preferredWidth, preferredHeight, g.Height >= 28 ? 6 : 1, FooterTop(g));

    private void DrawWelcome(DashboardGrid g)
    {
        g.Write(2, 0, ProductBrand.Name, "bold " + Cyan);
        int creditX = 4 + DashboardGrid.Measure(ProductBrand.Name);
        string help = g.Width >= 72 ? $"v{(global::Entry.Version)}  │  [F1] Help" : "[F1] Help";
        int helpX = g.Width - DashboardGrid.Measure(help) - 2;
        g.Write(creditX, 0, ProductBrand.AuthorCredit, Foreground, Math.Max(0, helpX - creditX - 2));
        g.Write(helpX, 0, help, Cyan);
        g.Fill(1, 1, g.Width - 2, '─', Border);
        if (g.Width >= 120)
        {
            int column = 2;
            foreach (string key in OverviewOrder)
            {
                string label = StatusMark(key) + " " + StatusText(key);
                g.Write(column, 2, label, Tone(key));
                column += label.Length + 3;
            }
            g.Fill(1, 3, g.Width - 2, '─', Border);
        }

        var (x, y, width, height) = Dialog(g, 94, 19);
        Frame(g, x, y, width, height, Violet);
        if (height >= 16)
        {
            DrawWordmark(g, x + 5, y + 2, width - 10);
            Center(g, x + 1, y + 6, width - 2, "──  " + ProductBrand.AuthorCredit + "  ──", Foreground);
            Center(g, x + 1, y + 8, width - 2, all.Count == 0
                ? "First run — Let’s find your Git repositories." : "Scan workspace", "bold " + Foreground);
        }
        if (height >= 13)
        {
            int innerX = x + 3, innerWidth = width - 6;
            Frame(g, innerX, y + height - 9, innerWidth, 4, Cyan);
            Center(g, innerX + 1, y + height - 8, innerWidth - 2, $"⌕  Scan {root} for Git repositories?", "bold " + Foreground);
            Center(g, innerX + 1, y + height - 7, innerWidth - 2, "Includes nested projects and verifies tracked remotes.", Muted);
        }
        else Center(g, x + 2, y + 2, width - 4, $"Scan {root} and check remotes?", "bold " + Foreground);

        string[] labels = ["[ Scan now ]", "[ Choose folder ]", "[ Later ]"];
        int buttonWidth = (width - 4) / 3;
        for (int i = 0; i < labels.Length; i++)
            Center(g, x + 2 + i * buttonWidth, y + height - 4, buttonWidth, labels[i],
                i == consentChoice ? "bold #061316 on #52E1EA" : Foreground);
        Center(g, x + 2, y + height - 2, width - 4, "← → Choose   Enter Confirm   L Local only", Muted);
    }

    private void DrawPicker(DashboardGrid g)
    {
        var (x, y, width, height) = Dialog(g, 112, 26);
        browser?.Draw(g, x, y, width, height);
    }

    private void DrawAbout(DashboardGrid g)
    {
        var bounds = Dialog(g, 94, 23);
        string linkStyle = "underline " + Cyan;
        var lines = new List<ConsoleLine>();
        if (bounds.Height >= 13)
        {
            lines.Add(new($"{ProductBrand.Name} {(global::Entry.Version)}", "bold " + Foreground));
            lines.Add(new(ProductBrand.AuthorCredit, Foreground));
        }
        if (bounds.Height >= 17) lines.Add(new(ProductBrand.Description, Muted));
        lines.Add(new("Member of the VibeWare movement", Foreground, SpaceBefore: true));
        lines.Add(new("[M] Read the manifesto", linkStyle));
        if (bounds.Width >= 54 && bounds.Height >= 13)
            lines.Add(new("[G] GitHub repository    [U] umbertogiacobbi.biz", linkStyle));
        else
        {
            lines.Add(new("[G] GitHub repository", linkStyle));
            if (bounds.Height >= 13) lines.Add(new("[U] umbertogiacobbi.biz", linkStyle));
        }
        if (bounds.Width >= 56)
            lines.Add(new("C# · Spectre.Console · SQLite  │  Open source · MIT", Muted));
        else
        {
            lines.Add(new("C# · Spectre.Console · SQLite", Muted));
            if (bounds.Height >= 15) lines.Add(new("Open source · MIT", Muted));
        }
        var content = new AboutContent("About " + ProductBrand.Name,
            lines,
            new KeyHint("Press ", "[ Escape ]", " to close."), Foreground, Border, "bold " + Violet);
        ConsoleWidgets.About(g, bounds, content, RadarPalette, DrawWordmark);
    }

    private void DrawTileSettings(DashboardGrid g)
    {
        var (x, y, width, height) = Dialog(g, 116, 31);
        Frame(g, x, y, width, height, Cyan);
        int left = x + 3;
        int inner = width - 6;
        int bottom = y + height - 1;
        bool framedActions = width >= 73 && height >= 15;
        int actions = bottom - (framedActions ? 3 : 1);
        bool showProfile = height >= 22;
        int optionsBottom = actions - (showProfile ? 3 : 1);
        bool split = width >= 81 && height >= 18;
        int menuWidth = split ? inner * 3 / 5 - 2 : inner;
        int menuTop = y + (height >= 16 ? 5 : 3);
        g.Write(left, y + 1, "▦  Tile Customization", "bold " + Foreground, inner);
        if (width >= 68) g.Write(x + width - 21, y + 1, "Workspace settings", Muted);
        g.Fill(left, y + 2, inner, '─', Border);
        if (menuTop > y + 3)
            g.Write(left, y + 3, "↑↓ Select an option   ←→ Change   Space Toggle", Muted, inner);

        string density = string.Join("  ", Sizes.Select((size, i) => (i == options.Size ? "◉ " : "○ ") + size));
        string[] labels = [
            "Tile density   " + density,
            "Sort by        " + Sorts[options.Sort],
            $"{Tick(options.ShowPath)}  Repository paths",
            $"{Tick(options.ShowBranches)}  Local and remote branch counts",
            $"{Tick(options.ShowCounts)}  Ahead / behind commit counts",
            $"{Tick(options.IncludeHidden)}  Show hidden repositories",
            $"{Tick(options.ShowFiles)}  Working-tree changes",
            $"{Tick(options.ShowChecked)}  Last checked time",
            $"{Tick(options.PinFirst)}  Pinned repositories first"
        ];
        int page = Math.Max(0, optionsBottom - menuTop);
        int start = Math.Max(0, layoutRow - page + 1);
        for (int i = start; i < labels.Length && i < start + page; i++)
        {
            bool focus = layoutRow == i;
            string style = focus ? "bold " + Cyan + " on " + Selected : Foreground;
            int row = menuTop + i - start;
            if (focus) g.Fill(left, row, menuWidth, ' ', style);
            g.Write(left, row, focus ? "› " : "  ", style, 2);
            g.Write(left + 2, row, DashboardGrid.Fit(labels[i], menuWidth - 2), style, menuWidth - 2);
        }

        if (split)
        {
            int right = left + menuWidth + 3;
            int span = left + inner - right;
            for (int row = menuTop; row < optionsBottom; row++) g.Write(right - 2, row, "│", Border);
            g.Write(right, menuTop, $"Preview ({Sizes[options.Size]})", "bold " + Foreground, span);
            int previewHeight = Math.Min(TileHeight(options.Size), optionsBottom - menuTop - 2);
            var repo = visible.ElementAtOrDefault(selected) ?? all.FirstOrDefault();
            if (repo != null && previewHeight >= 4)
                DrawSignalCard(g, ProjectTile(repo), right, menuTop + 2, span, previewHeight, focused: true, infoAvailable: false);
            else g.Write(right, menuTop + 2, "Scan a folder to preview a tile.", Muted, span);

            int legend = menuTop + previewHeight + 3;
            if (legend + 2 < optionsBottom)
            {
                g.Fill(right, legend++, span, '─', Border);
                g.Write(right, legend++, "Status legend", "bold " + Foreground, span);
                foreach (var category in new[] { "Green", "Amber", "Red", "Purple", "Unknown" })
                {
                    if (legend >= optionsBottom) break;
                    g.Write(right, legend++, StatusMark(category) + " " + TileStatusCaption(category), Tone(category), span);
                }
            }
        }

        if (showProfile)
        {
            g.Fill(left, optionsBottom, inner, '─', Border);
            g.Write(left, optionsBottom + 1, "Settings  " + Abbreviate(settings, inner - 10), Muted, inner);
        }

        if (!framedActions)
        {
            g.Write(left, actions, "[Enter] Apply  [Esc] Cancel  [R] Defaults", Cyan, inner);
            return;
        }

        string[] buttons = ["Apply (Enter)", "Cancel (Esc)", "Restore Defaults (R)"];
        int segment = inner / buttons.Length;
        for (int i = 0; i < buttons.Length; i++)
        {
            int buttonWidth = buttons[i].Length + 4;
            int buttonX = left + i * segment + (segment - buttonWidth) / 2;
            string ink = i == 0 ? Cyan : Muted;
            Frame(g, buttonX, actions, buttonWidth, 3, ink);
            Center(g, buttonX + 1, actions + 1, buttonWidth - 2, buttons[i], i == 0 ? "bold " + Cyan : Foreground);
        }
    }

    private static string TileStatusCaption(string category) => category switch
    {
        "Green" => "Synchronized", "Amber" => "Extra branches", "Red" => "Local changes",
        "Purple" => "Remote changes", _ => "Unverified"
    };

    private static string Tick(bool enabled) => enabled ? "☑" : "☐";

    private void DrawRepositoryDetails(DashboardGrid g, int x, int row, int width, int footer)
    {
        var repo = visible[selected];
        var alignment = RepositoryAlignment.Of(repo);
        if (footer <= row) return;
        g.Write(x, row++, RemoteName(repo), "bold " + Foreground, width);
        int badge = x;
        foreach (var key in OverviewOrder.Where(key => InFilter(repo, key)))
        {
            var label = $"[ {StatusMark(key)} {StatusText(key)} ]";
            int cells = TerminalText.Measure(label);
            if (badge - x + cells > width) break;
            g.Write(badge, row, label, "bold " + Tone(key));
            badge += cells + 2;
        }
        row += 2;
        if (footer <= row) return;
        g.Fill(x, row, width, '─', Border);
        var current = alignment.CurrentComparison;
        g.Write(x, row + 1, $"Branch  {repo.Branch}    │    Ahead  {Value(current?.Ahead ?? -1)}    Behind  {Value(current?.Behind ?? -1)}    │    Branches  {repo.LocalBranches}/{repo.RemoteBranches}", "bold " + Cyan, width);
        row += 3;
        int page = footer - row;
        if (page < 1) return;
        string verified = current != null ? "Verified " + repo.RemoteVerifiedUtc :
            repo.RemoteVerifiedUtc.Length > 0 ? "Unverified · last verified " + repo.RemoteVerifiedUtc : "Unverified";
        string[] fields = ["Remote   " + (repo.RemoteUrl.Length == 0 ? "—" : repo.RemoteUrl), "Path     " + repo.Path,
            "HEAD     " + repo.HeadSha, "Check    " + verified, "Status   " + repo.Reason,
            "CRC-32   " + repo.MetadataCrc32, "SHA-256  " + repo.MetadataSha256];
        var worktree = repo.WorktreeChanges.Select(change => change.Status + "  " + change.Path);
        var fingerprints = repo.Files.Select(f => $"{f.File} · CRC-32 {f.Crc32} · SHA-256 {f.Sha256}");
        var right = new[] { "Local branches" }.Concat(repo.LocalBranchRefs).Concat(["", "Remote branches"]).Concat(repo.RemoteBranchRefs)
            .Concat(["", "Branch observations"]).Concat(repo.BranchIssues);
        var deltas = alignment.Deltas;
        if (deltas.Count > 0) right = new[] { "Verified commit differences" }
            .Concat(deltas.Select(delta => WorkspaceTileViewModel.FormatDelta(delta, counts: true))).Concat([""]).Concat(right);
        bool split = width >= 72;
        int leftSpan = split ? width * 54 / 100 : width;
        var leftLines = fields.Take(5).Concat(["", repo.Dirty ? "Worktree changes" : "Worktree · no changes"]).Concat(worktree)
            .Concat(repo.WorktreeChangesOmitted > 0 ? [$"… {repo.WorktreeChangesOmitted} further paths"] : Array.Empty<string>())
            .Concat(["", "Git metadata fingerprints"]).Concat(fields.Skip(5)).Concat(fingerprints);
        if (!split) leftLines = leftLines.Concat(right);
        var left = leftLines.SelectMany(line => Wrap(line, leftSpan - 1)).ToList();
        var branches = split ? right.SelectMany(line => Wrap(line, width - leftSpan - 3)).ToList() : [];
        detailOffset = Math.Clamp(detailOffset, 0, Math.Max(0, Math.Max(left.Count, branches.Count) - page));
        for (int i = 0; i < page; i++)
        {
            if (i + detailOffset < left.Count) g.Write(x, row + i, left[i + detailOffset], Foreground, leftSpan);
            if (split) g.Write(x + leftSpan, row + i, "│", Border);
            if (i + detailOffset < branches.Count) g.Write(x + leftSpan + 2, row + i, branches[i + detailOffset], Tone("Amber"), width - leftSpan - 2);
        }
    }
}
