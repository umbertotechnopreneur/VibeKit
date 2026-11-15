using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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
    private readonly FrameworkElement surface;
    private readonly IntPtr hwnd;
    private readonly DispatcherQueueTimer resourceTimer;
    private readonly ResourceMonitor monitor;
    private readonly TrayService tray;
    private readonly StartupService startup;
    private readonly CaptureConsentService captureConsent;
    private readonly EmergencyShortcut emergencyShortcut;
    private ScreenEffectSession? screen;
    private GpuMemory memory;
    private long generation;
    private double nextPerformanceLog;
    internal ICommand ToggleScreenCommand { get; }
    internal ICommand RestartCommand { get; }
    internal ICommand OpenSettingsCommand { get; }
    internal ICommand MinimizeToTrayCommand { get; }
    internal ICommand InspectScreenCommand { get; }
    internal bool IsInTray => model.State.IsInTray;

    internal StudioController(StudioModel model, Window window, FrameworkElement surface)
    {
        this.model = model; this.window = window; this.surface = surface;
        startup = new StartupService(model.State);
        captureConsent = new CaptureConsentService(model.State);
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        tray = new TrayService(hwnd, Path.Combine(AppContext.BaseDirectory, "Assets", "WaterRipple.ico"), ShowFromTray, OpenSettings, ExitApp);
        window.AppWindow.Closing += MainWindowClosing;
        ToggleScreenCommand = new DelegateCommand(ToggleScreen);
        RestartCommand = new DelegateCommand(CheckCapabilities);
        OpenSettingsCommand = new DelegateCommand(OpenSettings);
        MinimizeToTrayCommand = new DelegateCommand(() =>
        {
            if (!model.State.IsClosing && !model.State.IsSettingsOpen && !model.State.IsCaptureConsentOpen && !model.State.IsClosePromptOpen)
                MinimizeToTray();
        });
        InspectScreenCommand = new DelegateCommand(() => screen?.InspectCapture());

        resourceTimer = window.DispatcherQueue.CreateTimer(); resourceTimer.Interval = TimeSpan.FromSeconds(1); resourceTimer.Tick += (_, _) => UpdateResources(); resourceTimer.Start();
        monitor = new ResourceMonitor(warning: sample =>
        {
            Log.Warning("Private memory threshold reached Bytes={PrivateBytes} WorkingSet={WorkingSet}", sample.PrivateBytes, sample.WorkingSetBytes);
            Native.MessageBox(IntPtr.Zero, "Water Ripple Studio is using at least 500 MB of private memory. This could be a memory leak. Please report it and include the recent application logs.\n\nStop the screen effect or close the app if memory keeps growing.", "Water Ripple Studio · memory", 0x50030);
        });
        emergencyShortcut = new EmergencyShortcut(model.Settings.EmergencyShortcutEnabled, status =>
            window.DispatcherQueue.TryEnqueue(() => model.State.EmergencyShortcutStatus = status));
        model.Settings.PropertyChanged += EmergencyShortcutChanged;
        model.Settings.PropertyChanged += SettingsChanged;
    }

    private bool initialized;
    internal void SurfaceLoaded()
    {
        if (initialized || model.State.IsClosing) return;
        initialized = true;
        CheckCapabilities();
        if (screen is null) StartScreen();
#if DEBUG
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--exercise-overlay") >= 0)
            _ = ExerciseOverlayAsync();
#endif
    }

    private void CheckCapabilities()
    {
        try
        {
            model.State.CaptureAllowed = Native.SetWindowDisplayAffinity(hwnd, 0);
            var b = Bounds();
            int hr = Native.RippleCheckCapabilities(b.X, b.Y, b.Width, b.Height, out var caps);
            model.State.GpuName = caps.AdapterName ?? "Display GPU";
            model.State.DedicatedVramBytes = caps.DedicatedVideoMemory;
            model.State.CanApply = hr >= 0 && caps.Supported != 0 && model.State.CaptureAllowed;
            model.State.CapabilityText = Describe(caps, hr);
        }
        catch (Exception ex) { model.State.CanApply = false; Fail("GPU check failed", ex); }
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
        try
        {
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
            model.State.StatusMessage = "Changes apply immediately to the screen effect.";
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
        model.State.StatusMessage = reason.Length > 0 ? reason : "Screen effect stopped.";
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
                Content = "Minimize to the notification area to keep enabled effects running. Closing the app stops effects and releases its resources.\n\n"
                    + (model.Settings.EmergencyShortcutEnabled && model.State.EmergencyShortcutStatus == "Emergency shortcut is active and immediately closes Water Ripple."
                        ? "In an emergency, press Ctrl + Alt + Shift + W to immediately close Water Ripple."
                        : "The emergency shortcut is disabled or unavailable. Check its status in the main window."),
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
            window.AppWindow.Hide();
            Log.Information("Main window minimized to tray ScreenActive={ScreenActive}", model.State.IsScreenEffectActive);
        }
        catch (Exception ex) { model.State.IsInTray = false; Fail("Could not minimize to tray", ex); }
    }
    internal void ActivateExistingInstance()
    {
        if (model.State.IsClosing) return;
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
        if (screen is not null) Native.SetTopmost(hwnd, true);
        Log.Information("Main window restored from tray");
    }
    private void ExitApp()
    {
        model.State.IsExiting = true;
        Dispose();
        window.Close();
    }

#if DEBUG
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
            for (int i = 0; i < 80 && !surface.IsLoaded; i++) await Task.Delay(50);
            if (model.State.IsInTray || !Native.IsWindowVisible(hwnd) || !surface.IsLoaded)
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
            StopScreen();
            for (int i = 1; i <= 3; i++)
            {
                StartScreen();
                await Task.Delay(1600);
                if (!model.State.IsScreenEffectActive || !model.State.ScreenOverlayVisible || !model.State.IsCaptureValid)
                    throw new InvalidOperationException($"Overlay cycle {i} failed to present valid pixels: {model.State.StatusMessage}");
                StopScreen();
                await Task.Delay(700);
                if (model.State.IsScreenEffectActive || model.State.ScreenOverlayVisible)
                    throw new InvalidOperationException($"Overlay cycle {i} did not stop");
                using var process = Process.GetCurrentProcess(); process.Refresh();
                Console.WriteLine($"PASS overlay cycle {i}; private={process.PrivateMemorySize64 / 1048576} MiB; phase={model.State.OverlayPhase}");
            }
            Log.Information("Overlay cycle checks passed");
            MinimizeToTray();
            await Task.Delay(300);
            if (!model.State.IsInTray || !tray.IsVisible || Native.IsWindowVisible(hwnd))
                throw new InvalidOperationException("Tray minimization did not hide the main window");
            ShowFromTray();
            await Task.Delay(500);
            if (model.State.IsInTray || tray.IsVisible || !Native.IsWindowVisible(hwnd))
                throw new InvalidOperationException("Tray restore did not restore the main window");
            Console.WriteLine("PASS native tray minimize/restore and screen control");
        }
        catch (Exception ex) { Environment.ExitCode = 1; Console.Error.WriteLine(ex); Log.Error(ex, "Overlay cycle checks failed"); }
        finally { ExitApp(); }
    }
#endif
    internal bool HandleKey(VirtualKey key)
    {
        switch (key)
        {
            case VirtualKey.F7: ToggleScreen(); return true;
            case VirtualKey.F8: OpenSettings(); return true;
            case VirtualKey.Escape: if (screen is not null) StopScreen(); else window.Close(); return true;
            default: return false;
        }
    }
    private (int X, int Y, int Width, int Height) Bounds()
    {
        return (window.AppWindow.Position.X, window.AppWindow.Position.Y,
            Math.Max(2, window.AppWindow.Size.Width), Math.Max(2, window.AppWindow.Size.Height));
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
    private void EmergencyShortcutChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.EmergencyShortcutEnabled))
        {
            model.State.EmergencyShortcutStatus = "Updating emergency shortcut…";
            emergencyShortcut.SetEnabled(model.Settings.EmergencyShortcutEnabled);
        }
    }

    public void Dispose()
    {
        if (model.State.IsClosing) return;
        model.State.IsClosing = true;
        window.AppWindow.Closing -= MainWindowClosing;
        model.Settings.PropertyChanged -= EmergencyShortcutChanged;
        emergencyShortcut.Dispose();
        tray.Dispose(); startup.Dispose();
        model.Settings.PropertyChanged -= SettingsChanged;
        resourceTimer.Stop(); monitor.Dispose();
        StopScreen(); model.Pipelines.Dispose();

    }
}
internal static partial class Native
{
    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)] internal static extern int MessageBox(IntPtr hwnd, string message, string title, uint flags);
}
