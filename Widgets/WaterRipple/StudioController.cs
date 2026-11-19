using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Threading.Tasks;
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
    private readonly StudioDiagnostics diagnostics;
    private readonly TrayService tray;
    private readonly StartupService startup;
    private readonly CaptureConsentService captureConsent;
    private readonly EmergencyShortcut emergencyShortcut;
    private readonly CloseSequence closeSequence;
    private bool closeFailureOpen;
    private ScreenEffectSession? screen;
    private bool notifyWhenRestored;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer performanceTimer, gpuJokeTimer;

    private readonly Stopwatch performanceClock = Stopwatch.StartNew();
    private double lastTick, stableSince = -1, recoveryAt, lastRecovery;
    private int recoveryFailures, lateTicks;
    private bool recoveryPending;
    private ulong previousIdle, previousKernel, previousUser;
    private bool haveCpuSample;
    private long generation;
    internal ICommand ToggleScreenCommand { get; }
    internal ICommand RestartCommand { get; }
    internal ICommand OpenSettingsCommand { get; }
    internal event Action? SettingsRequested;
    internal ICommand InspectScreenCommand { get; }
    internal bool IsInTray => model.State.IsInTray;

    internal StudioController(StudioModel model, Window window, FrameworkElement surface,
        Func<Task<bool>> flushSettings, Func<Task> discardSettings)
    {
        this.model = model; this.window = window; this.surface = surface;
        closeSequence = new CloseSequence(
            stop: () =>
            {
                StopScreen();
                model.Pipelines.Flush();
                model.State.StatusMessage = "Saving settings before closing…";
            },
            flush: flushSettings,
            close: () =>
            {
                model.State.IsExiting = true;
                Dispose();
                window.Close();
            },
            failed: () =>
            {
                if (model.State.IsInTray) ShowFromTray();
                else window.Activate();
                model.State.StatusMessage = "Settings could not be saved. The effect is stopped; close again to retry.";
            }, discard: discardSettings);
        startup = new StartupService(model.State);
        captureConsent = new CaptureConsentService(model.State);
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        tray = new TrayService(hwnd, Path.Combine(AppContext.BaseDirectory, "Assets", "WaterRipple.ico"), ShowFromTray, OpenSettings, ExitApp);
        window.AppWindow.Closing += MainWindowClosing;
        ToggleScreenCommand = new DelegateCommand(ToggleScreen);
        RestartCommand = new DelegateCommand(CheckCapabilities);
        OpenSettingsCommand = new DelegateCommand(OpenSettings);
        InspectScreenCommand = new DelegateCommand(() => screen?.InspectCapture());

        diagnostics = new StudioDiagnostics(model.State, window.DispatcherQueue);
        performanceTimer = window.DispatcherQueue.CreateTimer();
        performanceTimer.Interval = TimeSpan.FromSeconds(1);
        performanceTimer.Tick += PerformanceTick;
        performanceTimer.Start();
        emergencyShortcut = new EmergencyShortcut(model.Settings.EmergencyShortcutEnabled, status =>
            window.DispatcherQueue.TryEnqueue(() => model.State.EmergencyShortcutStatus = status));
        gpuJokeTimer = window.DispatcherQueue.CreateTimer();
        gpuJokeTimer.IsRepeating = false;
        gpuJokeTimer.Tick += ShowGpuJoke;
        if (model.Settings.ShowGpuJokes)
        {
            gpuJokeTimer.Interval = TimeSpan.FromSeconds(Random.Shared.Next(120, 241));
            gpuJokeTimer.Start();
        }
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
        CancelRecovery();
        if (closeSequence.IsPending || model.State.IsClosing || model.State.IsCaptureConsentOpen || model.State.IsSettingsOpen) return;
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
        if (closeSequence.IsPending || model.State.IsClosing) return;
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
                diagnostics.Observe(metrics);
                if (notifyWhenRestored && model.State.IsCaptureValid)
                {
                    notifyWhenRestored = false;
                    NotifyEffect("Ripple resumed", "The screen effect is active again.");
                }
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
        CancelRecovery();
        var active = screen; screen = null;
        model.State.IsScreenEffectActive = false;
        model.State.ScreenOverlayVisible = false;
        model.State.IsCaptureValid = false;
        active?.Dispose();
        Native.SetTopmost(hwnd, false);
        diagnostics.ClearCapture();
        Log.Information("ScreenStopped Generation={Generation} ReportedFailure={ReportedFailure}", generation, reason.Length > 0);
        model.State.StatusMessage = reason.Length > 0 ? reason : "Screen effect stopped.";
        if (active is not null && reason.Length > 0 && reason != "Stopped by global shortcut.")
        {
            notifyWhenRestored = true;
            NotifyEffect("Ripple stopped", reason);
        }
    }

    private void NotifyEffect(string title, string message)
    {
        try { tray.Notify(title, message); }
        catch (Exception ex) { Log.Warning(ex, "Ripple notification unavailable"); }
    }

    private void SettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        Log.Debug("Setting changed {Property}", e.PropertyName);
        if (e.PropertyName == nameof(AppSettings.ShowGpuJokes) && !model.Settings.ShowGpuJokes) gpuJokeTimer.Stop();
        if (e.PropertyName == nameof(AppSettings.AutoRecover) && !model.Settings.AutoRecover && recoveryPending)
        {
            CancelRecovery();
            model.State.StatusMessage = "Automatic recovery cancelled. The effect remains stopped.";
        }
    }

    private void ShowGpuJoke(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (model.State.IsClosing || closeSequence.IsPending || !model.Settings.ShowGpuJokes) return;
        try { tray.Notify("Water Ripple · That's a joke", GpuJokes.Captions[0], silent: true); }
        catch (Win32Exception ex) { Log.Debug(ex, "GPU joke notification unavailable"); }
    }

    private void CancelRecovery() { recoveryPending = false; stableSince = -1; }

    private void PerformanceTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        double now = performanceClock.Elapsed.TotalSeconds;
        double elapsed = lastTick == 0 ? 1 : now - lastTick;
        lastTick = now;
        bool healthy = SystemHealthy() && elapsed <= 2;
        if (screen is not null)
        {
            lateTicks = elapsed > 2 ? lateTicks + 1 : 0;
            if (elapsed > 4 || lateTicks >= 2)
            {
                lateTicks = 0;
                StopScreen();
                recoveryFailures = now - lastRecovery < 300 && lastRecovery > 0 ? recoveryFailures + 1 : 1;
                if (model.Settings.AutoRecover)
                {
                    recoveryPending = true;
                    recoveryAt = now + Math.Min(300, 60 * Math.Pow(2, Math.Min(3, recoveryFailures - 1)));
                    stableSince = -1;
                }
                model.State.StatusMessage = recoveryPending ? "Paused for performance; waiting for the system to recover." : "Paused for performance. Start the effect when ready.";
                NotifyEffect("Ripple paused for performance", model.State.StatusMessage);
            }
        }
        else if (recoveryPending)
        {
            if (!healthy) stableSince = -1;
            else if (stableSince < 0) stableSince = now;
            if (now >= recoveryAt && stableSince >= 0 && now - stableSince >= 30 && !model.State.IsClosing && !model.State.IsSettingsOpen && !model.State.IsCaptureConsentOpen)
            {
                CancelRecovery();
                lastRecovery = now;
                notifyWhenRestored = true;
                StartScreen();
                if (screen is null) notifyWhenRestored = false;
            }
        }
    }

    private bool SystemHealthy()
    {
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GetSystemTimes(out long idle, out long kernel, out long user) || !GlobalMemoryStatusEx(ref memory))
        { haveCpuSample = false; return false; }
        ulong i = unchecked((ulong)idle), k = unchecked((ulong)kernel), u = unchecked((ulong)user);
        bool valid = haveCpuSample && k >= previousKernel && u >= previousUser && i >= previousIdle;
        ulong total = valid ? (k - previousKernel) + (u - previousUser) : 0;
        ulong idleDelta = valid ? i - previousIdle : 0;
        previousIdle = i; previousKernel = k; previousUser = u; haveCpuSample = true;
        return total > 0 && idleDelta <= total && (double)idleDelta / total > .15 &&
            memory.TotalPhysical > 0 && memory.AvailablePhysical >= Math.Max(memory.TotalPhysical / 10, 512UL * 1024 * 1024);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatus
    {
        internal uint Length, MemoryLoad;
        internal ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    private async void OpenSettings()
    {
        if (model.State.IsClosing || model.State.IsSettingsOpen || model.State.IsClosePromptOpen) return;
        if (model.State.IsInTray) ShowFromTray();
        model.State.IsSettingsOpen = true;
        try
        {
            await startup.RefreshAsync();
            if (!model.State.IsClosing) SettingsRequested?.Invoke();
        }
        catch (Exception ex) { Fail("Settings could not be opened", ex); }
        finally { model.State.IsSettingsOpen = false; }
    }

    private async void MainWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (model.State.IsExiting) return;
        args.Cancel = true;
        if (closeFailureOpen || closeSequence.IsPending || model.State.IsClosePromptOpen || model.State.IsSettingsOpen || model.State.IsCaptureConsentOpen) return;
        model.State.IsClosePromptOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                Title = "Close Water Ripple?",
                Content = "Keep running in the notification area, or quit to stop the effect and close the app.\n\n"
                    + (model.Settings.EmergencyShortcutEnabled && model.State.EmergencyShortcutStatus == "Emergency shortcut is active and immediately closes Water Ripple."
                        ? "In an emergency, press Ctrl + Alt + Shift + W to immediately close Water Ripple."
                        : "The emergency shortcut is disabled or unavailable. Check its status in the main window."),
                PrimaryButtonText = "Keep in tray", SecondaryButtonText = "Quit", CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary, RequestedTheme = ((FrameworkElement)window.Content).ActualTheme,
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
    private async void ExitApp()
    {
        if (closeFailureOpen || closeSequence.IsPending) return;
        try
        {
            while (!await closeSequence.TryCloseAsync())
            {
                closeFailureOpen = true;
                ContentDialogResult result;
                try
                {
                    result = await new ContentDialog
                    {
                        Title = "Settings could not be saved",
                        Content = "The effect is stopped. You can retry, keep Ripple open, or discard unsaved preferences and close.\n\n" + model.State.PersistenceError,
                        PrimaryButtonText = "Retry", SecondaryButtonText = "Close without saving", CloseButtonText = "Keep open",
                        DefaultButton = ContentDialogButton.Close, RequestedTheme = ((FrameworkElement)window.Content).ActualTheme,
                        XamlRoot = ((FrameworkElement)window.Content).XamlRoot,
                    }.ShowAsync();
                }
                finally { closeFailureOpen = false; }
                if (result == ContentDialogResult.Secondary)
                {
                    await closeSequence.DiscardAndCloseAsync();
                    return;
                }
                if (result != ContentDialogResult.Primary) return;
            }
        }
        catch (Exception error) { Fail("Could not close Ripple", error); }
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
        gpuJokeTimer.Stop();
        gpuJokeTimer.Tick -= ShowGpuJoke;
        tray.Dispose(); startup.Dispose();
        model.Settings.PropertyChanged -= SettingsChanged;
        performanceTimer.Stop();
        performanceTimer.Tick -= PerformanceTick;
        diagnostics.Dispose();
        StopScreen(); model.Pipelines.Dispose();

    }
}
internal static partial class Native
{
    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)] internal static extern int MessageBox(IntPtr hwnd, string message, string title, uint flags);
}
