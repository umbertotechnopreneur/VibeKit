using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
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
        store = new SettingsStore(onError: ex =>
        {
            Log.Error(ex, "Settings persistence failed");
            pendingError = "Settings could not be saved: " + ex.Message;
            if (model is not null) model.State.PersistenceError = pendingError;
        });
        model = new StudioModel(store.Load());
        model.State.PersistenceError = pendingError;
        store.Attach(model.Settings);
        Log.Information("Constructing Studio window");
        var mainWindow = new MainWindow(model);
        window = mainWindow;
        var dispatcher = mainWindow.DispatcherQueue;
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
            store.Dispose();
            Exit();
        };
        Log.Information("Studio window constructed StartupTask={StartupTask}", startAtSignIn);
        if (startAtSignIn) mainWindow.Controller.StartAtSignIn();
        else mainWindow.Activate();
        Log.Information("Application started Version={Version} StartupTask={StartupTask}", typeof(App).Assembly.GetName().Version, startAtSignIn);
    }
}

internal sealed class MainWindow : Window
{
    internal StudioController Controller { get; }

    internal MainWindow(StudioModel model)
    {
        Title = "Water Ripple · VibeWare";
        AppWindow.Resize(new SizeInt32(1320, 960));
        var root = new Grid { RequestedTheme = ElementTheme.Dark, Padding = new Thickness(26), RowSpacing = 18 };
        root.DataContext = model;
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;
        Ui.AttachAppearance(this, root, model.Settings);
        root.Children.Add(Ui.BrandHeader("Water Ripple", "A ripple for every click."));
        var body = new Grid { ColumnSpacing = 24 };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        Grid.SetRow(body, 1); root.Children.Add(body);
        var preview = new Grid { RowSpacing = 10 };
        preview.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        preview.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        preview.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        preview.Children.Add(Ui.Text("RIPPLE STUDIO", 12));
        var canvas = new SwapChainPanel();
        var previewContent = new Grid();
        previewContent.Children.Add(canvas);
        var pausedContent = new StackPanel { Spacing = 12, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 370 };
        pausedContent.Children.Add(new TextBlock { Text = "Preview paused", FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        pausedContent.Children.Add(new TextBlock
        {
            Text = "Preview is not active because the effect is applied to the entire screen.",
            TextWrapping = TextWrapping.Wrap, FontSize = 15
        });
        var paused = new Border { Child = pausedContent, Padding = new Thickness(24),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(245, 32, 32, 36)) };
        paused.SetBinding(Border.VisibilityProperty, new Binding { Source = model.State,
            Path = new PropertyPath(nameof(AppState.IsScreenEffectActive)), Converter = new Ui.BooleanVisibilityConverter() });
        previewContent.Children.Add(paused);
        var frame = new Border { Child = previewContent, CornerRadius = new CornerRadius(10) };
        Grid.SetRow(frame, 1); preview.Children.Add(frame);
        var caption = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        Ui.Bind(caption, TextBlock.TextProperty, model.State, nameof(AppState.CaptureMessage));
        Grid.SetRow(caption, 2); preview.Children.Add(caption); body.Children.Add(preview);
        Controller = new StudioController(model, this, canvas);
        canvas.Loaded += (_, _) => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, Controller.SurfaceLoaded);
        canvas.PointerPressed += (_, e) =>
        {
            var point = e.GetCurrentPoint(canvas);
            if (point.Properties.IsLeftButtonPressed) Controller.AddPreviewWave(point.Position.X, point.Position.Y);
        };
        var controls = new StackPanel { Spacing = 12 };
        var scroll = new ScrollViewer { Content = controls, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var sidebar = new Border { Padding = new Thickness(18), CornerRadius = new CornerRadius(10), Child = scroll };
        Action hideScrollBar = Ui.HoverScrollBar(scroll, sidebar, root);
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
                hideScrollBar();
        };
        Grid.SetColumn(sidebar, 1); body.Children.Add(sidebar);
        controls.Children.Add(Ui.Text("Studio controls", 20));
        var apply = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, Style = (Style)Application.Current.Resources["AccentButtonStyle"], Command = Controller.ToggleScreenCommand };
        Ui.Bind(apply, Button.ContentProperty, model.State, nameof(AppState.ApplyButtonText));
        Ui.Bind(apply, Button.IsEnabledProperty, model.State, nameof(AppState.CanApply)); controls.Children.Add(apply);
#if DEBUG
        var screenshot = new Button { Content = "Pause capture for screenshot", Command = Controller.InspectScreenCommand, HorizontalAlignment = HorizontalAlignment.Stretch };
        Ui.Bind(screenshot, Button.IsEnabledProperty, model.State, nameof(AppState.IsScreenEffectActive));
        ToolTipService.SetToolTip(screenshot, "Freezes the screen effect for 20 seconds so it can be captured without feedback.");
        controls.Children.Add(screenshot);
#endif
        controls.Children.Add(new Button { Content = "Settings", Command = Controller.OpenSettingsCommand, HorizontalAlignment = HorizontalAlignment.Stretch });
        var freeze = new Button { Command = Controller.ToggleCaptureCommand, HorizontalAlignment = HorizontalAlignment.Stretch };
        Ui.Bind(freeze, Button.ContentProperty, model.State, nameof(AppState.FreezeButtonText));
        Ui.Bind(freeze, Button.IsEnabledProperty, model.State, nameof(AppState.CanFreeze)); controls.Children.Add(freeze);
        controls.Children.Add(new Button { Content = "Drop in the center", Command = Controller.DropCommand, HorizontalAlignment = HorizontalAlignment.Stretch });
        var diagnostics = new StackPanel { Spacing = 12, Visibility = Visibility.Collapsed };
        var capability = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        Ui.Bind(capability, TextBlock.TextProperty, model.State, nameof(AppState.CapabilityText)); diagnostics.Children.Add(capability);
        diagnostics.Children.Add(Ui.DiagnosticRows(model.State, nameof(AppState.MetricsText)));
        diagnostics.Children.Add(Ui.DiagnosticRows(model.State, nameof(AppState.ResourcesText)));
        diagnostics.Children.Add(new Button { Content = "Recheck GPU / restart preview", Command = Controller.RestartCommand, HorizontalAlignment = HorizontalAlignment.Stretch });
        var diagnosticsToggle = new ToggleButton { Content = "Diagnostics", HorizontalAlignment = HorizontalAlignment.Stretch };
        diagnosticsToggle.Checked += (_, _) => diagnostics.Visibility = Visibility.Visible;
        diagnosticsToggle.Unchecked += (_, _) => diagnostics.Visibility = Visibility.Collapsed;
        controls.Children.Add(diagnosticsToggle);
        controls.Children.Add(diagnostics);
        foreach (string property in new[] { nameof(AppState.StatusMessage), nameof(AppState.PersistenceError) })
        {
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            Ui.Bind(text, TextBlock.TextProperty, model.State, property); controls.Children.Add(text);
        }
        var footer = Ui.Footer(); Grid.SetRow((FrameworkElement)footer, 2); root.Children.Add(footer);
        // Overlay the existing bottom-left space without adding a layout row.
        var minimize = new Button
        {
            Content = "Minimize to tray", Command = Controller.MinimizeToTrayCommand,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
            Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(18), FontSize = 12
        };
        ToolTipService.SetToolTip(minimize, "Keep the click ripple running in the notification area.");
        Grid.SetRowSpan(minimize, 3);
        Canvas.SetZIndex(minimize, 20);
        root.Children.Add(minimize);
        root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) => { e.Handled = Controller.HandleKey(e.Key); }), true);
        Closed += (_, _) => Controller.Dispose();
    }
}
