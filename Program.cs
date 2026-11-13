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
        var mainWindow = new MainWindow(model, startAtSignIn);
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

    internal MainWindow(StudioModel model, bool startHidden)
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
        AppWindow.Resize(new SizeInt32(1320, 960));
        var root = new Grid { RequestedTheme = ElementTheme.Dark, Padding = new Thickness(26, 26, 26, 12) };
        root.DataContext = model;
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;
        Ui.AttachAppearance(this, root, model.Settings);
        root.Children.Add(Ui.BrandHeader("Water Ripple", "A ripple for every click."));
        var body = new Grid { ColumnSpacing = 24, Margin = new Thickness(0, 18, 0, 0) };
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
        var preset = new ComboBox { Header = "Ripple preset", HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = AppSettings.PresetNames };
        Ui.Bind(preset, ComboBox.SelectedIndexProperty, model.Settings, nameof(AppSettings.PresetIndex), BindingMode.TwoWay);
        controls.Children.Add(preset);
        controls.Children.Add(new Button { Content = "Settings", Command = Controller.OpenSettingsCommand, HorizontalAlignment = HorizontalAlignment.Stretch });
        var freeze = new Button { Command = Controller.ToggleCaptureCommand, HorizontalAlignment = HorizontalAlignment.Stretch };
        Ui.Bind(freeze, Button.ContentProperty, model.State, nameof(AppState.FreezeButtonText));
        Ui.Bind(freeze, Button.IsEnabledProperty, model.State, nameof(AppState.CanFreeze)); controls.Children.Add(freeze);
        controls.Children.Add(new Button { Content = "Drop in the center", Command = Controller.DropCommand, HorizontalAlignment = HorizontalAlignment.Stretch });
        var chromatic = new StackPanel { Spacing = 8 };
        Ui.Toggle(chromatic, "Color separation", model.Settings, nameof(AppSettings.ChromaticEnabled));
        var chromaticControls = new StackPanel { Spacing = 8 };
        var colorPreset = new ComboBox { Header = "RGB preset", HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "Custom", "Subtle", "Balanced", "Vivid" } };
        var colorDescription = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        chromaticControls.Children.Add(colorPreset);
        chromaticControls.Children.Add(colorDescription);
        var separation = Ui.Slider(chromaticControls, "Channel separation", model.Settings, nameof(AppSettings.ChromaticSeparation), 0, 100, 1, "%");
        var blend = Ui.Slider(chromaticControls, "Color blend", model.Settings, nameof(AppSettings.ChromaticBlend), 0, 100, 1, "%");
        Ui.Bind(colorPreset, Control.IsEnabledProperty, model.Settings, nameof(AppSettings.ChromaticEnabled));
        Ui.Bind(separation, Control.IsEnabledProperty, model.Settings, nameof(AppSettings.ChromaticEnabled));
        Ui.Bind(blend, Control.IsEnabledProperty, model.Settings, nameof(AppSettings.ChromaticEnabled));
        bool updatingColorPreset = false;
        void SyncColorPreset()
        {
            updatingColorPreset = true;
            var s = model.Settings;
            colorPreset.SelectedIndex = (s.ChromaticSeparation, s.ChromaticBlend) switch
            {
                (12, 35) => 1, (18, 45) => 2, (45, 70) => 3, _ => 0
            };
            colorDescription.Text = colorPreset.SelectedIndex switch
            {
                1 => "A delicate color fringe for a natural ripple.",
                2 => "A balanced color trail along the wave.",
                3 => "A stronger, more colorful refracted trail.",
                _ => "Fine-tune the separation and color blend below."
            };
            updatingColorPreset = false;
        }
        colorPreset.SelectionChanged += (_, _) =>
        {
            if (updatingColorPreset) return;
            var values = colorPreset.SelectedIndex switch
            {
                1 => (12d, 35d), 2 => (18d, 45d), 3 => (45d, 70d), _ => (-1d, -1d)
            };
            if (values.Item1 >= 0)
            {
                updatingColorPreset = true;
                model.Settings.ChromaticSeparation = values.Item1;
                model.Settings.ChromaticBlend = values.Item2;
                updatingColorPreset = false;
            }
            SyncColorPreset();
        };
        model.Settings.PropertyChanged += (_, e) =>
        {
            if (!updatingColorPreset && (e.PropertyName == nameof(AppSettings.ChromaticSeparation) || e.PropertyName == nameof(AppSettings.ChromaticBlend)))
                SyncColorPreset();
        };
        SyncColorPreset();
        chromatic.Children.Add(chromaticControls);
        ToggleButton? openFilter = null;
        void AddExpandableFilter(string title, StackPanel content)
        {
            var section = new StackPanel { Spacing = 8 };
            var heading = new ToggleButton { Content = title, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(heading, title);
            content.Visibility = Visibility.Collapsed;
            heading.Checked += (_, _) =>
            {
                if (openFilter is not null && openFilter != heading) openFilter.IsChecked = false;
                openFilter = heading;
                content.Visibility = Visibility.Visible;
            };
            heading.Unchecked += (_, _) =>
            {
                if (openFilter == heading) openFilter = null;
                content.Visibility = Visibility.Collapsed;
            };
            section.Children.Add(heading);
            section.Children.Add(content);
            controls.Children.Add(section);
        }
        AddExpandableFilter("RGB refraction", chromatic);
        void AddFilter(string title, string enabledProperty, string intensityProperty,
            Func<double> getIntensity, Action<double> setIntensity, string[] names, string[] descriptions)
        {
            var content = new StackPanel { Spacing = 8 };
            Ui.Toggle(content, "Enable", model.Settings, enabledProperty);
            var options = new StackPanel { Spacing = 8 };
            var filterPreset = new ComboBox { Header = "Preset", HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = new[] { "Custom", names[0], names[1], names[2] } };
            var description = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            options.Children.Add(filterPreset);
            options.Children.Add(description);
            var intensity = Ui.Slider(options, "Intensity", model.Settings, intensityProperty, 0, 100, 1, "%");
            Ui.Bind(filterPreset, Control.IsEnabledProperty, model.Settings, enabledProperty);
            Ui.Bind(intensity, Control.IsEnabledProperty, model.Settings, enabledProperty);
            bool updating = false;
            void SyncPreset()
            {
                updating = true;
                filterPreset.SelectedIndex = getIntensity() switch { 25 => 1, 50 => 2, 85 => 3, _ => 0 };
                description.Text = filterPreset.SelectedIndex == 0 ? "Adjust the intensity below." : descriptions[filterPreset.SelectedIndex - 1];
                updating = false;
            }
            filterPreset.SelectionChanged += (_, _) =>
            {
                if (updating) return;
                double value = filterPreset.SelectedIndex switch { 1 => 25, 2 => 50, 3 => 85, _ => -1 };
                if (value >= 0) setIntensity(value);
                SyncPreset();
            };
            model.Settings.PropertyChanged += (_, e) => { if (!updating && e.PropertyName == intensityProperty) SyncPreset(); };
            SyncPreset();
            content.Children.Add(options);
            AddExpandableFilter(title, content);
        }
        AddFilter("Crest reflections", nameof(AppSettings.CrestEnabled), nameof(AppSettings.CrestIntensity),
            () => model.Settings.CrestIntensity, value => model.Settings.CrestIntensity = value,
            new[] { "Soft glint", "Water shine", "Bright crest" },
            new[] { "A faint glint follows the wave.", "Adds a clear reflection to the crests.", "Gives the crests a pronounced bright shine." });
        AddFilter("Multiple ripples", nameof(AppSettings.RipplesEnabled), nameof(AppSettings.RipplesIntensity),
            () => model.Settings.RipplesIntensity, value => model.Settings.RipplesIntensity = value,
            new[] { "Fine ripples", "Layered waves", "Choppy water" },
            new[] { "Adds fine secondary ripples.", "Adds a visible pattern of smaller waves.", "Makes the secondary ripples more pronounced." });
        AddFilter("Glass / liquid", nameof(AppSettings.LiquidEnabled), nameof(AppSettings.LiquidIntensity),
            () => model.Settings.LiquidIntensity, value => model.Settings.LiquidIntensity = value,
            new[] { "Soft lens", "Liquid glass", "Deep lens" },
            new[] { "Adds a gentle lens-like distortion.", "Gives the wave a liquid-glass refraction.", "Strengthens the lens-like refraction." });
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
