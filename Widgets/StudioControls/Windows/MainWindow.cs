using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Markup;
using System.Text.Json;
using VibeKit.StudioControls.Core;
using global::Windows.UI.ViewManagement;
using Color = global::Windows.UI.Color;

namespace VibeKit.StudioControls.Windows;

public sealed partial class MainWindow : Window
{
    private readonly NativeLocalization localization = new();
    private readonly ComboBox languageSelector = new() { Width = 168 };
    private readonly Grid root = new() { Padding = new(14), RowSpacing = 8 };
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim obsGate = new(1, 1);
    private readonly DispatcherTimer polling = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer effectsTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly DispatcherTimer cameraTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly UISettings systemSettings = new();
    private readonly Lazy<CameraController> camera = new(() => new CameraController());
    private DispatcherTimer? smokeTimer;
    private readonly PanelSettings settings;
    private ObsClient? client;
    private StudioService? service;
    private StudioStatus? state;
    private bool closed, syncing, commandBusy, rendering, cameraBusy, pollBusy, connecting, stopPending;
    private readonly Dictionary<string, Func<CancellationToken, Task>> pendingEffects = new();
    private readonly Dictionary<CameraProperty, int> pendingCamera = new();
    private IReadOnlyDictionary<CameraProperty, CameraValue> cameraValues = new Dictionary<CameraProperty, CameraValue>();
    private readonly Dictionary<string, Button> sceneButtons = new();
    private readonly Dictionary<string, FontIcon> sceneIcons = new();
    private readonly Dictionary<CameraProperty, Slider> cameraSliders = new();
    private readonly Dictionary<string, (ToggleSwitch Toggle, Slider Slider)> vignettes = new();
    private readonly Dictionary<string, TextBox> projectFields = new();
    private readonly Dictionary<string, Button> audioButtons = new();
    private readonly TextBlock status = Text("Not connected · open OBS, then choose Connect.");
    private readonly TextBlock notice = Text("");
    private readonly TextBlock cameraNotice = Text("Choose a camera to open its native controls. No preview is captured.");
    private readonly TextBlock projectNotice = Text("");
    private readonly Dictionary<string, ToggleButton> themeButtons = new(StringComparer.Ordinal);
    private readonly ComboBox cameras = new() { DisplayMemberPath = "Name", MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox host = new() { PlaceholderText = "127.0.0.1" };
    private readonly NumberBox port = new() { Minimum = 1, Maximum = 65535, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly PasswordBox password = new() { PasswordRevealMode = PasswordRevealMode.Hidden };
    private readonly Button connect = new() { Content = "Connect" };
    private readonly Button disconnect = new() { Content = "Disconnect", IsEnabled = false };
    private readonly Button record = new() { Content = "Start recording", IsEnabled = false };
    private readonly Button stop = new() { Content = "■ Stop and save", IsEnabled = false };
    private readonly Button pause = new() { Content = "Pause", IsEnabled = false };
    private readonly Button apply = new() { Content = "Save project & apply to OBS" };
    private readonly ToggleSwitch blur = new() { IsEnabled = false };
    private readonly Slider blurIntensity = Slider(1, 20, 3, "Blur intensity");
    private readonly Slider screenZoom = Slider(1, 4, 1, "Screen zoom", .01);
    private readonly Slider screenX = Slider(-100, 100, 0, "Horizontal position", 1);
    private readonly Slider screenY = Slider(-100, 100, 0, "Vertical position", 1);
    private readonly ToggleSwitch vibe = new();
    private readonly ToggleSwitch sound = new();
    private readonly Slider soundVolume = Slider(0, 100, 18, "Intro sound volume");
    private readonly TextBox logo = new() { IsReadOnly = true, PlaceholderText = "Choose a local PNG, JPG or BMP" };

    public MainWindow()
    {
        settings = LocalSettings.Load(out string? settingsWarning);
        localization.Language = settings.Language;
        root.Language = localization.Language;
        Title = "Studio Controls · Umberto’s Lab";
        AppWindow.SetIcon(IconPath);
        AppWindow.Resize(new(1120, 940));
        Content = root;
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        BuildHeader();
        LoadWorkflowLibrary();
        BuildNavigation();
        BuildTransport();
        host.Text = settings.Host;
        port.Value = settings.Port;
        try { password.Password = LocalSettings.ReadPassword(settings); }
        catch (Exception error) when (error is System.Security.Cryptography.CryptographicException or FormatException)
        { Diagnostics.Record("credentials.read", error); notice.Text = "Re-enter the OBS password. It is protected for the Windows user who saved it."; }
        SelectTheme(settings.Theme, persist: false);
        languageSelector.ItemTemplate = (DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <StackPanel Orientation="Horizontal" Spacing="8" VerticalAlignment="Center">
                    <Image Source="{Binding Flag}" Width="22" Height="16" Stretch="Uniform" />
                    <TextBlock Text="{Binding Label}" VerticalAlignment="Center" />
                </StackPanel>
            </DataTemplate>
            """);
        var languages = StudioTranslations.Languages.Select(language => new WindowsLanguageChoice(language)).ToArray();
        languageSelector.ItemsSource = languages;
        languageSelector.SelectedItem = languages.First(item => item.Code == localization.Language);
        languageSelector.SelectionChanged += (_, _) =>
        {
            if (languageSelector.SelectedItem is not WindowsLanguageChoice selected) return;
            settings.Language = selected.Code;
            root.Language = selected.Code;
            localization.Language = selected.Code;
            timerLocalization.Language = selected.Code;
            if (timerWindow is { Content: FrameworkElement body } timer)
            {
                timer.Title = localization.Text("Timer");
                body.Language = selected.Code;
            }
            RefreshPlayback();
            RefreshWorkflowEditor();
            SavePreferences();
        };
        systemSettings.ColorValuesChanged += SystemThemeChanged;
        if (settingsWarning is not null) notice.Text = settingsWarning;
        LoadProject();
        RefreshWorkflowEditor();
        polling.Tick += async (_, _) => await PollAsync();
        effectsTimer.Tick += async (_, _) => await FlushEffectsAsync();
        cameraTimer.Tick += async (_, _) => await FlushCameraAsync();
        localization.Attach(root);
        InitializeDeck();
        Closed += (_, _) => Shutdown();
    }

    private void BuildHeader()
    {
        var header = new Grid();
        header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.ColumnSpacing = 12;
        var language = WithTrailing(FieldLabel(languageSelector, "Language",
            "Choose the language for labels and help. Project text stays as entered."), languageSelector);
        language.ColumnSpacing = 5;
        var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right };
        header.Children.Add(new TextBlock { Text = "Studio Controls", FontSize = 24, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.Bold });
        var appearance = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4,
            VerticalAlignment = VerticalAlignment.Bottom };
        AutomationProperties.SetName(appearance, "Appearance");
        foreach (var (name, glyph) in new[] { ("Light", "\uE706"), ("Dark", "\uE708"), ("System", "\uE7F4") })
        {
            var button = new ToggleButton
            {
                Width = 38,
                Height = 32,
                Padding = new(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Content = new FontIcon { FontFamily = new FontFamily("Segoe Fluent Icons"), Glyph = glyph, FontSize = 16 },
            };
            AutomationProperties.SetName(button, name);
            ActionHelp(button, name);
            button.Click += (_, _) => SelectTheme(name);
            themeButtons.Add(name, button);
            appearance.Children.Add(button);
        }
        options.Children.Add(appearance);
        options.Children.Add(language);
        Grid.SetColumn(options, 1);
        header.Children.Add(options);
        header.RowDefinitions.Add(new() { Height = GridLength.Auto });
        header.RowDefinitions.Add(new() { Height = GridLength.Auto });
        header.SizeChanged += (_, e) =>
        {
            bool compact = e.NewSize.Width < 660;
            options.Orientation = e.NewSize.Width < 415 ? Orientation.Vertical : Orientation.Horizontal;
            Grid.SetRow(options, compact ? 1 : 0);
            Grid.SetColumn(options, compact ? 0 : 1);
            options.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        };
        root.Children.Add(header);
    }

    private FrameworkElement BuildConnection()
    {
        var endpoint = ResponsiveColumns(370,
            Field(host, "OBS host", "Enter the address of the computer running OBS. Use 127.0.0.1 when OBS is on this computer."),
            Field(port, "WebSocket port", "Use the port shown in OBS under Tools → WebSocket Server Settings. The default is 4455."));
        var credential = Field(password, "OBS WebSocket password", "Enter the server password from OBS. Windows protects the saved password for your account.");
        var session = new ActionStrip();
        ActionHelp(connect, "Connect");
        connect.Click += async (_, _) => await ConnectAsync();
        connect.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        session.Children.Add(connect);
        ActionHelp(disconnect, "Disconnect");
        disconnect.Click += async (_, _) =>
        {
            disconnect.IsEnabled = false;
            try { await DisconnectAsync(); }
            catch (Exception error) { Fail("obs.disconnect", error); }
            finally { if (!closed) UpdateEnabled(service is not null && state?.ConnectionAvailable == true); }
        };
        session.Children.Add(disconnect);
        var connection = Card(Heading("OBS connection"), Text("Enable the WebSocket server in OBS before connecting."), endpoint, credential, session);
        var actions = new ActionStrip();
        actions.Children.Add(ActionButton("Export diagnostics…", ExportAsync));
        actions.Children.Add(ActionButton("About", () =>
        {
            ShowAbout();
            return Task.CompletedTask;
        }));
        var tools = Card(Heading("Scene collection"), Text("Create a new JSON scene collection to import into OBS."),
            ActionButton("Prepare OBS scene collection…", PrepareCollectionAsync), Heading("Support"), actions);
        return ResponsiveColumns(740, connection, tools);
    }

    private readonly NavigationView navigation = new()
    {
        PaneDisplayMode = NavigationViewPaneDisplayMode.Top, IsSettingsVisible = false,
        IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed, AlwaysShowHeader = false,
        IsPaneToggleButtonVisible = false
    };
    private NavigationViewItem workflowPage = null!;

    private void BuildNavigation()
    {
        var connectionPage = Page("OBS", "\uE713", "OBS connection & setup", BuildConnection());
        var cameraPage = Page("Camera", "\uE714", "Camera & recording", BuildCameraAndScenes());
        var screenPage = Page("Screen", "\uE7F4", "Screen & vignettes", BuildEffects());
        var projectPage = Page("Project", "\uE8B7", "Project & cards", BuildProject());
        workflowPage = Page("Workflow editor", "\uE8FD", "Workflow", BuildWorkflow());
        foreach (var page in new[] { connectionPage, workflowPage, cameraPage, screenPage, projectPage })
            navigation.MenuItems.Add(page);
        navigation.SelectionChanged += (_, args) =>
        {
            if (args.SelectedItem is NavigationViewItem item) navigation.Content = item.Tag;
        };
        navigation.SelectedItem = connectionPage;
        navigation.Content = connectionPage.Tag;
        Grid.SetRow(navigation, 1);
        root.Children.Add(navigation);
    }

    private void BuildTransport()
    {
        var transport = Column(8);
        transport.Children.Add(status);
        transport.Children.Add(notice);
        notice.Visibility = Visibility.Collapsed;
        notice.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) =>
            notice.Visibility = string.IsNullOrEmpty(notice.Text) ? Visibility.Collapsed : Visibility.Visible);
        var buttons = new ActionStrip();
        ActionHelp(record, "Start recording");
        ActionHelp(stop, "■ Stop and save");
        ActionHelp(pause, "Pause recording");
        record.Click += async (_, _) => await ManualRecordAsync();
        stop.Click += async (_, _) => await StopRecordingAsync();
        pause.Click += async (_, _) =>
        {
            bool hold = !(state?.IsPaused ?? false);
            bool takeover = hold && LiveCheckActive;
            if (hold) InterruptLivePlayback("Recording interrupted.");
            await CommandAsync("record.pause", token => service!.SetPausedAsync(hold, token), takeover);
        };
        buttons.Children.Add(record); buttons.Children.Add(stop); buttons.Children.Add(pause);
        foreach (string input in new[] { "Mic/Aux", "Desktop Audio" })
        {
            var button = new Button { Content = input + " · offline", IsEnabled = false };
            button.Click += async (_, _) => await CommandAsync("audio.mute", token => service!.SetAudioMutedAsync(input, !(state?.AudioMutes.GetValueOrDefault(input) ?? false), token));
            ActionHelp(button, input);
            audioButtons[input] = button; buttons.Children.Add(button);
        }
        transport.Children.Add(buttons);
        Grid.SetRow(transport, 2); root.Children.Add(transport);
    }

    private static NavigationViewItem Page(string title, string glyph, string description, UIElement content)
    {
        var page = new NavigationViewItem
        {
            Content = title, Icon = new FontIcon { Glyph = glyph, FontSize = 16 },
            Tag = new ScrollViewer
            {
                Content = new Border { Padding = new(8, 12, 8, 8), Child = content },
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollMode = ScrollMode.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            }
        };
        Help(page, description);
        return page;
    }

    private static StackPanel Column(double spacing = 12) => new() { Spacing = spacing };
    private static StackPanel Row() => new() { Orientation = Orientation.Horizontal, Spacing = 10 };
    private static Grid Columns(int count)
    {
        var grid = new Grid { ColumnSpacing = 24 };
        for (int index = 0; index < count; index++) grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        return grid;
    }
    private static TextBlock Text(string value) => new() { Text = value, TextWrapping = TextWrapping.Wrap };
    private static TextBlock Heading(string value) => new() { Text = value, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private static Slider Slider(double minimum, double maximum, double value, string name, double step = 1)
    {
        var slider = new Slider { Minimum = minimum, Maximum = maximum, Value = value, StepFrequency = step, IsEnabled = false };
        AutomationProperties.SetName(slider, name);
        return slider;
    }
    private Button ActionButton(string label, Func<Task> action)
    {
        var button = new Button { Content = label };
        ActionHelp(button, label);
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await action(); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception error) { Fail("action", error); }
            finally { if (!closed) button.IsEnabled = true; }
        };
        return button;
    }

    private void SelectTheme(string name, bool persist = true)
    {
        foreach (var (option, button) in themeButtons) button.IsChecked = option == name;
        settings.Theme = name;
        ApplyTheme();
        if (persist) SavePreferences();
    }

    private void SavePreferences()
    {
        try { LocalSettings.Save(settings); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Fail("settings.save", error); }
    }

    private void SystemThemeChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(() => { if (!closed && settings.Theme == "System") ApplyTheme(); });

    private void ApplyTheme()
    {
        root.RequestedTheme = settings.Theme switch { "Dark" => ElementTheme.Dark, "Light" => ElementTheme.Light, _ => ElementTheme.Default };
        if (timerWindow?.Content is FrameworkElement timerContent) timerContent.RequestedTheme = root.RequestedTheme;
        Color background = systemSettings.GetColorValue(UIColorType.Background);
        bool dark = settings.Theme == "Dark" || (settings.Theme == "System" && background.R + background.G + background.B < 384);
        root.Background = Brush(dark ? 0x171A1Eu : 0xF5F2EEu);
        AppWindow.TitleBar.ButtonBackgroundColor = ToColor(dark ? 0x20242Au : 0xECE7E1u);
        AppWindow.TitleBar.ButtonForegroundColor = ToColor(dark ? 0xEEE9E2u : 0x292622u);
        foreach (var scene in SceneStyles)
        {
            if (!sceneIcons.TryGetValue(scene.Name, out var icon)) continue;
            bool selected = state?.SceneName == scene.Name;
            icon.Foreground = Brush(selected || !dark ? scene.Light : scene.Dark);
            sceneButtons[scene.Name].Background = Brush(selected ? 0xEBA295u : dark ? 0x292E35u : 0xFFFDF9u);
            sceneButtons[scene.Name].Foreground = Brush(selected ? 0x29201Cu : dark ? 0xEEE9E2u : 0x292622u);
        }
    }

    private static Color ToColor(uint rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    private static SolidColorBrush Brush(uint rgb) => new(ToColor(rgb));
    private void Fail(string operation, Exception error)
    {
        Diagnostics.Record(operation, error);
        if (!closed) notice.Text = "Action could not be confirmed: " + error.Message;
    }

    internal void RunUiSmoke()
    {
        if (!UiSmoke.Enabled) throw new InvalidOperationException("UI smoke requires its isolated launch mode.");
        foreach (double invalidPort in new[] { double.NaN, 0, 65536, 1.5 })
        {
            bool rejected = false;
            try { ParseObsPort(invalidPort); }
            catch (ArgumentException) { rejected = true; }
            if (!rejected) throw new InvalidOperationException("Invalid connection input was accepted.");
        }
        int stage = 0;
        smokeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        smokeTimer.Tick += async (_, _) =>
        {
            try
            {
                if (root.XamlRoot is null) throw new InvalidOperationException("The native window did not load its XAML root.");
                if (camera.IsValueCreated || client is not null || service is not null)
                    throw new InvalidOperationException("The startup test unexpectedly initialized an external control connection.");
                string[] choices = ["Light", "Dark", "System"];
                if (stage < choices.Length)
                {
                    SelectTheme(choices[stage++]);
                    ElementTheme expected = settings.Theme switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
                    if (root.RequestedTheme != expected || LocalSettings.Load(out _).Theme != settings.Theme)
                        throw new InvalidOperationException("Appearance did not apply or persist.");
                    return;
                }
                int languageIndex = stage - choices.Length;
                if (languageIndex < StudioTranslations.Languages.Count)
                {
                    var projectText = projectFields.ToDictionary(pair => pair.Key, pair => pair.Value.Text);
                    languageSelector.SelectedIndex = languageIndex;
                    string code = StudioTranslations.Languages[languageIndex].Code;
                    if (localization.Language != code || LocalSettings.Load(out _).Language != code ||
                        !Equals(record.Content, localization.Text("Start recording")) ||
                        !Equals(connect.Content, localization.Text("Connect")) || projectFields.Any(pair => pair.Value.Text != projectText[pair.Key]))
                        throw new InvalidOperationException("Native language selection did not apply or preserve project data.");
                    stage++;
                    return;
                }
                smokeTimer.Stop();
                await VerifyDiscoverabilityUiAsync();
                await VerifyWorkflowUiAsync();
                UiSmoke.Complete();
            }
            catch (Exception error) { UiSmoke.Fail(error); }
            smokeTimer.Stop();
            Close();
        };
        smokeTimer.Start();
    }
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class WindowsLanguageChoice
{
    public string Code { get; }
    public string Label { get; }
    public SvgImageSource Flag { get; }

    internal WindowsLanguageChoice(UiLanguage language)
    {
        Code = language.Code;
        Label = language.Label;
        string country = language.Code switch
        {
            "en" => "gb",
            "vi" => "vn",
            "ko" => "kr",
            "zh" => "cn",
            "ja" => "jp",
            _ => language.Code,
        };
        Flag = new SvgImageSource(new Uri($"ms-appx:///Flags/{country}.svg"));
    }
}
