using System.ComponentModel;
using Microsoft.UI.Windowing;
using global::Windows.Graphics;

namespace OpenAIUsage.Windows;

public sealed partial class MainWindow
{
    private DesktopAppBar? desktopAppBar;
    private RectInt32? floatingBounds, dockBounds;
    private bool? dockedBottom;
    private string? dockError;

    private bool IsDocked => dockedBottom.HasValue;
    private bool DockedAtBottom => dockedBottom == true;

    private void RestorePreferredDock()
    {
        if (!IsDocked && dockError is null && settings.DockPosition is "top" or "bottom")
            DockWidget(settings.DockPosition == "bottom");
    }

    private void DockWidget(bool bottom)
    {
        if (closed || exitPending || !root.IsLoaded || dockedBottom == bottom) return;
        dockError = null;
        try
        {
            desktopAppBar ??= new DesktopAppBar(WinRT.Interop.WindowNative.GetWindowHandle(this), DispatcherQueue, QueueLayout);
            if (!IsDocked)
            {
                if (!desktopAppBar.TryGetMonitorBounds(out _))
                {
                    DockFailed("Windows could not find a display for the docked widget.");
                    return;
                }
                var position = AppWindow.Position;
                var size = AppWindow.Size;
                floatingBounds = new RectInt32(position.X, position.Y, size.Width, size.Height);
                if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsResizable = false;
            }
            dockedBottom = bottom;
            FitWindow();
        }
        catch (Win32Exception error)
        {
            DockFailed(error.Message);
        }
        if (IsDocked)
        {
            string edge = bottom ? "bottom" : "top";
            if (settings.DockPosition != edge) UpdateSettings(next => next.DockPosition = edge);
        }
        RefreshMenuState();
        UpdateMenuStatus();
    }

    private bool ApplyDockBounds(int outerHeight)
    {
        if (!desktopAppBar!.TryReserve(DockedAtBottom, outerHeight, out var bounds))
        {
            DockFailed("Windows could not reserve space for the widget.");
            return false;
        }
        if (!desktopAppBar.TryPlace(bounds))
        {
            DockFailed("Windows could not position the widget in the reserved space.");
            return false;
        }
        dockBounds = bounds;
        return true;
    }

    private void UndockWidget()
    {
        LeaveDock();
        dockError = null;
        if (settings.DockPosition is not null) UpdateSettings(next => next.DockPosition = null);
        RefreshMenuState();
        UpdateMenuStatus();
    }

    private void LeaveDock()
    {
        if (!IsDocked) return;
        desktopAppBar!.Release();
        dockedBottom = null;
        dockBounds = null;
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsResizable = true;
        ApplyTopmost();
        if (floatingBounds is { } bounds)
        {
            var workArea = DisplayArea.GetFromRect(bounds, DisplayAreaFallback.Nearest).WorkArea;
            bounds.Width = Math.Min(bounds.Width, workArea.Width);
            bounds.Height = Math.Min(bounds.Height, workArea.Height);
            bounds.X = Math.Clamp(bounds.X, workArea.X, workArea.X + workArea.Width - bounds.Width);
            bounds.Y = Math.Clamp(bounds.Y, workArea.Y, workArea.Y + workArea.Height - bounds.Height);
            AppWindow.MoveAndResize(bounds);
        }
        floatingBounds = null;
        RequestWidgetFit();
    }

    private void DockFailed(string message)
    {
        LeaveDock();
        dockError = message;
        Diagnostics.Record("window.dock.failed", message);
        tray?.NotifyError(message);
        RefreshMenuState();
        UpdateMenuStatus();
    }
}
