using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace WaterRipple;

// A private message loop keeps emergency exit independent of the WinUI dispatcher.
internal sealed class EmergencyShortcut : IDisposable
{
    private const uint WmHotkey = 0x0312, WmUpdate = 0x8001, WmQuit = 0x0012;
    private const int HotkeyId = 1;
    private readonly Action<string> report;
    private readonly ManualResetEventSlim ready = new(false);
    private readonly Thread thread;
    private int enabled;
    private uint threadId;
    private bool disposed;

    internal EmergencyShortcut(bool enabled, Action<string> report)
    {
        this.enabled = enabled ? 1 : 0;
        this.report = report;
        thread = new Thread(Run) { IsBackground = true, Name = "Emergency shortcut" };
        thread.Start();
        ready.Wait();
        ready.Dispose();
    }

    internal void SetEnabled(bool value)
    {
        Interlocked.Exchange(ref enabled, value ? 1 : 0);
        if (!disposed) PostThreadMessage(threadId, WmUpdate, IntPtr.Zero, IntPtr.Zero);
    }

    private void Run()
    {
        threadId = GetCurrentThreadId();
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        bool registered = false;
        void Update()
        {
            bool want = Volatile.Read(ref enabled) != 0;
            if (want && !registered)
                registered = RegisterHotKey(IntPtr.Zero, HotkeyId, 0x0001 | 0x0002 | 0x0004 | 0x4000, 0x57);
            else if (!want && registered)
            {
                UnregisterHotKey(IntPtr.Zero, HotkeyId);
                registered = false;
            }
            report(!want ? "Emergency shortcut is disabled." : registered
                ? "Emergency shortcut is active and immediately closes Water Ripple."
                : "Emergency shortcut unavailable. Another app may be using it.");
        }
        ready.Set();
        Update();
        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            if (message.Message == WmUpdate) Update();
            else if (message.Message == WmHotkey && message.WParam == (IntPtr)HotkeyId && registered && Volatile.Read(ref enabled) != 0)
                TerminateProcess(GetCurrentProcess(), 1);
        }
        if (registered) UnregisterHotKey(IntPtr.Zero, HotkeyId);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        PostThreadMessage(threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinMessage
    {
        internal IntPtr Hwnd;
        internal uint Message;
        internal IntPtr WParam, LParam;
        internal uint Time;
        internal int X, Y;
        internal uint Private;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] private static extern int GetMessage(out WinMessage message, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out WinMessage message, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint id, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
}
