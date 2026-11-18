using System;
using System.ComponentModel;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Serilog;

namespace WaterRipple;

internal readonly record struct ScreenSessionMetrics(NativeStats Native, GpuMemory Memory, double Presents, double Captures);

// Owns renderer and input resources; Window remains a presentation-only surface.
internal sealed class ScreenEffectSession : IDisposable
{
    private readonly StudioModel model;
    private readonly GpuCapabilities caps;
    private readonly ScreenEffectWindow window = new();
    private readonly EffectFrameState frame = new();
    private readonly Action<ScreenSessionMetrics> metrics;
    private readonly Action<ScreenEffectSession, string> stopped;
    private readonly DispatcherQueueTimer timer;
    private readonly Native.HookProc mouseCallback;
    private readonly Native.SubclassProc windowCallback;
    private IntPtr renderer, mouseHook;
    private bool disposed, registered, subclassed, notified;
    private double invalidSince;
    private double inspectionUntil;
    private bool inspectionVisible;

    private string reason = "";
    private const int HotkeyId = 0x5752;
    internal ScreenEffectSession(StudioModel model, GpuCapabilities caps, Action<ScreenSessionMetrics> metrics, Action<ScreenEffectSession, string> stopped)
    {
        this.model = model; this.caps = caps; this.metrics = metrics; this.stopped = stopped;
        mouseCallback = MouseEvent; windowCallback = WindowMessage;
        timer = window.DispatcherQueue.CreateTimer(); timer.Interval = TimeSpan.FromMilliseconds(4); timer.Tick += (_, _) => Render();
        window.Surface.Loaded += (_, _) => Initialize();
        window.Closed += (_, _) => { Release(false); NotifyStopped(); };
    }
    internal void Start()
    {
        model.State.OverlayPhase = "CreatingHiddenSurface";
        model.State.ScreenOverlayVisible = false;
        model.State.IsCaptureValid = false;
        window.ShowSurface(caps);
        Log.Information("OverlayStart Monitor={Left},{Top},{Width},{Height} GPU={GPU}", caps.Left, caps.Top, caps.Width, caps.Height, caps.AdapterName);
    }
    private void Initialize()
    {
        if (disposed || renderer != IntPtr.Zero) return;
        try
        {
            IntPtr panel = WinRT.MarshalInspectable<Microsoft.UI.Xaml.Controls.SwapChainPanel>.FromManaged(window.Surface);
            try { Marshal.ThrowExceptionForHR(Native.RippleCreate(panel, Path.Combine(AppContext.BaseDirectory, "Shaders"), out renderer)); }
            finally { Marshal.Release(panel); }
            Marshal.ThrowExceptionForHR(Native.RippleSetScreenMode(renderer, 1));
            Native.RippleSetInteropWindow(renderer, window.Handle);
            subclassed = Native.SetWindowSubclass(window.Handle, windowCallback, 1, UIntPtr.Zero);
            if (!subclassed) throw new Win32Exception("Stop command could not be installed.");
            registered = Native.RegisterHotKey(window.Handle, HotkeyId, 0x4003, 0x1B);
            if (!registered) throw new Win32Exception("Ctrl+Alt+Esc is already in use; screen mode was not started.");
            mouseHook = Native.SetWindowsHookEx(14, mouseCallback, Native.GetModuleHandle(null), 0);
            if (mouseHook == IntPtr.Zero) throw new Win32Exception("Mouse observer could not start.");
            model.State.OverlayPhase = "WaitingForValidCapture";
            timer.Start();
            Log.Information("OverlayInitialized WaitingForValidCapture");
        }
        catch (Exception ex) { Log.Error(ex, "Overlay initialization failed"); Stop("Screen effect could not start: " + ex.Message); }
    }
    private void Render()
    {
        if (disposed || renderer == IntPtr.Zero) return;
        double now = frame.Clock.Elapsed.TotalSeconds;
        var settings = model.Settings;
        if (now < frame.NextFrame) return;
        frame.NextFrame = Math.Max(frame.NextFrame + 1 / settings.AnimationFps, now);
        try
        {
            bool inspecting = now < inspectionUntil;
            if (!inspecting && inspectionVisible)
            {
                // Re-enable exclusion BEFORE live capture resumes.
                window.SetInspectionCapture(false);
                inspectionVisible = false;
                model.State.StatusMessage = "";
            }
            frame.Prepare(settings, now);
            bool captureDue = !inspecting && now >= frame.NextCapture;
            if (captureDue) frame.NextCapture = Math.Max(frame.NextCapture + 1 / settings.CaptureFps, now);
            float rasterizationScale = (float)(window.Surface.XamlRoot?.RasterizationScale ?? 1);
            var parameters = RenderParameters.From(settings, caps, rasterizationScale, captureEnabled: !inspecting, captureDue);
            int renderResult = Native.Render(renderer, parameters, frame.ShaderWaves, frame.Waves.Count);
            Marshal.ThrowExceptionForHR(renderResult);

            if (Native.RippleGetStats(renderer, out var stats) < 0) return;
            bool valid = stats.HasPresentedCapture;
            if (inspecting && !inspectionVisible && valid)
            {
                // The WGC session was closed by Render(captureEnabled=false), so this cannot recurse.
                window.SetInspectionCapture(true);
                inspectionVisible = true;
                model.State.StatusMessage = "Capture paused for screenshot inspection (20 seconds).";
            }
            if (valid != model.State.IsCaptureValid)
            {
                model.State.IsCaptureValid = valid;
                window.SetSurfaceVisible(valid);
                model.State.ScreenOverlayVisible = valid;
                model.State.OverlayPhase = valid ? "VisibleWithValidCapture" : "HiddenWaitingForCapture";
                window.TraceSurface(model.State.OverlayPhase);
                Log.Information("OverlayValidity Valid={Valid} CaptureCount={Captures} Status={Status} HRESULT={Result}", valid, stats.CaptureCount, stats.Status, stats.Reserved);
            }
            if (valid) invalidSince = now;
            if (!valid && now - invalidSince > 3) { Stop("No valid desktop frame could be presented. The effect was hidden."); return; }
            model.State.ActiveWaveCount = frame.Waves.Count;
            if (frame.StatisticsClock.Elapsed.TotalSeconds >= 1)
            {
                double seconds = frame.StatisticsClock.Elapsed.TotalSeconds;
                GpuMemory gpu = default;
                if (Native.RippleGetGpuMemory(renderer, out var reading) == 0) gpu = reading;
                metrics(new ScreenSessionMetrics(stats, gpu, (stats.PresentCount - frame.LastPresents) / seconds, (stats.CaptureCount - frame.LastCaptures) / seconds));
                frame.LastPresents = stats.PresentCount; frame.LastCaptures = stats.CaptureCount;
                frame.StatisticsClock.Restart();
            }
        }
        catch (Exception ex) { Log.Error(ex, "Overlay render failed"); Stop("Screen effect was hidden after a graphics error: " + ex.Message); }
    }
    private IntPtr MouseEvent(int code, IntPtr message, IntPtr data)
    {
        try
        {
            int id = message.ToInt32();
            if (code >= 0 && !disposed && (id == 0x201 || id == 0x204 || id == 0x207 || id == 0x20B))
            {
                var mouse = Marshal.PtrToStructure<Native.MouseData>(data);
                frame.AddWave((mouse.Point.X - caps.Left) / (double)caps.Width, (mouse.Point.Y - caps.Top) / (double)caps.Height);
            }
        }
        catch (Exception) { }
        return Native.CallNextHookEx(mouseHook, code, message, data);
    }
    private IntPtr WindowMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        if (message == 0x0312 && wParam.ToInt32() == HotkeyId) { window.DispatcherQueue.TryEnqueue(() => Stop("Stopped by global shortcut.")); return IntPtr.Zero; }
        if (message == 0x007E) window.DispatcherQueue.TryEnqueue(() => Stop("Display configuration changed; recheck the GPU."));
        return Native.DefSubclassProc(hwnd, message, wParam, lParam);
    }
    private void Stop(string message) { reason = message; Dispose(); }
    internal void InspectCapture()
    {
        if (disposed || renderer == IntPtr.Zero || !model.State.IsCaptureValid) return;
        inspectionUntil = frame.Clock.Elapsed.TotalSeconds + 20;
        frame.NextFrame = 0;
        Render();
    }

    public void Dispose() { Release(true); NotifyStopped(); }
    private void Release(bool closeWindow)
    {
        if (disposed) return;
        disposed = true;
        model.State.OverlayPhase = "HidingBeforeRelease";
        timer.Stop();
        try { window.HideBeforeRelease(); }
        catch (Exception ex) { Log.Warning(ex, "Overlay hiding after window close was unavailable"); }
        model.State.ScreenOverlayVisible = false; model.State.IsCaptureValid = false;
        if (mouseHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(mouseHook); mouseHook = IntPtr.Zero; }
        if (registered) { Native.UnregisterHotKey(window.Handle, HotkeyId); registered = false; }
        if (subclassed) { Native.RemoveWindowSubclass(window.Handle, windowCallback, 1); subclassed = false; }
        if (renderer != IntPtr.Zero) { Native.RippleDestroy(renderer); renderer = IntPtr.Zero; }
        Log.Information("OverlayGpuReleased HWND={Handle} Visible={Visible}", window.Handle, Native.IsWindowVisible(window.Handle));
        model.State.OverlayPhase = "Closed";
        if (closeWindow)
        {
            try { window.Close(); }
            catch (Exception ex) { Log.Warning(ex, "Overlay window was already closed"); }
        }
    }
    private void NotifyStopped()
    {
        if (notified) return;
        notified = true;
        stopped(this, reason);
    }
}
