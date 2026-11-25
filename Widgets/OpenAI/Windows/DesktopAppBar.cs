using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using global::Windows.Graphics;

namespace OpenAIUsage.Windows;

// UI-thread adapter for the Shell's desktop work-area reservations.
internal sealed class DesktopAppBar : IDisposable
{
    private const uint AppBarNew = 0, AppBarRemove = 1, AppBarQueryPosition = 2, AppBarSetPosition = 3;
    private const uint AppBarActivate = 6, AppBarWindowPositionChanged = 9;
    private const nuint PositionChanged = 1, FullscreenApp = 2;
    private const uint Activate = 0x0006, Destroy = 0x0002, WindowPositionChanged = 0x0047;
    private const uint DisplayChanged = 0x007E, DpiChanged = 0x02E0;
    private const uint NoActivate = 0x0010;
    private const uint KeepWindowBounds = 0x0001 | 0x0002 | NoActivate;
    private const uint PlaceWithoutAdjustment = 0x0004 | NoActivate | 0x0400; // NOZORDER | NOACTIVATE | NOSENDCHANGING
    private const int ExtendedStyle = -20, TopmostStyle = 0x00000008;
    private const nuint SubclassId = 1;
    private static readonly nint Bottom = new(1), Topmost = new(-1), NotTopmost = new(-2);

    private readonly nint hwnd;
    private readonly DispatcherQueue dispatcher;
    private readonly Action requestLayout;
    private readonly SubclassProcedure subclass;
    private readonly uint callbackMessage;
    private readonly uint taskbarCreated;
    private bool requested, queued, shellRestarted, disposed;
    private bool managesZOrder, floatingTopmost, fullscreen;
    private nint dockMonitor;
    private (bool Bottom, RectInt32 Proposal, RectInt32 Approved)? reservation;

    public DesktopAppBar(nint hwnd, DispatcherQueue dispatcher, Action requestLayout)
    {
        this.hwnd = hwnd;
        this.dispatcher = dispatcher;
        this.requestLayout = requestLayout;
        callbackMessage = RegisterWindowMessage("VibeWare.OpenAIUsage.DesktopAppBar");
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        subclass = ProcessMessage;
        if (callbackMessage == 0 || taskbarCreated == 0 || !SetWindowSubclass(hwnd, subclass, SubclassId, 0))
            throw new Win32Exception("Could not connect to the Windows desktop bar service.");
    }

    public bool IsRegistered { get; private set; }

    public bool TryGetMonitorBounds(out RectInt32 bounds)
    {
        var data = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        // Keep the original display when Windows briefly repositions the HWND during a work-area change.
        if (dockMonitor == 0 || !GetMonitorInfo(dockMonitor, ref data))
        {
            dockMonitor = MonitorFromWindow(hwnd, 2); // MONITOR_DEFAULTTONEAREST
            if (dockMonitor == 0 || !GetMonitorInfo(dockMonitor, ref data))
            {
                bounds = default;
                return false;
            }
        }
        bounds = data.Monitor.ToRectInt32();
        return bounds.Width > 0 && bounds.Height > 0;
    }

    public bool TryReserve(bool bottom, int height, out RectInt32 bounds)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        requested = true;
        bounds = default;
        if (!TryGetMonitorBounds(out RectInt32 monitor))
        {
            Unregister();
            return false;
        }
        bool newRegistration = !IsRegistered;
        if (newRegistration)
        {
            var registration = Data();
            registration.Callback = callbackMessage;
            IsRegistered = SHAppBarMessage(AppBarNew, ref registration) != 0;
            if (!IsRegistered)
            {
                RestoreFloatingZOrder();
                return false;
            }
        }

        height = Math.Clamp(height, 1, monitor.Height);
        int monitorBottom = monitor.Y + monitor.Height;
        var position = Data();
        position.Edge = bottom ? 3u : 1u; // ABE_BOTTOM or ABE_TOP
        position.Bounds = new Rect
        {
            Left = monitor.X,
            Right = monitor.X + monitor.Width,
            Top = bottom ? monitorBottom - height : monitor.Y,
            Bottom = bottom ? monitorBottom : monitor.Y + height
        };
        SHAppBarMessage(AppBarQueryPosition, ref position);

        // The Shell subtracts competing bars from the proposal without preserving its thickness.
        if (bottom)
            position.Bounds.Top = Math.Max(monitor.Y, position.Bounds.Bottom - height);
        else
            position.Bounds.Bottom = Math.Min(monitorBottom, position.Bounds.Top + height);
        var proposal = position.Bounds.ToRectInt32();
        if (reservation is { } previous && previous.Bottom == bottom && previous.Proposal.Equals(proposal))
        {
            bounds = previous.Approved;
            return true;
        }
        if (!FitsMonitor(proposal, monitor) || SHAppBarMessage(AppBarSetPosition, ref position) == 0)
        {
            Unregister();
            return false;
        }

        var approved = position.Bounds.ToRectInt32();
        if (!FitsMonitor(approved, monitor))
        {
            Unregister();
            return false;
        }
        reservation = (bottom, proposal, approved);
        bounds = approved;

        if (!managesZOrder)
        {
            floatingTopmost = (GetWindowLongPtr(hwnd, ExtendedStyle) & TopmostStyle) != 0;
            managesZOrder = true;
        }
        if (newRegistration) ApplyDockedZOrder();
        return true;
    }

    public bool TryPlace(RectInt32 bounds)
    {
        if (!IsRegistered || bounds.Width <= 0 || bounds.Height <= 0) return false;
        if (HasBounds(bounds)) return true;

        // The approved rectangle is outside the work area after the Shell reserves it.
        // Bypass the usual window-placement adjustments and verify the native result.
        return SetWindowPos(hwnd, 0, bounds.X, bounds.Y, bounds.Width, bounds.Height, PlaceWithoutAdjustment)
            && HasBounds(bounds);
    }

    private bool HasBounds(RectInt32 bounds) =>
        GetWindowRect(hwnd, out Rect current) && current.ToRectInt32().Equals(bounds);

    private static bool FitsMonitor(RectInt32 bounds, RectInt32 monitor) =>
        bounds.Width > 0 && bounds.Height > 0 && bounds.X >= monitor.X && bounds.Y >= monitor.Y &&
        bounds.X + bounds.Width <= monitor.X + monitor.Width && bounds.Y + bounds.Height <= monitor.Y + monitor.Height;

    public void Release()
    {
        requested = false;
        shellRestarted = false;
        Unregister();
        dockMonitor = 0;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Release();
        RemoveWindowSubclass(hwnd, subclass, SubclassId);
    }

    private void Unregister(bool restoreZOrder = true)
    {
        reservation = null;
        if (IsRegistered)
        {
            IsRegistered = false;
            var data = Data();
            SHAppBarMessage(AppBarRemove, ref data);
        }
        fullscreen = false;
        if (restoreZOrder) RestoreFloatingZOrder();
    }

    private void ApplyDockedZOrder() =>
        SetWindowPos(hwnd, fullscreen ? Bottom : Topmost, 0, 0, 0, 0, KeepWindowBounds);

    private void RestoreFloatingZOrder()
    {
        if (!managesZOrder) return;
        managesZOrder = false;
        SetWindowPos(hwnd, floatingTopmost ? Topmost : NotTopmost, 0, 0, 0, 0, KeepWindowBounds);
    }

    private void ScheduleLayout()
    {
        if (!requested || queued || disposed) return;
        queued = true;
        if (!dispatcher.TryEnqueue(() =>
        {
            queued = false;
            if (!requested || disposed) return;
            if (shellRestarted)
            {
                shellRestarted = false;
                // TaskbarCreated also fires on primary DPI changes with the old Shell still alive.
                Unregister(restoreZOrder: false);
            }
            requestLayout();
        })) queued = false;
    }

    private nint ProcessMessage(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint reference)
    {
        if (message == Destroy)
        {
            Dispose();
        }
        else if (message == taskbarCreated)
        {
            shellRestarted = requested;
            ScheduleLayout();
        }
        else if (message == callbackMessage && IsRegistered)
        {
            if (wParam == PositionChanged) ScheduleLayout();
            else if (wParam == FullscreenApp && fullscreen != (lParam != 0))
            {
                fullscreen = lParam != 0;
                dispatcher.TryEnqueue(() =>
                {
                    if (IsRegistered && managesZOrder) ApplyDockedZOrder();
                });
            }
            return 0;
        }

        nint result = DefSubclassProc(window, message, wParam, lParam);
        if (message is DisplayChanged or DpiChanged) ScheduleLayout();
        if (IsRegistered && message is Activate or WindowPositionChanged)
        {
            var data = Data();
            data.Parameter = (wParam & 0xFFFF) != 0 ? 1 : 0;
            SHAppBarMessage(message == Activate ? AppBarActivate : AppBarWindowPositionChanged, ref data);
            if (message == WindowPositionChanged && reservation is { } current && !HasBounds(current.Approved))
                ScheduleLayout();
        }
        return result;
    }

    private AppBarData Data() => new() { Size = (uint)Marshal.SizeOf<AppBarData>(), Window = hwnd };

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
        public readonly RectInt32 ToRectInt32() => new(Left, Top, Right - Left, Bottom - Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint Size;
        public nint Window;
        public uint Callback, Edge;
        public Rect Bounds;
        public nint Parameter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor, WorkArea;
        public uint Flags;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProcedure(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint reference);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern bool SetWindowSubclass(nint hwnd, SubclassProcedure callback, nuint id, nuint reference);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProcedure callback, nuint id);

    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern nuint SHAppBarMessage(uint message, ref AppBarData data);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo information);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool GetWindowRect(nint hwnd, out Rect bounds);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
}
