using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.AppLifecycle;
using Serilog;
using Tools.Widgets;
using VirtualKey = Windows.System.VirtualKey;

namespace WaterRipple;

public partial class App : Application
{
    private MainWindow? window;
    private SettingsStore? store;
    private EventWaitHandle? relaunchSignal;
    private bool startAtSignIn;

    [STAThread]
    private static void Main()
    {
        string[] arguments = Environment.GetCommandLineArgs();
        if (Array.IndexOf(arguments, WindowsSignIn.RemoveArgument) >= 0)
        {
            StartupService.SignIn.Remove();
            return;
        }
#if DEBUG
        if (Array.IndexOf(arguments, "--check-resource-guard") >= 0) { Environment.ExitCode = ResourceChecks.Run(); return; }
        if (Array.IndexOf(arguments, "--check-settings") >= 0) { Environment.ExitCode = SettingsChecks.Run(); return; }
        if (Array.IndexOf(arguments, "--check-logging") >= 0) { Environment.ExitCode = LoggingChecks.Run(); return; }
        if (Array.IndexOf(arguments, "--check-capabilities") >= 0)
        {
            int hr = Native.RippleCheckCapabilities(0, 0, 1, 1, out var caps);
            Console.WriteLine($"HRESULT=0x{hr:X8}; GPU={caps.AdapterName}; DedicatedVRAM={caps.DedicatedVideoMemory}; FeatureLevel=0x{caps.FeatureLevel:X}; Supported={caps.Supported}; Reason={caps.Reason}");
            Environment.ExitCode = hr < 0 ? 1 : 0; return;
        }
#endif
        WinRT.ComWrappersSupport.InitializeComWrappers();
        bool startAtSignIn = Array.IndexOf(arguments, WindowsSignIn.StartArgument) >= 0
            || AppInstance.GetCurrent().GetActivatedEventArgs().Kind == ExtendedActivationKind.StartupTask;
#if DEBUG
        startAtSignIn |= Array.IndexOf(arguments, "--check-hidden-startup") >= 0;
#endif

        // Share one instance across the installed package, its alias and local builds.
        using var identity = WindowsIdentity.GetCurrent();
        string instanceName = $@"Local\VibeWare.WaterRipple.{identity.User?.Value}";
        using var activation = new EventWaitHandle(false, EventResetMode.AutoReset, instanceName + ".Activate");
        using var instance = new Mutex(false, instanceName + ".Instance");
        bool isPrimary;
        try { isPrimary = instance.WaitOne(0); }
        catch (AbandonedMutexException) { isPrimary = true; }
        if (!isPrimary)
        {
            if (!startAtSignIn) activation.Set();
            return;
        }

        try { RunApplication(activation, startAtSignIn); }
        finally { instance.ReleaseMutex(); }
    }

    private static void RunApplication(EventWaitHandle activation, bool startAtSignIn)
    {
        LoggingService.Initialize();
        DispatcherQueue? dispatcher = null;
        using var memoryWatchdog = new VibeWare.MemoryProtection.ProcessMemoryWatchdog(
            "Water Ripple Studio", Tools.Widgets.VibeWareDiagnostics.GetLogDirectory(LoggingService.AppId),
            1_000_000_000, () => Volatile.Read(ref dispatcher)?.TryEnqueue(() =>
            {
                if (Current is App app && app.window is { } mainWindow) mainWindow.Controller.RequestMemoryShutdown();
                else Current.Exit();
            }), showWindowsNotice: true);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            Log.Fatal(ex, "Unhandled process exception HRESULT={HResult:X8} Terminating={Terminating}", ex?.HResult, e.IsTerminating);
            LoggingService.Shutdown();
        };
        try
        {
            Log.Information("Starting application PID={ProcessId} Version={Version} Windows={WindowsVersion} Architecture={Architecture} StartupTask={StartupTask}",
                Environment.ProcessId, typeof(App).Assembly.GetName().Version, Environment.OSVersion.Version,
                RuntimeInformation.ProcessArchitecture, startAtSignIn);
            Start(_ =>
            {
                var queue = DispatcherQueue.GetForCurrentThread();
                Log.Information("WinUI dispatcher started");
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                new App { relaunchSignal = activation, startAtSignIn = startAtSignIn };
                Volatile.Write(ref dispatcher, queue);
            });
        }
        catch (Exception ex) { Log.Fatal(ex, "Application startup failed HRESULT={HResult:X8}", ex.HResult); throw; }
        finally { Log.Information("Application stopped"); LoggingService.Shutdown(); }
    }

    public App()
    {
        UnhandledException += (_, e) =>
        {
            Log.Fatal(e.Exception, "Unhandled WinUI exception HRESULT={HResult:X8}", e.Exception.HResult);
            LoggingService.Shutdown();
        };
        Log.Information("Initializing WinUI resources");
        InitializeComponent();
        Log.Information("WinUI resources initialized");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (window is not null)
        {
            window.Controller.ActivateExistingInstance();
            return;
        }
        try { CreateMainWindow(); }
        catch (Exception ex)
        {
#if DEBUG
            Console.Error.WriteLine(ex);
#endif
            Log.Fatal(ex, "Main window construction or activation failed HRESULT={HResult:X8}", ex.HResult);
            LoggingService.Shutdown();
            throw;
        }
    }

    private void CreateMainWindow()
    {
        Log.Information("Loading Studio settings");
        StudioModel? model = null;
        string pendingError = "";
        Microsoft.UI.Dispatching.DispatcherQueue? persistenceDispatcher = null;
        void UpdatePersistenceError(string message)
        {
            if (persistenceDispatcher is null) pendingError = message;
            else persistenceDispatcher.TryEnqueue(() =>
            {
                if (model is not null) model.State.PersistenceError = message;
            });
        }
        store = new SettingsStore(onError: ex =>
        {
            Log.Error(ex, "Settings persistence failed");
            UpdatePersistenceError("Settings could not be saved: " + ex.Message);
        }, onSaved: () => UpdatePersistenceError(""));
        model = new StudioModel(store.Load());
        model.State.PersistenceError = pendingError;
        store.Attach(model.Settings);
        Log.Information("Constructing Studio window");
        var mainWindow = new MainWindow(model, startAtSignIn, store.FlushAsync, () => store.DiscardAsync().AsTask());
        window = mainWindow;
        var dispatcher = mainWindow.DispatcherQueue;
        persistenceDispatcher = dispatcher;
        RegisteredWaitHandle? activationListener = null;
        if (relaunchSignal is not null)
        {
            activationListener = ThreadPool.RegisterWaitForSingleObject(relaunchSignal, (_, _) =>
            {
                dispatcher.TryEnqueue(() =>
                {
                    if (ReferenceEquals(window, mainWindow)) mainWindow.Controller.ActivateExistingInstance();
                });
            }, null, Timeout.Infinite, executeOnlyOnce: false);
        }
        mainWindow.Closed += (_, _) =>
        {
            window = null;
            activationListener?.Unregister(null);
            // The close sequence awaited FlushAsync before allowing native Window.Close.
            store.Dispose();
            Exit();
        };
        Log.Information("Studio window constructed StartupTask={StartupTask}", startAtSignIn);
        if (startAtSignIn)
        {
            mainWindow.Controller.StartAtSignIn();
            mainWindow.FinishHiddenStartup();
#if DEBUG
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--check-hidden-startup") >= 0)
                mainWindow.Controller.CheckHiddenStartup();
#endif
        }
        else mainWindow.Activate();
        Log.Information("Application started Version={Version} StartupTask={StartupTask}", typeof(App).Assembly.GetName().Version, startAtSignIn);
    }
}

internal sealed class MainWindow : Window
{
    internal StudioController Controller { get; }
    private readonly IntPtr startupHandle;
    private readonly bool startupWasLayered;
    private bool startupTransparent;

    internal MainWindow(StudioModel model, bool startHidden, Func<System.Threading.Tasks.Task<bool>> flushSettings,
        Func<System.Threading.Tasks.Task> discardSettings)
    {
        if (startHidden)
        {
            // The native window may be exposed before XAML has finished building.
            // Keep even its non-client area transparent until it is in the tray.
            startupHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            long style = Native.GetWindowLongPtr(startupHandle, -20).ToInt64();
            startupWasLayered = (style & 0x00080000L) != 0;
            Marshal.SetLastPInvokeError(0);
            IntPtr previous = Native.SetWindowLongPtr(startupHandle, -20, new IntPtr(style | 0x00080000L));
            if (previous == IntPtr.Zero && Marshal.GetLastPInvokeError() != 0)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            if (!Native.SetLayeredWindowAttributes(startupHandle, 0, 0, 2))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            startupTransparent = true;
        }
        Title = "VibeWare – Water Ripple";
        var root = new Grid { DataContext = model };
        EffectStudioLayout.Initialize(this, root, "Water Ripple", "A ripple for every click.");
        (int X, int Y, int Width, int Height)? startupBounds = null;
        if (startHidden)
        {
            // Capture the restore rectangle before minimizing: minimized HWND coordinates
            // are not a valid monitor-selection hint for the desktop effect.
            startupBounds = (AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
            if (AppWindow.Presenter is not OverlappedPresenter presenter)
                throw new InvalidOperationException("The startup window requires an overlapped presenter.");
            // Establish the native startup show state while the transparency guard is
            // still in place, before constructing services or starting capture.
            AppWindow.Show(false);
            presenter.Minimize(false);
            Log.Information("Startup main window initially minimized Visible={Visible} Minimized={Minimized} Foreground={Foreground}",
                Native.IsWindowVisible(startupHandle), Native.IsIconic(startupHandle), Native.GetForegroundWindow() == startupHandle);
#if DEBUG
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--check-hidden-startup") >= 0)
            {
                if (!Native.IsIconic(startupHandle) || Native.GetForegroundWindow() == startupHandle)
                    throw new InvalidOperationException("Sign-in startup did not minimize without taking focus before service initialization.");
                Console.WriteLine("PASS sign-in Studio is minimized without focus before services and capture initialize");
            }
#endif
        }
        Ui.AttachAppearance(this, root, model.Settings);
        Controller = new StudioController(model, this, root, flushSettings, discardSettings, startupBounds);
        var effectSwitch = new ToggleSwitch();
        var effectStatus = Ui.Text("Checking the display GPU…", 13);
        var emergencyLabel = Ui.Text("Emergency shortcut: Ctrl + Alt + Shift + W", 12);
        var emergencyStatus = Ui.Text("", 12);
        emergencyStatus.Style = Ui.SecondaryTextStyle;
        var emergencyHeading = new Grid { ColumnSpacing = 8 };
        emergencyHeading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        emergencyHeading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        emergencyHeading.Children.Add(new FontIcon { Glyph = "\uE765", FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(emergencyLabel, 1);
        emergencyHeading.Children.Add(emergencyLabel);
        var emergencyText = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        emergencyText.Children.Add(emergencyHeading);
        emergencyText.Children.Add(emergencyStatus);
        var emergencyToggle = new ToggleSwitch
        {
            MinWidth = 0, Width = 40, OffContent = null, OnContent = null,
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(emergencyToggle, "Emergency exit shortcut");
        Ui.Bind(emergencyToggle, ToggleSwitch.IsOnProperty, model.Settings,
            nameof(AppSettings.EmergencyShortcutEnabled), BindingMode.TwoWay);
        var emergency = EffectStudioLayout.Columns(emergencyText, emergencyToggle);
        var statusBar = EffectStudioLayout.StatusBar(effectStatus, effectSwitch);
        Grid.SetRow(statusBar, 0);
        root.Children.Add(statusBar);

        bool syncingEffect = false, changingEffect = false;
        void RefreshEffectSwitch()
        {
            syncingEffect = true;
            effectSwitch.IsOn = model.State.IsScreenEffectActive;
            effectSwitch.IsEnabled = !changingEffect && (model.State.IsScreenEffectActive || model.State.CanApply);
            syncingEffect = false;
        }
        void RefreshEmergencyStatus()
        {
            string status = model.State.EmergencyShortcutStatus;
            emergencyStatus.Text = status;
            emergencyStatus.Visibility = status == EmergencyShortcut.RegisteredStatus ? Visibility.Collapsed : Visibility.Visible;
            AutomationProperties.SetHelpText(emergencyToggle, status);
            ToolTipService.SetToolTip(emergencyToggle, status);
        }
        void EffectStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(AppState.IsScreenEffectActive) or nameof(AppState.CanApply))
                RefreshEffectSwitch();
            if (e.PropertyName == nameof(AppState.StatusMessage)) effectStatus.Text = model.State.StatusMessage;
            if (e.PropertyName == nameof(AppState.EmergencyShortcutStatus)) RefreshEmergencyStatus();
        }
        effectSwitch.Toggled += async (_, _) =>
        {
            if (syncingEffect || changingEffect || effectSwitch.IsOn == model.State.IsScreenEffectActive) return;
            changingEffect = true;
            effectSwitch.IsEnabled = false;
            try { await Controller.ToggleScreenAsync(); }
            finally
            {
                changingEffect = false;
                RefreshEffectSwitch();
            }
        };
        model.State.PropertyChanged += EffectStateChanged;
        RefreshEffectSwitch();
        RefreshEmergencyStatus();
        var controls = new RippleView(model, Controller, this, root, effectSwitch);
        Grid.SetRow(controls, 1);
        root.Children.Add(controls);
        var footer = EffectStudioLayout.FooterBar(emergency,
            WidgetFooter.CreateCompact("Water Ripple", () => new AboutWindow(model.Settings).Activate()));
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        root.Loaded += (_, _) => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, Controller.SurfaceLoaded);
        root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) => { e.Handled = Controller.HandleKey(e.Key); }), true);
        Closed += (_, _) =>
        {
            model.State.PropertyChanged -= EffectStateChanged;
            Controller.Dispose();
        };
    }

    internal void FinishHiddenStartup()
    {
        if (!startupTransparent) return;
        // Successful tray startup stays hidden. If the shell rejects the tray icon,
        // leave a minimized taskbar entry instead of opening the full control window.
        bool inTray = Controller.IsInTray;
        if (inTray) AppWindow.Hide();
        else
        {
            AppWindow.Show(false);
            if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Minimize(false);
        }
        if (!Native.SetLayeredWindowAttributes(startupHandle, 0, 255, 2))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        if (!startupWasLayered)
        {
            long style = Native.GetWindowLongPtr(startupHandle, -20).ToInt64();
            Marshal.SetLastPInvokeError(0);
            IntPtr previous = Native.SetWindowLongPtr(startupHandle, -20, new IntPtr(style & ~0x00080000L));
            if (previous == IntPtr.Zero && Marshal.GetLastPInvokeError() != 0)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }
        startupTransparent = false;
        Log.Information("Startup main window presentation ready InTray={InTray} Visible={Visible} Minimized={Minimized}",
            inTray, Native.IsWindowVisible(startupHandle), Native.IsIconic(startupHandle));
    }
}
