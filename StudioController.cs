using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;
using VirtualKey = Windows.System.VirtualKey;

namespace WaterRipple;

internal sealed class DelegateCommand(Action execute) : ICommand
{
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}

internal sealed partial class StudioController : IDisposable
{
    private readonly StudioModel model;
    private readonly Window window;
    private readonly SwapChainPanel canvas;
    private readonly IntPtr hwnd;
    private readonly DispatcherQueueTimer timer, resourceTimer;
    private readonly ResourceMonitor monitor;
    private readonly TrayService tray;
    private EffectFrameState frame = new();
    private IntPtr renderer;
    private ScreenEffectSession? screen;
    private GpuMemory memory;
    private long generation;
    private double nextPerformanceLog;
    internal ICommand ToggleScreenCommand { get; }
    internal ICommand ToggleCaptureCommand { get; }
    internal ICommand DropCommand { get; }
    internal ICommand RestartCommand { get; }
    internal ICommand OpenSettingsCommand { get; }
    internal ICommand InspectScreenCommand { get; }

    internal StudioController(StudioModel model, Window window, SwapChainPanel canvas)
    {
        this.model = model; this.window = window; this.canvas = canvas;
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        tray = new TrayService(hwnd, Path.Combine(AppContext.BaseDirectory, "Assets", "WaterRipple.ico"), ShowFromTray, OpenSettings, ExitApp);
        window.AppWindow.Closing += MainWindowClosing;
        ToggleScreenCommand = new DelegateCommand(ToggleScreen);
        ToggleCaptureCommand = new DelegateCommand(() => { if (model.State.CanFreeze) _ = RefreshPreviewAsync(); });
        DropCommand = new DelegateCommand(() => { frame.AddWave(.5, .5); });
        RestartCommand = new DelegateCommand(StartPreview);
        OpenSettingsCommand = new DelegateCommand(OpenSettings);
        InspectScreenCommand = new DelegateCommand(() => screen?.InspectCapture());

        timer = window.DispatcherQueue.CreateTimer(); timer.Interval = TimeSpan.FromMilliseconds(4); timer.Tick += (_, _) => RenderPreview();
        resourceTimer = window.DispatcherQueue.CreateTimer(); resourceTimer.Interval = TimeSpan.FromSeconds(1); resourceTimer.Tick += (_, _) => UpdateResources(); resourceTimer.Start();
        monitor = new ResourceMonitor(warning: sample =>
        {
            Log.Warning("Private memory threshold reached Bytes={PrivateBytes} WorkingSet={WorkingSet}", sample.PrivateBytes, sample.WorkingSetBytes);
            Native.MessageBox(IntPtr.Zero, "Water Ripple Studio is using at least 1 GiB of private memory. Stop the screen effect or reduce its settings if memory keeps growing.", "Water Ripple Studio · memory", 0x50030);
        });
        model.Settings.PropertyChanged += SettingsChanged;
    }

    internal void SurfaceLoaded()
    {
        if (renderer == IntPtr.Zero && screen is null && !model.State.IsInTray && !model.State.IsClosing) StartPreview();
#if DEBUG
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--exercise-overlay") >= 0 && !checksStarted)
        { checksStarted = true; _ = ExerciseOverlayAsync(); }
#endif
    }

    internal void StartPreview()
    {
        if (model.State.IsClosing) return;
        if (screen is not null) { StopScreen(); return; }
        generation++;
        ReleasePreview();
        frame = new EffectFrameState();
        model.State.IsCaptureLive = false;
        model.State.CaptureAllowed = Native.SetWindowDisplayAffinity(hwnd, 0);
        model.State.IsCaptureValid = false;
        model.State.OverlayPhase = "PreviewStarting";
        try
        {
            var b = Bounds();
            int result = Native.RippleCheckCapabilities(b.X, b.Y, b.Width, b.Height, out var caps);
            model.State.GpuName = caps.AdapterName ?? "Display GPU";
            model.State.DedicatedVramBytes = caps.DedicatedVideoMemory;
            model.State.CanApply = result >= 0 && caps.Supported != 0 && model.State.CaptureAllowed;
            model.State.CapabilityText = Describe(caps, result);
            Log.Information("PreviewStarting Generation={Generation} GPU={GPU} DedicatedVRAM={Vram} Supported={Supported} Reason={Reason} HRESULT={Result}", generation, caps.AdapterName, caps.DedicatedVideoMemory, caps.Supported, caps.Reason, result);
            IntPtr panel = WinRT.MarshalInspectable<SwapChainPanel>.FromManaged(canvas);
            try { Marshal.ThrowExceptionForHR(Native.RippleCreate(panel, Path.Combine(AppContext.BaseDirectory, "Shaders"), out renderer)); }
            finally { Marshal.Release(panel); }
            model.State.CaptureMessage = "Desktop snapshot. Refresh background captures again; this window remains available in screenshots.";
            model.State.StatusMessage = "";
            // Let WinUI finish its Loaded/Activate transaction before changing visibility.
            long expected = generation;
            window.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (!model.State.IsClosing && generation == expected) _ = RefreshPreviewAsync();
            });
        }
        catch (Exception ex) { Fail("Preview could not start", ex); ReleasePreview(); }
    }

    private void RenderPreview()
    {
        if (model.State.IsClosing || renderer == IntPtr.Zero || Native.IsIconic(hwnd) || canvas.ActualWidth < 2 || canvas.ActualHeight < 2) return;
        double now = frame.Clock.Elapsed.TotalSeconds;
        var settings = model.Settings;
        if (now < frame.NextFrame) return;
        frame.NextFrame = Math.Max(frame.NextFrame + 1 / settings.AnimationFps, now);
        frame.Prepare(settings, now);
        var b = Bounds();
        int hr = Native.RippleRender(renderer, b.X, b.Y, b.Width, b.Height, (float)canvas.ActualWidth, (float)canvas.ActualHeight,
            (float)settings.Strength, (float)(settings.WaveSize * .35), (float)settings.Speed, (float)settings.Duration,
            frame.ShaderWaves, frame.Waves.Count, 0, 0);
        if (hr < 0) { Fail("Preview rendering stopped", new COMException("Graphics operation failed", hr)); timer.Stop(); return; }
        if (frame.StatisticsClock.Elapsed.TotalSeconds >= 1 && Native.RippleGetStats(renderer, out var stats) >= 0)
        {
            double seconds = frame.StatisticsClock.Elapsed.TotalSeconds;
            model.State.IsCaptureValid = (stats.Status & 32) != 0;
            model.State.ActiveWaveCount = frame.Waves.Count;
            model.State.MetricsText = $"{(stats.PresentCount - frame.LastPresents) / seconds:F1} presents/s\n{(stats.CaptureCount - frame.LastCaptures) / seconds:F1} desktop updates/s\nCapture submit {stats.LastCaptureMs:F2} ms\nRender submit {stats.LastRenderCpuMs:F2} ms\n{stats.CaptureWidth} × {stats.CaptureHeight} px · {frame.Waves.Count} active events";
            frame.LastPresents = stats.PresentCount; frame.LastCaptures = stats.CaptureCount;
            if (Native.RippleGetGpuMemory(renderer, out var gpu) == 0) memory = gpu;
            frame.StatisticsClock.Restart();
        }
    }

    internal void AddPreviewWave(double x, double y)
    {
        frame.AddWave(x / Math.Max(1, canvas.ActualWidth), y / Math.Max(1, canvas.ActualHeight));

    }

    private async Task RefreshPreviewAsync()
    {
        if (renderer == IntPtr.Zero || screen is not null || model.State.IsClosing || model.State.IsInTray || model.State.OverlayPhase == "CapturingPreviewSnapshot") return;
        long expected = generation;
        model.State.OverlayPhase = "CapturingPreviewSnapshot";
        timer.Stop();
        try
        {
            var bounds = Bounds();
            float widthDip = (float)canvas.ActualWidth, heightDip = (float)canvas.ActualHeight;
            int disableTransitions = 1;
            Native.DwmSetWindowAttribute(hwnd, 3, in disableTransitions, sizeof(int));
            window.AppWindow.Hide();
            Native.ShowWindow(hwnd, 0);
            Native.DwmFlush();
            // Visibility changes from WinUI need a compositor turn before WGC starts.
            await Task.Delay(80);
            if (model.State.IsClosing || generation != expected || renderer == IntPtr.Zero || model.State.IsInTray) return;
            Native.DwmFlush();
            bool valid = false;
            long firstValidAt = -1;
            var settle = Stopwatch.StartNew();
            for (int attempt = 0; attempt < 30; attempt++)
            {
                if (model.State.IsClosing || generation != expected || renderer == IntPtr.Zero || model.State.IsInTray) return;
                var s = model.Settings;
                int hr = Native.RippleRender(renderer, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                    widthDip, heightDip, (float)s.Strength, (float)(s.WaveSize * .35), (float)s.Speed, (float)s.Duration,
                    frame.ShaderWaves, 0, 1, 1);
                Marshal.ThrowExceptionForHR(hr);
                if (Native.RippleGetStats(renderer, out var stats) == 0 && (stats.Status & 32) != 0)
                {
                    valid = true;
                    if (firstValidAt < 0) firstValidAt = settle.ElapsedMilliseconds;
                    if (settle.ElapsedMilliseconds - firstValidAt >= 100) break;
                }
                await Task.Delay(35);
            }
            if (!valid) throw new InvalidOperationException("No desktop snapshot could be captured.");
            model.State.IsCaptureValid = true;
            Log.Information("Preview snapshot captured Width={Width} Height={Height}", bounds.Width, bounds.Height);
        }
        catch (Exception ex) { Fail("Background refresh failed", ex); }
        finally
        {
            if (!model.State.IsClosing && generation == expected && !model.State.IsInTray)
            {
                model.State.OverlayPhase = "Preview";
                try
                {
                    window.AppWindow.Show(true); window.Activate();
                    timer.Start();
                }
                catch (Exception ex) { Fail("Preview window could not be restored", ex); }
            }
            int enableTransitions = 0;
            Native.DwmSetWindowAttribute(hwnd, 3, in enableTransitions, sizeof(int));
        }
    }

    private void ToggleScreen()
    {
        if (screen is not null) { StopScreen(); return; }
        if (model.State.IsClosing) return;
        generation++;
        timer.Stop(); // Retain the frozen preview surface; its WGC session is already closed.
        try
        {
            var b = Bounds();
            int hr = Native.RippleCheckCapabilities(b.X, b.Y, b.Width, b.Height, out var caps);
            Log.Information("ApplyRequested Generation={Generation} Supported={Supported} Reason={Reason} GPU={GPU} VRAM={Vram}", generation, caps.Supported, caps.Reason, caps.AdapterName, caps.DedicatedVideoMemory);
            if (hr < 0 || caps.Supported == 0 || !model.State.CaptureAllowed)
            {
                StartPreview(); model.State.StatusMessage = Describe(caps, hr); return;
            }
            var session = new ScreenEffectSession(model, caps, metrics =>
            {
                memory = metrics.Memory;
                model.State.MetricsText = $"SCREEN EFFECT\n{metrics.Presents:F1} presents/s\n{metrics.Captures:F1} desktop updates/s\nCapture submit {metrics.Native.LastCaptureMs:F2} ms\nRender submit {metrics.Native.LastRenderCpuMs:F2} ms\n{metrics.Native.CaptureWidth} × {metrics.Native.CaptureHeight} px";
            }, (closed, reason) => { if (ReferenceEquals(screen, closed)) StopScreen(reason); });
            screen = session;
            model.State.IsScreenEffectActive = true; model.State.CanApply = true;
            model.State.CaptureMessage = "Screen effect active. Stop with the button or Ctrl+Alt+Esc.";
            session.Start();
            if (ReferenceEquals(screen, session))
            {
                Native.SetTopmost(hwnd, true);
            }
        }
        catch (Exception ex) { StopScreen(ex.Message); if (renderer == IntPtr.Zero) QueuePreview(ex.Message); Log.Error(ex, "Apply failed"); }
    }

    private void StopScreen(string reason = "")
    {
        var active = screen; screen = null;
        model.State.IsScreenEffectActive = false;
        model.State.ScreenOverlayVisible = false;
        model.State.IsCaptureValid = false;
        active?.Dispose();
        Native.SetTopmost(hwnd, false);
        memory = default;
        Log.Information("ScreenStopped Generation={Generation} Reason={Reason}", generation, reason);
        if (!model.State.IsClosing && !model.State.IsInTray) QueuePreview(reason);
    }

    private void QueuePreview(string reason)
    {
        long expected = ++generation;
        window.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (model.State.IsClosing || model.State.IsInTray || screen is not null || expected != generation) return;
            StartPreview();
            if (reason.Length > 0) model.State.StatusMessage = reason;
        });
    }

    private void ReleasePreview()
    {
        timer.Stop();
        if (renderer != IntPtr.Zero) { Native.RippleDestroy(renderer); renderer = IntPtr.Zero; }
        Log.Information("PreviewReleased Generation={Generation}", generation);
    }

    private void UpdateResources()
    {
        var sample = monitor.Snapshot;
        model.State.PrivateRamBytes = sample.PrivateBytes; model.State.WorkingSetBytes = sample.WorkingSetBytes; model.State.CpuPercent = sample.CpuPercent;
        model.State.LocalGpuUsageBytes = memory.LocalUsage; model.State.LocalGpuBudgetBytes = memory.LocalBudget;
        model.State.ResourcesText = $"Private RAM {sample.PrivateBytes / 1048576.0:F0} MiB\nWorking set {sample.WorkingSetBytes / 1048576.0:F0} MiB · CPU {sample.CpuPercent:F1}%\nGPU local {memory.LocalUsage / 1048576.0:F0} / {memory.LocalBudget / 1048576.0:F0} MiB\nRAM warning at 1 GiB";
        double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        if (now >= nextPerformanceLog)
        {
            nextPerformanceLog = now + 60;
            Log.Information("ResourceSummary ScreenActive={ScreenActive} PrivateBytes={PrivateBytes} WorkingSetBytes={WorkingSetBytes} CPU={CPU} GpuLocalBytes={GpuLocalBytes} DroppedLogs={DroppedLogs}",
                model.State.IsScreenEffectActive, sample.PrivateBytes, sample.WorkingSetBytes, sample.CpuPercent, memory.LocalUsage, LoggingService.DroppedEvents);
        }
    }
    private void SettingsChanged(object? sender, PropertyChangedEventArgs e) => Log.Debug("Setting changed {Property}", e.PropertyName);
    private void OpenSettings() => ShowFromTray();

    private async void MainWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (model.State.IsExiting) return;
        args.Cancel = true;
        if (model.State.IsClosePromptOpen) return;
        model.State.IsClosePromptOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                Title = "Keep Water Ripple running?",
                Content = "Minimize to the notification area to keep enabled effects running. Closing the app stops effects and releases its resources.",
                PrimaryButtonText = "Minimize to tray", SecondaryButtonText = "Close app", CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary, RequestedTheme = ElementTheme.Dark,
                XamlRoot = ((FrameworkElement)window.Content).XamlRoot
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary) MinimizeToTray();
            else if (result == ContentDialogResult.Secondary) ExitApp();
        }
        catch (Exception ex) { Fail("Close choice could not be shown", ex); }
        finally { model.State.IsClosePromptOpen = false; }
    }

    private void MinimizeToTray()
    {
        try
        {
            tray.Show();
            model.State.IsInTray = true;
            if (screen is null) ReleasePreview();
            window.AppWindow.Hide();
            Log.Information("Main window minimized to tray ScreenActive={ScreenActive}", model.State.IsScreenEffectActive);
        }
        catch (Exception ex) { model.State.IsInTray = false; Fail("Could not minimize to tray", ex); }
    }
    private void ShowFromTray()
    {
        model.State.IsInTray = false;
        tray.Hide();
        window.AppWindow.Show(true); window.Activate();
        if (screen is null) { if (renderer == IntPtr.Zero) StartPreview(); } else Native.SetTopmost(hwnd, true);
        Log.Information("Main window restored from tray");
    }
    private void ExitApp()
    {
        model.State.IsExiting = true;
        Dispose();
        window.Close();
    }

#if DEBUG
    private bool checksStarted;
    private async Task ExerciseOverlayAsync()
    {
        try
        {
            for (int wait = 0; wait < 80 && (model.State.OverlayPhase != "Preview" || !model.State.IsCaptureValid); wait++) await Task.Delay(25);
            if (model.State.OverlayPhase != "Preview" || !model.State.IsCaptureValid) throw new InvalidOperationException("Preview snapshot did not become ready");
            for (int i = 1; i <= 3; i++)
            {
                ToggleScreen();
                await Task.Delay(1600);
                if (!model.State.IsScreenEffectActive || !model.State.ScreenOverlayVisible || !model.State.IsCaptureValid)
                    throw new InvalidOperationException($"Overlay cycle {i} failed to present valid pixels: {model.State.StatusMessage}");
                StopScreen();
                await Task.Delay(700);
                if (model.State.IsScreenEffectActive || model.State.ScreenOverlayVisible || renderer == IntPtr.Zero)
                    throw new InvalidOperationException($"Overlay cycle {i} did not restore preview");
                using var process = Process.GetCurrentProcess(); process.Refresh();
                Console.WriteLine($"PASS overlay cycle {i}; private={process.PrivateMemorySize64 / 1048576} MiB; phase={model.State.OverlayPhase}");
            }
            Log.Information("Overlay cycle checks passed");
            MinimizeToTray();
            await Task.Delay(300);
            if (!model.State.IsInTray || !tray.IsVisible || Native.IsWindowVisible(hwnd) || renderer != IntPtr.Zero)
                throw new InvalidOperationException("Tray minimization did not release the preview and hide the main window");
            ShowFromTray();
            await Task.Delay(500);
            if (model.State.IsInTray || tray.IsVisible || !Native.IsWindowVisible(hwnd) || renderer == IntPtr.Zero)
                throw new InvalidOperationException("Tray restore did not restore the main window and preview");
            Console.WriteLine("PASS native tray minimize/restore and preview resource release");
        }
        catch (Exception ex) { Environment.ExitCode = 1; Console.Error.WriteLine(ex); Log.Error(ex, "Overlay cycle checks failed"); }
        finally { ExitApp(); }
    }
#endif
    internal bool HandleKey(VirtualKey key)
    {
        switch (key)
        {
            case VirtualKey.F5: DropCommand.Execute(null); return true;
            case VirtualKey.F6: ToggleCaptureCommand.Execute(null); return true;
            case VirtualKey.F7: ToggleScreen(); return true;
            case VirtualKey.F8: OpenSettings(); return true;
            case VirtualKey.Escape: if (screen is not null) StopScreen(); else window.Close(); return true;
            default: return false;
        }
    }
    private (int X, int Y, int Width, int Height) Bounds()
    {
        double scale = canvas.XamlRoot?.RasterizationScale ?? 1;
        var p = canvas.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point());
        var origin = new NativePoint { X = (int)Math.Round(p.X * scale), Y = (int)Math.Round(p.Y * scale) };
        if (!Native.ClientToScreen(hwnd, ref origin)) throw new System.ComponentModel.Win32Exception();
        return (origin.X, origin.Y, Math.Max(2, (int)Math.Round(canvas.ActualWidth * scale)), Math.Max(2, (int)Math.Round(canvas.ActualHeight * scale)));
    }
    private void Fail(string operation, Exception ex) { model.State.StatusMessage = operation + ": " + ex.Message; Log.Error(ex, "{Operation}", operation); }
    private static string Describe(GpuCapabilities caps, int hr)
    {
        string prefix = $"{caps.AdapterName}\n{caps.DedicatedVideoMemory / 1000000000.0:F2} GB dedicated VRAM";
        if (hr < 0) return prefix + $"\nGPU check failed (0x{hr:X8}).";
        return prefix + (caps.Supported != 0 ? " · screen effect available" : caps.Reason switch
        {
            1 => "\nAt least 8 GB dedicated VRAM is required. Shared memory does not count.",
            2 => "\nA hardware GPU is required.", 3 => "\nDirect3D feature level 11.0 is required.",
            4 => "\nRotated displays are not supported.", _ => "\nDesktop capture is unavailable."
        });
    }
    public void Dispose()
    {
        if (model.State.IsClosing) return;
        model.State.IsClosing = true;
        window.AppWindow.Closing -= MainWindowClosing;
        tray.Dispose();
        model.Settings.PropertyChanged -= SettingsChanged;
        resourceTimer.Stop(); monitor.Dispose();
        StopScreen(); ReleasePreview();

    }
}
internal static partial class Native
{
    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)] internal static extern int MessageBox(IntPtr hwnd, string message, string title, uint flags);
    [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attribute, in int value, int size);
}
