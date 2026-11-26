using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace WaterRipple;

internal sealed class ScreenEffectInput : IDisposable
{
    private const uint HotkeyMessage = 0x0312, DisplayChangeMessage = 0x007E, DeviceChangeMessage = 0x0219;
    private const int HotkeyId = 0x5752;
    private static readonly UIntPtr SubclassId = new((uint)HotkeyId);
    private readonly IntPtr owner;
    private readonly DispatcherQueue dispatcher;
    private readonly Action stopRequested, displaysChanged;
    private readonly Action<int, int> clicked;
    private readonly Native.HookProc mouseCallback;
    private readonly Native.SubclassProc windowCallback;
    private readonly bool subclassed;
    private IntPtr mouseHook;
    private bool registered, disposed;

    internal ScreenEffectInput(IntPtr owner, DispatcherQueue dispatcher, Action stopRequested,
        Action displaysChanged, Action<int, int> clicked)
    {
        this.owner = owner;
        this.dispatcher = dispatcher;
        this.stopRequested = stopRequested;
        this.displaysChanged = displaysChanged;
        this.clicked = clicked;
        mouseCallback = MouseEvent;
        windowCallback = WindowMessage;
        subclassed = Native.SetWindowSubclass(owner, windowCallback, SubclassId, UIntPtr.Zero);
    }

    internal void Start()
    {
        if (mouseHook != IntPtr.Zero) return;
        if (!subclassed) throw new Win32Exception("Stop command could not be installed.");
        registered = Native.RegisterHotKey(owner, HotkeyId, 0x4003, 0x1B);
        if (!registered) throw new Win32Exception("Ctrl+Alt+Esc is already in use; screen mode was not started.");
        mouseHook = Native.SetWindowsHookEx(14, mouseCallback, Native.GetModuleHandle(null), 0);
        if (mouseHook != IntPtr.Zero) return;
        Stop();
        throw new Win32Exception("Mouse observer could not start.");
    }

    internal void Stop()
    {
        if (mouseHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(mouseHook); mouseHook = IntPtr.Zero; }
        if (registered) { Native.UnregisterHotKey(owner, HotkeyId); registered = false; }
    }

    private IntPtr MouseEvent(int code, IntPtr message, IntPtr data)
    {
        try
        {
            if (code >= 0 && message.ToInt32() is 0x201 or 0x204 or 0x207 or 0x20B)
            {
                var point = Marshal.PtrToStructure<Native.MouseData>(data).Point;
                clicked(point.X, point.Y);
            }
        }
        catch (Exception) { }
        return Native.CallNextHookEx(mouseHook, code, message, data);
    }

    private IntPtr WindowMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        if (message == HotkeyMessage && wParam.ToInt32() == HotkeyId && registered)
        {
            dispatcher.TryEnqueue(() => { if (registered) stopRequested(); });
            return IntPtr.Zero;
        }
        if (message == DisplayChangeMessage || (message == DeviceChangeMessage && wParam.ToInt32() == 7))
            dispatcher.TryEnqueue(() => { if (!disposed) displaysChanged(); });
        return Native.DefSubclassProc(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Stop();
        if (subclassed) Native.RemoveWindowSubclass(owner, windowCallback, SubclassId);
    }
}
