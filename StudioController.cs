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
    private readonly StartupService startup;
    private readonly CaptureConsentService captureConsent;
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
    internal ICommand MinimizeToTrayCommand { get; }
    internal ICommand InspectScreenCommand { get; }
    internal bool IsInTray => model.State.IsInTray;

    internal StudioController(StudioModel model, Window window, SwapChainPanel canvas)
    {
        this.model = model; this.window = window; this.canvas = canvas;
        startup = new StartupService(model.State);
        captureConsent = new CaptureConsentService(model.State);
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        tray = new TrayService(hwnd, Path.Combine(AppContext.BaseDirectory, "Assets", "WaterRipple.ico"), ShowFromTray, OpenSettings, ExitApp);
        window.AppWindow.Closing += MainWindowClosing;
        ToggleScreenCommand = new DelegateCommand(ToggleScreen);
        ToggleCaptureCommand = new DelegateCommand(() => { if (model.State.CanFreeze) _ = RefreshPreviewAsync(); });
        DropCommand = new DelegateCommand(() => { frame.AddWave(.5, .5); });
        RestartCommand = new DelegateCommand(StartPreview);
        OpenSettingsCommand = new DelegateCommand(OpenSettings);
        MinimizeToTrayCommand = new DelegateCommand(() =>
        {
            if (!model.State.IsClosing && !model.State.IsSettingsOpen && !model.State.IsCaptureConsentOpen && !model.State.IsClosePromptOpen)
                MinimizeToTray();
        });
        InspectScreenCommand = new DelegateCommand(() => screen?.InspectCapture());

        timer = window.DispatcherQueue.CreateTimer(); timer.Interval = TimeSpan.FromMilliseconds(4); timer.Tick += (_, _) => RenderPreview();
        resourceTimer = window.DispatcherQueue.CreateTimer(); resourceTimer.Interval = TimeSpan.FromSeconds(1); resourceTimer.Tick += (_, _) => UpdateResources(); resourceTimer.Start();
        monitor = new ResourceMonitor(warning: sample =>
        {
            Log.Warning("Private memory threshold reached Bytes={PrivateBytes} WorkingSet={WorkingSet}", sample.PrivateBytes, sample.WorkingSetBytes);
            Native.MessageBox(IntPtr.Zero, "Water Ripple Studio is using at least 500 MB of private memory. This could be a memory leak. Please report it and include the recent application logs.\n\nStop the screen effect or close the app if memory keeps growing.", "Water Ripple Studio · memory", 0x50030);
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
            Log.Information("Checking preview GPU Generation={Generation} Width={Width} Height={Height} CaptureAllowed={CaptureAllowed}",
                generation, b.Width, b.Height, model.State.CaptureAllowed);
            int result = Native.RippleCheckCapabilities(b.X, b.Y, b.Width, b.Height, out var caps);
            model.State.GpuName = caps.AdapterName ?? "Display GPU";
            model.State.DedicatedVramBytes = caps.DedicatedVideoMemory;
            model.State.CanApply = result >= 0 && caps.Supported != 0 && model.State.CaptureAllowed;
            model.State.CapabilityText = Describe(caps, result);
            Log.Information("PreviewStarting Generation={Generation} GPU={GPU} DedicatedVRAM={Vram} FeatureLevel=0x{FeatureLevel:X} Supported={Supported} Reason={Reason} HRESULT=0x{HResult:X8}",
                generation, caps.AdapterName, caps.DedicatedVideoMemory, caps.FeatureLevel, caps.Supported, caps.Reason, result);
            IntPtr panel = WinRT.MarshalInspectable<SwapChainPanel>.FromManaged(canvas);
            try { Marshal.ThrowExceptionForHR(Native.RippleCreate(panel, Path.Combine(AppContext.BaseDirectory, "Shaders"), out renderer)); }
            finally { Marshal.Release(panel); }
            Log.Information("Preview renderer created Generation={Generation}", generation);
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
            settings.ChromaticEnabled ? (float)(settings.ChromaticSeparation / 100) : 0, (float)(settings.ChromaticBlend / 100),
            settings.CrestEnabled ? (float)(settings.CrestIntensity / 100) : 0, settings.RipplesEnabled ? (float)(settings.RipplesIntensity / 100) : 0, settings.LiquidEnabled ? (float)(settings.LiquidIntensity / 100) : 0,
            frame.ShaderWaves, frame.Waves.Count, 0, 0);
        if (hr < 0) { Fail("Preview rendering stopped", new COMException("Graphics operation failed", hr)); timer.Stop(); return; }
        if (frame.StatisticsClock.Elapsed.TotalSeconds >= 1 && Native.RippleGetStats(renderer, out var stats) >= 0)
        {
            double seconds = frame.StatisticsClock.Elapsed.TotalSeconds;
            model.State.IsCaptureValid = (stats.Status & 32) != 0;
            model.State.ActiveWaveCount = frame.Waves.Count;
            model.State.MetricsText = $"Presents\t{(stats.PresentCount - frame.LastPresents) / seconds:F1} /s\nDesktop updates\t{(stats.CaptureCount - frame.LastCaptures) / seconds:F1} /s\nCapture submit\t{stats.LastCaptureMs:F2} ms\nRender submit\t{stats.LastRenderCpuMs:F2} ms\nCapture size\t{stats.CaptureWidth} × {stats.CaptureHeight} px\nActive events\t{frame.Waves.Count}";
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
            Log.Information("Starting preview snapshot Generation={Generation} Width={Width} Height={Height} WidthDip={WidthDip} HeightDip={HeightDip}",
                expected, bounds.Width, bounds.Height, widthDip, heightDip);
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
            int statsResult = 0;
            uint captureStatus = 0;
            for (int attempt = 0; attempt < 30; attempt++)
            {
                if (model.State.IsClosing || generation != expected || renderer == IntPtr.Zero || model.State.IsInTray) return;
                var s = model.Settings;
                int hr = Native.RippleRender(renderer, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                    widthDip, heightDip, (float)s.Strength, (float)(s.WaveSize * .35), (float)s.Speed, (float)s.Duration,
                    s.ChromaticEnabled ? (float)(s.ChromaticSeparation / 100) : 0, (float)(s.ChromaticBlend / 100),
                    s.CrestEnabled ? (float)(s.CrestIntensity / 100) : 0, s.RipplesEnabled ? (float)(s.RipplesIntensity / 100) : 0, s.LiquidEnabled ? (float)(s.LiquidIntensity / 100) : 0,
                    frame.ShaderWaves, 0, 1, 1);
                Marshal.ThrowExceptionForHR(hr);
                statsResult = Native.RippleGetStats(renderer, out var stats);
                captureStatus = unchecked((uint)stats.Status);
                if (statsResult == 0 && (captureStatus & 32) != 0)
                {
                    valid = true;
                    if (firstValidAt < 0)
                    {
                        firstValidAt = settle.ElapsedMilliseconds;
                        Log.Information("First valid preview capture Generation={Generation} Attempt={Attempt} ElapsedMs={ElapsedMs} CaptureWidth={CaptureWidth} CaptureHeight={CaptureHeight} Presents={PresentCount} Status=0x{Status:X}",
                            expected, attempt + 1, firstValidAt, stats.CaptureWidth, stats.CaptureHeight, stats.PresentCount, captureStatus);
                    }
                    if (settle.ElapsedMilliseconds - firstValidAt >= 100) break;
                }
                await Task.Delay(35);
            }
            if (!valid)
            {
                Log.Warning("Preview capture timed out Generation={Generation} ElapsedMs={ElapsedMs} StatsHRESULT=0x{HResult:X8} Status=0x{Status:X}",
                    expected, settle.ElapsedMilliseconds, statsResult, captureStatus);
                throw new InvalidOperationException("No desktop snapshot could be captured.");
            }
            model.State.IsCaptureValid = true;
            Log.Information("Preview snapshot captured Generation={Generation} Width={Width} Height={Height}", expected, bounds.Width, bounds.Height);
        }
        catch (Exception ex) { Fail("Background refresh failed", ex); }
        finally
        {
            if (!model.State.IsClosing && generation == expected && !model.State.IsInTray)
            {
                model.State.OverlayPhase = "Preview";
                try
                {
                    Log.Information("Restoring Studio window after preview capture Generation={Generation}", expected);
                    window.AppWindow.Show(true); window.Activate();
                    timer.Start();
                }
                catch (Exception ex) { Fail("Preview window could not be restored", ex); }
            }
            int enableTransitions = 0;
            Native.DwmSetWindowAttribute(hwnd, 3, in enableTransitions, sizeof(int));
        }
    }

    private async void ToggleScreen()
    {
        if (screen is not null) { StopScreen(); return; }
        if (model.State.IsClosing || model.State.IsCaptureConsentOpen || model.State.IsSettingsOpen) return;
        await captureConsent.EnsureAsync(((FrameworkElement)window.Content).XamlRoot);
        if (model.State.IsClosing || screen is not null) return;
        StartScreen();
    }

    internal void StartAtSignIn()
    {
        MinimizeToTray();
        model.State.CaptureAllowed = Native.SetWindowDisplayAffinity(hwnd, 0);
        // The optional borderless-capture prompt belongs to the interactive Apply action.
        StartScreen(atSignIn: true);
        if (!model.State.IsInTray) window.Activate();
    }

    private void StartScreen(bool atSignIn = false)
    {
        generation++;
        timer.Stop(); // Retain the frozen preview surface; its WGC session is already closed.
        try
        {
            // At sign-in the Studio canvas has not been loaded or laid out.
            var b = atSignIn ? (window.AppWindow.Position.X, window.AppWindow.Position.Y,
                window.AppWindow.Size.Width, window.AppWindow.Size.Height) : Bounds();
            Log.Information("Checking screen effect GPU Generation={Generation} StartupTask={StartupTask} Width={Width} Height={Height} CaptureAllowed={CaptureAllowed}",
                generation, atSignIn, b.Width, b.Height, model.State.CaptureAllowed);
            int hr = Native.RippleCheckCapabilities(b.X, b.Y, b.Width, b.Height, out var caps);
            Log.Information("ApplyRequested Generation={Generation} GPU={GPU} DedicatedVRAM={Vram} FeatureLevel=0x{FeatureLevel:X} Supported={Supported} Reason={Reason} HRESULT=0x{HResult:X8}",
                generation, caps.AdapterName, caps.DedicatedVideoMemory, caps.FeatureLevel, caps.Supported, caps.Reason, hr);
            model.State.CapabilityText = Describe(caps, hr);
            if (hr < 0 || caps.Supported == 0 || !model.State.CaptureAllowed)
            {
                if (!atSignIn) StartPreview();
                model.State.StatusMessage = model.State.CaptureAllowed ? Describe(caps, hr) : "Desktop capture is unavailable.";
                return;
            }
            var session = new ScreenEffectSession(model, caps, metrics =>
            {
                memory = metrics.Memory;
                model.State.MetricsText = $"Mode\tScreen effect\nPresents\t{metrics.Presents:F1} /s\nDesktop updates\t{metrics.Captures:F1} /s\nCapture submit\t{metrics.Native.LastCaptureMs:F2} ms\nRender submit\t{metrics.Native.LastRenderCpuMs:F2} ms\nCapture size\t{metrics.Native.CaptureWidth} × {metrics.Native.CaptureHeight} px";
            }, (closed, reason) => { if (ReferenceEquals(screen, closed)) StopScreen(reason); });
            screen = session;
            model.State.IsScreenEffectActive = true; model.State.CanApply = true;
            model.State.CaptureMessage = "Preview is not active because the effect is applied to the entire screen.";
            Log.Information("Starting screen effect session Generation={Generation}", generation);
            session.Start();
            if (ReferenceEquals(screen, session) && !model.State.IsInTray)
            {
                Native.SetTopmost(hwnd, true);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Apply failed Generation={Generation} HRESULT=0x{HResult:X8}", generation, ex.HResult);
            StopScreen(ex.Message);
            if (renderer == IntPtr.Zero) QueuePreview(ex.Message);
        }
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
        model.State.ResourcesText = $"Private RAM\t{sample.PrivateBytes / 1048576.0:F0} MiB\nWorking set\t{sample.WorkingSetBytes / 1048576.0:F0} MiB\nCPU\t{sample.CpuPercent:F1}%\nGPU local\t{memory.LocalUsage / 1048576.0:F0} / {memory.LocalBudget / 1048576.0:F0} MiB\nRAM warning\t500 MB";
        double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        if (now >= nextPerformanceLog)
        {
            nextPerformanceLog = now + 60;
            Log.Information("ResourceSummary ScreenActive={ScreenActive} PrivateBytes={PrivateBytes} WorkingSetBytes={WorkingSetBytes} CPU={CPU} GpuLocalBytes={GpuLocalBytes} DroppedLogs={DroppedLogs}",
                model.State.IsScreenEffectActive, sample.PrivateBytes, sample.WorkingSetBytes, sample.CpuPercent, memory.LocalUsage, LoggingService.DroppedEvents);
        }
    }
    private void SettingsChanged(object? sender, PropertyChangedEventArgs e) => Log.Debug("Setting changed {Property}", e.PropertyName);
    private async void OpenSettings()
    {
        if (model.State.IsClosing || model.State.IsSettingsOpen || model.State.IsClosePromptOpen) return;
        if (model.State.IsInTray) ShowFromTray();
        model.State.IsSettingsOpen = true;
        try
        {
            await startup.RefreshAsync();
            if (!model.State.IsClosing) await new SettingsDialog(model, ((FrameworkElement)window.Content).XamlRoot).ShowAsync();
        }
        catch (Exception ex) { Fail("Settings could not be opened", ex); }
        finally { model.State.IsSettingsOpen = false; }
    }

    private async void MainWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (model.State.IsExiting) return;
        args.Cancel = true;
        if (model.State.IsClosePromptOpen || model.State.IsSettingsOpen || model.State.IsCaptureConsentOpen) return;
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
            if (!tray.IsVisible) throw new InvalidOperationException("The Windows notification area is unavailable.");
            model.State.IsInTray = true;
            if (screen is null) ReleasePreview();
            window.AppWindow.Hide();
            Log.Information("Main window minimized to tray ScreenActive={ScreenActive}", model.State.IsScreenEffectActive);
        }
        catch (Exception ex) { model.State.IsInTray = false; Fail("Could not minimize to tray", ex); }
    }
    internal void ActivateExistingInstance()
    {
        if (model.State.IsClosing || model.State.OverlayPhase == "CapturingPreviewSnapshot") return;
        if (model.State.IsInTray)
        {
            ShowFromTray();
            return;
        }
        if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, 9); // SW_RESTORE
        window.Activate();
    }

    private void ShowFromTray()
    {
        model.State.IsInTray = false;
        tray.Hide();
        // Use WinUI activation directly: a startup-task window may never have
        // been activated before this point.
        window.Activate();
        if (screen is null)
        {
            // On the first activation Loaded will start the preview after layout.
            if (renderer == IntPtr.Zero && canvas.IsLoaded)
                window.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, SurfaceLoaded);
        }
        else Native.SetTopmost(hwnd, true);
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
    internal async void CheckHiddenStartup()
    {
        try
        {
            for (int i = 0; i < 80 && !model.State.ScreenOverlayVisible; i++) await Task.Delay(50);
            if (!model.State.IsInTray || !tray.IsVisible || Native.IsWindowVisible(hwnd))
                throw new InvalidOperationException("Startup did not keep Studio hidden in the notification area.");
            if (!model.State.IsScreenEffectActive || !model.State.ScreenOverlayVisible || !model.State.IsCaptureValid)
                throw new InvalidOperationException("Startup did not present the screen effect: " + model.State.StatusMessage);
            Console.WriteLine("PASS startup keeps Studio hidden while the screen effect runs");
            ShowFromTray();
            for (int i = 0; i < 80 && !canvas.IsLoaded; i++) await Task.Delay(50);
            if (model.State.IsInTray || !Native.IsWindowVisible(hwnd) || !canvas.IsLoaded)
                throw new InvalidOperationException("Studio could not be restored from its first hidden startup.");
            Console.WriteLine("PASS Studio restores with its XAML content loaded");
        }
        catch (Exception ex) { Environment.ExitCode = 1; Console.Error.WriteLine(ex); Log.Error(ex, "Hidden startup check failed"); }
        finally { ExitApp(); }
    }

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
    private void Fail(string operation, Exception ex)
    {
        Log.Error(ex, "{Operation} Generation={Generation} Phase={Phase} HRESULT=0x{HResult:X8}", operation, generation, model.State.OverlayPhase, ex.HResult);
        model.State.StatusMessage = operation + ": " + ex.Message;
    }
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
        tray.Dispose(); startup.Dispose();
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
