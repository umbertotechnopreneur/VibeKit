using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;

namespace WaterRipple;

// A presentation surface only. Settings, effects and lifecycle state live in models/controllers.
internal sealed class ScreenEffectWindow : Window
{
    internal SwapChainPanel Surface { get; } = new() { IsHitTestVisible = false };
    internal IntPtr Handle { get; }

    internal ScreenEffectWindow()
    {
        Title = "Water Ripple screen effect";
        Content = Surface;
        Handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
    }

    internal void ShowSurface(GpuCapabilities caps)
    {
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = presenter.IsMaximizable = presenter.IsMinimizable = false;
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(caps.Left, caps.Top, caps.Width, caps.Height));
        long style = Native.GetWindowLongPtr(Handle, -20).ToInt64();
        style = (style & ~0x00040000L) | 0x00080000L | 0x00000020L | 0x00000080L | 0x08000000L;
        if (Native.SetWindowLongPtr(Handle, -20, new IntPtr(style)) == IntPtr.Zero && Marshal.GetLastWin32Error() != 0) throw new Win32Exception();
        SetSurfaceVisible(false);
        if (!Native.SetWindowDisplayAffinity(Handle, 0x11)) throw new Win32Exception("Screen capture exclusion could not be enabled.");
        AppWindow.Show(false);
        Native.SetOverlayTopmost(Handle);
        TraceSurface("WindowShownHidden");
    }

    internal void SetSurfaceVisible(bool visible)
    {
        if (!Native.SetLayeredWindowAttributes(Handle, 0, visible ? (byte)255 : (byte)0, 2)) throw new Win32Exception();
    }

    internal void HideBeforeRelease()
    {
        // Remove the HWND before detaching its composition surface; never expose the XAML fallback.
        Native.SetLayeredWindowAttributes(Handle, 0, 0, 2);
        Native.ShowWindow(Handle, 0);
        AppWindow.Hide();
        Native.DwmFlush();
        TraceSurface("HiddenBeforeGpuRelease");
    }

    internal void TraceSurface(string phase)
    {
        bool layered = Native.GetLayeredWindowAttributes(Handle, out _, out byte alpha, out uint flags);
        Native.GetWindowDisplayAffinity(Handle, out uint affinity);
        Log.Information("Overlay {Phase} HWND={Handle} Visible={Visible} Layered={Layered} Alpha={Alpha} Flags={Flags} Affinity={Affinity} Styles={Styles}",
            phase, Handle, Native.IsWindowVisible(Handle), layered, alpha, flags, affinity, Native.GetWindowLongPtr(Handle, -20).ToInt64());
    }

    internal void SetInspectionCapture(bool enabled)
    {
        if (!Native.SetWindowDisplayAffinity(Handle, enabled ? 0u : 0x11u)) throw new Win32Exception();
        AppWindow.IsShownInSwitchers = enabled;
        long style = Native.GetWindowLongPtr(Handle, -20).ToInt64();
        style = enabled ? (style & ~0x80L) | 0x40000L : (style & ~0x40000L) | 0x80L;
        Native.SetWindowLongPtr(Handle, -20, new IntPtr(style));
        TraceSurface(enabled ? "ScreenshotInspectionEnabled" : "ScreenshotInspectionEnded");
    }
}
