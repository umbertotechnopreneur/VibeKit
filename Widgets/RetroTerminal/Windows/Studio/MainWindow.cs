using System.Globalization;
using System.IO;
using System.Security;
using Microsoft.Win32;
using System.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Serilog;
using Tools.Widgets;
using Windows.UI;
using static Tools.Widgets.EffectStudioLayout;

namespace RetroStudio;
internal sealed class MainWindow : Window
{
    private readonly nint engine;
    private readonly StudioPages pages;
    private readonly int builtInPresetCount = Native.RetroBuiltInPresetCount();
    private readonly Grid root = new();
    private readonly Button tryButton = new();
    private readonly ToggleSwitch effectSwitch = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button cancelRecovery = new() { Content = "Cancel automatic restart", Visibility = Visibility.Collapsed };
    private bool syncingEffect;
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Grid diagnosticMetrics = new() { ColumnSpacing = 12, RowSpacing = 6 };
    private string diagnosticStatus = "";
    private readonly TextBlock effectState = new(), feedback = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly Slider[] sliders = new Slider[18];
    private readonly ComboBox palette = new(), mask = new(), preset = new();
    private string targetDisplay = "";
    private readonly ToggleSwitch persistence = new(), random = new();
    private readonly CheckBox[] types = new CheckBox[4];
    private readonly ColorPicker color = new() { IsAlphaEnabled = false, IsAlphaSliderVisible = false, IsAlphaTextInputVisible = false };
    private FrameworkElement colorSection = null!;
    private bool syncing, closing, closePrompt, systemCloseQueued, saveFailed, inTray;
    private string? loggedStatus;
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer statusTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    // Index order is the native settings ABI; presentation order is independent.
    private static readonly (string label, double min, double max, double step, string format)[] Specs = {
        ("Effect mix",0,1,.01,"0.00"), ("Scanline strength",0,.6,.01,"0.00"), ("Scanline spacing · px",2,12,.1,"0.0"), ("Scanline width · px",.25,6,.05,"0.00"),
        ("Phosphor glow",0,.8,.01,"0.00"), ("Glow radius · px",.5,8,.1,"0.0"), ("Darkened edges",0,.6,.01,"0.00"), ("Static grain",0,.1,.001,"0.000"),
        ("Mask strength",0,.5,.01,"0.00"), ("Black level",-.2,.2,.01,"0.00"), ("Contrast",.5,2,.01,"0.00"), ("Afterglow half-life · ms",40,500,1,"0"),
        ("Afterglow mix",0,.6,.01,"0.00"), ("Minimum interval · s",5,300,1,"0"), ("Maximum interval · s",5,300,1,"0"),
        ("Minimum duration · ms",80,1200,1,"0"), ("Maximum duration · ms",80,1200,1,"0"), ("Glitch intensity",0,1,.01,"0.00")
    };
    private static Style GetStyle(string key) => (Style)Application.Current.Resources[key];
    private static TextBlock Text(string text, int size=14, bool secondary=false)
    {
        var label = new TextBlock { Text=text, FontSize=size, TextWrapping=TextWrapping.Wrap };
        if (secondary) label.Style = GetStyle("SecondaryTextStyle");
        return label;
    }
    private static StackPanel Stack(double spacing=12) => new() { Spacing=spacing };
    private void Notice(string message) { feedback.Text=message; feedback.Visibility=Visibility.Visible; }

    public MainWindow()
    {
        Title="VibeWare – Retro Terminal";
        var initialTheme = ReadPreference("WindowTheme", 0, 0, 2);
        ApplyTheme(initialTheme);
        Initialize(this, root, "Retro Terminal", "CRT color, texture and motion for your desktop.");
        engine=Native.RetroCreate(WinRT.Interop.WindowNative.GetWindowHandle(this));
        if(engine==0) throw new InvalidOperationException("Retro Terminal could not initialize safely.");
        targetDisplay = ReadPreferenceValue("TargetDisplay") as string ?? "";
        effectSwitch.Toggled += (_, _) =>
        {
            if (syncingEffect) return;
            if (effectSwitch.IsOn)
            {
                var display = DisplayTarget.Resolve(targetDisplay);
                Native.RetroStart(engine, display?.Handle ?? 0);
            }
            else Native.RetroStop(engine);
            RefreshStatus();
        };
        cancelRecovery.Click += (_, _) =>
        {
            Native.RetroStop(engine);
            RefreshStatus();
        };
        var statusBar = StatusBar(effectState, effectSwitch, cancelRecovery);
        root.Children.Add(statusBar);

        var effect = Stack(12);
        effect.Children.Add(Text("Effect", 26));
        var presets = Stack(12);
        presets.Children.Add(Text("Presets", 26));
        var settings = SettingsPage();
        var diagnostics = Stack(12);
        diagnostics.Children.Add(Text("Diagnostics", 26));
        var jokeSection = JokeSection(this, ReadPreference("ShowGpuJokes", 1, 0, 1) == 1, enabled =>
        {
            if (Native.RetroSetGpuJokes(engine, enabled ? 1 : 0) == 0)
                Notice("Could not save the GPU jokes preference.");
        }, Native.GpuJoke);
        var target = ApplyToPicker();
        target.SelectedIndex = ReadPreference("EffectTarget", 0, 0, 2);
        Native.RetroSetTarget(engine, target.SelectedIndex);
        target.SelectionChanged += (_, _) =>
        {
            if (target.SelectedIndex < 0) return;
            Native.RetroSetTarget(engine, target.SelectedIndex);
            SavePreference("EffectTarget", target.SelectedIndex);
        };
        effect.Children.Add(target);
        preset.Header="Preset"; preset.HorizontalAlignment=HorizontalAlignment.Stretch;
        preset.Items.Add("Custom");
        for (int index = 0; index < builtInPresetCount; index++) preset.Items.Add(Native.BuiltInPresetName(index));
        for (int slot = 1; slot <= 3; slot++) preset.Items.Add($"My preset {slot}");
        preset.SelectedIndex=0;
        preset.SelectionChanged+=(_,_)=> {
            if(syncing || preset.SelectedIndex<=0) return;
            if(!TryApplyPreset(preset.SelectedIndex)) { Notice("This personal preset is empty. Save it in Presets first."); SetCustom(); }
            else { Refresh(); Save(); }
        };
        effect.Children.Add(preset); effect.Children.Add(Text("Changes apply immediately. Save a personal preset to keep a favorite combination.",12,secondary:true));

        var phosphor=Stack(16);
        palette.Header="Palette"; palette.HorizontalAlignment=HorizontalAlignment.Stretch;
        foreach(var name in new[]{"Green phosphor","Amber terminal","Original color","Custom phosphor"}) palette.Items.Add(name);
        palette.SelectionChanged+=(_,_)=> { if(syncing)return; Native.RetroSetPalette(engine,palette.SelectedIndex); Changed(); };
        phosphor.Children.Add(palette);
        colorSection=Section("Custom phosphor color","Choose the tint for the custom palette.",color); phosphor.Children.Add(colorSection);
        color.ColorChanged+=(_,args)=> { if(syncing)return; Native.RetroSetColor(engine,0,args.NewColor.R/255f); Native.RetroSetColor(engine,1,args.NewColor.G/255f); Native.RetroSetColor(engine,2,args.NewColor.B/255f); Changed(); };
        AddSliders(phosphor,0,9,10); effect.Children.Add(Section("Phosphor & color","Palette, effect mix and picture balance",phosphor));

        var surface=Stack(16); AddSliders(surface,1,2,3,4,5);
        mask.Header="Phosphor mask"; mask.HorizontalAlignment=HorizontalAlignment.Stretch;
        foreach(var name in new[]{"None","RGB stripes","Phosphor dots"})mask.Items.Add(name);
        mask.SelectionChanged+=(_,_)=> { if(syncing)return; Native.RetroSetMask(engine,mask.SelectedIndex); Changed(); };
        surface.Children.Add(mask); AddSliders(surface,8,6,7);
        effect.Children.Add(Section("Monitor surface","Scanlines, glow, phosphor mask and texture",surface));

        var afterglow=Stack(16); afterglow.Children.Add(Text("A fading trail behind moving content. Uses extra GPU memory.",12,secondary:true)); AddSliders(afterglow,11,12);
        persistence.Toggled+=(_,_)=>SetPersistence();
        effect.Children.Add(Section("Phosphor persistence","Temporal afterglow",afterglow,persistence));

        var glitches=Stack(14); var allowed=Stack(4);
        for(int i=0;i<4;i++) {
            types[i]=new CheckBox { Content=new[]{"RGB misalignment","Horizontal tear","Local line noise","Soft scan band"}[i] };
            types[i].Checked+=(_,_)=>SetTypes(); types[i].Unchecked+=(_,_)=>SetTypes(); allowed.Children.Add(types[i]);
        }
        glitches.Children.Add(allowed); AddSliders(glitches,17,15,16);
        tryButton.Content="Try a glitch"; tryButton.Click+=(_,_)=> { if(Native.RetroTryGlitch(engine)==0) Notice("A glitch is already playing or resting. Wait two seconds and try again."); else Notice("Glitch preview requested."); };
        glitches.Children.Add(tryButton); glitches.Children.Add(Text("Turn on CRT to preview. Episodes never overlap and have at least two seconds of rest.",12,secondary:true));
        effect.Children.Add(Section("Signal glitches","Allowed distortions, duration and manual preview",glitches));
        var timing=Stack(16); timing.Children.Add(Text("Plays the allowed signal glitches at random intervals. Manual preview also works when this is off.",12,secondary:true)); AddSliders(timing,13,14);
        random.Toggled+=(_,_)=>SetRandom();
        effect.Children.Add(Section("Automatic glitches","Random playback and timing",timing,random));

        presets.Children.Add(Text("Personal presets",18)); presets.Children.Add(Text("Save stores the current settings in that slot, replacing its previous look.",13,secondary:true));
        for(int i=1;i<=3;i++) {
            int slot=i; var actions=new StackPanel { Orientation=Orientation.Horizontal, Spacing=8 };
            var apply=new Button { Content="Apply" }; apply.Click+=(_,_)=>ApplyPreset(builtInPresetCount + slot);
            var save=new Button { Content="Save current" }; save.Click+=(_,_)=> { if(Native.RetroSavePreset(engine,slot)==0) Notice("Could not save the personal preset."); else { syncing=true; preset.SelectedIndex=builtInPresetCount + slot; syncing=false; Notice("Saved to My preset "+slot+"."); } };
            actions.Children.Add(apply); actions.Children.Add(save); presets.Children.Add(Card(Columns(Text("My preset "+slot,15),actions)));
        }
        presets.Children.Add(Text("Built-in looks", 18));
        presets.Children.Add(Text("Apply a starting point, then adjust it in Effect.", 13, secondary: true));
        for (int index = 1; index <= builtInPresetCount; index++)
        {
            int menuIndex = index;
            var apply = new Button { Content = "Apply", VerticalAlignment = VerticalAlignment.Center };
            apply.Click += (_, _) => ApplyPreset(menuIndex);
            presets.Children.Add(Card(Columns(Text(Native.BuiltInPresetName(index - 1), 15), apply)));
        }
        var theme = new ComboBox { ItemsSource = new[] { "System", "Light", "Dark" }, SelectedIndex = initialTheme };
        theme.SelectionChanged += (_, _) =>
        {
            if (theme.SelectedIndex < 0) return;
            ApplyTheme(theme.SelectedIndex);
            SavePreference("WindowTheme", theme.SelectedIndex);
        };
        var startup = new ToggleSwitch();
        var startupStatus = SettingsHint("");
        bool syncingStartup = false;
        void UpdateStartup(bool? requested)
        {
            syncingStartup = true;
            try
            {
                if (requested.HasValue) App.SignIn.SetEnabled(requested.Value);
                var state = App.SignIn.Read();
                startup.IsOn = state.Enabled;
                startup.IsEnabled = !state.Blocked;
                startupStatus.Text = state.Blocked
                    ? "Disabled in Windows Startup Apps. Re-enable it there to allow startup."
                    : "Open in tray at sign-in. Start the effect manually.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException)
            {
                startup.IsOn = requested == false;
                startup.IsEnabled = requested.HasValue;
                startupStatus.Text = "Could not update the Windows startup setting.";
                Log.Warning("Windows startup setting unavailable Type={Type}", ex.GetType().Name);
            }
            finally { syncingStartup = false; }
        }
        UpdateStartup(null);
        startup.Toggled += (_, _) =>
        {
            if (!syncingStartup) UpdateStartup(startup.IsOn);
        };
        var startupRow = PreferenceRow("Start with Windows", startupStatus, startup);

        var displayPicker = new DisplayTargetPicker(() => targetDisplay, device =>
        {
            targetDisplay = device;
            SavePreference("TargetDisplay", targetDisplay);
        }, compact: true);
        settings.Children.Add(RowGroup("Display & appearance",
            PreferenceRow("Effect", "Enable or disable the retro terminal effect.", effectSwitch),
            PreferenceRow("Theme", "App theme.", theme),
            PreferenceRow("Target display", "Control window's display for the effect.", displayPicker)));
        var autoRestart = new ToggleSwitch
        {
            IsOn = ReadPreference("AutoRestart", 1, 0, 1) == 1
        };
        var recoveryDelay = new NumberBox
        {
            Minimum = 30, Maximum = 300, MinWidth = 112,
            SmallChange = 10, LargeChange = 30, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Value = ReadPreference("RecoveryDelaySeconds", 60, 30, 300),
            IsEnabled = autoRestart.IsOn
        };
        void ApplyRecovery() => Native.RetroSetRecovery(engine, autoRestart.IsOn ? 1 : 0, (int)recoveryDelay.Value);
        autoRestart.Toggled += (_, _) =>
        {
            recoveryDelay.IsEnabled = autoRestart.IsOn;
            ApplyRecovery();
            SavePreference("AutoRestart", autoRestart.IsOn ? 1 : 0);
            RefreshStatus();
        };
        recoveryDelay.ValueChanged += (_, args) =>
        {
            if (!double.IsFinite(args.NewValue))
            {
                recoveryDelay.Value = double.IsFinite(args.OldValue) ? args.OldValue : 60;
                return;
            }
            var seconds = (int)Math.Round(args.NewValue);
            if (args.NewValue != seconds) { recoveryDelay.Value = seconds; return; }
            ApplyRecovery();
            SavePreference("RecoveryDelaySeconds", seconds);
        };
        ApplyRecovery();
        settings.Children.Add(RowGroup("Startup", startupRow,
            PreferenceRow("Resume automatically", "Resume the effect automatically after a temporary stop.", autoRestart)));
        var recoveryValue = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
        };
        recoveryDelay.Width = 112;
        recoveryValue.Children.Add(recoveryDelay);
        recoveryValue.Children.Add(new TextBlock { Text = "s", VerticalAlignment = VerticalAlignment.Center });
        AutomationProperties.SetName(recoveryDelay, "Minimum recovery delay in seconds");
        var recoveryRow = Stack(0);
        recoveryRow.Children.Add(PreferenceRow("Minimum recovery delay", "Delay before automatically resuming the effect.", recoveryValue));
        var recoveryHint = SettingsHint("Requires 30 seconds of stability. Up to 2 retries per manual start.");
        recoveryHint.Margin = new Thickness(16, 0, 16, 12);
        recoveryRow.Children.Add(recoveryHint);
        settings.Children.Add(RowGroup("Performance protection", recoveryRow));
        diagnostics.Children.Add(Text("Live renderer status", 18));
        for (int i = 0; i < 4; i++) diagnosticMetrics.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var statusContent = Stack(10); statusContent.Children.Add(status); statusContent.Children.Add(diagnosticMetrics);
        diagnostics.Children.Add(Card(statusContent));
        diagnostics.Children.Add(Text("Presentation counts and CPU submission time describe the renderer. GPU frame time is not measured.", 13, secondary: true));
        diagnostics.Children.Add(Text("CRT pauses if the desktop stops responding or rendering stays slow.", 13, secondary: true));
        diagnostics.Children.Add(jokeSection);

        pages = new StudioPages(
            new StudioPage("Effect", "\uE790", effect),
            new StudioPage("Presets", "\uE8F1", presets),
            new StudioPage("Settings", "\uE713", settings, displayPicker.Refresh),
            new StudioPage("Diagnostics", "\uE9D9", diagnostics));
        pages.Select("Settings");
        foreach (var scroll in pages.Scrollers)
            ScrollBarBehavior.HoverScrollBar(scroll, scroll, root);
        Grid.SetRow(pages, 1);
        root.Children.Add(pages);

        feedback.Style = GetStyle("SecondaryTextStyle");
        feedback.Margin = new Thickness(24, 8, 24, 4);
        var footer = Stack(0);
        footer.Children.Add(feedback);
        var emergencyLabel = Text("Emergency shortcut: Ctrl + Alt + F11", 12);
        var emergency = new Grid { ColumnSpacing = 8 };
        emergency.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        emergency.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        emergency.Children.Add(new FontIcon { Glyph = "\uE765", FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(emergencyLabel, 1);
        emergency.Children.Add(emergencyLabel);
        footer.Children.Add(FooterBar(emergency, WidgetFooter.CreateCompact("Retro Terminal", async () => await ShowAboutAsync())));
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); Save(); };
        Refresh();
        RefreshStatus();
        statusTimer.Tick += PollStatus;
        statusTimer.Start();
        AppWindow.Closing+=OnClosing;
    }

    private const string PreferencesPath = @"Software\VibeWare\RetroTerminal";

    private static int ReadPreference(string name, int fallback, int minimum, int maximum) =>
        ReadPreferenceValue(name) is int value && value >= minimum && value <= maximum ? value : fallback;

    private static object? ReadPreferenceValue(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PreferencesPath);
            return key?.GetValue(name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }

    private void ApplyTheme(int index) => root.RequestedTheme = index switch
    {
        1 => ElementTheme.Light,
        2 => ElementTheme.Dark,
        _ => ElementTheme.Default
    };

    private void SavePreference(string name, object value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(PreferencesPath);
            key.SetValue(name, value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            Notice("Could not save this preference.");
        }
    }

    private async Task ShowAboutAsync()
    {
        if (closePrompt || closing) return;
        closePrompt = true;
        try
        {
            var content = Stack(12);
            content.Children.Add(Text("A CRT effect for your desktop.", 15));
            content.Children.Add(DiagnosticAboutActions.Create("RetroTerminal", "Retro Terminal"));
            await new ContentDialog
            {
                XamlRoot = root.XamlRoot, RequestedTheme = root.ActualTheme,
                Title = "About Retro Terminal", Content = content, CloseButtonText = "Close"
            }.ShowAsync();
        }
        finally { closePrompt = false; }
    }

    private void AddSliders(Panel parent,params int[] indices)
    {
        foreach(int index in indices) {
            var spec=Specs[index]; var group=Stack(2); var value=Text("",12,secondary:true); value.VerticalAlignment=VerticalAlignment.Center;
            group.Children.Add(Columns(Text(spec.label,13),value));
            var slider=new Slider { Minimum=spec.min, Maximum=spec.max, StepFrequency=spec.step, HorizontalAlignment=HorizontalAlignment.Stretch };
            AutomationProperties.SetName(slider,spec.label); sliders[index]=slider;
            value.Text=slider.Value.ToString(spec.format,CultureInfo.CurrentCulture);
            slider.ValueChanged+=(_,_)=> { value.Text=slider.Value.ToString(spec.format,CultureInfo.CurrentCulture); if(syncing)return; Native.RetroSetSetting(engine,index,(float)slider.Value); Changed(); };
            group.Children.Add(slider); parent.Children.Add(group);
        }
    }
    private void SetCustom() { syncing=true; preset.SelectedIndex=0; syncing=false; }
    private void Changed() { SetCustom(); Refresh(); saveTimer.Stop(); saveTimer.Start(); }
    private void SetPersistence() { if(syncing)return; Native.RetroSetPersistence(engine,persistence.IsOn?1:0); Changed(); }
    private void SetRandom() { if(syncing)return; Native.RetroSetRandom(engine,random.IsOn?1:0); Changed(); }
    private void SetTypes() { if(syncing)return; int bits=0; for(int i=0;i<4;i++) if(types[i].IsChecked==true)bits|=1<<i; Native.RetroSetGlitchTypes(engine,bits); Changed(); }
    private bool TryApplyPreset(int menuIndex) => menuIndex <= builtInPresetCount
        ? Native.RetroApplyBuiltInPreset(engine, menuIndex - 1) != 0
        : Native.RetroPreset(engine, menuIndex - builtInPresetCount + 3) != 0;

    private void ApplyPreset(int index)
    {
        if(!TryApplyPreset(index)) { Notice("This personal preset is empty. Save the current settings to it first."); return; }
        syncing=true; preset.SelectedIndex=index; syncing=false; Refresh(); Save();
        // Applying a look keeps the current section, viewport and keyboard focus in place.
    }
    private const string SaveFailure="Could not save preferences. Your current values remain in this window.";
    private void Save()
    {
        saveFailed=Native.RetroSave(engine)==0;
        if(saveFailed) Notice(SaveFailure);
        else if(feedback.Text==SaveFailure) { feedback.Text=""; feedback.Visibility=Visibility.Collapsed; }
    }
    private void UpdateDependencies()
    {
        colorSection.Visibility=palette.SelectedIndex==3?Visibility.Visible:Visibility.Collapsed;
        sliders[8].IsEnabled=mask.SelectedIndex>0;
        sliders[11].IsEnabled=sliders[12].IsEnabled=persistence.IsOn;
        sliders[13].IsEnabled=sliders[14].IsEnabled=random.IsOn;
        tryButton.IsEnabled=Native.RetroRunning(engine)!=0 && types.Any(t=>t.IsChecked==true) && sliders[0].Value>0 && sliders[17].Value>0;
    }
    private void RefreshStatus()
    {
        bool running=Native.RetroRunning(engine)!=0;
        bool pending = Native.RetroRecoveryPending(engine) != 0;
        syncingEffect = true;
        effectSwitch.IsOn = running;
        syncingEffect = false;
        cancelRecovery.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        var b=new StringBuilder(1024); Native.RetroStatus(engine,b,b.Capacity);
        string raw = b.ToString();
        var lines = raw.Split('\n');
        var summary = lines[0];
        status.Text = summary;
        if (raw != diagnosticStatus)
        {
            diagnosticStatus = raw;
            diagnosticMetrics.Children.Clear(); diagnosticMetrics.RowDefinitions.Clear();
            if (lines.Length > 1)
            {
                var parts = lines[1].Split(" · CPU submission ", 2, StringSplitOptions.None);
                var values = new List<(string Label, string Value)> { ("Presentations", parts[0]) };
                if (parts.Length > 1) values.Add(("CPU submission", parts[1]));
                diagnosticMetrics.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                for (int i = 0; i < values.Count; i++)
                {
                    var label = Text(values[i].Label, 12, true); label.TextAlignment = TextAlignment.Right;
                    var value = Text(values[i].Value, 12); value.TextAlignment = TextAlignment.Left;
                    Grid.SetColumn(label, i * 2); Grid.SetColumn(value, i * 2 + 1);
                    diagnosticMetrics.Children.Add(label); diagnosticMetrics.Children.Add(value);
                }
            }
        }
        effectState.Text = running ? "Retro terminal effect is running."
            : summary == "Stopped. Your desktop is unchanged." ? "Retro terminal effect is not running." : summary;
        if (summary != loggedStatus)
        {
            loggedStatus = summary;
            Log.Information("CRT state Running={Running} RecoveryPending={RecoveryPending} Status={Status}", running, pending, summary);
        }
        UpdateDependencies();
    }
    private void Refresh()
    {
        syncing=true;
        try {
            palette.SelectedIndex=Native.RetroGetPalette(engine); mask.SelectedIndex=Native.RetroGetMask(engine);
            persistence.IsOn=Native.RetroGetPersistence(engine)!=0; random.IsOn=Native.RetroGetRandom(engine)!=0;
            int bits=Native.RetroGetGlitchTypes(engine); for(int i=0;i<4;i++)types[i].IsChecked=(bits&(1<<i))!=0;
            for(int i=0;i<sliders.Length;i++)sliders[i].Value=Native.RetroGetSetting(engine,i);
            color.Color=Color.FromArgb(255,(byte)(Native.RetroGetColor(engine,0)*255),(byte)(Native.RetroGetColor(engine,1)*255),(byte)(Native.RetroGetColor(engine,2)*255));
            UpdateDependencies();
        } finally { syncing=false; }
    }
    private async void PollStatus(object? sender, object args)
    {
        RefreshStatus();
        switch (Native.RetroTakeTrayAction(engine))
        {
            case TrayAction.Open: RestoreWindow(); break;
            case TrayAction.Preferences:
                pages.Select("Settings");
                RestoreWindow();
                break;
            case TrayAction.About:
                RestoreWindow();
                if (root.XamlRoot is not null) await ShowAboutAsync();
                else root.Loaded += ShowAboutWhenLoaded;
                break;
            case TrayAction.Quit: await CloseOrHideAsync(fromTray: true); break;
        }
    }

    private async void ShowAboutWhenLoaded(object sender, RoutedEventArgs args)
    {
        root.Loaded -= ShowAboutWhenLoaded;
        await ShowAboutAsync();
    }

    internal void RestoreWindow()
    {
        if (closing) return;
        if (inTray) inTray = false;
        Native.RetroLeaveTray(engine);
        Activate();
    }

    internal bool HideToTray()
    {
        if (Native.RetroKeepInTray(engine) == 0) return false;
        inTray = true;
        AppWindow.Hide();
        Log.Information("Control window minimized to the notification area");
        return true;
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if(closing) return;
        args.Cancel=true;
        if (systemCloseQueued) return;
        var systemClose = Native.RetroSystemClose(engine);
        if (systemClose != SystemClose.None)
        {
            systemCloseQueued = true;
            statusTimer.Stop();
            saveTimer.Stop();
            if (systemClose == SystemClose.SaveFailed)
                Log.Warning("Could not save CRT preferences during Windows servicing");
            else
                Log.Information("Closing Retro Terminal for Windows servicing");
            DispatcherQueue.TryEnqueue(FinishClose);
            return;
        }
        await CloseOrHideAsync(fromTray: false);
    }

    private void FinishClose()
    {
        if (closing) return;
        closing = true;
        statusTimer.Stop();
        Native.RetroDestroy(engine);
        Close();
    }

    internal void RequestMemoryShutdown() => _ = CloseOrHideAsync(fromTray: true);

    private async Task CloseOrHideAsync(bool fromTray)
    {
        if(closePrompt || systemCloseQueued || closing) return;
        closePrompt=true;
        try
        {
            if (!fromTray)
            {
                var confirm = new ContentDialog
                {
                    XamlRoot = root.XamlRoot, RequestedTheme = root.ActualTheme, Title = "Close Retro Terminal?",
                    Content = "Minimize to the notification area to keep CRT available. Quitting stops the effect and releases its resources.\n\nEmergency stop: Ctrl + Alt + F11.",
                    PrimaryButtonText = "Minimize to tray", SecondaryButtonText = "Quit", CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary
                };
                var choice = await confirm.ShowAsync();
                if (systemCloseQueued || closing) return;
                if (choice == ContentDialogResult.Primary)
                {
                    if (!HideToTray()) Notice("The notification area is unavailable. Retro Terminal is still open.");
                    return;
                }
                if (choice != ContentDialogResult.Secondary) return;
            }
            Native.RetroStop(engine);
            saveTimer.Stop();
            if(Native.RetroSave(engine)==0)
            {
                if (inTray) RestoreWindow();
                var dialog=new ContentDialog { XamlRoot=root.XamlRoot, RequestedTheme=root.ActualTheme,
                    DefaultButton=ContentDialogButton.Close, Title="Preferences could not be saved",
                    Content="Your changes are still in this window. Retry saving, keep the window open, or close without saving.",
                    PrimaryButtonText="Retry", SecondaryButtonText="Close without saving", CloseButtonText="Keep open" };
                var choice=await dialog.ShowAsync();
                if (systemCloseQueued || closing) return;
                if(choice==ContentDialogResult.None) return;
                if(choice==ContentDialogResult.Primary && Native.RetroSave(engine)==0) { Notice(SaveFailure); return; }
            }
            FinishClose();
        }
        finally { closePrompt=false; }
    }
}
