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
        Title = "Water Ripple · VibeWare";
        AppWindow.Resize(new SizeInt32(820, 920));
        var root = new Grid { Padding = new Thickness(24, 20, 24, 12) };
        root.DataContext = model;
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;
        Ui.AttachAppearance(this, root, model.Settings);
        var header = Ui.BrandHeader("Water Ripple", "A ripple for every click. Changes apply immediately.");
        root.Children.Add(header);
        Controller = new StudioController(model, this, root, flushSettings, discardSettings);
        var controls = new RippleView(model, Controller, this);
        var effectButton = new Button { Command = Controller.ToggleScreenCommand, VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(14, 8, 14, 8) };
        var indicator = new Grid { Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center };
        var glow = new Border { Width = 14, Height = 14, Opacity = 0, IsHitTestVisible = false };
        glow.Loaded += (_, _) =>
        {
            var compositor = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(glow).Compositor;
            var shadow = compositor.CreateDropShadow();
            shadow.Color = Windows.UI.Color.FromArgb(255, 255, 42, 42);
            shadow.BlurRadius = 9;
            shadow.Opacity = 0.8f;
            var visual = compositor.CreateSpriteVisual();
            visual.Size = new System.Numerics.Vector2(8, 8);
            visual.Offset = new System.Numerics.Vector3(3, 3, 0);
            visual.Brush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(255, 205, 40, 40));
            visual.Shadow = shadow;
            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetElementChildVisual(glow, visual);
        };
        var led = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 8, Height = 8,
            StrokeThickness = 1, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        indicator.Children.Add(glow); indicator.Children.Add(led);
        var effectLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var effectContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        effectContent.Children.Add(indicator); effectContent.Children.Add(effectLabel); effectButton.Content = effectContent;
        var pulse = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        var animation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = 0.3, To = 0.85, Duration = new Duration(TimeSpan.FromSeconds(1.3)),
            AutoReverse = true, RepeatBehavior = Microsoft.UI.Xaml.Media.Animation.RepeatBehavior.Forever,
            EnableDependentAnimation = true
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animation, glow);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animation, "Opacity");
        pulse.Children.Add(animation);
        var accessibility = new Windows.UI.ViewManagement.UISettings();
        void UpdateEffectButton()
        {
            bool active = model.State.IsScreenEffectActive;
            pulse.Stop();
            effectLabel.Text = active ? "Effect on" : "Effect off";
            led.Fill = active ? Ui.Brush(255, 75, 76) : Ui.Brush(65, 58, 60);
            led.Stroke = active ? Ui.Brush(255, 164, 151) : Ui.Brush(148, 132, 133);
            glow.Opacity = active ? 0.45 : 0;
            if (active && accessibility.AnimationsEnabled) pulse.Begin();
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(effectButton, model.State.ApplyButtonText);
            ToolTipService.SetToolTip(effectButton, model.State.ApplyButtonText);
        }
        model.State.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AppState.IsScreenEffectActive)) UpdateEffectButton(); };
        Ui.Bind(effectButton, Control.IsEnabledProperty, model.State, nameof(AppState.CanApply));
        UpdateEffectButton(); Grid.SetColumn(effectButton, 1); header.Children.Add(effectButton);
        Grid.SetRow(controls, 1); root.Children.Add(controls);
        var footer = Tools.Widgets.WidgetFooter.Create("Water Ripple", () => new AboutWindow(model.Settings).Activate());
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        root.Loaded += (_, _) => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, Controller.SurfaceLoaded);
        root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) => { e.Handled = Controller.HandleKey(e.Key); }), true);
        Closed += (_, _) => { pulse.Stop(); Controller.Dispose(); };
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
