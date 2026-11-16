using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
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
    private const int EstimateSeconds = 10, VoucherSeconds = 5;
    private const double CountdownWidth = 290;
    private const double MetricHeadingSize = 16, MetricValueSize = 27;
    private const string UsageUrl = "https://chatgpt.com/settings/usage";
    private const string ManifestoUrl = "https://umbertogiacobbi.biz/vibeware/manifesto";
    private static readonly int[] WindowOpacityOptions = [25, 50, 75, 100];
    private static readonly (double Percent, uint Light, uint Dark)[] UsageColors =
    [
        (0,   0x7B1223, 0xF64868),
        (5,   0x90142A, 0xFF5372),
        (10,  0xA52338, 0xFF6375),
        (20,  0xA23818, 0xFF865F),
        (50,  0x855600, 0xFFD064),
        (75,  0x506D08, 0xB9DE79),
        (100, 0x0C6845, 0x75E7AC)
    ];
    private static readonly SolidColorBrush Accent=new(), PrimaryText=new(), SecondaryText=new(), WarningText=new();
    private static readonly SolidColorBrush PinIdle = new(), PinActive = new(), CloseText = new();
    private static readonly FontFamily MetricFont=new("Cascadia Mono, Consolas");
    private static readonly LinearGradientBrush ValueGradient=new() { StartPoint=new(0,0), EndPoint=new(1,.25), GradientStops={new(),new(){Offset=1}} };
    private readonly Storage storage=new();
    private WidgetSettings settings;
    private readonly SettingsSaveQueue settingsWriter;
    private string? settingsError;
    private bool exitPending;
    private TextBlock? settingsStatus;
    private WidgetState state=new();
    private readonly Grid root=new() { Background=new SolidColorBrush(Colors.Transparent) };
    private readonly Grid strip = new() { ColumnSpacing = 14, Margin = new(10, 8, 10, 8) };
    private readonly Grid actions = new() { ColumnSpacing = 8, RowSpacing = 4 };
    private readonly TextBlock usageHeading = MetricHeading("Usage Remaining", TextAlignment.Left);
    private readonly TextBlock remaining = MetricValue(TextAlignment.Left);
    private readonly LinearGradientBrush usageGradient = new()
    {
        StartPoint = new(0, 0),
        EndPoint = new(1, 1),
        GradientStops = { new(), new() { Offset = 1 } }
    };
    private Storyboard? usageColorTransition;
    private readonly TextBlock forecastHeading = MetricHeading("Estimated time remaining", TextAlignment.Left);
    private readonly ColumnDefinition forecastColumn = new();
    private readonly StackPanel forecastRow = new()
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Left,
        Spacing = 5
    };
    private readonly TextBlock forecastValue = MetricValue(TextAlignment.Left);
    private readonly Border forecastInfo = InfoIcon("About the displayed metric");
    private readonly ColumnDefinition creditsColumn = new() { Width = GridLength.Auto };
    private readonly TextBlock creditsHeading = MetricHeading("Remaining Credits", TextAlignment.Right);
    private readonly TextBlock credits = MetricValue(TextAlignment.Right);
    private readonly Stopwatch voucherCycle = new();
    private string? quotaTooltip, forecastTooltip;
    private readonly FlipTile[] tiles=[new("DAYS"),new("HOURS"),new("MINUTES"),new("SECONDS")];
    private readonly Button profile = Button(Logo(28), "Open Profile");
    private readonly Button usageLink = Button("", "Open usage and resets in ChatGPT");
    private readonly Button forecastLink = Button("", "Open usage and resets in ChatGPT");
    private readonly Button countdownLink = Button("", "Open usage and resets in ChatGPT");
    private readonly ToggleButtonCompat pin=new();
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
    private readonly HashSet<int> notifiedThresholds = [];
    private string? alertWindow;
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
            presenter.IsMinimizable=false; presenter.IsMaximizable=false; presenter.IsResizable=false;
        }
        if(WidgetAcrylicBackdrop.IsSupported)SystemBackdrop=new WidgetAcrylicBackdrop();
        ApplySystemTheme();
        systemSettings.ColorValuesChanged+=ThemeChanged;
        strip.ColumnDefinitions.Add(new(){Width=new(28)});
        strip.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});
        strip.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        profile.Padding = new(0);
        profile.Width = 28;
        profile.VerticalAlignment = VerticalAlignment.Center;
        profile.Click += (_, _) => OpenLink("https://chatgpt.com/profile");
        strip.Children.Add(profile);
        var data = new Grid { ColumnSpacing = 14, VerticalAlignment = VerticalAlignment.Center };
        data.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        data.ColumnDefinitions.Add(new() { Width = new(CountdownWidth) });
        var summary = new Grid { ColumnSpacing = 10, RowSpacing = 7, VerticalAlignment = VerticalAlignment.Center };
        summary.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        summary.ColumnDefinitions.Add(forecastColumn);
        summary.ColumnDefinitions.Add(new() { Width = new(1) });
        summary.ColumnDefinitions.Add(creditsColumn);
        usageHeading.Margin = new(0, 0, 9, 0);
        AddMetricLink(summary, 0, usageLink, usageHeading, remaining);
        forecastRow.Children.Add(forecastValue);
        forecastRow.Children.Add(forecastInfo);
        AddMetricLink(summary, 1, forecastLink, forecastHeading, forecastRow);
        var separator = new Border
        {
            Height = 40,
            Background = SecondaryText,
            Opacity = .38,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        Grid.SetColumn(separator, 2);
        summary.Children.Add(separator);
        var creditsMetric = new Grid { RowSpacing = 7 };
        AddMetric(creditsMetric, 0, creditsHeading, credits);
        Grid.SetColumn(creditsMetric, 3);
        summary.Children.Add(creditsMetric);
        data.Children.Add(summary);
        var tileRow = new Grid { ColumnSpacing = 14, Width = 430 };
        for (int i = 0; i < tiles.Length; i++)
        {
            tiles[i].SetCompact(true);
            double width = i == tiles.Length - 1 ? 1.13 : 1;
            tileRow.ColumnDefinitions.Add(new() { Width = new(width, GridUnitType.Star) });
            Grid.SetColumn(tiles[i], i);
            tileRow.Children.Add(tiles[i]);
        }
        var clockView = new Viewbox { Child = tileRow, Stretch = Stretch.Uniform, MaxWidth = CountdownWidth };
        countdownLink.Content = clockView;
        countdownLink.Padding = new(0);
        countdownLink.Click += (_, _) => OpenLink(UsageUrl);
        Grid.SetColumn(countdownLink, 1);
        data.Children.Add(countdownLink);
        Grid.SetColumn(data,1); strip.Children.Add(data);
        for (int i = 0; i < 3; i++)
            actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        actions.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        actions.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        connectionError.Visibility = Visibility.Collapsed;
        connectionError.VerticalAlignment = VerticalAlignment.Center;
        connectionError.Click += async (_, _) => await ShowSettingsAsync();
        Grid.SetRowSpan(connectionError, 2);
        actions.Children.Add(connectionError);

        pin.Content = new FontIcon { Glyph = "\uE718", FontSize = 16, Foreground = PinIdle };
        pin.MinWidth=26; pin.Padding=new(5); pin.Background=new SolidColorBrush(Colors.Transparent); pin.BorderThickness=new(0);
        foreach(var key in new[]{"ToggleButtonBackground","ToggleButtonBackgroundPointerOver","ToggleButtonBackgroundPressed","ToggleButtonBackgroundChecked","ToggleButtonBackgroundCheckedPointerOver","ToggleButtonBackgroundCheckedPressed"})pin.Resources[key]=new SolidColorBrush(Colors.Transparent);
        ToolTipService.SetToolTip(pin,"Always on top"); Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(pin,"Always on top");
        pin.IsChecked = settings.AlwaysOnTop;
        pin.Click += (_, _) => { settings.AlwaysOnTop = pin.IsChecked == true; ApplyTopmost(); SaveSettings(); UpdatePinVisual(); };
        AddAction(pin, 1, 0);
        var close = Button("\uE8BB", "Close", CloseText);
        close.Click += (_, _) => Close();
        AddAction(close, 2, 0);
        var preferences = Button("\uE713", "Settings");
        preferences.Click += async (_, _) => await ShowSettingsAsync();
        AddAction(preferences, 1, 1);
        var about = Button("\uE946", "About OpenAI Usage");
        about.Click += async (_, _) => await ShowAboutAsync();
        AddAction(about, 2, 1);

        void AddAction(Control control, int column, int row)
        {
            control.HorizontalAlignment = HorizontalAlignment.Center;
            control.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, column);
            Grid.SetRow(control, row);
            actions.Children.Add(control);
        }

        Grid.SetColumn(actions,2); strip.Children.Add(actions);
        ApplySize();
        var view=new Viewbox { Child=strip, Stretch=Stretch.Uniform, VerticalAlignment=VerticalAlignment.Center };
        root.Children.Add(WarmAccent(.24, .72, .32, 28, 248, 112, 42));
        root.Children.Add(WarmAccent(.68, .25, .28, 23, 233, 65, 68));
        root.Children.Add(view);
        var manifesto = new HyperlinkButton
        {
            NavigateUri = new Uri(ManifestoUrl),
            Content = new Image
            {
                Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Branding", "VibeWare.png"))),
                Width = 16, Height = 16, Stretch = Stretch.Uniform
            },
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new(0, 0, 10, 4), Padding = new(0),
            MinWidth = 0, MinHeight = 0, BorderThickness = new(0),
            Background = new SolidColorBrush(Colors.Transparent), Opacity = .78
        };
        foreach (string state in new[] { "", "PointerOver", "Pressed", "Disabled" })
        {
            manifesto.Resources["HyperlinkButtonBackground" + state] = new SolidColorBrush(Colors.Transparent);
            manifesto.Resources["HyperlinkButtonBorderBrush" + state] = new SolidColorBrush(Colors.Transparent);
        }
        ToolTipService.SetToolTip(manifesto, "VibeWare manifesto");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(manifesto, "VibeWare manifesto");
        manifesto.PointerEntered += (_, _) => manifesto.Opacity = 1;
        manifesto.PointerExited += (_, _) => manifesto.Opacity = .78;
        root.Children.Add(manifesto);
        Content=root; ApplyTopmost(); UpdatePinVisual();
        root.SizeChanged+=(_,_)=>UpdateDragRegions(); actions.SizeChanged+=(_,_)=>UpdateDragRegions(); view.SizeChanged+=(_,_)=>UpdateDragRegions();
        forecastRow.SizeChanged += (_, _) => UpdateDragRegions();
        usageLink.SizeChanged += (_, _) => UpdateDragRegions();
        forecastLink.SizeChanged += (_, _) => UpdateDragRegions();
        countdownLink.SizeChanged += (_, _) => UpdateDragRegions();
        clock.Tick+=(_,_)=>Tick(); refresh.Tick+=async(_,_)=>await RefreshAsync();
        root.Loaded+=async(_,_)=>
        {
            root.XamlRoot.Changed+=(_,_)=>UpdateDragRegions();
            ApplySize();
            try { ApplyOpacity(settings.WindowOpacityPercent); }
            catch (Win32Exception error) { Diagnostics.Record("window.opacity.failed", error.Message); }
            try { tray=new TrayIcon(Path.Combine(AppContext.BaseDirectory,"Branding","app-icon.ico"),ShowWindow,
                ()=>{ShowWindow();_=ShowAboutAsync();},()=>{ShowWindow();_=ShowSettingsAsync();},
                ()=>OpenLink("https://umbertogiacobbi.biz"),()=>OpenLink("https://umbertogiacobbi.biz/vibeware/manifesto"),ExitApplication);
                Diagnostics.Record("tray.ready","Native notification icon registered."); }
            catch(Exception error) { Diagnostics.Record("tray.unavailable",error.Message); }
            ApplyTaskbarVisibility();
            clock.Start(); refresh.Start(); await RefreshAsync();
            DispatcherQueue.TryEnqueue(()=>{root.UpdateLayout();UpdateDragRegions();LogLayout();});
        };
        AppWindow.Closing+=AskToClose;
        Closed+=(_,_)=>{closed=true;activeDialog?.Close();tray?.Dispose();clock.Stop();refresh.Stop();usageColorTransition?.Stop();lifetime.Cancel();systemSettings.ColorValuesChanged-=ThemeChanged;};
    }

    private void Tick()
    {
        connectionError.Visibility = settingsError is not null || !state.Connected ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(connectionError, settingsError ?? state.Status);
        UpdateForecastMetric();
        long? seconds=state.ResetsAt is {} end?Math.Max(0,end-DateTimeOffset.UtcNow.ToUnixTimeSeconds()):null;
        string[] values=seconds is {} s?[$"{s/86400:00}",$"{s/3600%24:00}",$"{s/60%60:00}",$"{s%60:00}"]:["--","--","--","--"];
        for(int i=0;i<4;i++)tiles[i].Set(values[i],systemSettings.AnimationsEnabled);
        string quotaDetail="Percentage remaining for the selected Codex allowance. " + state.ResetLabel;
        if (quotaTooltip != quotaDetail)
        {
            quotaTooltip=quotaDetail;
            ToolTipService.SetToolTip(remaining,quotaDetail);
        }
        bool elapsed=state.ResetsAt is {} reset && reset<=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if(elapsed && !busy && state.Connected && DateTimeOffset.UtcNow-lastResetRefresh>TimeSpan.FromMinutes(1)) {lastResetRefresh=DateTimeOffset.UtcNow;_=RefreshAsync();}
    }
    private void UpdateForecastMetric()
    {
        string heading = "Estimated time remaining";
        string value = state.ForecastAvailable ? state.ForecastCompact : "—";
        string detail = "Estimated time until your remaining Codex usage runs out at the observed rate. Your allowance may reset first. " +
            state.ForecastDetail;

        if (state.ResetVoucherCount is > 0 and var count)
        {
            voucherCycle.Start();
            var expirations = state.ResetVoucherExpirations;
            int cycleSeconds = EstimateSeconds + VoucherSeconds * (1 + expirations.Count);
            int elapsed = (int)(voucherCycle.Elapsed.TotalSeconds % cycleSeconds);
            if (elapsed >= EstimateSeconds)
            {
                int slot = (elapsed - EstimateSeconds) / VoucherSeconds;
                if (slot == 0)
                {
                    heading = "Resets available";
                    value = count.ToString(CultureInfo.CurrentCulture);
                    detail = "Number of earned vouchers you can use to reset your Codex usage allowance.";
                }
                else
                {
                    heading = $"Reset {slot} expires";
                    var expiration = expirations[slot - 1];
                    value = expiration.Value;
                    detail = $"Expiration of earned reset voucher {slot}. {expiration.Detail}";
                }
            }
        }

        if (forecastHeading.Text != heading) forecastHeading.Text = heading;
        if (forecastValue.Text != value) forecastValue.Text = value;
        if (forecastTooltip == detail) return;
        forecastTooltip = detail;
        ToolTipService.SetToolTip(forecastInfo, detail);
    }
    private void CheckAlerts()
    {
        if (!state.Connected || state.Remaining is not { } remaining || state.SelectedWindow is null) return;
        if (alertWindow != state.SelectedWindow) { notifiedThresholds.Clear(); alertWindow = state.SelectedWindow; }
        var enabled = new Dictionary<int, bool> { [50] = settings.Alert50, [20] = settings.Alert20, [5] = settings.Alert5 };
        foreach (var item in enabled)
            if (!item.Value || remaining > item.Key) notifiedThresholds.Remove(item.Key);
        var crossed = enabled.Where(item => item.Value && remaining <= item.Key && !notifiedThresholds.Contains(item.Key)).Select(item => item.Key).ToArray();
        if (crossed.Length == 0 || tray?.IsAvailable != true) return;
        int threshold = crossed.Min();
        if (tray.NotifyQuota($"{state.AllowanceLabel}: {remaining:0.#}% remaining (at or below {threshold}%)."))
            foreach (int value in crossed) notifiedThresholds.Add(value);
    }
    private void ApplySize()
    {
        bool large = settings.BigSize;
        double headingSize = large ? 17 : MetricHeadingSize;
        double valueSize = large ? 29 : MetricValueSize;
        usageHeading.FontSize = forecastHeading.FontSize = creditsHeading.FontSize = headingSize;
        remaining.FontSize = forecastValue.FontSize = credits.FontSize = valueSize;
        forecastHeading.Margin = forecastRow.Margin = new(large ? 5 : 0, 0, 0, 0);
        forecastColumn.Width = new(large ? 290 : 260);
        creditsColumn.MaxWidth = large ? 165 : 150;
        strip.Width = large ? 1112 : 1052;

        if (!root.IsLoaded) return;
        double scale = root.XamlRoot.RasterizationScale * (large ? 1.25 : 1);
        double width = (strip.Width + strip.Margin.Left + strip.Margin.Right) * .74;
        AppWindow.Resize(new((int)Math.Round(width * scale), (int)Math.Round(86 * scale)));
        UpdateDragRegions();
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
    private void ApplyTopmost() {if(AppWindow.Presenter is OverlappedPresenter p)p.IsAlwaysOnTop=settings.AlwaysOnTop;}
    private void ApplyTaskbarVisibility() => AppWindow.IsShownInSwitchers = settings.ShowInTaskbar || tray?.IsAvailable != true;
    private void UpdatePinVisual()
    {
        bool enabled = pin.IsChecked == true;
        if (pin.Content is FontIcon glyph)
            glyph.Foreground = enabled ? PinActive : PinIdle;
        string description = enabled ? "Always on top: on" : "Always on top: off";
        ToolTipService.SetToolTip(pin, description);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(pin, description);
    }
    private void ShowWindow() {AppWindow.Show();if(activeDialog is not null)activeDialog.Activate();else Activate();}
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
    private static void OpenLink(string url)
    {
        try {System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url){UseShellExecute=true});}
        catch(Exception error){Diagnostics.Record("link.failed",error.GetType().Name);}
    }
    private void UpdateDragRegions()
    {
        if (closed || !root.IsLoaded || actions.ActualWidth <= 0 || root.ActualWidth <= 0) return;
        double dpi = root.XamlRoot.RasterizationScale;
        int width = (int)Math.Round(root.ActualWidth * dpi);
        int height = (int)Math.Round(root.ActualHeight * dpi);
        var actionBounds = actions.TransformToVisual(root).TransformBounds(new(0, 0, actions.ActualWidth, actions.ActualHeight));
        int captionRight = Math.Clamp((int)Math.Floor(actionBounds.X * dpi) - 8, 0, width);
        var caption = new List<RectInt32>();
        var passthrough = new List<RectInt32> { new(captionRight, 0, width - captionRight, height) };
        int previousRight = 0;
        // Keep links responsive while the spaces between them remain draggable.
        foreach (var target in new FrameworkElement[] { profile, usageLink, forecastLink, countdownLink })
        {
            var bounds = target.TransformToVisual(root)
                .TransformBounds(new(0, 0, target.ActualWidth, target.ActualHeight));
            int left = Math.Clamp((int)Math.Floor(bounds.Left * dpi), previousRight, captionRight);
            int right = Math.Clamp((int)Math.Ceiling(bounds.Right * dpi), left, captionRight);
            int top = Math.Clamp((int)Math.Floor(bounds.Top * dpi), 0, height);
            int bottom = Math.Clamp((int)Math.Ceiling(bounds.Bottom * dpi), top, height);
            if (left == right || top == bottom) continue;
            caption.Add(new(previousRight, 0, left - previousRight, height));
            caption.Add(new(left, 0, right - left, top));
            caption.Add(new(left, bottom, right - left, height - bottom));
            passthrough.Add(new(left, top, right - left, bottom - top));
            previousRight = right;
        }
        caption.Add(new(previousRight, 0, captionRight - previousRight, height));
        nonClientInput ??= InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        nonClientInput.SetRegionRects(NonClientRegionKind.Caption, caption.Where(rect => rect.Width > 0 && rect.Height > 0).ToArray());
        nonClientInput.SetRegionRects(NonClientRegionKind.Passthrough, passthrough.Where(rect => rect.Width > 0 && rect.Height > 0).ToArray());
    }
    private void LogLayout()
    {
        var captions=nonClientInput?.GetRegionRects(NonClientRegionKind.Caption);
        Diagnostics.Record("window.layout",$"compactOnly=True; client={root.ActualWidth:0}x{root.ActualHeight:0}; toolbarActions={actions.Children.OfType<Control>().Count(c=>c.Visibility==Visibility.Visible)}; captionRegions={captions?.Length??0}; metricsClipped={remaining.IsTextTrimmed || forecastValue.IsTextTrimmed || credits.IsTextTrimmed}; acrylicSupported={WidgetAcrylicBackdrop.IsSupported}");
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
        var accent=systemSettings.GetColorValue(UIColorType.Accent); Accent.Color=accent;
        PrimaryText.Color=dark?ColorHelper.FromArgb(255,235,241,249):ColorHelper.FromArgb(255,23,39,62);
        SecondaryText.Color=dark?ColorHelper.FromArgb(255,194,205,221):ColorHelper.FromArgb(255,69,87,111);
        WarningText.Color=dark?ColorHelper.FromArgb(255,255,203,107):ColorHelper.FromArgb(255,148,82,0);
        PinIdle.Color = Rgb(dark ? 0x87A9C6u : 0x426987u);
        PinActive.Color = Rgb(dark ? 0x71BEFFu : 0x0568BAu);
        CloseText.Color = Rgb(dark ? 0xFF6C78u : 0xB62036u);
        ValueGradient.GradientStops[0].Color=dark?Blend(Colors.White,accent,.12):ColorHelper.FromArgb(255,18,24,34);
        ValueGradient.GradientStops[1].Color=dark?Blend(Colors.White,accent,.38):Blend(Colors.Black,accent,.58);
        UpdateUsageColor(animate: false);
        UpdatePinVisual();
        if(activeDialog is { ExtendsContentIntoTitleBar: true, Content: FrameworkElement dialogRoot })
        {
            dialogRoot.RequestedTheme=root.RequestedTheme;
            activeDialog.AppWindow.TitleBar.ButtonForegroundColor=PrimaryText.Color;
            activeDialog.AppWindow.TitleBar.ButtonInactiveForegroundColor=SecondaryText.Color;
        }
    }

    private void UpdateUsageColor(bool animate)
    {
        usageColorTransition?.Stop();
        usageColorTransition = null;
        if (state.Remaining is not { } remainingPercent)
        {
            remaining.Foreground = ValueGradient;
            return;
        }

        bool dark = root.RequestedTheme == ElementTheme.Dark;
        double percent = Math.Clamp(remainingPercent, 0, 100);
        int upperIndex = Array.FindIndex(UsageColors, stop => stop.Percent >= percent);
        var upper = UsageColors[upperIndex];
        var lower = UsageColors[Math.Max(0, upperIndex - 1)];
        var lowerColor = Rgb(dark ? lower.Dark : lower.Light);
        var upperColor = Rgb(dark ? upper.Dark : upper.Light);
        double progress = upperIndex == 0 ? 0 : (percent - lower.Percent) / (upper.Percent - lower.Percent);
        var color = Blend(lowerColor, upperColor, progress);
        WindowsColor[] targetColors =
        [
            Blend(color, Colors.White, dark ? .16 : .03),
            Blend(color, Colors.Black, dark ? .06 : .12)
        ];

        var transition = animate && systemSettings.AnimationsEnabled ? new Storyboard() : null;
        for (int i = 0; i < targetColors.Length; i++)
        {
            var stop = usageGradient.GradientStops[i];
            var previousColor = stop.Color;
            stop.Color = targetColors[i];
            if (transition is null || previousColor.Equals(targetColors[i])) continue;

            var fade = new ColorAnimation
            {
                From = previousColor,
                To = targetColors[i],
                Duration = new Duration(TimeSpan.FromMilliseconds(420)),
                FillBehavior = FillBehavior.Stop,
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(fade, stop);
            Storyboard.SetTargetProperty(fade, nameof(GradientStop.Color));
            transition.Children.Add(fade);
        }
        remaining.Foreground = usageGradient;
        if (transition is { Children.Count: > 0 })
        {
            usageColorTransition = transition;
            transition.Begin();
        }
    }

    private static WindowsColor Rgb(uint rgb) =>
        ColorHelper.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private static WindowsColor Blend(WindowsColor a, WindowsColor b, double amount) =>
        ColorHelper.FromArgb(255,
            (byte)(a.R * (1 - amount) + b.R * amount),
            (byte)(a.G * (1 - amount) + b.G * amount),
            (byte)(a.B * (1 - amount) + b.B * amount));

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
    private static TextBlock MetricHeading(string label, TextAlignment alignment) => new()
    {
        Text = label,
        FontSize = MetricHeadingSize,
        FontWeight = Microsoft.UI.Text.FontWeights.Normal,
        Foreground = SecondaryText,
        TextAlignment = alignment,
        TextWrapping = TextWrapping.NoWrap
    };

    private static TextBlock MetricValue(TextAlignment alignment) => new()
    {
        Text = "—",
        FontSize = MetricValueSize,
        FontFamily = MetricFont,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = ValueGradient,
        TextAlignment = alignment,
        TextWrapping = TextWrapping.NoWrap,
        TextTrimming = TextTrimming.CharacterEllipsis
    };

    private static Border InfoIcon(string name)
    {
        var icon = new Border
        {
            Width = 22,
            Height = 24,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Colors.Transparent),
            Child = new FontIcon { Glyph = "\uE946", FontSize = 16, Foreground = SecondaryText }
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(icon, name);
        return icon;
    }

    private static void AddMetric(Grid grid, int column, TextBlock heading, FrameworkElement value)
    {
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Grid.SetColumn(heading, column);
        grid.Children.Add(heading);
        Grid.SetColumn(value, column);
        Grid.SetRow(value, 1);
        grid.Children.Add(value);
    }
    private static void AddMetricLink(Grid parent, int column, Button link, TextBlock heading, FrameworkElement value)
    {
        var metric = new Grid { RowSpacing = 7 };
        AddMetric(metric, 0, heading, value);
        link.Content = metric;
        link.Padding = new(0);
        link.HorizontalAlignment = HorizontalAlignment.Stretch;
        link.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        link.Click += (_, _) => OpenLink(UsageUrl);
        Grid.SetColumn(link, column);
        parent.Children.Add(link);
    }
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
    private sealed class ToggleButtonCompat:Microsoft.UI.Xaml.Controls.Primitives.ToggleButton{}
}
