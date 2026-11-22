using static WorkspaceRepo.DashboardTerminal;

namespace WorkspaceRepo;

public sealed partial class Dashboard
{
    private sealed record HelpTopic(string Name, string Icon, string Description, string Heading, IReadOnlyList<ConsoleLine> Lines);

    private static readonly HelpTopic[] HelpTopics =
    [
        new("Overview", "📖", "Start here", "From disk to a reviewed action",
        [
            new("What it does", "bold " + FocusCyan),
            new("Repo Radar builds an inventory of Git checkouts on a drive or in a folder. It shows local edits, commits to push or pull, and missing upstream branches so you can decide which checkouts need attention.", Foreground),
            new("From status to action", "bold " + FocusCyan, SpaceBefore: true),
            new("An overview reflects the latest indexed observations. A batch plan checks the marked checkouts again and lists what it can run. Each copy of a repository is a separate checkout in the inventory.", Foreground),
            new("Typical workflow", "bold " + FocusCyan, SpaceBefore: true),
            new("1. Scan a folder and check its remotes.", Foreground),
            new("2. Read the status and open Info for a checkout that needs attention.", Foreground),
            new("3. Mark the checkouts you want to act on.", Foreground),
            new("4. Open Operations, check the plan, then confirm when you are ready.", Foreground),
            new("Use ← → to change chapters, ↑ ↓ to scroll, and Esc to return to your work.", Muted, SpaceBefore: true)
        ]),
        new("Scan", "🔎", "Find Git repos and remotes", "Choose the scan scope",
        [
            new("What it does", "bold " + FocusCyan),
            new("A scan discovers Git working trees under the chosen root, including nested repositories. The progress window shows the current folder and the number of repositories found.", Foreground),
            new("What changes", "bold " + FocusCyan, SpaceBefore: true),
            new("A local-only scan reads Git metadata. Checking remotes can update local tracking refs. Your working files and local commits stay as they are. An incomplete remote check appears as Unverified.", Foreground),
            new("Typical workflow", "bold " + FocusCyan, SpaceBefore: true),
            new("1. Press O to choose a drive or folder. Press S to open the scan prompt for the current root.", Foreground),
            new("2. Press Enter to discover checkouts and check remotes, or L to inspect local data only.", Foreground),
            new("3. Press F5 and confirm to refresh remote status for indexed checkouts. Rescan to discover new checkouts.", Foreground)
        ]),
        new("Read", "👀", "Understand the results", "Read the status before acting",
        [
            new("Choose your view", "bold " + FocusCyan),
            new("1 Dashboard highlights checkouts to review. 2 Repositories gives you a table with paths and status. B Branches shows branch information. M Remotes shows the remote address recorded for each checkout.", Foreground),
            new("Read the evidence", "bold " + FocusCyan, SpaceBefore: true),
            new("• Synchronized — a clean checkout with a verified match. ↑ To push — outgoing local commits. ↓ To pull — incoming remote commits.", Foreground),
            new("• Upstream gone — the tracked remote branch was confirmed missing. File changes — local edits. Unverified — Repo Radar lacks a reliable remote comparison.", Foreground),
            new("Every status reflects an observation at the time of the last check. Use F5 when you need a fresh remote comparison.", Muted),
            new("Typical workflow", "bold " + FocusCyan, SpaceBefore: true),
            new("1. Press V for out-of-sync checkouts, G for missing upstreams, or D for local edits.", Foreground),
            new("2. Move to a checkout and press I to read why it has that status. Press Enter from the list or grid for technical details.", Foreground)
        ]),
        new("Select", "📌", "Mark your batch targets", "Pick the checkouts to include",
        [
            new("What it does", "bold " + FocusCyan),
            new("▶ is the cursor. [X] is a mark that includes the checkout in a batch. Marks follow the local path across Dashboard, Repositories, Branches and Remotes.", Foreground),
            new("Scope", "bold " + FocusCyan, SpaceBefore: true),
            new("A filter changes what you can see. The count still includes checkouts marked outside the current view. Review them with Selected only before opening Operations.", Foreground),
            new("Typical workflow", "bold " + FocusCyan, SpaceBefore: true),
            new("1. Move with the arrows. Press Space or X to mark or unmark the focused checkout.", Foreground),
            new("2. Use Ctrl+A to add every checkout in the filtered list, or Ctrl+X to clear all marks.", Foreground),
            new("3. Press Y to toggle Selected only. Clear filters first if you want to see every marked checkout.", Foreground)
        ]),
        new("Run", "🚀", "Preview and run Git actions", "Choose an action for your batch",
        [
            new("Clean and update", "bold " + FocusCyan),
            new("• Prune tracking refs — clears cached names of branches that are gone from the server. Your local branches, server branches and working files stay as they are.", Foreground),
            new("• Pull (fast-forward) — brings in remote commits when your branch can advance directly. Checkouts with local edits or separate local commits need manual review.", Foreground),
            new("Commit and publish", "bold " + FocusCyan, SpaceBefore: true),
            new("• Commit staged files — saves a local commit with your message. Stage the changes you want in Git first. Other edits stay in your working files.", Foreground),
            new("• Push only — publishes existing commits to the tracked branch on the server when it can advance directly. Uncommitted edits stay local.", Foreground),
            new("• Commit → Push — creates the staged commit first and pushes only after it succeeds. If the push fails, the commit stays in the local branch.", Foreground),
            new("Save local work", "bold " + FocusCyan, SpaceBefore: true),
            new("• Stash only — saves local changes for you to apply manually. Press U while choosing the action to include untracked files.", Foreground),
            new("• Stash → Pull (keep stash) — saves local changes, then fast-forwards if the upstream can be reached safely. The saved stash stays available even if the pull fails.", Foreground),
            new("Typical workflow", "bold " + FocusCyan, SpaceBefore: true),
            new("1. To update two clean checkouts, mark those two, open 3 Operations, choose Pull (fast-forward), and press Enter to build a plan.", Foreground),
            new("2. Read the paths, branches, remotes and skipped items. Press Enter to approve the review, then R to run.", Foreground),
            new("3. Read Results for each checkout. Any saved stash appears with its reference for manual application.", Foreground),
            new("For a mixed batch, press A in the action picker to queue an action for the marks. Change your marks, queue the next action, and use R in the picker to build the review.", Muted, SpaceBefore: true),
            new("Esc goes back during review. During a run it requests a stop after the current checkout.", Muted)
        ])
    ];

    private View helpReturn = View.Grid;
    private int helpTopic, helpOffset, helpLimit, helpPageSize = 1, helpLength;

    private void OpenHelp()
    {
        helpReturn = view;
        helpTopic = helpOffset = 0;
        view = View.Help;
    }

    private void HandleHelp(ConsoleKeyInfo key)
    {
        if (key.Key is ConsoleKey.Escape or ConsoleKey.F1 || key.KeyChar == '?')
        {
            view = helpReturn;
            return;
        }
        int next = key.Key switch
        {
            ConsoleKey.LeftArrow => helpTopic - 1,
            ConsoleKey.RightArrow => helpTopic + 1,
            ConsoleKey.Tab => helpTopic + (key.Modifiers.HasFlag(ConsoleModifiers.Shift) ? -1 : 1),
            >= ConsoleKey.D0 and <= ConsoleKey.D4 => key.Key - ConsoleKey.D0,
            >= ConsoleKey.NumPad0 and <= ConsoleKey.NumPad4 => key.Key - ConsoleKey.NumPad0,
            _ => helpTopic
        };
        if (next != helpTopic)
        {
            helpTopic = (next + HelpTopics.Length) % HelpTopics.Length;
            helpOffset = 0;
            return;
        }
        helpOffset = Math.Clamp(key.Key switch
        {
            ConsoleKey.UpArrow => helpOffset - 1,
            ConsoleKey.DownArrow => helpOffset + 1,
            ConsoleKey.PageUp => helpOffset - helpPageSize,
            ConsoleKey.PageDown => helpOffset + helpPageSize,
            ConsoleKey.Home => 0,
            ConsoleKey.End => helpLimit,
            _ => helpOffset
        }, 0, helpLimit);
    }

    private void DrawHelpPage(DashboardGrid g, int top)
    {
        int footer = FooterTop(g);
        const int sidebarWidth = 30;
        bool wide = g.Width >= 96 && footer - top >= HelpTopics.Length * 2 + 3;
        int left = wide ? Math.Max(3, (g.Width - 130) / 2) : 3;
        int bodyX = left + (wide ? sidebarWidth + 3 : 0);
        int bodyWidth = Math.Min(95, g.Width - bodyX - 3);
        var topic = HelpTopics[helpTopic];
        g.Write(left, top, "Repo Radar Help", "bold " + Foreground);
        if (wide)
        {
            g.Write(left, top + 2, "Contents", "bold underline " + Cyan);
            for (int i = 0; i < HelpTopics.Length; i++)
            {
                bool active = i == helpTopic;
                int row = top + 3 + i * 2;
                if (active)
                {
                    g.Fill(left, row, sidebarWidth, ' ', FocusCyan + FocusBackground);
                    g.Fill(left, row + 1, sidebarWidth, ' ', FocusCyan + FocusBackground);
                }
                var entry = HelpTopics[i];
                string label = $"{(active ? "▶" : " ")} {i}  {entry.Name}";
                g.Write(left, row, label, active ? "bold " + FocusTitle + FocusBackground : Foreground, sidebarWidth);
                g.Write(left + 3, row + 1, entry.Description, active ? FocusTitle + FocusBackground : Muted, sidebarWidth - 3);
            }
            for (int row = top + 2; row < footer; row++) g.Write(bodyX - 2, row, "│", Border);
        }
        if (top + 1 < footer)
            g.Write(bodyX, top + 1,
                (global::Ui.UseEmoji ? topic.Icon + "  " : "") + $"{helpTopic}  {topic.Name}  ·  {(wide ? topic.Heading : topic.Description)}",
                "bold underline " + Cyan, bodyWidth);

        int firstLine = top + 3;
        if (footer - firstLine < 3) firstLine--;
        var lines = new List<ConsoleLine>();
        foreach (var line in topic.Lines)
        {
            if (line.SpaceBefore) lines.Add(new("", Foreground));
            int prefixLength = line.Text.StartsWith("• ", StringComparison.Ordinal) ? 2 :
                line.Text.Length >= 3 && char.IsAsciiDigit(line.Text[0]) && line.Text[1..3] == ". " ? 3 : 0;
            string prefix = line.Text[..prefixLength];
            string continuation = new(' ', prefixLength);
            foreach (string chunk in TerminalText.Wrap(line.Text[prefixLength..], bodyWidth - prefixLength))
            {
                lines.Add(new(prefix + chunk, line.Style));
                prefix = continuation;
            }
        }
        helpLength = lines.Count;
        helpPageSize = Math.Max(1, footer - firstLine);
        helpLimit = Math.Max(0, helpLength - helpPageSize);
        helpOffset = Math.Min(helpOffset, helpLimit);
        for (int i = 0; firstLine + i < footer && helpOffset + i < helpLength; i++)
        {
            var line = lines[helpOffset + i];
            g.Write(bodyX, firstLine + i, line.Text, line.Style, bodyWidth);
        }
    }

    private void DrawHelpFooter(DashboardGrid g)
    {
        int footer = FooterTop(g);
        g.Fill(1, footer, g.Width - 2, '─', Border);
        ShortcutHint[] shortcuts = [new("Esc", "Back"), new("0–4", "Topics"), new("←→", "Topics"), new("↑↓", "Scroll")];
        ConsoleWidgets.Shortcuts(g, 2, footer + 1, g.Width - 4, shortcuts, "bold " + FocusCyan, Foreground);
        if (g.Height >= 20)
        {
            string? position = helpLimit > 0 ? $"{helpOffset + 1}–{Math.Min(helpLength, helpOffset + helpPageSize)} of {helpLength}" : null;
            ConsoleWidgets.StatusLine(g, 2, footer + 3, g.Width - 4, $"Help · {HelpTopics[helpTopic].Name}", Muted, position, Muted);
        }
    }
}
