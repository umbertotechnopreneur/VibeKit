using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.AppLifecycle;
using Windows.Graphics;
using Serilog;
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
#if DEBUG
        string[] arguments = Environment.GetCommandLineArgs();
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
        bool startAtSignIn = AppInstance.GetCurrent().GetActivatedEventArgs().Kind == ExtendedActivationKind.StartupTask;
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
                Log.Information("WinUI dispatcher started");
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                new App { relaunchSignal = activation, startAtSignIn = startAtSignIn };
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
        var mainWindow = new MainWindow(model, startAtSignIn, store.FlushAsync);
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

    internal MainWindow(StudioModel model, bool startHidden, Func<System.Threading.Tasks.Task<bool>> flushSettings)
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
        Title = "Water Ripple · VibeWare";
        AppWindow.Resize(new SizeInt32(820, 920));
        var root = new Grid { RequestedTheme = ElementTheme.Dark, Padding = new Thickness(24, 20, 24, 12) };
        root.DataContext = model;
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;
        Ui.AttachAppearance(this, root, model.Settings);
        root.Children.Add(Ui.BrandHeader("Water Ripple", "A ripple for every click. Changes apply immediately."));
        Controller = new StudioController(model, this, root, flushSettings);
        var controls = new RippleView(model, Controller, this);
        Grid.SetRow(controls, 1); root.Children.Add(controls);
        var footer = Ui.Footer(); Grid.SetRow((FrameworkElement)footer, 2); root.Children.Add(footer);
        root.Loaded += (_, _) => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, Controller.SurfaceLoaded);
        root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) => { e.Handled = Controller.HandleKey(e.Key); }), true);
        Closed += (_, _) => Controller.Dispose();
    }

    internal void FinishHiddenStartup()
    {
        if (!startupTransparent) return;
        // Keep the window hidden while restoring its ordinary appearance. If tray
        // setup failed, reveal it again so the app remains reachable.
        bool inTray = Controller.IsInTray;
        if (inTray) Native.ShowWindow(startupHandle, 0);
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
        if (!inTray) Activate();
    }
}
