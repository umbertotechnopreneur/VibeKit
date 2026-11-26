// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using Tools.Widgets;
using Serilog;

namespace VibeWare.Vignette;

public partial class App : Application
{
    internal static readonly WindowsSignIn SignIn = new("Vignette");
    private readonly EventWaitHandle activation;
    private readonly bool startAtSignIn;
    private Studio? studio;
    private RegisteredWaitHandle? activationListener;

    [STAThread]
    private static void Main()
    {
        string[] args = Environment.GetCommandLineArgs();
        if (args.Contains(WindowsSignIn.RemoveArgument))
        {
            SignIn.Remove();
            return;
        }

        bool startup = args.Contains(WindowsSignIn.StartArgument);
        using var activation = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\VibeWare.Vignette.Activate");
        using var singleton = new Mutex(false, @"Local\VibeWare.Vignette");
        bool primary;
        try { primary = singleton.WaitOne(0); }
        catch (AbandonedMutexException) { primary = true; }
        if (!primary)
        {
            if (!startup) activation.Set();
            return;
        }

        using var logger = VibeWareDiagnostics.CreateLogger("Vignette");
        Log.Logger = logger;
        Log.Information("Vignette starting Version={Version}", typeof(App).Assembly.GetName().Version);
        try
        {
            SetCurrentProcessExplicitAppUserModelID("biz.umbertogiacobbi.Vignette");
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Start(_ =>
            {
                var queue = DispatcherQueue.GetForCurrentThread();
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(queue));
                new App(activation, startup);
            });
        }
        finally { singleton.ReleaseMutex(); }
    }

    private App(EventWaitHandle activation, bool startAtSignIn)
    {
        this.activation = activation;
        this.startAtSignIn = startAtSignIn;
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (studio is not null)
        {
            studio.Reveal();
            return;
        }

        var store = new SettingsStore();
        Preferences preferences;
        try { preferences = store.Load(); }
        catch (InvalidDataException error)
        {
            Log.Error(error, "Could not load Vignette settings");
            MessageBoxW(0, error.Message, "Vignette could not start", 0x10);
            Environment.ExitCode = 1;
            Exit();
            return;
        }
        var window = new Studio(store, preferences);
        studio = window;
        var dispatcher = window.DispatcherQueue;
        activationListener = ThreadPool.RegisterWaitForSingleObject(activation, (_, _) =>
            dispatcher.TryEnqueue(window.Reveal), null, Timeout.Infinite, executeOnlyOnce: false);
        window.Closed += (_, _) =>
        {
            activationListener?.Unregister(null);
            activationListener = null;
            studio = null;
            Exit();
        };
        if (!startAtSignIn) window.Reveal();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(nint owner, string text, string caption, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string id);
}

internal sealed partial class Studio : Window
{
    private const string Site = "https://umbertogiacobbi.biz";
    private const int Hotkey = 0x5649;
    private const uint WindowShown = 0x8002, WindowReordered = 0x8004, SkipOwnProcess = 2;
    private const uint WindowSubclassId = 0x56474E;
    private readonly WidgetCloseDialog closeDialog = new();
    private readonly SettingsStore store;
    private readonly IReadOnlyList<Pipeline> builtins = SettingsStore.Builtins();
    private readonly DesktopOverlay overlay = new();
    private readonly DispatcherQueueTimer renderTimer, peerOrderTimer, saveTimer, recoveryTimer;
    private readonly DesktopEffectRecovery recovery;
    private readonly WidgetTrayIcon tray;
    private readonly WinEventCallback winEvent;
    private readonly SubclassCallback subclass;
    private readonly nint handle;
    private Preferences preferences;
    private IReadOnlyList<DisplayTarget> displays = [];
    private nint orderHook;
    private bool syncing, closing, hotkeyRegistered, dirty, quitPending;
    private string? editId;

    internal Studio(SettingsStore store, Preferences preferences)
    {
        this.store = store;
        this.preferences = preferences;
        recovery = new DesktopEffectRecovery("Vignette", () => SetEffect(true, automatic: true));
        Title = "VibeWare – Vignette";
        handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Vignette.ico");
        AppWindow.SetIcon(iconPath);

        renderTimer = DispatcherQueue.CreateTimer();
        renderTimer.Interval = TimeSpan.FromMilliseconds(33);
        renderTimer.IsRepeating = false;
        renderTimer.Tick += (_, _) => Render();
        recoveryTimer = DispatcherQueue.CreateTimer();
        recoveryTimer.Interval = TimeSpan.FromSeconds(1);
        recoveryTimer.Tick += (_, _) =>
        {
            recovery.Tick(!closing && !quitPending, effect.IsOn);
            if (recovery.Pending && !recovery.Available)
            {
                recovery.Cancel();
                status.Text = "Automatic recovery is unavailable; enable Vignette manually.";
            }
        };
        recoveryTimer.Start();
        saveTimer = DispatcherQueue.CreateTimer();
        saveTimer.Interval = TimeSpan.FromMilliseconds(300);
        saveTimer.IsRepeating = false;
        saveTimer.Tick += (_, _) => Flush();
        winEvent = OnPeerWindowChanged;
        peerOrderTimer = DispatcherQueue.CreateTimer();
        peerOrderTimer.Interval = TimeSpan.FromMilliseconds(16);
        peerOrderTimer.IsRepeating = false;
        peerOrderTimer.Tick += (_, _) =>
        {
            try { overlay.EnsureAbovePeers(); }
            catch (Win32Exception error) { SetEffect(false, "Vignette stopped. " + error.Message); }
        };

        BuildUi();
        var menu = new WidgetTrayMenu(this, Reveal, () => { ShowSettings(); Reveal(); }, ShowAbout, () => _ = QuitAsync(),
            () => (effect.IsOn, hotkeyRegistered), enabled => SetEffect(enabled),
            items => items.Add("Stop", () => SetEffect(false)));
        tray = new WidgetTrayIcon(this, "Vignette", iconPath, menu, Reveal);
        tray.Show();

        subclass = OnWindowMessage;
        if (SetWindowSubclass(handle, subclass, WindowSubclassId, 0))
            hotkeyRegistered = RegisterHotKey(handle, Hotkey, 0x4000 | 8 | 4, 0x79);
        UpdateEmergencyShortcut();
        status.Text = hotkeyRegistered ? "Vignette effect is not running." : "The emergency shortcut could not be registered.";
        effect.Toggled += (_, _) => { if (!syncing && !closing) SetEffect(effect.IsOn); };
        AppWindow.Closing += OnClosing;
        Closed += (_, _) => EndSession();
        SystemEvents.DisplaySettingsChanged += DisplaysChanged;
        SystemEvents.SessionSwitch += SessionChanged;
        SystemEvents.PowerModeChanged += PowerChanged;

        RefreshDisplays();
        RefreshPipelines();
        SyncAppearance();
    }

    internal void Reveal()
    {
        if (closing) return;
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
    }

    private void ApplyTheme()
    {
        root.RequestedTheme = preferences.Theme switch
        {
            ThemeMode.Light => ElementTheme.Light,
            ThemeMode.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        if (preferences.UseAcrylic == (SystemBackdrop is not null)) return;
        root.ClearValue(Panel.BackgroundProperty);
        try
        {
            SystemBackdrop = preferences.UseAcrylic ? new DesktopAcrylicBackdrop() : null;
            if (SystemBackdrop is not null) root.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
        catch (COMException)
        {
            SystemBackdrop = null;
        }
    }

    private void CommitColor()
    {
        var updated = preferences.Appearance with { Color = ReadEdgeColor() };
        if (updated.Valid) edgeColor.Text = updated.Color;
        else { edgeColor.Text = preferences.Appearance.Color; ShowMessage("Enter a color in #RRGGBB format."); }
    }

    private string ReadEdgeColor() => edgeColor.Text.Trim().ToUpperInvariant();

    private void Change(Appearance value, bool sync = true)
    {
        if (!value.Valid) return;
        bool colorChanged = value.Color != preferences.Appearance.Color;
        preferences = preferences with { Appearance = value };
        if (sync) SyncAppearance();
        else
        {
            if (colorChanged) UpdateSwatch(value.Color);
            UpdatePipelineSelection();
        }
        ScheduleSave();
        // Apply the latest values during a drag without uploading a surface for every input event.
        if (effect.IsOn && !renderTimer.IsRunning) renderTimer.Start();
    }

    private void SyncAppearance()
    {
        syncing = true;
        try
        {
            var value = preferences.Appearance;
            var values = new[] { value.Intensity, value.Radius, value.Softness, value.Aspect };
            edgeColor.Text = value.Color;
            UpdateSwatch(value.Color);
            for (int i=0; i<sliders.Length; i++) sliders[i].Value = values[i];
        }
        finally { syncing = false; }
        UpdatePipelineSelection();
    }

    private void RefreshPipelines()
    {
        syncing = true;
        try { pipeline.ItemsSource = builtins.Concat(preferences.Personal).ToArray(); }
        finally { syncing = false; }
        UpdatePipelineSelection();
    }

    private void UpdateSwatch(string color) => swatch.Background = new SolidColorBrush(ReadColor(color));

    private void UpdatePipelineSelection()
    {
        var item = builtins.Concat(preferences.Personal)
            .FirstOrDefault(p => p.Id == editId && p.Appearance == preferences.Appearance)
            ?? builtins.Concat(preferences.Personal).FirstOrDefault(p => p.Appearance == preferences.Appearance);
        syncing = true;
        try { pipeline.SelectedItem = item; }
        finally { syncing = false; }
        description.Text = item?.Description ?? (item?.Name ?? "Custom settings");
        RefreshPresetRows();
        updatePipeline.IsEnabled = removePipeline.IsEnabled = preferences.Personal.Any(p => p.Id == editId);
    }

    private bool EditPipeline(string? id)
    {
        try
        {
            if (id is null && preferences.Personal.Length >= 60) throw new InvalidOperationException("The personal library can hold 60 pipelines.");
            var existing = preferences.Personal.FirstOrDefault(item => item.Id == id);
            var value = Pipeline.Personal(id, pipelineName.Text.Length == 0 ? existing?.Name ?? "" : pipelineName.Text, preferences.Appearance);
            preferences = preferences with { Personal = [..preferences.Personal.Where(item => item.Id != id), value] };
            editId = value.Id;
            RefreshPipelines();
            ScheduleSave();
            return true;
        }
        catch (InvalidOperationException error) { ShowMessage(error.Message); return false; }
    }

    private void DuplicatePreset(string id)
    {
        var source = builtins.Concat(preferences.Personal).FirstOrDefault(p => p.Id == id);
        if (source is null) return;
        try
        {
            if (preferences.Personal.Length >= 60) throw new InvalidOperationException("The personal library can hold 60 presets.");
            var value = Pipeline.Personal(null, source.Name[..Math.Min(source.Name.Length, 75)] + " copy", source.Appearance);
            preferences = preferences with { Personal = [..preferences.Personal, value] };
            editId = value.Id;
            Change(value.Appearance);
            RefreshPipelines();
        }
        catch (InvalidOperationException error) { ShowMessage(error.Message); }
    }

    private async Task RemovePipelineAsync(string id)
    {
        var item = preferences.Personal.FirstOrDefault(p => p.Id == id);
        if (item is null) return;
        var dialog = new ContentDialog
        {
            Title = "Remove personal preset",
            Content = $"Remove “{item.Name}”?",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await ShowStudioDialogAsync(dialog) != ContentDialogResult.Primary) return;
        preferences = preferences with { Personal = preferences.Personal.Where(p => p.Id != item.Id).ToArray() };
        if (editId == id) editId = null;
        pipelineName.Text = "";
        RefreshPipelines();
        ScheduleSave();
    }

    private void DisplaysChanged(object? sender, EventArgs args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (closing) return;
        var current = DisplayTarget.GetConnected();
        if (displays.Count == current.Count && displays.Zip(current).All(pair =>
            pair.First.Handle == pair.Second.Handle && pair.First.Bounds == pair.Second.Bounds &&
            pair.First.Label == pair.Second.Label && pair.First.Details == pair.Second.Details &&
            pair.First.Identifiers.SequenceEqual(pair.Second.Identifiers, StringComparer.OrdinalIgnoreCase))) return;
        if (effect.IsOn) PauseForRecovery("Display layout changed.");
        displays = current;
        RefreshMonitorRows();
    });

    private void SessionChanged(object sender, SessionSwitchEventArgs args)
    {
        if (args.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
            DispatcherQueue.TryEnqueue(() => { if (!closing && effect.IsOn) PauseForRecovery("Desktop session became unavailable."); });
    }

    private void PowerChanged(object sender, PowerModeChangedEventArgs args)
    {
        if (args.Mode == PowerModes.Suspend)
            DispatcherQueue.TryEnqueue(() => { if (!closing && effect.IsOn) PauseForRecovery("Windows is going to sleep."); });
    }

    private void PauseForRecovery(string reason)
    {
        SetEffect(false, reason);
        if (recovery.Pause(preferences.AutoRecover, 60))
            status.Text = reason + " Waiting for coordinated recovery.";
        else if (preferences.AutoRecover && !recovery.Available)
            status.Text = reason + " Automatic recovery is unavailable; enable Vignette manually.";
    }

    private bool Selected(DisplayTarget target) => preferences.Screens is null || preferences.Screens.Any(target.Matches);

    private void RefreshDisplays()
    {
        displays = DisplayTarget.GetConnected();
        RefreshMonitorRows();
    }

    private void SetEffect(bool enabled, string? stopReason = null, bool automatic = false)
    {
        Log.Information("Effect enabled={Enabled} automatic={Automatic} reason={Reason}", enabled, automatic, stopReason);
        if (!automatic)
        {
            if (enabled) recovery.Reset();
            else recovery.Cancel();
        }
        if (enabled && !hotkeyRegistered) { SetEffect(false, "The emergency shortcut is unavailable."); return; }
        syncing = true;
        try { effect.IsOn = enabled; }
        finally { syncing = false; }
        if (enabled)
        {
            Render();
        }
        else
        {
            renderTimer.Stop();
            TrackPeerOrder(false);
            overlay.Dispose();
            status.Text = stopReason ?? "Vignette effect is not running.";
        }
    }

    private void Render()
    {
        try
        {
            if (!effect.IsOn) return;
            int count = overlay.Apply(displays.Where(Selected), preferences.Appearance);
            TrackPeerOrder(overlay.HasSurfaces);
            status.Text = count == 0 ? "Select a display for the effect."
                : !overlay.HasSurfaces ? "The effect is transparent at zero intensity."
                : $"Vignette effect is active on {count} {(count == 1 ? "display" : "displays")}.";
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            SetEffect(false, "Vignette stopped. " + error.Message);
        }
    }

    private void TrackPeerOrder(bool active)
    {
        if (active)
        {
            if (orderHook == 0)
                orderHook = SetWinEventHook(WindowShown, WindowReordered, 0, winEvent, 0, 0, SkipOwnProcess);
        }
        else
        {
            peerOrderTimer.Stop();
            if (orderHook != 0) { UnhookWinEvent(orderHook); orderHook = 0; }
        }
    }

    private void OnPeerWindowChanged(nint hook, uint type, nint window, int objectId, int childId, uint thread, uint tick)
    {
        if (hook != orderHook || window == 0 || objectId != 0 || childId != 0 || peerOrderTimer.IsRunning ||
            type is not (WindowShown or WindowReordered)) return;
        // A reorder may name the parent of the moved windows instead of a peer.
        if ((type == WindowReordered && window == GetDesktopWindow()) || overlay.IsPeerOnSelectedDisplay(window))
            peerOrderTimer.Start();
    }

    private nint OnWindowMessage(nint window, uint code, nint wp, nint lp, nuint id, nuint data)
    {
        const uint hotkeyMessage = 0x0312, endSession = 0x0016, size = 0x0005;
        if ((code == hotkeyMessage && wp == Hotkey) || DesktopEffectCommands.IsDisableAll(code))
        {
            DispatcherQueue.TryEnqueue(() => { if (!closing) SetEffect(false); });
            return 0;
        }
        if (DesktopEffectCommands.IsEnableAll(code))
        {
            DispatcherQueue.TryEnqueue(() => { if (!closing && !quitPending) SetEffect(true); });
            return 0;
        }
        if (code == endSession && wp != 0) EndSession();
        if (code == size && wp == 1)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!closing && AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
                    AppWindow.Hide();
            });
        }
        return DefSubclassProc(window, code, wp, lp);
    }

    private void ScheduleSave()
    {
        dirty = true;
        saveTimer.Stop();
        saveTimer.Start();
    }

    private void Flush()
    {
        if (!dirty) return;
        try { store.Save(preferences); dirty = false; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (!closing) ShowMessage("Could not save settings. " + error.Message);
        }
    }

    private void EndSession()
    {
        if (closing) return;
        closing = true;
        saveTimer.Stop();
        Flush();
        Release();
    }

    /// <summary>Keep quit and persistence in the host while the family dialog handles the close choice.</summary>
    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (closing) return;
        args.Cancel = true;
        if (closeDialog.FocusIfOpen(this) || dialogOpen || quitPending) return;
        dialogOpen = true;
        WidgetCloseChoice choice;
        try { choice = await closeDialog.ChooseAsync(this, "Vignette", tray.IsVisible); }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            ShowMessage("Could not display close options: " + ex.Message);
            return;
        }
        finally { dialogOpen = false; }

        if (closing || quitPending) return;
        if (choice == WidgetCloseChoice.Quit) await QuitAsync();
        else if (choice == WidgetCloseChoice.Minimize)
        {
            if (tray.IsVisible) sender.Hide();
            else ShowMessage("The notification area is unavailable. The window is still open.");
        }
    }

    private async Task QuitAsync()
    {
        if (closing || quitPending || closeDialog.RequestQuitIfOpen()) return;
        saveTimer.Stop();
        Flush();
        if (dirty)
        {
            quitPending = true;
            try
            {
                Reveal();
                var dialog = new ContentDialog
                {
                    Title = "Settings could not be saved",
                    Content = "Quit without saving your changes?",
                    PrimaryButtonText = "Quit",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close
                };
                if (await ShowStudioDialogAsync(dialog) != ContentDialogResult.Primary) return;
            }
            finally { quitPending = false; }
        }
        closing = true;
        Release();
        Close();
    }

    private void Release()
    {
        renderTimer.Stop();
        recoveryTimer.Stop();
        recovery.Dispose();
        TrackPeerOrder(false);
        overlay.Dispose();
        if (hotkeyRegistered) UnregisterHotKey(handle, Hotkey);
        RemoveWindowSubclass(handle, subclass, WindowSubclassId);
        SystemEvents.DisplaySettingsChanged -= DisplaysChanged;
        SystemEvents.SessionSwitch -= SessionChanged;
        SystemEvents.PowerModeChanged -= PowerChanged;
        tray.Dispose();
    }

    private static void OpenLink(string url)
    {
        using var browser = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private delegate nint SubclassCallback(nint window, uint message, nint wp, nint lp, nuint id, nuint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint window, SubclassCallback callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint window, SubclassCallback callback, nuint id);
    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint window, uint message, nint wp, nint lp);
    private delegate void WinEventCallback(nint hook, uint evt, nint window, int obj, int child, uint thread, uint time);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint min, uint max, nint dll, WinEventCallback callback, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();
}
