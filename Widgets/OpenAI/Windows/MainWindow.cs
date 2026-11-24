using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Tools.Widgets;
using Windows.ApplicationModel;
using Windows.UI.ViewManagement;
using global::Windows.Graphics;
using WindowsColor = global::Windows.UI.Color;

namespace OpenAIUsage.Windows;

public sealed partial class MainWindow : Window
{
    private const string StartupTaskId = "OpenAIUsageStartup";
    private const string UsageUrl = "https://chatgpt.com/settings/usage";
    private const string ProjectUrl = "https://github.com/umbertotechnopreneur/VibeKit/tree/main/Widgets/OpenAI";
    private const string ManifestoUrl = "https://umbertogiacobbi.biz/vibeware/manifesto";
    private static readonly int[] WindowOpacityOptions = [25, 50, 75, 100];
    private static readonly SolidColorBrush PrimaryText = new(), SecondaryText = new(), WarningText = new(), CloseText = new();
    private static readonly SolidColorBrush SettingsPanelFill = new();
    private static readonly FontFamily MetricFont = new("Cascadia Mono, Consolas");
    private readonly Storage storage=new();
    private WidgetSettings settings;
    private readonly SettingsSaveQueue settingsWriter;
    private string? settingsError;
    private bool exitPending;
    private TextBlock? settingsStatus;
    private WidgetState state=new();
    private readonly Grid root=new() { Background=new SolidColorBrush(Colors.Transparent) };
    private readonly Button connectionError=Button("\uE7BA","Connection settings");
    private readonly UISettings systemSettings=new();
    private readonly DispatcherTimer clock=new(){Interval=TimeSpan.FromSeconds(1)}, refresh=new(){Interval=TimeSpan.FromSeconds(60)};
    private readonly CancellationTokenSource lifetime=new();
    private readonly SemaphoreSlim dialogGate=new(1,1);
    private InputNonClientPointerSource? nonClientInput;
    private TrayIcon? tray;
    private Window? activeDialog;
    private bool busy, closed, allowClose, closingPrompt, refreshPending;
    private bool addedLayeredStyle;
    private DateTimeOffset lastResetRefresh=DateTimeOffset.MinValue;

    public MainWindow()
    {
        SettingsForEditing editableSettings;
        try { editableSettings = storage.LoadSettingsForEditing(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            editableSettings = new(new(), "Settings could not be read. " + error.Message);
            Diagnostics.Record("settings.failed", error.Message);
        }
        settings = editableSettings.Settings;
        detailsOpen = true;
        settingsError = editableSettings.RecoveryMessage;
        settingsWriter = new SettingsSaveQueue(storage.SaveSettings);
        settingsWriter.Saved += error => DispatcherQueue.TryEnqueue(() =>
        {
            if (closed) return;
            settingsError = error?.Message;
            UpdateSettingsStatus();
            if (error is not null)
            {
                Diagnostics.Record("settings.failed", error.Message);
                tray?.NotifyError("Could not save settings. Open Preferences to retry.");
            }
        });
        if (!WindowOpacityOptions.Contains(settings.WindowOpacityPercent))
            settings.WindowOpacityPercent = 100;
        Title="OpenAI Usage";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory,"Branding","app-icon.ico"));
        if(AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true,false);
            presenter.IsMinimizable=false; presenter.IsMaximizable=false; presenter.IsResizable=true;
        }
        if(WidgetAcrylicBackdrop.IsSupported)SystemBackdrop=new WidgetAcrylicBackdrop();
        ApplySystemTheme();
        systemSettings.ColorValuesChanged+=ThemeChanged;
        BuildWidgetStrip();
        pinButton.Click += (_, _) => UpdateSettings(next => next.AlwaysOnTop = !next.AlwaysOnTop);
        AddAction(pinButton);
        ApplyTopmost();
        connectionError.Visibility = Visibility.Collapsed;
        connectionError.Click += async (_, _) => await ShowSettingsAsync();
        AddAction(connectionError);
        detailsButton.Click += (_, _) => SetDetailsOpen(!detailsOpen);

        var preferences = Button("\uE713", "Settings");
        preferences.Click += async (_, _) =>
        {
            if (settingsPane is null) await ShowSettingsAsync();
            else await HideSettingsAsync();
        };
        AddAction(preferences);
        var about = Button("\uE946", "About OpenAI Usage");
        about.Click += async (_, _) => await ShowAboutAsync();
        AddAction(about);
        AddAction(detailsButton);
        var close = Button("\uE8BB", "Close", CloseText);
        if (close.Content is FontIcon closeIcon) closeIcon.FontSize = 22;
        close.Click += (_, _) => Close();
        AddAction(close);

        void AddAction(Control control)
        {
            control.Width = control.Height = 28;
            control.MinWidth = control.MinHeight = 0;
            control.Padding = new(0);
            control.HorizontalContentAlignment = HorizontalAlignment.Center;
            control.VerticalContentAlignment = VerticalAlignment.Center;
            actions.Children.Add(control);
        }

        BuildResponsiveLayout();
        SetDetailsOpen(detailsOpen);
        root.Children.Add(WarmAccent(.24, .72, .32, 28, 248, 112, 42));
        root.Children.Add(WarmAccent(.68, .25, .28, 23, 233, 65, 68));
        root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = new(0) });
        root.Children.Add(viewport);
        root.KeyDown += async (_, args) =>
        {
            if (args.Key != global::Windows.System.VirtualKey.Escape) return;
            if (settingsPane is not null)
            {
                args.Handled = true;
                await HideSettingsAsync();
            }
            else if (detailsOpen)
            {
                args.Handled = true;
                SetDetailsOpen(false);
                detailsButton.Focus(FocusState.Programmatic);
            }
        };
        Content=root;
        root.SizeChanged += (_, _) => QueueLayout();
        actions.SizeChanged += (_, _) => QueueLayout();
        strip.SizeChanged += (_, _) => QueueLayout();
        clock.Tick+=(_,_)=>Tick(); refresh.Tick+=async(_,_)=>await RefreshAsync();
        root.Loaded+=async(_,_)=>
        {
            root.XamlRoot.Changed += (_, _) => QueueLayout();
            double dpi = root.XamlRoot.RasterizationScale;
            int workWidth = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea.Width;
            AppWindow.ResizeClient(new(Math.Min(workWidth - 16, (int)(1240 * dpi)), AppWindow.ClientSize.Height));
            ApplySize();
            try { ApplyWidgetOpacity(settings.WindowOpacityPercent); }
            catch (Win32Exception error) { Diagnostics.Record("window.opacity.failed", error.Message); }
            try { tray=new TrayIcon(Path.Combine(AppContext.BaseDirectory,"Branding","app-icon.ico"),ShowWindow,
                ()=>{ShowWindow();_=ShowAboutAsync();},()=>{ShowWindow();_=ShowSettingsAsync();},
                ()=>OpenLink("https://umbertogiacobbi.biz"),()=>OpenLink(ManifestoUrl),ExitApplication);
                Diagnostics.Record("tray.ready","Native notification icon registered."); }
            catch(Exception error) { Diagnostics.Record("tray.unavailable",error.Message); }
            ApplyTaskbarVisibility();
            clock.Start(); refresh.Start(); await RefreshAsync();
            QueueLayout();
        };
        AppWindow.Closing+=AskToClose;
        Closed+=(_,_)=>{closed=true;activeDialog?.Close();tray?.Dispose();clock.Stop();refresh.Stop();resetClock.StopAnimation();StopSignalTransition();lifetime.Cancel();systemSettings.ColorValuesChanged-=ThemeChanged;};
    }

    private void Tick()
    {
        var connectionVisibility = settingsError is not null || !state.Connected ? Visibility.Visible : Visibility.Collapsed;
        if (connectionError.Visibility != connectionVisibility)
        {
            connectionError.Visibility = connectionVisibility;
            QueueLayout();
        }
        ToolTipService.SetToolTip(connectionError, settingsError ?? state.Status);
        var now = DateTimeOffset.UtcNow;
        long? seconds = state.ResetsAt is { } reset ? Math.Max(0, reset - now.ToUnixTimeSeconds()) : null;
        UpdateResetClock(seconds);
        UpdateDetails(now.ToUnixTimeSeconds());
        UpdateVoucherExpiry(now.ToUnixTimeSeconds());
        MaybeFlicker();
        if (seconds == 0 && !busy && state.Connected && now - lastResetRefresh > TimeSpan.FromMinutes(1))
        {
            lastResetRefresh = now;
            _ = RefreshAsync();
        }
    }

    private void ApplyOpacity(int percent)
    {
        const int extendedStyleIndex = -20;
        const int layeredStyle = 0x00080000;
        const uint useAlpha = 0x00000002;
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        nint style = GetWindowLongPtr(hwnd, extendedStyleIndex);
        if (style == 0 && Marshal.GetLastPInvokeError() is not 0 and var readError)
            throw new Win32Exception(readError);

        bool alreadyLayered = (style & layeredStyle) != 0;
        if (percent == 100 && !alreadyLayered) return;
        if (!alreadyLayered)
        {
            if (SetWindowLongPtr(hwnd, extendedStyleIndex, style | layeredStyle) == 0 &&
                Marshal.GetLastPInvokeError() is not 0 and var styleError)
                throw new Win32Exception(styleError);
            addedLayeredStyle = true;
        }

        byte alpha = (byte)Math.Round(255 * percent / 100.0);
        if (!SetLayeredWindowAttributes(hwnd, 0, alpha, useAlpha))
        {
            int error = Marshal.GetLastPInvokeError();
            if (!alreadyLayered)
            {
                SetWindowLongPtr(hwnd, extendedStyleIndex, style);
                addedLayeredStyle = false;
            }
            throw new Win32Exception(error);
        }

        if (percent == 100 && addedLayeredStyle)
        {
            if (SetWindowLongPtr(hwnd, extendedStyleIndex, style & ~layeredStyle) == 0 &&
                Marshal.GetLastPInvokeError() is not 0 and var styleError)
                throw new Win32Exception(styleError);
            addedLayeredStyle = false;
        }
    }
    private void ApplyTaskbarVisibility() => AppWindow.IsShownInSwitchers = settings.ShowInTaskbar || tray?.IsAvailable != true;
    private void ShowWindow()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        AppWindow.Show();
        var target = activeDialog ?? this;
        target.Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(target));
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint window);
    private async void ExitApplication()
    {
        if (exitPending) return;
        exitPending = true;
        root.IsHitTestVisible = false;
        activeDialog?.Close();
        try
        {
            await settingsWriter.FlushAsync();
            allowClose = true;
            Close();
        }
        catch (Exception error)
        {
            settingsError = error.Message;
            Diagnostics.Record("settings.failed", error.Message);
            ShowWindow();
            await ShowDialogAsync("Settings were not saved", Text(error.Message + " Retry Exit to save again.", 15), height: 280);
        }
        finally { exitPending = false; root.IsHitTestVisible = true; }
    }
    internal static void OpenLink(string url)
    {
        object? windows = null, desktop = null, view = null, shell = null;
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new ArgumentException("Expected an HTTPS link.", nameof(url));

            // Use the existing Explorer desktop to start external apps outside the widget's job.
            var shellWindowsClass = new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
            windows = Activator.CreateInstance(Type.GetTypeFromCLSID(shellWindowsClass, throwOnError: true)!);
            const int desktopWindow = 8, needDispatch = 1, showNormal = 1;
            object location = 0;
            object root = null!;
            desktop = ((dynamic)windows!).FindWindowSW(ref location, ref root, desktopWindow, out int _, needDispatch);
            if (desktop is null) throw new InvalidOperationException("The Explorer desktop is unavailable.");
            view = ((dynamic)desktop).Document;
            shell = ((dynamic)view!).Application;
            ((dynamic)shell!).ShellExecute(uri.AbsoluteUri, "", "", "open", showNormal);
        }
        catch (Exception error) { Diagnostics.Record("link.failed", error.GetType().Name); }
        finally
        {
            foreach (var instance in new[] { shell, view, desktop, windows })
                if (instance is not null && System.Runtime.InteropServices.Marshal.IsComObject(instance))
                    System.Runtime.InteropServices.Marshal.ReleaseComObject(instance);
        }
    }
    private async void AskToClose(AppWindow sender,AppWindowClosingEventArgs args)
    {
        if(allowClose)return;
        args.Cancel=true; if(closingPrompt || dialogGate.CurrentCount==0)return; closingPrompt=true;
        try
        {
            var choice=await ShowDialogAsync("OpenAI Usage · Close",
                Text(tray?.IsAvailable==true?"Keep monitoring in the notification area, or exit the app.":"The notification area is unavailable. You can exit or keep the widget open.",15),
                closeButtonText:"Cancel",primaryButtonText:"Hide to tray",secondaryButtonText:"Exit",isPrimaryEnabled:tray?.IsAvailable==true,width:500,height:280);
            if(choice==ContentDialogResult.Secondary)ExitApplication();
            else if(choice==ContentDialogResult.Primary && tray?.IsAvailable==true)AppWindow.Hide();
        }
        catch(Exception error){Diagnostics.Record("close.prompt.failed",error.Message);}
        finally{closingPrompt=false;}
    }
    private static Image Logo(double size)=>new(){Source=new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory,"Branding","app-icon-512.png"))),Width=size,Height=size,HorizontalAlignment=HorizontalAlignment.Left};
    private void ThemeChanged(UISettings sender,object args)=>DispatcherQueue.TryEnqueue(ApplySystemTheme);
    private void ApplySystemTheme()
    {
        var background=systemSettings.GetColorValue(UIColorType.Background);
        bool dark=background.R*299+background.G*587+background.B*114<128000;
        root.RequestedTheme=dark?ElementTheme.Dark:ElementTheme.Light;
        root.Background=new SolidColorBrush(WidgetAcrylicBackdrop.IsSupported?Colors.Transparent:dark?ColorHelper.FromArgb(255,24,30,37):ColorHelper.FromArgb(255,245,249,252));
        PrimaryText.Color=dark?ColorHelper.FromArgb(255,235,241,249):ColorHelper.FromArgb(255,23,39,62);
        SecondaryText.Color=dark?ColorHelper.FromArgb(255,194,205,221):ColorHelper.FromArgb(255,69,87,111);
        WarningText.Color=dark?ColorHelper.FromArgb(255,255,203,107):ColorHelper.FromArgb(255,148,82,0);
        CloseText.Color = Rgb(dark ? 0xFF6C78u : 0xB62036u);
        SettingsPanelFill.Color = Rgb(dark ? 0x182029u : 0xF5F9FCu);
        if(activeDialog is { ExtendsContentIntoTitleBar: true, Content: FrameworkElement dialogRoot })
        {
            dialogRoot.RequestedTheme=root.RequestedTheme;
            activeDialog.AppWindow.TitleBar.ButtonForegroundColor=PrimaryText.Color;
            activeDialog.AppWindow.TitleBar.ButtonInactiveForegroundColor=SecondaryText.Color;
        }
    }

    private static WindowsColor Rgb(uint rgb) =>
        ColorHelper.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private static Border WarmAccent(double x, double y, double radiusX, byte alpha, byte red, byte green, byte blue) =>
        new()
        {
            IsHitTestVisible = false,
            Background = new RadialGradientBrush
            {
                Center = new(x, y),
                RadiusX = radiusX,
                RadiusY = .82,
                GradientStops =
                {
                    new() { Color = ColorHelper.FromArgb(alpha, red, green, blue) },
                    new() { Color = ColorHelper.FromArgb((byte)(alpha / 3), red, green, blue), Offset = .56 },
                    new() { Color = ColorHelper.FromArgb(0, red, green, blue), Offset = 1 }
                }
            }
        };

    private static TextBlock Text(string text,double size,bool bold=false)=>new(){Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap,FontWeight=bold?Microsoft.UI.Text.FontWeights.SemiBold:Microsoft.UI.Text.FontWeights.Normal,Foreground=PrimaryText};
    private static Button Button(string glyph, string tooltip, Brush? foreground = null) =>
        Button(new FontIcon { Glyph = glyph, FontSize = 16, Foreground = foreground ?? PrimaryText }, tooltip);

    private static Button Button(UIElement icon, string tooltip)
    {
        var button = new Button
        {
            Content = icon,
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new(0),
            Padding = new(5),
            MinWidth = 26
        };
        ToolTipService.SetToolTip(button, tooltip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, tooltip);
        return button;
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(nint window, uint colorKey, byte alpha, uint flags);
    [DllImport("user32.dll")]private static extern bool EnableWindow(nint window,bool enabled);
}
