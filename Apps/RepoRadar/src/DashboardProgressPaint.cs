using static WorkspaceRepo.DashboardTerminal;

namespace WorkspaceRepo;

public sealed partial class Dashboard
{
    private static void DrawScanPopup(DashboardGrid g, int top, WorkspaceScanSnapshot scan)
    {
        var (x, y, width, height) = ConsoleWidgets.PlaceDialog(g, 78, 11, Math.Max(1, top - 2), FooterTop(g));
        if (height < 5)
            (x, y, width, height) = ConsoleWidgets.PlaceDialog(g, 78, 9, 1, FooterTop(g));
        Frame(g, x, y, width, height);
        int left = x + 3, inside = width - 6;
        int iconWidth = global::Ui.UseEmoji ? 4 : 0;
        if (iconWidth > 0) g.Write(left, y + 1, "🔎", Cyan);
        g.Write(left + iconWidth, y + 1, scan.Title, "bold " + Cyan, inside - iconWidth);
        if (height >= 9)
        {
            g.Fill(left, y + 2, inside, '─', Border);
            g.Write(left, y + 3, scan.Caption, Foreground, inside);
            g.Write(left, y + 4, Abbreviate(scan.Path, inside), "bold " + Cyan, inside);
            g.Write(left, y + 6, scan.Counts, Foreground, inside);
        }
        else
        {
            g.Write(left, y + 2, Abbreviate(scan.Path, inside), "bold " + Cyan, inside);
            if (height >= 6) g.Write(left, y + 3, scan.Counts, Foreground, inside);
        }
        if (height >= 7)
        {
            double? fraction = null;
            if (!scan.Cancelling && scan.Phase == ScanPhase.Inspection && scan.Activity is { } activity)
                fraction = activity.Percent is { } percent ? percent / 100d :
                    activity.Total > 0 ? (double)activity.Completed / activity.Total : null;
            ConsoleWidgets.Progress(g, left, y + height - 2, inside, scan.Frame, fraction, Palette, "#46505D");
        }
    }
}
