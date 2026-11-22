using static WorkspaceRepo.DashboardTerminal;

namespace WorkspaceRepo;

public sealed partial class Dashboard
{
    private void DrawBulkOperations(DashboardGrid g)
    {
        var (x, y, width, height) = Dialog(g, Math.Max(62, Math.Min(154, g.Width - 12)),
            Math.Max(14, Math.Min(38, g.Height - 12)));
        if (width < 18 || height < 5) return;

        Frame(g, x, y, width, height);
        int left = x + 2;
        int span = width - 4;
        int last = y + height - 2;
        string title = bulkStage switch
        {
            BulkStage.Message => "Operations · Commit message",
            BulkStage.Planning => "Operations · Checking targets",
            BulkStage.Review => "Operations · Review plan (1 of 2)",
            BulkStage.Confirm => "Operations · Confirm run (2 of 2)",
            BulkStage.Running => "Operations · In progress",
            BulkStage.Results => "Operations · Results",
            _ => "Operations"
        };
        string count = $"{BulkSelectionCount} selected" + (BulkQueuedCount > 0 ? $" · {BulkQueuedCount} queued" : "");
        ConsoleWidgets.StatusLine(g, left, y + 1, span, title, "bold " + FocusCyan, count, Muted);

        int first = y + 2;
        if (height >= 8) g.Fill(left, first++, span, '─', Border);
        bool hasFeedback = bulkFeedback.Length > 0;
        bool secondaryHelp = bulkStage == BulkStage.Choose && height >= 11 && span < 111;
        int footer = last - (secondaryHelp ? 1 : 0);
        int end = footer - (hasFeedback && footer - first >= 3 ? 1 : 0);
        var body = new DialogBounds(left, first, span, Math.Max(0, end - first));
        bulkPageSize = Math.Max(1, body.Height);

        switch (bulkStage)
        {
            case BulkStage.Choose:
                DrawBulkActionPicker(g, body);
                break;
            case BulkStage.Message:
                DrawBulkCommitMessage(g, body);
                break;
            case BulkStage.Planning:
                DrawBulkPlanning(g, body);
                break;
            case BulkStage.Review:
            case BulkStage.Confirm:
                DrawBulkPlan(g, body);
                break;
            case BulkStage.Running:
            case BulkStage.Results:
                DrawBulkRun(g, body);
                break;
        }

        if (end < footer) g.Write(left, end, DashboardGrid.Fit(bulkFeedback, span), Gold, span);
        DrawBulkShortcuts(g, left, footer, span, secondaryHelp);
    }

    private void DrawBulkActionPicker(DashboardGrid g, DialogBounds body)
    {
        var (menu, overview) = BulkPanes(g, body, 34, BulkActions.Length + 1);
        if (menu.Height > 0)
        {
            g.Write(menu.X, menu.Y, "Action", "bold " + Cyan, menu.Width);
            var options = menu with { Y = menu.Y + 1, Height = menu.Height - 1 };
            bulkPageSize = Math.Max(1, options.Height);
            DrawBulkEntries(g, options, BulkActions.Length, bulkActionIndex,
                i => (BulkOperations.Label(BulkActions[i]), Foreground));
        }

        if (overview.Height < 1) return;
        var lines = new List<ConsoleLine>();
        AddBulkText(lines, BulkActionDescription(CurrentBulkAction), overview.Width, Foreground);
        if (CurrentBulkAction is BulkAction.Stash or BulkAction.StashPull)
            lines.Add(new(bulkIncludeUntracked ? "[x] Include untracked files" : "[ ] Include untracked files", Cyan));
        var marked = BulkMarkedRepositories;
        if (marked.Count == 0)
        {
            string message = BulkQueuedCount == 0 ? "Mark repositories with Space on the dashboard or in the list."
                : "Review the queued actions with R.";
            AddBulkText(lines, message, overview.Width, Muted);
        }
        else if (overview.Height > lines.Count)
        {
            lines.Add(new("Selected checkouts", "bold " + Cyan));
            int room = Math.Max(0, overview.Height - lines.Count);
            int count = Math.Min(marked.Count, Math.Max(0, room - (marked.Count > room ? 1 : 0)));
            foreach (var repo in marked.Take(count))
                lines.Add(new(TerminalText.Abbreviate($"{WorkspaceViewModel.RemoteName(repo)}  ·  {repo.Path}", overview.Width), Muted));
            if (count < marked.Count) lines.Add(new($"+{marked.Count - count} more in the plan", Muted));
        }
        DrawBulkLines(g, overview, lines);
    }

    private static string BulkActionDescription(BulkAction action) => action switch
    {
        BulkAction.Prune => "Remove the stale local tracking refs listed in the plan.",
        BulkAction.Pull => "Fast-forward the checked-out branch to its upstream.",
        BulkAction.Commit => "Commit the staged files with the message you enter.",
        BulkAction.Push => "Push the checked-out branch to its configured upstream.",
        BulkAction.CommitPush => "Commit the staged files, then push after the commit succeeds.",
        BulkAction.Stash => "Save local changes and keep the stash for you to apply later.",
        BulkAction.StashPull => "Save local changes, fast-forward, and keep the stash for later.",
        _ => ""
    };

    private void DrawBulkCommitMessage(DashboardGrid g, DialogBounds body)
    {
        var (x, y, width, height) = body;
        if (height < 1) return;
        g.Write(x, y, "Your message will be used for the staged commits in this group.", Muted, width);
        if (height < 2) return;
        g.Fill(x, y + 1, width, ' ', FocusCyan + FocusBackground);
        string text = "› " + input + "▏";
        g.Write(x, y + 1, TerminalText.Abbreviate(text, width), "bold " + FocusTitle + FocusBackground, width);
    }

    private void DrawBulkPlanning(DashboardGrid g, DialogBounds body)
    {
        var (x, y, width, height) = body;
        if (height < 1) return;
        g.Write(x, y, bulkStopping ? "Cancelling the review…" : "Checking repository state and remote targets for the preview.",
            bulkStopping ? Gold : Foreground, width);
        if (height > 2)
            ConsoleWidgets.Progress(g, x, y + 2, width, (int)(Environment.TickCount64 / 150 % 256), null, RadarPalette, "#46505D");
    }

    private void DrawBulkPlan(DashboardGrid g, DialogBounds body)
    {
        if (body.Height < 1 || bulkPlan is not { } plan) return;
        if (plan.Items.Count == 0)
        {
            g.Write(body.X, body.Y, "The plan has no checkout actions.", Muted, body.Width);
            return;
        }
        int ready = plan.Items.Count(item => item.Ready);
        int held = plan.Items.Count - ready;
        string status = bulkStage == BulkStage.Confirm
            ? $"Run {ready} approved {(ready == 1 ? "action" : "actions")}? {held} will be skipped."
            : $"{ready} ready · {held} skipped or blocked · {plan.Items.Count} in plan";
        g.Write(body.X, body.Y, status, "bold " + (bulkStage == BulkStage.Confirm ? Gold : Foreground), body.Width);
        body = body with { Y = body.Y + 1, Height = body.Height - 1 };
        var (listing, detail) = BulkPanes(g, body, 45, plan.Items.Count);
        bulkPageSize = Math.Max(1, listing.Height);
        int focused = Math.Clamp(bulkItemIndex, 0, plan.Items.Count - 1);
        DrawBulkEntries(g, listing, plan.Items.Count, focused, i =>
        {
            var entry = plan.Items[i];
            string marker = entry.Ready ? "Ready" : "Skip";
            return ($"{marker,-5}  {BulkCheckoutName(entry.Path)}  ·  {BulkOperations.Label(entry.Action)}", entry.Ready ? Good : Gold);
        });

        if (detail.Height < 1) return;
        var item = plan.Items[focused];
        var lines = new List<ConsoleLine>();
        AddBulkText(lines, $"{focused + 1}/{plan.Items.Count}  {BulkOperations.Label(item.Action)}", detail.Width, "bold " + Cyan);
        AddBulkText(lines, "Checkout  " + item.Path, detail.Width, Foreground);
        if (item.Branch.Length > 0) AddBulkText(lines, "Branch    " + item.Branch, detail.Width, Foreground);
        if (item.Remote.Length > 0) AddBulkText(lines, "Remote    " + item.Remote, detail.Width, Foreground);
        AddBulkText(lines, item.Summary, detail.Width, item.Ready ? Good : Gold);
        foreach (var line in item.Details) AddBulkText(lines, line, detail.Width, Foreground);
        DrawBulkDetail(g, detail, lines);
    }

    private void DrawBulkRun(DashboardGrid g, DialogBounds body)
    {
        if (body.Height < 1) return;
        int total = bulkPlan?.Items.Count ?? bulkResults.Count;
        int ok = bulkResults.Count(result => result.Success && !result.Skipped);
        int skipped = bulkResults.Count(result => result.Skipped);
        int failed = bulkResults.Count - ok - skipped;
        string status = bulkStopping ? "Stopping after the current checkout…" :
            $"{bulkResults.Count}/{total} processed · {ok} succeeded · {skipped} skipped · {failed} need attention";
        g.Write(body.X, body.Y, status, "bold " + (failed > 0 || bulkStopping ? Gold : Foreground), body.Width);
        int top = body.Y + 1;
        if (bulkStage == BulkStage.Running && !bulkStopping && body.Height > 2 &&
            bulkPlan?.Items.ElementAtOrDefault(bulkResults.Count) is { } current)
        {
            string checkout = $"{BulkOperations.Label(current.Action)}  ·  {current.Path}";
            g.Write(body.X, top++, TerminalText.Abbreviate(checkout, body.Width), Cyan, body.Width);
        }
        if (bulkStage == BulkStage.Running && body.Height > 3 && total > 0)
            ConsoleWidgets.Progress(g, body.X, top++, body.Width, 0, (double)bulkResults.Count / total, RadarPalette, "#46505D");
        body = body with { Y = top, Height = Math.Max(0, body.Y + body.Height - top) };
        if (bulkResults.Count == 0)
        {
            if (body.Height > 0) g.Write(body.X, body.Y, "Waiting for the first result…", Muted, body.Width);
            return;
        }

        var (listing, detail) = BulkPanes(g, body, 45, bulkResults.Count);
        bulkPageSize = Math.Max(1, listing.Height);
        int focused = bulkStage == BulkStage.Running ? bulkResults.Count - 1 : Math.Clamp(bulkItemIndex, 0, bulkResults.Count - 1);
        DrawBulkEntries(g, listing, bulkResults.Count, focused, i =>
        {
            var result = bulkResults[i];
            var (label, style) = BulkResultStyle(result);
            return ($"{label,-7}  {BulkCheckoutName(result.Path)}  ·  {BulkOperations.Label(result.Action)}", style);
        });

        if (detail.Height < 1) return;
        var entry = bulkResults[focused];
        var lines = new List<ConsoleLine>();
        AddBulkText(lines, BulkOperations.Label(entry.Action), detail.Width, "bold " + Cyan);
        AddBulkText(lines, "Checkout  " + entry.Path, detail.Width, Foreground);
        AddBulkText(lines, entry.Summary, detail.Width, BulkResultStyle(entry).Style);
        if (entry.Stash.Length > 0)
        {
            AddBulkText(lines, "Saved stash", detail.Width, "bold " + Cyan);
            AddBulkText(lines, entry.Stash, detail.Width, Foreground);
            AddBulkText(lines, "To restore it manually, run this in the checkout:", detail.Width, Muted);
            AddBulkText(lines, "git stash apply " + entry.Stash, detail.Width, Foreground);
        }
        DrawBulkDetail(g, detail, lines);
    }

    private static (string Label, string Style) BulkResultStyle(BulkResult result) => result.Skipped ? ("Skipped", Muted)
        : result.Success ? ("Done", Good) : ("Failed", Gold);

    private static string BulkCheckoutName(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return name.Length > 0 ? name : path;
    }

    private static (DialogBounds List, DialogBounds Detail) BulkPanes(DashboardGrid g, DialogBounds body, int preferred, int entries)
    {
        if (body.Width >= 84)
        {
            int first = Math.Min(preferred, body.Width * 2 / 5);
            for (int row = body.Y; row < body.Y + body.Height; row++) g.Write(body.X + first + 1, row, "│", Border);
            return (body with { Width = first }, body with { X = body.X + first + 3, Width = body.Width - first - 3 });
        }

        if (body.Height < 2) return (body, body with { Height = 0 });
        if (body.Height == 2) return (body with { Height = 1 }, body with { Y = body.Y + 1, Height = 1 });
        int top = Math.Min(entries, Math.Min(8, body.Height / 2));
        g.Fill(body.X, body.Y + top, body.Width, '─', Border);
        return (body with { Height = top }, body with { Y = body.Y + top + 1, Height = body.Height - top - 1 });
    }

    private static void DrawBulkEntries(DashboardGrid g, DialogBounds pane, int count, int focused,
        Func<int, (string Text, string Style)> get)
    {
        if (pane.Height < 1 || count < 1) return;
        int items = Math.Min(count, pane.Height);
        int start = Math.Clamp(focused - items / 2, 0, count - items);
        for (int row = 0; row < items; row++)
        {
            int index = start + row;
            var (text, style) = get(index);
            bool highlight = index == focused;
            if (highlight) g.Fill(pane.X, pane.Y + row, pane.Width, ' ', FocusCyan + FocusBackground);
            var item = (highlight ? FocusMark + " " : "  ") + DashboardGrid.Fit(text, pane.Width - 2);
            g.Write(pane.X, pane.Y + row, item, highlight ? "bold " + FocusTitle + FocusBackground : style, pane.Width);
        }
    }

    private static void AddBulkText(List<ConsoleLine> lines, string text, int width, string style)
    {
        foreach (var line in TerminalText.Wrap(text, width)) lines.Add(new(line, style));
    }

    private static void DrawBulkLines(DashboardGrid g, DialogBounds pane, IReadOnlyList<ConsoleLine> lines, int start = 0)
    {
        for (int row = 0; row < pane.Height && start + row < lines.Count; row++)
        {
            var line = lines[start + row];
            g.Write(pane.X, pane.Y + row, line.Text, line.Style, pane.Width);
        }
    }

    private void DrawBulkDetail(DashboardGrid g, DialogBounds pane, IReadOnlyList<ConsoleLine> lines)
    {
        int rows = pane.Height;
        bool overflow = lines.Count > rows;
        if (overflow && rows > 1) rows--;
        bulkDetailOffset = Math.Clamp(bulkDetailOffset, 0, Math.Max(0, lines.Count - rows));
        DrawBulkLines(g, pane with { Height = rows }, lines, bulkDetailOffset);
        if (pane.Height > rows)
            g.Write(pane.X, pane.Y + rows, $"[←→] Details  {bulkDetailOffset + 1}–{bulkDetailOffset + rows}/{lines.Count}", Muted, pane.Width);
    }

    private void DrawBulkShortcuts(DashboardGrid g, int x, int row, int width, bool secondaryHelp)
    {
        if (bulkStage == BulkStage.Choose)
        {
            var secondary = new List<ShortcutHint>();
            if (BulkQueuedCount > 0) secondary.Add(new("R", "Review queue"));
            if (BulkSelectionCount > 0) secondary.Add(new("A", "Queue action"));
            if (CurrentBulkAction is BulkAction.Stash or BulkAction.StashPull) secondary.Add(new("U", "Untracked"));
            if (BulkQueuedCount > 0) secondary.Add(new("X", "Clear queue"));
            var primary = new List<ShortcutHint> { new("Esc", "Back") };
            if (BulkSelectionCount > 0) primary.Add(new("Enter", "Review selection"));
            if (!secondaryHelp) primary.AddRange(secondary);
            ConsoleWidgets.Shortcuts(g, x, row, width, primary, Cyan, Foreground);
            if (secondaryHelp) ConsoleWidgets.Shortcuts(g, x, row + 1, width, secondary, Cyan, Foreground);
            return;
        }

        bool canRun = bulkPlan?.Items.Any(item => item.Ready) == true;
        ShortcutHint[] hints = bulkStage switch
        {
            BulkStage.Message => [new("Esc", "Back"), new("Enter", "Continue")],
            BulkStage.Planning when !bulkStopping => [new("Esc", "Cancel")],
            BulkStage.Planning => [],
            BulkStage.Review when canRun => [new("Esc", "Back"), new("Enter", "Confirm plan"), new("↑↓", "Select checkout")],
            BulkStage.Confirm when canRun => [new("Esc", "Back"), new("R", "Run approved plan")],
            BulkStage.Running when !bulkStopping => [new("Esc", "Stop after current checkout")],
            BulkStage.Running => [],
            BulkStage.Results => [new("Esc", "Dashboard"), new("↑↓", "Select result")],
            _ => [new("Esc", "Back")]
        };
        ConsoleWidgets.Shortcuts(g, x, row, width, hints, Cyan, Foreground);
    }
}
