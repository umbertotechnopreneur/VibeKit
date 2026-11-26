using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Serilog;

namespace WaterRipple;

internal readonly record struct ScreenSessionMetrics(NativeStats Native, GpuMemory Memory, double Presents, double Captures);

// Owns one monitor's native renderer and presentation surface.
internal sealed class ScreenEffectSession : IDisposable
{
    private readonly AppSettings settings;
    private readonly GpuCapabilities caps;
    private readonly ScreenEffectWindow window = new();
    private readonly EffectFrameState frame = new();
    private readonly Action<ScreenEffectSession> updated;
    private readonly Action<ScreenEffectSession, string, bool> stopped;
    private readonly Action reportMetrics;
    private readonly DispatcherQueueTimer timer;
    private readonly FrameSchedule schedule = new();
    private IntPtr renderer;
    private bool disposed, notified;
    private double invalidSince;
    private double inspectionUntil;
    private int targetIndex = -1;

    private string reason = "";
    private bool recoverable;
    internal bool CaptureValid { get; private set; }
    internal bool OverlayVisible { get; private set; }
    internal bool InspectionVisible { get; private set; }
    internal int ActiveWaveCount { get; private set; }
    internal ScreenSessionMetrics? LastMetrics { get; private set; }

    internal ScreenEffectSession(AppSettings settings, GpuCapabilities caps, Action<ScreenEffectSession> updated,
        Action<ScreenEffectSession, string, bool> stopped, Action reportMetrics)
    {
        this.settings = settings; this.caps = caps; this.updated = updated; this.stopped = stopped;
        this.reportMetrics = reportMetrics;
        timer = window.DispatcherQueue.CreateTimer();
        timer.IsRepeating = false;
        timer.Tick += (_, _) => Render();
        window.Surface.Loaded += (_, _) => Initialize();
        window.Closed += (_, _) => { Release(false); NotifyStopped(); };
    }
    internal void Start()
    {
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
            ArmTimer(0);
            Log.Information("OverlayInitialized WaitingForValidCapture");
        }
        catch (Exception ex) { Log.Error(ex, "Overlay initialization failed"); Stop("Screen effect could not start: " + ex.Message, IsTransientGraphicsError(ex)); }
    }
    private void Render()
    {
        if (disposed || renderer == IntPtr.Zero) return;
        double now = frame.Clock.Elapsed.TotalSeconds;
        bool retryPresent = false;
        try
        {
            bool inspecting = now < inspectionUntil;
            bool changed = false;
            if (!inspecting && InspectionVisible)
            {
                // Re-enable exclusion BEFORE live capture resumes.
                window.SetInspectionCapture(false);
                InspectionVisible = false;
                changed = true;
                schedule.RefreshCapture();
            }
            frame.Prepare(settings, now);
            schedule.Rendered(now, settings.AnimationFps);
            if (settings.TargetIndex != targetIndex)
            {
                targetIndex = settings.TargetIndex;
                Native.RippleSetTarget(renderer, targetIndex);
            }
            bool captureDue = !inspecting && schedule.CaptureDue(now, Math.Min(settings.AnimationFps, settings.CaptureFps));
            float rasterizationScale = (float)(window.Surface.XamlRoot?.RasterizationScale ?? 1);
            var parameters = RenderParameters.From(settings, caps, rasterizationScale, captureEnabled: !inspecting, captureDue);
            int renderResult = Native.Render(renderer, parameters, frame.ShaderWaves, frame.Waves.Count);
            Marshal.ThrowExceptionForHR(renderResult);

            if (Native.RippleGetStats(renderer, out var stats) < 0) return;
            retryPresent = (stats.Status & CaptureStatus.PresentBusy) != 0;
            bool locallyVisible = stats.HasPresentedCapture;
            bool valid = locallyVisible || stats.HasCrtPresentation;
            bool visibilityChanged = locallyVisible != OverlayVisible;
            if (visibilityChanged) window.SetSurfaceVisible(locallyVisible);
            if (visibilityChanged || valid != CaptureValid)
            {
                CaptureValid = valid;
                OverlayVisible = locallyVisible;
                window.TraceSurface(locallyVisible ? "VisibleWithValidCapture" :
                    valid ? "PresentedByCrt" : "HiddenWaitingForCapture");
                Log.Information("OverlayValidity Valid={Valid} CaptureCount={Captures} Status={Status} HRESULT={Result}", valid, stats.CaptureCount, stats.Status, stats.Reserved);
                changed = true;
            }
            if (inspecting && !InspectionVisible && locallyVisible)
            {
                // Capture and export are stopped, and the frozen frame is locally visible.
                window.SetInspectionCapture(true);
                InspectionVisible = true;
                changed = true;
            }
            if (valid) invalidSince = now;
            if (!valid && now - invalidSince > 3) { Stop("No valid desktop frame could be presented. The effect was hidden.", true); return; }
            if (ActiveWaveCount != frame.Waves.Count)
            {
                ActiveWaveCount = frame.Waves.Count;
                changed = true;
            }
            if (frame.StatisticsClock.Elapsed.TotalSeconds >= 1)
            {
                double seconds = frame.StatisticsClock.Elapsed.TotalSeconds;
                GpuMemory gpu = default;
                if (Native.RippleGetGpuMemory(renderer, out var reading) == 0) gpu = reading;
                LastMetrics = new ScreenSessionMetrics(stats, gpu, (stats.PresentCount - frame.LastPresents) / seconds,
                    (stats.CaptureCount - frame.LastCaptures) / seconds);
                frame.LastPresents = stats.PresentCount; frame.LastCaptures = stats.CaptureCount;
                frame.StatisticsClock.Restart();
                reportMetrics();
            }
            if (changed) updated(this);
        }
        catch (Exception ex) { Log.Error(ex, "Overlay render failed"); Stop("Screen effect was hidden after a graphics error: " + ex.Message, IsTransientGraphicsError(ex)); }
        finally
        {
            if (!disposed)
            {
                var delay = schedule.DelayUntilWork(frame.Clock.Elapsed.TotalSeconds, frame.Waves.Count > 0,
                    inspectionUntil, retryPresent);
                ArmTimer(delay);
            }
        }
    }

    private void ArmTimer(double seconds)
    {
        timer.Stop();
        timer.Interval = TimeSpan.FromSeconds(Math.Max(.001, seconds));
        timer.Start();
    }

    internal bool AddClick(int x, int y)
    {
        int localX = x - caps.Left, localY = y - caps.Top;
        if (disposed || localX < 0 || localX >= caps.Width || localY < 0 || localY >= caps.Height) return false;
        bool idle = frame.Waves.Count == 0;
        frame.AddWave(localX / (double)caps.Width, localY / (double)caps.Height);
        if (idle && renderer != IntPtr.Zero)
        {
            schedule.BeginAnimation();
            ArmTimer(0);
        }
        return true;
    }
    private static bool IsTransientGraphicsError(Exception error) => unchecked((uint)error.HResult) is
        0x887A0005 or 0x887A0007 or 0x887A0026; // DXGI device removed, reset, or access lost.

    private void Stop(string message, bool mayRecover = false) { reason = message; recoverable = mayRecover; Dispose(); }
    internal void InspectCapture()
    {
        if (disposed || renderer == IntPtr.Zero || !CaptureValid) return;
        inspectionUntil = frame.Clock.Elapsed.TotalSeconds + 20;
        Render();
    }

    internal void EndInspection()
    {
        if (inspectionUntil <= 0) return;
        inspectionUntil = 0;
        schedule.RefreshCapture();
        Render();
    }

    public void Dispose() { Release(true); NotifyStopped(); }
    private void Release(bool closeWindow)
    {
        if (disposed) return;
        disposed = true;
        timer.Stop();
        try { window.HideBeforeRelease(); }
        catch (Exception ex) { Log.Warning(ex, "Overlay hiding after window close was unavailable"); }
        OverlayVisible = false;
        CaptureValid = false;
        InspectionVisible = false;
        if (renderer != IntPtr.Zero) { Native.RippleDestroy(renderer); renderer = IntPtr.Zero; }
        Log.Information("OverlayGpuReleased HWND={Handle} Visible={Visible}", window.Handle, Native.IsWindowVisible(window.Handle));
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
        stopped(this, reason, recoverable);
    }
}
