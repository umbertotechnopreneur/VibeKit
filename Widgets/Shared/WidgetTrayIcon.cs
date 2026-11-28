using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Tools.Widgets;

// UI-thread infrastructure only. Application/tray presentation state belongs to AppState.
public sealed class WidgetTrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8000 + 0x576;
    private const uint IconId = 0x5752;
    private static readonly UIntPtr SubclassId = new(0x57525452);
    private readonly IntPtr hwnd;
    private readonly DispatcherQueue dispatcher;
    private readonly string title;
    private readonly WidgetTrayMenu menu;
    private readonly Action show;
    private readonly Action<bool>? globalCommand;
    private readonly SubclassCallback callback;
    private readonly uint taskbarCreated;
    private IntPtr icon;
    private bool ownsIcon, visible, kept, versionFour, disposed;

    public WidgetTrayIcon(Window owner, string title, string iconPath, WidgetTrayMenu menu, Action show,
        Action<bool>? globalCommand = null)
    {
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(owner);
        this.title = title;
        this.menu = menu;
        this.show = show;
        this.globalCommand = globalCommand;
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

    public bool IsVisible => visible;

    public void Show()
    {
        EnsureVisible();
        kept = true;
    }

    private void EnsureVisible()
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

    public void Hide()
    {
        kept = false;
        if (!visible) return;
        var data = Data();
        ShellNotifyIcon(2, ref data);
        visible = false;
        versionFour = false;
    }

    public void Notify(string title, string message, bool silent = false)
    {
        EnsureVisible();
        var data = Data();
        data.Flags = 0x10; // NIF_INFO
        data.InfoTitle = title.Length > 63 ? title[..63] : title;
        data.Info = message.Length > 255 ? message[..255] : message;
        data.InfoFlags = silent ? 0x11u : 0x1u; // NIIF_INFO with optional NIIF_NOSOUND
        if (ShellNotifyIcon(1, ref data)) return;
        if (!kept) Hide();
        throw new Win32Exception("The notification could not be shown.");
    }

    private NotifyIconData Data() => new()
    {
        Size = checked((uint)Marshal.SizeOf<NotifyIconData>()),
        Window = hwnd,
        Id = IconId,
        Callback = CallbackMessage,
        Icon = icon,
        Tip = title,
        Info = "",
        InfoTitle = ""
    };

    private IntPtr WindowMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        try
        {
            if (!disposed && globalCommand is not null && DesktopEffectStop.Matches(message))
            {
                Dispatch(() => globalCommand(false));
                return IntPtr.Zero;
            }
            if (!disposed && globalCommand is not null && DesktopEffectStop.MatchesEnable(message))
            {
                Dispatch(() => globalCommand(true));
                return IntPtr.Zero;
            }
            if (!disposed && message == taskbarCreated && visible)
            {
                // Explorer discarded its icon list. Re-register without growing resources.
                visible = false;
                versionFour = false;
                if (kept)
                {
                    try { EnsureVisible(); }
                    catch (Win32Exception) { Dispatch(show); }
                }
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
                // NIN_BALLOONHIDE, NIN_BALLOONTIMEOUT and NIN_BALLOONUSERCLICK.
                if (notification is 0x403 or 0x404 or 0x405)
                {
                    if (!kept) Hide();
                    if (notification == 0x405) Dispatch(show);
                }
                else if (notification is 0x400 or 0x401 || (!versionFour && notification == 0x0202))
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
        var point = new Point { X = unchecked((short)anchor.ToInt64()), Y = unchecked((short)(anchor.ToInt64() >> 16)) };
        if ((!versionFour || (point.X == -1 && point.Y == -1)) && !GetCursorPos(out point)) return;
        Dispatch(() => menu.Show(point.X, point.Y));
    }

    private void Dispatch(Action action)
    {
        dispatcher.TryEnqueue(() => { if (!disposed) action(); });
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        menu.Dispose();
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
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out Point point);
}
