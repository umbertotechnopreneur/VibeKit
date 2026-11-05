using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Serilog;
using VirtualKey = Windows.System.VirtualKey;

namespace WaterRipple;

public partial class App : Application
{
    private MainWindow? window;
    private SettingsStore? store;

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
        LoggingService.Initialize();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled process exception");
            LoggingService.Shutdown();
        };
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Start(_ =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                new App();
            });
        }
        catch (Exception ex) { Log.Fatal(ex, "Application startup failed"); throw; }
        finally { Log.Information("Application stopped"); LoggingService.Shutdown(); }
    }

    public App()
    {
        UnhandledException += (_, e) =>
        {
            Log.Fatal(e.Exception, "Unhandled WinUI exception");
            LoggingService.Shutdown();
        };
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try { CreateMainWindow(); }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Main window construction or activation failed");
            LoggingService.Shutdown();
            throw;
        }
    }

    private void CreateMainWindow()
    {
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
        window = new MainWindow(model);
        window.Closed += (_, _) => { store.Dispose(); Exit(); };
        window.Activate();
        Log.Information("Application started Version={Version}", typeof(App).Assembly.GetName().Version);
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
        var frame = new Border { Child = canvas, CornerRadius = new CornerRadius(10) };
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
        sidebar.PointerEntered += (_, _) => scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        sidebar.PointerExited += (_, e) =>
        {
            var p = e.GetCurrentPoint(sidebar).Position;
            if (p.X < 0 || p.Y < 0 || p.X >= sidebar.ActualWidth || p.Y >= sidebar.ActualHeight) scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
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
        var capability = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        Ui.Bind(capability, TextBlock.TextProperty, model.State, nameof(AppState.CapabilityText)); controls.Children.Add(capability);
        var preset = new ComboBox { Header = "Ripple preset", HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = new[] { "Gentle", "Water drop", "Deep water", "Shockwave" } };
        Ui.Bind(preset, ComboBox.SelectedIndexProperty, model.Settings, nameof(AppSettings.PresetIndex), BindingMode.TwoWay); controls.Children.Add(preset);
        Ui.Slider(controls, "Animation limit", model.Settings, nameof(AppSettings.AnimationFps), 15, 120, 1, "fps");
        Ui.Slider(controls, "Capture limit", model.Settings, nameof(AppSettings.CaptureFps), 5, 60, 1, "fps");
        Ui.Slider(controls, "Refraction", model.Settings, nameof(AppSettings.Strength), 0, 25, 1, "px");
        Ui.Slider(controls, "Wave size", model.Settings, nameof(AppSettings.WaveSize), 30, 220, 1, "px");
        Ui.Slider(controls, "Expansion", model.Settings, nameof(AppSettings.Speed), 40, 350, 1, "px/s");
        Ui.Slider(controls, "Duration", model.Settings, nameof(AppSettings.Duration), .4, 3, .1, "s");
        Ui.Toggle(controls, "Acrylic background", model.Settings, nameof(AppSettings.UseAcrylic));
        var freeze = new Button { Command = Controller.ToggleCaptureCommand, HorizontalAlignment = HorizontalAlignment.Stretch };
        Ui.Bind(freeze, Button.ContentProperty, model.State, nameof(AppState.FreezeButtonText));
        Ui.Bind(freeze, Button.IsEnabledProperty, model.State, nameof(AppState.CanFreeze)); controls.Children.Add(freeze);
        controls.Children.Add(new Button { Content = "Drop in the center", Command = Controller.DropCommand, HorizontalAlignment = HorizontalAlignment.Stretch });
        foreach (string property in new[] { nameof(AppState.MetricsText), nameof(AppState.ResourcesText), nameof(AppState.StatusMessage), nameof(AppState.PersistenceError) })
        {
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            Ui.Bind(text, TextBlock.TextProperty, model.State, property); controls.Children.Add(text);
        }
        controls.Children.Add(new Button { Content = "Recheck GPU / restart preview", Command = Controller.RestartCommand, HorizontalAlignment = HorizontalAlignment.Stretch });
        var footer = Ui.Footer(model.Settings); Grid.SetRow((FrameworkElement)footer, 2); root.Children.Add(footer);
        root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) => { e.Handled = Controller.HandleKey(e.Key); }), true);
        Closed += (_, _) => Controller.Dispose();
    }
}
