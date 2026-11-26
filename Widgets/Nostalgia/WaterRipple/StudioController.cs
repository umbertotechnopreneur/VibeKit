using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;
using Tools.Widgets;
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
    private readonly WidgetTrayIcon tray;
    private readonly WidgetCloseDialog closeDialog = new();
    private readonly StartupService startup;
    private readonly CaptureConsentService captureConsent;
    private readonly EmergencyShortcut emergencyShortcut;
    private readonly ScreenEffectInput input;
    private readonly CloseSequence closeSequence;
    private bool closeFailureOpen;
    private readonly Dictionary<string, (DisplayTarget Display, ScreenEffectSession Session)> screens = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> displayFailures = new(StringComparer.OrdinalIgnoreCase);
    private DisplayTarget[] connectedDisplays = [];
    private bool announcedEffect, applyingDisplaySelection;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer performanceTimer, gpuJokeTimer;

    private readonly Stopwatch performanceClock = Stopwatch.StartNew();
    private double lastTick;
    private int lateTicks;
    private readonly DesktopEffectRecovery recovery;
    private long generation;
    internal ICommand RestartCommand { get; }
    internal ICommand OpenSettingsCommand { get; }
    internal event Action? SettingsRequested;
    internal event Action? DisplaysChanged;
    internal ICommand InspectScreenCommand { get; }
    internal bool IsInTray => model.State.IsInTray;
    internal IReadOnlyCollection<string> DisplaySelection => model.Settings.TargetDisplays
        ?? throw new InvalidOperationException("The studio monitor selection has not been initialized.");

    internal StudioController(StudioModel model, Window window, FrameworkElement surface,
        Func<Task<bool>> flushSettings, Func<Task> discardSettings)
    {
        this.model = model; this.window = window; this.surface = surface;
        recovery = new DesktopEffectRecovery("Water Ripple", () => StartScreen());
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
        var menu = new WidgetTrayMenu(window, ShowFromTray, OpenSettings, () => AboutWindow.Show(window, model.Settings), ExitApp,
            () => (model.State.EffectEnabled, model.State.EffectEnabled || CanStartFromTray()), SetTrayEffect);
        tray = new WidgetTrayIcon(window, "Water Ripple", Path.Combine(AppContext.BaseDirectory, "Assets", "WaterRipple.ico"),
            menu, ShowFromTray, SetTrayEffect);
        window.AppWindow.Closing += MainWindowClosing;
        RestartCommand = new DelegateCommand(CheckCapabilities);
        OpenSettingsCommand = new DelegateCommand(OpenSettings);
        InspectScreenCommand = new DelegateCommand(InspectScreen);
        input = new ScreenEffectInput(hwnd, window.DispatcherQueue, () => StopScreen("Stopped by global shortcut."),
            DisplayTopologyChanged, AddClick);
        connectedDisplays = DisplayTarget.GetConnected().ToArray();
        // On first launch, select the studio monitor once and persist its identity across reconnects.
        model.Settings.TargetDisplays ??= DisplayTarget.GetStudioDisplay(connectedDisplays, hwnd)?.Identifiers ?? [];

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
            gpuJokeTimer.Interval = TimeSpan.FromMinutes(2);
            gpuJokeTimer.Start();
        }
        model.Settings.PropertyChanged += EmergencyShortcutChanged;
        model.Settings.PropertyChanged += SettingsChanged;
    }

    private bool initialized;
    private long stopRequests;
    internal void SurfaceLoaded()
    {
        if (initialized || model.State.IsClosing) return;
        initialized = true;
        CheckCapabilities();
        if (!model.State.EffectEnabled && stopRequests == 0) StartScreen();
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
            var displays = SelectedDisplays();
            model.State.CanApply = false;
            if (displays.Length == 0)
            {
                model.State.CanApply = model.State.CaptureAllowed;
                model.State.CapabilityText = NoMonitorStatus();
                return;
            }
            foreach (var display in displays)
            {
                var (hr, caps) = CheckDisplay(display);
                model.State.GpuName = caps.AdapterName ?? "Display GPU";
                model.State.DedicatedVramBytes = caps.DedicatedVideoMemory;
                model.State.CapabilityText = Describe(caps, hr);
                if (hr < 0 || caps.Supported == 0 || !model.State.CaptureAllowed) continue;
                model.State.CanApply = true;
                break;
            }
        }
        catch (Exception ex) { model.State.CanApply = false; Fail("GPU check failed", ex); }
    }

    internal async Task ToggleScreenAsync()
    {
        if (model.State.EffectEnabled) { StopScreen(); return; }
        recovery.Reset();
        if (closeSequence.IsPending || model.State.IsClosing || model.State.IsCaptureConsentOpen || model.State.IsSettingsOpen) return;
        long priorStops = stopRequests;
        await captureConsent.EnsureAsync(((FrameworkElement)window.Content).XamlRoot);
        if (priorStops != stopRequests || model.State.IsClosing || model.State.EffectEnabled) return;
        StartScreen();
    }

    private bool CanStartFromTray() => model.State.CanApply && !model.State.IsClosing &&
        !closeSequence.IsPending && !model.State.IsSettingsOpen && !model.State.IsCaptureConsentOpen &&
        !model.State.IsClosePromptOpen;

    private void SetTrayEffect(bool enabled)
    {
        if (!enabled) { StopScreen(); return; }
        if (!model.State.EffectEnabled && CanStartFromTray())
        {
            recovery.Reset();
            StartScreen();
        }
    }

    internal void StartAtSignIn()
    {
        // Startup owns the first capture attempt; Loaded must not retry it on restore.
        initialized = true;
        MinimizeToTray();
        if (!model.State.IsInTray)
            Log.Warning("Startup tray unavailable; the main window remains minimized in the taskbar");
        model.State.CaptureAllowed = Native.SetWindowDisplayAffinity(hwnd, 0);
        // The optional borderless-capture prompt belongs to the interactive Apply action.
        StartScreen(atSignIn: true);
    }

    private void StartScreen(bool atSignIn = false)
    {
        if (closeSequence.IsPending || model.State.IsClosing) return;
        if (!model.State.CaptureAllowed) { StopScreen("Desktop capture is unavailable."); return; }
        generation++;
        model.State.EffectEnabled = true;
        ApplyDisplaySelection(atSignIn);
        DisplaysChanged?.Invoke();
    }

    private DisplayTarget[] SelectedDisplays() =>
        DisplayTarget.GetSelected(connectedDisplays, DisplaySelection).ToArray();

    private string NoMonitorStatus() => model.Settings.TargetDisplays?.Count == 0 ?
        "No monitors selected." : "No selected monitor is connected.";

    private static (int Result, GpuCapabilities Caps) CheckDisplay(DisplayTarget display)
    {
        var bounds = display.Bounds;
        int result = Native.RippleCheckCapabilities(bounds.X, bounds.Y, bounds.Width, bounds.Height, out var caps);
        return (result, caps);
    }

    private void ApplyDisplaySelection(bool atSignIn = false)
    {
        applyingDisplaySelection = true;
        try
        {
            var desired = SelectedDisplays().ToDictionary(display => display.Id, StringComparer.OrdinalIgnoreCase);
            foreach (string id in displayFailures.Keys.Where(id => !desired.ContainsKey(id)).ToArray())
                displayFailures.Remove(id);
            foreach (var (id, entry) in screens.ToArray())
            {
                if (desired.TryGetValue(id, out var display) && display.Handle == entry.Display.Handle &&
                    display.Bounds == entry.Display.Bounds) continue;
                screens.Remove(id);
                entry.Session.Dispose();
            }
            if (desired.Count > 0) input.Start();
            foreach (var (id, display) in desired)
            {
                if (screens.ContainsKey(id) || displayFailures.ContainsKey(id)) continue;
                StartDisplay(display, atSignIn);
            }
            if (screens.Count == 0 && displayFailures.Count > 0) { StopScreen(displayFailures.Values.First()); return; }
            if (screens.Count == 0) input.Stop();
            RefreshScreenState();
            RefreshMetrics();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Apply failed Generation={Generation} HRESULT=0x{HResult:X8}", generation, ex.HResult);
            StopScreen(ex.Message);
        }
        finally { applyingDisplaySelection = false; }
    }

    private void StartDisplay(DisplayTarget display, bool atSignIn)
    {
        ScreenEffectSession? session = null;
        try
        {
            var (hr, caps) = CheckDisplay(display);
            Log.Information("ApplyRequested Generation={Generation} StartupTask={StartupTask} GPU={GPU} DedicatedVRAM={Vram} FeatureLevel=0x{FeatureLevel:X} Supported={Supported} Reason={Reason} HRESULT=0x{HResult:X8}",
                generation, atSignIn, caps.AdapterName, caps.DedicatedVideoMemory, caps.FeatureLevel, caps.Supported, caps.Reason, hr);
            model.State.CapabilityText = Describe(caps, hr);
            if (hr < 0 || caps.Supported == 0)
            {
                displayFailures[display.Id] = $"{display.Label} · {model.State.CapabilityText}";
                return;
            }
            if ((caps.Left, caps.Top, caps.Width, caps.Height) != display.Bounds)
            {
                displayFailures[display.Id] = $"{display.Label} · The display configuration changed. Turn this monitor on again.";
                return;
            }
            model.State.CanApply = true;
            model.State.GpuName = caps.AdapterName ?? "Display GPU";
            model.State.DedicatedVramBytes = caps.DedicatedVideoMemory;
            session = new ScreenEffectSession(model.Settings, caps, _ => RefreshScreenState(),
                (closed, reason, recoverable) => DisplayStopped(display, closed, reason, recoverable), RefreshMetrics);
            screens.Add(display.Id, (display, session));
            session.Start();
        }
        catch (Exception ex)
        {
            screens.Remove(display.Id);
            session?.Dispose();
            displayFailures[display.Id] = $"{display.Label} · {ex.Message}";
            Log.Error(ex, "Display overlay failed Generation={Generation} HRESULT=0x{HResult:X8}", generation, ex.HResult);
        }
    }

    private void DisplayStopped(DisplayTarget display, ScreenEffectSession session, string reason, bool recoverable)
    {
        if (!screens.TryGetValue(display.Id, out var entry) || !ReferenceEquals(entry.Session, session)) return;
        screens.Remove(display.Id);
        reason = $"{display.Label} · {(reason.Length == 0 ? "The overlay closed." : reason)}";
        displayFailures[display.Id] = reason;
        if (applyingDisplaySelection) return;
        if (recoverable) { PauseForRecovery(reason); return; }
        if (screens.Count == 0) { StopScreen(reason); return; }
        RefreshScreenState();
        RefreshMetrics();
    }

    private void RefreshScreenState()
    {
        var active = screens.Values;
        int ready = active.Count(entry => entry.Session.CaptureValid);
        bool valid = ready > 0;
        bool visible = active.Any(entry => entry.Session.OverlayVisible);
        bool inspecting = active.Any(entry => entry.Session.InspectionVisible);
        model.State.IsScreenEffectActive = active.Count > 0;
        model.State.IsCaptureValid = valid;
        model.State.ScreenOverlayVisible = visible;
        model.State.ActiveWaveCount = active.Sum(entry => entry.Session.ActiveWaveCount);
        model.State.OverlayPhase = visible ? "VisibleWithValidCapture" : valid ? "PresentedByCrt" :
            active.Count > 0 ? "WaitingForValidCapture" : "Inactive";
        if (inspecting) model.State.StatusMessage = "Capture paused for screenshot inspection (20 seconds).";
        else if (displayFailures.Count > 0) model.State.StatusMessage = displayFailures.Values.First();
        else if (active.Count == 0) model.State.StatusMessage = NoMonitorStatus();
        else if (!valid) model.State.StatusMessage = "Waiting for screen frames…";
        else if (ready < active.Count) model.State.StatusMessage = $"Water Ripple is active on {ready} of {active.Count} monitors.";
        else model.State.StatusMessage = ready == 1 ? "Water Ripple effect is active." :
            $"Water Ripple is active on {ready} monitors.";
        if (active.Count == 0) announcedEffect = false;
        if (valid && !announcedEffect)
        {
            announcedEffect = true;
            NotifyEffect("Ripple effect on", "The screen effect is active.");
        }
    }

    private void RefreshMetrics()
    {
        var metrics = screens.Values.Select(entry => entry.Session.LastMetrics).OfType<ScreenSessionMetrics>().ToArray();
        if (metrics.Length == 0) diagnostics.ClearCapture();
        else diagnostics.Observe(metrics);
    }

    private void AddClick(int x, int y)
    {
        foreach (var entry in screens.Values)
            if (entry.Session.AddClick(x, y)) break;
    }

    private void InspectScreen()
    {
        var current = DisplayTarget.GetStudioDisplay(connectedDisplays, hwnd);
        var sessions = screens.Values.ToArray();
        var chosen = sessions.FirstOrDefault(entry => StringComparer.OrdinalIgnoreCase.Equals(entry.Display.Id, current?.Id) &&
            entry.Session.CaptureValid).Session ??
            sessions.FirstOrDefault(entry => entry.Session.CaptureValid).Session;
        if (chosen is null) return;
        foreach (var entry in sessions)
            if (!ReferenceEquals(entry.Session, chosen)) entry.Session.EndInspection();
        chosen.InspectCapture();
    }

    private void DisplayTopologyChanged()
    {
        var current = DisplayTarget.GetConnected().ToArray();
        if (connectedDisplays.Length == current.Length && connectedDisplays.Zip(current).All(pair =>
            pair.First.Handle == pair.Second.Handle && pair.First.Bounds == pair.Second.Bounds &&
            pair.First.Label == pair.Second.Label && pair.First.Details == pair.Second.Details &&
            pair.First.Identifiers.SequenceEqual(pair.Second.Identifiers, StringComparer.OrdinalIgnoreCase))) return;
        connectedDisplays = current;
        if (model.State.EffectEnabled) ApplyDisplaySelection();
        else CheckCapabilities();
        DisplaysChanged?.Invoke();
    }

    private void StopScreen(string reason = "")
    {
        ++stopRequests;
        CancelRecovery();
        bool hadEffect = model.State.IsScreenEffectActive || screens.Count > 0;
        var active = screens.Values.ToArray();
        screens.Clear();
        input.Stop();
        announcedEffect = false;
        displayFailures.Clear();
        model.State.EffectEnabled = false;
        model.State.IsScreenEffectActive = false;
        model.State.ScreenOverlayVisible = false;
        model.State.IsCaptureValid = false;
        model.State.OverlayPhase = "Closed";
        model.State.ActiveWaveCount = 0;
        foreach (var entry in active) entry.Session.Dispose();
        diagnostics.ClearCapture();
        Log.Information("ScreenStopped Generation={Generation} ReportedFailure={ReportedFailure}", generation, reason.Length > 0);
        model.State.StatusMessage = reason.Length > 0 ? reason : "Screen effect stopped.";
        if (hadEffect) NotifyEffect("Ripple effect off", model.State.StatusMessage);
    }

    private void NotifyEffect(string title, string message)
    {
        try { tray.Notify(title, message); }
        catch (Exception ex) { Log.Warning(ex, "Ripple notification unavailable"); }
    }

    private void SettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        Log.Debug("Setting changed {Property}", e.PropertyName);
        if (e.PropertyName == nameof(AppSettings.TargetDisplays))
        {
            if (model.State.EffectEnabled) ApplyDisplaySelection();
            else CheckCapabilities();
        }
        if (e.PropertyName == nameof(AppSettings.ShowGpuJokes) && !model.Settings.ShowGpuJokes) gpuJokeTimer.Stop();
        if (e.PropertyName == nameof(AppSettings.AutoRecover) && !model.Settings.AutoRecover && recovery.Pending)
        {
            CancelRecovery();
            model.State.StatusMessage = "Automatic recovery cancelled. The effect remains stopped.";
        }
    }

    private void ShowGpuJoke(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (model.State.IsClosing || closeSequence.IsPending || !model.Settings.ShowGpuJokes) return;
        try { tray.Notify("Water Ripple · That's a joke", GpuJokes.Caption(0), silent: true); }
        catch (Win32Exception ex) { Log.Debug(ex, "GPU joke notification unavailable"); }
    }

    private void CancelRecovery() => recovery.Cancel();

    private void PauseForRecovery(string reason)
    {
        StopScreen(reason);
        if (recovery.Pause(model.Settings.AutoRecover, 60))
        {
            model.State.StatusMessage = reason + " Waiting for coordinated recovery.";
        }
        else if (model.Settings.AutoRecover && !recovery.Available)
            model.State.StatusMessage = reason + " Automatic recovery is unavailable; enable the effect manually.";
    }

    private void PerformanceTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        double now = performanceClock.Elapsed.TotalSeconds;
        double elapsed = lastTick == 0 ? 1 : now - lastTick;
        lastTick = now;
        if (screens.Count > 0)
        {
            lateTicks = elapsed > 2 ? lateTicks + 1 : 0;
            if (elapsed > 4 || lateTicks >= 2)
            {
                lateTicks = 0;
                PauseForRecovery("Paused for performance.");
            }
        }
        recovery.Tick(!model.State.IsClosing && !closeSequence.IsPending && !model.State.IsSettingsOpen &&
            !model.State.IsCaptureConsentOpen && !model.State.IsClosePromptOpen, screens.Count > 0);
        model.State.AllEffectsEnabled = recovery.AllEnabled;
        model.State.AnyEffectsEnabled = recovery.AnyEnabled;
        model.State.CoordinationAvailable = recovery.Available;
        if (recovery.Pending && !recovery.Available)
        {
            CancelRecovery();
            model.State.StatusMessage = "Automatic recovery is unavailable; enable the effect manually.";
        }
    }
    private void OpenSettings()
    {
        if (model.State.IsClosing || model.State.IsSettingsOpen || model.State.IsClosePromptOpen) return;
        if (model.State.IsInTray) ShowFromTray();
        model.State.IsSettingsOpen = true;
        try
        {
            startup.Refresh();
            if (!model.State.IsClosing) SettingsRequested?.Invoke();
        }
        catch (Exception ex) { Fail("Settings could not be opened", ex); }
        finally { model.State.IsSettingsOpen = false; }
    }

    private async void MainWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (model.State.IsExiting) return;
        args.Cancel = true;
        if (closeDialog.FocusIfOpen(window) || closeFailureOpen || closeSequence.IsPending || model.State.IsClosePromptOpen || model.State.IsSettingsOpen || model.State.IsCaptureConsentOpen) return;
        model.State.IsClosePromptOpen = true;
        bool trayWasVisible = tray.IsVisible;
        try
        {
            bool canMinimize;
            try { tray.Show(); canMinimize = tray.IsVisible; }
            catch (Win32Exception error)
            {
                Log.Warning("Tray registration failed before closing: {Message}", error.Message);
                canMinimize = false;
            }
            string emergency = model.Settings.EmergencyShortcutEnabled && model.State.EmergencyShortcutStatus == EmergencyShortcut.RegisteredStatus
                ? "In an emergency, press Ctrl + Alt + Shift + W to immediately close Water Ripple."
                : "The emergency shortcut is disabled or unavailable. Check its status in the main window.";
            var result = await closeDialog.ChooseAsync(window, "Water Ripple", canMinimize, emergency);
            if (model.State.IsExiting) return;
            if (result == WidgetCloseChoice.Minimize) MinimizeToTray();
            else if (result == WidgetCloseChoice.Quit) ExitApp();
        }
        catch (Exception ex) { Fail("Close choice could not be shown", ex); }
        finally
        {
            model.State.IsClosePromptOpen = false;
            if (!trayWasVisible && !model.State.IsInTray && !model.State.IsClosing && !model.State.IsExiting) tray.Hide();
        }
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
        // A sign-in window starts genuinely minimized. Restore it only in response
        // to this explicit user action, then activate its WinUI content.
        if (Native.IsIconic(hwnd) && window.AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.Restore(false);
        window.Activate();
        Log.Information("Main window restored from tray");
    }
    internal void RequestMemoryShutdown() => ExitApp();

    private async void ExitApp()
    {
        if (closeDialog.RequestQuitIfOpen() || closeFailureOpen || closeSequence.IsPending) return;
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
            for (int i = 0; i < 80 && !model.State.IsCaptureValid; i++) await Task.Delay(50);
            if (!model.State.IsInTray || !tray.IsVisible || Native.IsWindowVisible(hwnd) || !Native.IsIconic(hwnd))
                throw new InvalidOperationException("Startup did not keep Studio minimized and hidden in the notification area.");
            if (!model.State.IsScreenEffectActive || !model.State.IsCaptureValid)
                throw new InvalidOperationException("Startup did not present the screen effect: " + model.State.StatusMessage);
            Console.WriteLine("PASS startup keeps Studio hidden while the screen effect runs");
            ShowFromTray();
            for (int i = 0; i < 80 && !surface.IsLoaded; i++) await Task.Delay(50);
            if (model.State.IsInTray || !Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd) || !surface.IsLoaded)
                throw new InvalidOperationException("Studio could not be restored from its first hidden startup.");
            var loadedWorkProcessed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!window.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => loadedWorkProcessed.TrySetResult(true)))
                throw new InvalidOperationException("The dispatcher could not finish the startup restore check.");
            await loadedWorkProcessed.Task;
            if (generation != 1)
                throw new InvalidOperationException($"Startup restore initiated another capture attempt (generation {generation}).");
            Console.WriteLine("PASS Studio restores with its XAML content loaded without another capture attempt");
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
                if (!model.State.IsScreenEffectActive || !model.State.IsCaptureValid)
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
            case VirtualKey.F7: _ = ToggleScreenAsync(); return true;
            case VirtualKey.F8: OpenSettings(); return true;
            case VirtualKey.Escape: if (model.State.EffectEnabled) StopScreen(); else window.Close(); return true;
            default: return false;
        }
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
            _ => "\nDesktop capture is unavailable."
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
        recovery.Dispose();
        diagnostics.Dispose();
        StopScreen(); input.Dispose(); model.Pipelines.Dispose();

    }
}
internal static partial class Native
{
    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)] internal static extern int MessageBox(IntPtr hwnd, string message, string title, uint flags);
}
