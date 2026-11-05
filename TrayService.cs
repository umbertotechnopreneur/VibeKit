using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace WaterRipple;

// UI-thread infrastructure only. Application/tray presentation state belongs to AppState.
internal sealed class TrayService : IDisposable
{
    private const uint CallbackMessage = 0x8000 + 0x576;
    private const uint IconId = 0x5752;
    private static readonly UIntPtr SubclassId = new(0x57525452);
    private readonly IntPtr hwnd;
    private readonly DispatcherQueue dispatcher;
    private readonly Action show, settings, exit;
    private readonly SubclassCallback callback;
    private readonly uint taskbarCreated;
    private IntPtr icon;
    private bool ownsIcon, visible, versionFour, disposed;

    internal TrayService(IntPtr hwnd, string iconPath, Action show, Action settings, Action exit)
    {
        if (hwnd == IntPtr.Zero) throw new ArgumentException("A window handle is required.", nameof(hwnd));
        this.hwnd = hwnd;
        this.show = show ?? throw new ArgumentNullException(nameof(show));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.exit = exit ?? throw new ArgumentNullException(nameof(exit));
        dispatcher = DispatcherQueue.GetForCurrentThread() ??
            throw new InvalidOperationException("The tray service must be created on the window's UI thread.");
        callback = WindowMessage;
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        if (taskbarCreated == 0) throw new Win32Exception();

        if (!string.IsNullOrWhiteSpace(iconPath))
        {
            icon = LoadImage(IntPtr.Zero, iconPath, 1, 0, 0, 0x10 | 0x40);
            ownsIcon = icon != IntPtr.Zero;
        }
        if (icon == IntPtr.Zero) icon = LoadIcon(IntPtr.Zero, new IntPtr(32512));
        if (icon == IntPtr.Zero) throw new Win32Exception("The notification icon could not be loaded.");
        if (!SetWindowSubclass(hwnd, callback, SubclassId, UIntPtr.Zero))
        {
            if (ownsIcon) DestroyIcon(icon);
            icon = IntPtr.Zero;
            throw new Win32Exception("The notification icon could not attach to the window.");
        }
    }

    internal bool IsVisible => visible;

    internal void Show()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (visible) return;
        var data = Data();
        data.Flags = 0x1 | 0x2 | 0x4 | 0x80; // Message, icon, tooltip and standard tooltip display.
        if (!ShellNotifyIcon(0, ref data)) throw new Win32Exception("The notification icon could not be added.");
        visible = true;
        data.TimeoutOrVersion = 4;
        versionFour = ShellNotifyIcon(4, ref data);
    }

    internal void Hide()
    {
        if (!visible) return;
        var data = Data();
        ShellNotifyIcon(2, ref data);
        visible = false;
        versionFour = false;
    }

    private NotifyIconData Data() => new()
    {
        Size = checked((uint)Marshal.SizeOf<NotifyIconData>()),
        Window = hwnd,
        Id = IconId,
        Callback = CallbackMessage,
        Icon = icon,
        Tip = "Water Ripple",
        Info = "",
        InfoTitle = ""
    };

    private IntPtr WindowMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        try
        {
            if (!disposed && message == taskbarCreated && visible)
            {
                // Explorer discarded its icon list. Re-register without growing resources.
                visible = false;
                versionFour = false;
                try { Show(); }
                catch (Win32Exception) { Dispatch(show); }
                return IntPtr.Zero;
            }
            if (!disposed && message == CallbackMessage)
            {
                uint notification = unchecked((uint)lParam.ToInt64());
                if (versionFour)
                {
                    if ((notification >> 16) != IconId) return IntPtr.Zero;
                    notification &= 0xFFFF;
                }
                if (notification is 0x400 or 0x401 || (!versionFour && notification == 0x0202))
                    Dispatch(show);
                else if (notification == 0x007B || (!versionFour && notification == 0x0205))
                    ShowMenu(wParam);
                return IntPtr.Zero;
            }
            if (message == 0x0082) Dispose(); // The owning HWND is being destroyed.
        }
        catch (Exception)
        {
            // Managed failures must never cross a native window-procedure boundary.
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    private void ShowMenu(IntPtr anchor)
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            if (!AppendMenu(menu, 0, 1, "Show Water Ripple") ||
                !AppendMenu(menu, 0, 2, "Ripple settings") ||
                !AppendMenu(menu, 0x800, 0, null) ||
                !AppendMenu(menu, 0, 3, "Exit")) return;
            var point = new Point();
            if (versionFour)
            {
                long coordinates = anchor.ToInt64();
                point.X = unchecked((short)(coordinates & 0xFFFF));
                point.Y = unchecked((short)((coordinates >> 16) & 0xFFFF));
            }
            if (!versionFour || (point.X == -1 && point.Y == -1)) GetCursorPos(out point);
            SetForegroundWindow(hwnd);
            uint command = TrackPopupMenu(menu, 0x100 | 0x80 | 0x2, point.X, point.Y, 0, hwnd, IntPtr.Zero);
            PostMessage(hwnd, 0, IntPtr.Zero, IntPtr.Zero);
            if (command == 1) Dispatch(show);
            else if (command == 2) Dispatch(settings);
            else if (command == 3) Dispatch(exit);
        }
        finally { DestroyMenu(menu); }
    }

    private void Dispatch(Action action)
    {
        dispatcher.TryEnqueue(() => { if (!disposed) action(); });
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Hide();
        RemoveWindowSubclass(hwnd, callback, SubclassId);
        if (ownsIcon && icon != IntPtr.Zero) DestroyIcon(icon);
        icon = IntPtr.Zero;
        ownsIcon = false;
    }

    private delegate IntPtr SubclassCallback(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id, Flags, Callback;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid ItemGuid;
        public IntPtr BalloonIcon;
    }
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShellNotifyIcon(uint action, ref NotifyIconData data);
    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "LoadIconW", SetLastError = true)] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);
    [DllImport("comctl32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr window, SubclassCallback callback, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr window, SubclassCallback callback, UIntPtr id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr owner, IntPtr exclude);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
