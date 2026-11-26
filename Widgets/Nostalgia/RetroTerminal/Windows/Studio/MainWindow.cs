using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.UI;
using Microsoft.Win32;
using System.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Serilog;
using Tools.Widgets;
using Windows.UI;
using static Tools.Widgets.EffectStudioLayout;

namespace RetroStudio;
internal sealed class MainWindow : Window
{
    private readonly nint engine;
    private readonly DesktopEffectRecovery recovery;
    private bool autoRecover;
    private int recoveryDelaySeconds;
    private readonly StudioPages pages;
    private readonly WidgetCloseDialog closeDialog = new();
    private readonly Action applyDisplaySelection;
    private readonly WidgetTrayMenu trayMenu;
    private readonly Native.TrayMenuCallback showTrayMenu;
    private readonly DisplayTargetPicker displayPicker;
    private uint displayTopologyGeneration;
    private readonly int builtInPresetCount = Native.RetroBuiltInPresetCount();
    private readonly Grid root = new();
    private readonly Button tryButton = new();
    private readonly ToggleSwitch effectSwitch = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button cancelRecovery = new() { Content = "Cancel automatic restart", Visibility = Visibility.Collapsed };
    private bool syncingEffect;
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Grid diagnosticMetrics = new() { ColumnSpacing = 12, RowSpacing = 6 };
    private readonly TextBlock displayDiagnostics = SettingsHint("");
    private string diagnosticStatus = "";
    private readonly TextBlock effectState = new(), feedback = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly Slider[] sliders = new Slider[18];
    private readonly ComboBox palette = new(), mask = new(), preset = new();
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
        Initialize(this, root, "Retro Terminal");
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "RetroTerminal.ico");
        AppWindow.SetIcon(iconPath);
        var owner = WinRT.Interop.WindowNative.GetWindowHandle(this);
        engine = Native.RetroCreate(owner, out var initializationError);
        if (engine == 0) throw new InvalidOperationException(Marshal.PtrToStringUni(initializationError));
        autoRecover = ReadPreference("AutoRestart", 1, 0, 1) == 1;
        recoveryDelaySeconds = ReadPreference("RecoveryDelaySeconds", 60, 30, 300);
        recovery = new DesktopEffectRecovery("Retro Terminal", ResumeRecovery);
        const string displayPreference = "SelectedDisplays";
        var storedDisplays = ReadPreferenceValue(displayPreference);
        // Only a first launch selects the studio display. A disconnected selection remains saved.
        string[] selectedDisplays = storedDisplays switch
        {
            string[] ids => ids,
            null => DisplayTarget.GetStudioDisplay(DisplayTarget.GetConnected(), owner)?.Identifiers ?? [],
            _ => throw new InvalidDataException("SelectedDisplays must be a list of monitor identifiers.")
        };
        if (storedDisplays is null) SavePreference(displayPreference, selectedDisplays);
        void ApplyDisplaySelection()
        {
            var targets = DisplayTarget.GetSelected(DisplayTarget.GetConnected(), selectedDisplays);
            var monitors = targets.Select(display => display.Handle).ToArray();
            Native.RetroSetDisplays(engine, monitors, monitors.Length);
        }
        cancelRecovery.Click += (_, _) =>
        {
            recovery.Cancel();
            Native.RetroStop(engine);
            RefreshStatus();
        };
        var statusBar = StatusBar(effectState, effectSwitch, cancelRecovery,
            isActive: () => !closing && Native.RetroRunning(engine) != 0);
        root.Children.Add(statusBar);

        var effect = StudioSectionPage("Effect");
        var presets = StudioSectionPage("Presets");
        var settings = StudioSectionPage("Settings");
        var diagnostics = StudioSectionPage("Diagnostics");
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
            if(!TryApplyPreset(preset.SelectedIndex)) SetCustom();
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
            var save=new Button { Content="Save current" }; save.Click+=(_,_)=> { if(Native.RetroSavePreset(engine,slot)==0) Notice($"Could not save My preset {slot}. Check that its snapshot is valid and writable."); else { syncing=true; preset.SelectedIndex=builtInPresetCount + slot; syncing=false; Notice("Saved to My preset "+slot+"."); } };
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
        const string acrylicPreference = "UseAcrylic";
        var acrylic = new ToggleSwitch { IsOn = ReadPreference(acrylicPreference, 0, 0, 1) == 1 };
        ApplyBackdrop(acrylic.IsOn);
        acrylic.Toggled += (_, _) =>
        {
            ApplyBackdrop(acrylic.IsOn);
            SavePreference(acrylicPreference, acrylic.IsOn ? 1 : 0);
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

        displayPicker = new DisplayTargetPicker(() => selectedDisplays, ids =>
        {
            selectedDisplays = ids;
            SavePreference(displayPreference, ids);
            ApplyDisplaySelection();
            RefreshStatus();
        });
        applyDisplaySelection = ApplyDisplaySelection;
        effectSwitch.Toggled += (_, _) =>
        {
            if (syncingEffect) return;
            if (effectSwitch.IsOn)
            {
                recovery.Reset();
                ApplyDisplaySelection();
                Native.RetroStart(engine);
                displayPicker.Refresh();
            }
            else { recovery.Cancel(); Native.RetroStop(engine); }
            RefreshStatus();
        };
        settings.Children.Add(AppearanceGroup("Enable or disable the retro terminal effect.", effectSwitch, theme, acrylic));
        settings.Children.Add(displayPicker);

        const string animationPreference = "AnimationFps", capturePreference = "CaptureFps";
        var animationLimit = FrameLimitSlider(animationPreference, 60, 15, 120);
        var captureLimit = FrameLimitSlider(capturePreference, 30, 5, 60);
        void ApplyFrameLimits() => Native.RetroSetFrameLimits(engine, (int)animationLimit.Value, (int)captureLimit.Value);
        var animationRow = FrameLimitRow("Animation limit", "Maximum animation frame rate for the effect.",
            animationLimit, animationPreference, ApplyFrameLimits);
        var captureRow = FrameLimitRow("Capture limit", "Maximum screen capture frame rate.",
            captureLimit, capturePreference, ApplyFrameLimits);
        ApplyFrameLimits();

        var autoRestart = new ToggleSwitch
        {
            IsOn = autoRecover
        };
        var recoveryDelay = new NumberBox
        {
            Header = "Minimum recovery delay (seconds)",
            Minimum = 30, Maximum = 300, MinWidth = 112,
            SmallChange = 10, LargeChange = 30, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Value = recoveryDelaySeconds,
        };
        var recoveryTiming = SettingsHint("");
        recoveryTiming.TextAlignment = TextAlignment.Right;
        var recoveryDetails = Stack(8);
        recoveryDetails.Children.Add(recoveryDelay);
        recoveryDetails.Children.Add(SettingsHint("Requires 30 seconds of stability. Up to 2 retries until the effect runs stably again."));
        var recoveryButton = new Button
        {
            Content = recoveryTiming,
            Flyout = new Flyout { Content = recoveryDetails },
            Padding = new Thickness(0), BorderThickness = new Thickness(0),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            IsEnabled = autoRestart.IsOn
        };
        ToolTipService.SetToolTip(recoveryButton, "Change minimum recovery delay");
        AutomationProperties.SetHelpText(recoveryButton, "Opens minimum recovery delay settings.");
        void UpdateRecoveryTiming(int seconds)
        {
            string delay = seconds % 60 == 0 ? $"{seconds / 60} min" : $"{seconds} s";
            recoveryTiming.Text = $"At least {delay} delay · 30 s stable";
        }
        void ApplyRecovery()
        {
            autoRecover = autoRestart.IsOn;
            recoveryDelaySeconds = (int)recoveryDelay.Value;
            if (!autoRecover) recovery.Cancel();
        }
        autoRestart.Toggled += (_, _) =>
        {
            recoveryButton.IsEnabled = autoRestart.IsOn;
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
            UpdateRecoveryTiming(seconds);
        };
        ApplyRecovery();
        UpdateRecoveryTiming((int)recoveryDelay.Value);
        settings.Children.Add(RowGroup("Startup", startupRow,
            PreferenceRow("Resume automatically", "Resume after a temporary desktop interruption.", autoRestart)));
        settings.Children.Add(RowGroup("Performance protection", animationRow, captureRow,
            PreferenceRow("Recovery", "Resume only after the system becomes stable again.", recoveryButton)));
        diagnostics.Children.Add(Text("Live renderer status", 18));
        for (int i = 0; i < 4; i++) diagnosticMetrics.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var statusContent = Stack(10); statusContent.Children.Add(status); statusContent.Children.Add(diagnosticMetrics);
        statusContent.Children.Add(displayDiagnostics);
        diagnostics.Children.Add(Card(statusContent));
        diagnostics.Children.Add(Text("Presentation counts and CPU submission time describe the renderer. GPU frame time is not measured.", 13, secondary: true));
        diagnostics.Children.Add(Text("CRT pauses if the desktop stops responding or rendering stays slow.", 13, secondary: true));
        diagnostics.Children.Add(jokeSection);

        pages = StandardPages(effect, presets, settings, diagnostics, displayPicker.Refresh);
        pages.Select("Settings");
        foreach (var scroll in pages.Scrollers)
            ScrollBarBehavior.HoverScrollBar(scroll, scroll, root);
        Grid.SetRow(pages, 1);
        root.Children.Add(pages);

        feedback.Style = GetStyle("SecondaryTextStyle");
        feedback.Margin = new Thickness(24, 8, 24, 4);
        var footer = Stack(0);
        footer.Children.Add(feedback);
        const string emergencyPreference = "EmergencyShortcutEnabled";
        var emergency = new EmergencyShortcutRow("Ctrl + Alt + F11", "Emergency stop shortcut");
        emergency.Toggle.IsOn = ReadPreference(emergencyPreference, 1, 0, 1) == 1;
        void ApplyEmergencyShortcut()
        {
            var state = Native.RetroSetEmergencyShortcut(engine, emergency.Toggle.IsOn ? 1 : 0);
            string message = state switch
            {
                EmergencyShortcutState.Registered => "Emergency shortcut is active and immediately stops the effect.",
                EmergencyShortcutState.Disabled => "Emergency shortcut is disabled.",
                _ => "Emergency shortcut unavailable. Another app may be using it."
            };
            emergency.SetStatus(message, state == EmergencyShortcutState.Registered);
        }
        ApplyEmergencyShortcut();
        emergency.Toggle.Toggled += (_, _) =>
        {
            ApplyEmergencyShortcut();
            SavePreference(emergencyPreference, emergency.Toggle.IsOn ? 1 : 0);
            RefreshStatus();
        };
        footer.Children.Add(FooterBar(emergency, WidgetFooter.Create("Retro Terminal", ShowAbout)));
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); Save(); };
        Refresh();
        RefreshStatus();
        trayMenu = new WidgetTrayMenu(this, RestoreWindow, () => { pages.Select("Settings"); RestoreWindow(); },
            ShowAbout, () => _ = CloseOrHideAsync(fromTray: true), () => (Native.RetroIsArmed(engine) != 0, true),
            enabled => HandleTrayAction(enabled ? TrayAction.Enable : TrayAction.Stop), AddTrayCommands);
        showTrayMenu = (x, y) => DispatcherQueue.TryEnqueue(() => { if (!closing) trayMenu.Show(x, y); });
        Native.RetroSetTrayMenu(engine, showTrayMenu);
        statusTimer.Tick += PollStatus;
        statusTimer.Start();
        AppWindow.Closing+=OnClosing;
    }

    private const string PreferencesPath = @"Software\VibeWare\RetroTerminal";

    private static int ReadPreference(string name, int initialValue, int minimum, int maximum) =>
        ReadPreferenceValue(name) switch
        {
            null => initialValue,
            int value when value >= minimum && value <= maximum => value,
            _ => throw new InvalidDataException($"Preference '{name}' must be an integer between {minimum} and {maximum}.")
        };

    private static Slider FrameLimitSlider(string preference, int initialValue, int minimum, int maximum) => new()
    {
        Minimum = minimum, Maximum = maximum, StepFrequency = 1, MinHeight = 28,
        Value = ReadPreference(preference, initialValue, minimum, maximum)
    };

    private Grid FrameLimitRow(string label, string description, Slider slider, string preference, Action apply)
    {
        var value = Text($"{slider.Value:0} fps", 14);
        slider.ValueChanged += (_, args) =>
        {
            value.Text = $"{args.NewValue:0} fps";
            apply();
            SavePreference(preference, (int)args.NewValue);
        };
        return SliderRow(label, description, slider, value);
    }

    private static object? ReadPreferenceValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(PreferencesPath);
        return key?.GetValue(name);
    }

    private void ApplyTheme(int index) => root.RequestedTheme = index switch
    {
        1 => ElementTheme.Light,
        2 => ElementTheme.Dark,
        _ => ElementTheme.Default
    };

    private void ApplyBackdrop(bool acrylic)
    {
        root.ClearValue(Grid.BackgroundProperty);
        try
        {
            SystemBackdrop = acrylic ? new DesktopAcrylicBackdrop() : null;
            if (SystemBackdrop is not null) root.Background = new SolidColorBrush(Colors.Transparent);
        }
        catch (COMException)
        {
            // Keep the themed solid surface if the graphics session cannot create acrylic.
            SystemBackdrop = null;
        }
    }

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

    private void ShowAbout() => WidgetAboutWindow.Show(this, new WidgetIdentity(
        "RetroTerminal", "Retro Terminal", typeof(MainWindow).Assembly.GetName().Version!,
        "CRT color, texture and motion for your desktop.", "Assets/RetroTerminal.ico",
        "https://github.com/umbertotechnopreneur/VibeKit/tree/main/Widgets/Nostalgia/RetroTerminal", "MIT License"));

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
    private bool TryApplyPreset(int menuIndex)
    {
        if (menuIndex <= builtInPresetCount) return Native.RetroApplyBuiltInPreset(engine, menuIndex - 1) != 0;
        int slot = menuIndex - builtInPresetCount;
        var result = Native.RetroLoadPersonalPreset(engine, slot);
        if (result == SnapshotRead.Ok) return true;
        Notice(result == SnapshotRead.Missing ? $"My preset {slot} is empty. Save a preset in that slot first." :
            $"My preset {slot} is invalid or unreadable. Check its saved snapshot.");
        return false;
    }

    private void ApplyPreset(int index)
    {
        if(!TryApplyPreset(index)) return;
        syncing=true; preset.SelectedIndex=index; syncing=false; Refresh(); Save();
        // Applying a look keeps the current section, viewport and keyboard focus in place.
    }
    private const string SaveFailure="Could not save preferences. Check that the settings snapshot is valid and writable.";
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
        int activeDisplays = Native.RetroActiveDisplayCount(engine);
        bool running = activeDisplays != 0;
        bool armed = Native.RetroIsArmed(engine) != 0;
        bool pending = recovery.Pending;
        uint topology = Native.RetroDisplayTopologyGeneration(engine);
        bool refreshDisplays = topology != displayTopologyGeneration;
        displayTopologyGeneration = topology;
        if (refreshDisplays)
        {
            applyDisplaySelection();
            displayPicker.Refresh();
        }
        syncingEffect = true;
        effectSwitch.IsOn = armed || pending;
        syncingEffect = false;
        cancelRecovery.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        var b=new StringBuilder(4096); Native.RetroStatus(engine,b,b.Capacity);
        string raw = b.ToString();
        if (pending) raw = "Waiting for coordinated recovery.\n" + raw;
        var lines = raw.Split('\n');
        var summary = lines[0];
        status.Text = summary;
        if (raw != diagnosticStatus)
        {
            diagnosticStatus = raw;
            diagnosticMetrics.Children.Clear(); diagnosticMetrics.RowDefinitions.Clear();
            displayDiagnostics.Text = string.Join("\n", lines.Skip(2));
            displayDiagnostics.Visibility = displayDiagnostics.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (lines.Length > 1 && lines[1].Length != 0)
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
        effectState.Text = running ? (activeDisplays == 1
            ? "Retro terminal effect is running on one monitor."
            : $"Retro terminal effect is running on {activeDisplays} monitors.")
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
    private void PollStatus(object? sender, object args)
    {
        int recoveryEvent = Native.RetroTakeRecoveryEvent(engine);
        if (recoveryEvent == 1 && !recovery.Pause(autoRecover, recoveryDelaySeconds) && autoRecover && !recovery.Available)
            Notice("Automatic recovery is unavailable; enable the effect manually.");
        else if (recoveryEvent == 2) recovery.Cancel();
        recovery.Tick(!closing && !closePrompt && !systemCloseQueued, Native.RetroRunning(engine) != 0);
        if (recovery.Pending && !recovery.Available)
        {
            recovery.Cancel();
            Notice("Automatic recovery is unavailable; enable the effect manually.");
        }
        RefreshStatus();
        HandleTrayAction(Native.RetroTakeTrayAction(engine));
    }

    private void AddTrayCommands(WidgetTrayMenu.Items menu)
    {
        menu.Add("Try for 15 seconds", () => HandleTrayAction(TrayAction.Preview), Native.RetroIsArmed(engine) == 0);
        menu.Add("Green phosphor", () => HandleTrayAction(TrayAction.Green));
        menu.Add("Amber terminal", () => HandleTrayAction(TrayAction.Amber));
        menu.Add("Arcade color", () => HandleTrayAction(TrayAction.Arcade));
        menu.Add("Try a glitch now", () => HandleTrayAction(TrayAction.Glitch), Native.RetroRunning(engine) != 0);
        menu.Add("Stop", () => HandleTrayAction(TrayAction.Stop));
    }

    private void HandleTrayAction(TrayAction action)
    {
        switch (action)
        {
            case TrayAction.Enable:
            case TrayAction.Preview:
                if (!closing && !closePrompt && Native.RetroIsArmed(engine) == 0) {
                    recovery.Reset();
                    applyDisplaySelection();
                    if (action == TrayAction.Preview) Native.RetroPreview(engine);
                    else Native.RetroStart(engine);
                    displayPicker.Refresh();
                }
                break;
            case TrayAction.Stop: recovery.Cancel(); Native.RetroStop(engine); break;
            case TrayAction.Green:
            case TrayAction.Amber:
            case TrayAction.Arcade:
                Native.RetroSetPalette(engine, (int)action - (int)TrayAction.Green);
                Changed();
                break;
            case TrayAction.Glitch: Native.RetroTryGlitch(engine); break;
            case TrayAction.Open: RestoreWindow(); break;
        }
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
        recovery.Dispose();
        Native.RetroSetTrayMenu(engine, null);
        trayMenu.Dispose();
        Native.RetroDestroy(engine);
        Close();
    }

    private void ResumeRecovery()
    {
        applyDisplaySelection();
        Native.RetroStart(engine);
        displayPicker.Refresh();
    }

    internal void RequestMemoryShutdown() => _ = CloseOrHideAsync(fromTray: true);

    private async Task CloseOrHideAsync(bool fromTray)
    {
        if (systemCloseQueued || closing) return;
        if (fromTray ? closeDialog.RequestQuitIfOpen() : closeDialog.FocusIfOpen(this)) return;
        if (closePrompt) return;
        closePrompt = true;
        try
        {
            if (!fromTray)
            {
                bool canMinimize = Native.RetroKeepInTray(engine) != 0;
                WidgetCloseChoice choice;
                try
                {
                    choice = await closeDialog.ChooseAsync(this, "Retro Terminal", canMinimize,
                        "Emergency stop: Ctrl + Alt + F11.");
                    if (systemCloseQueued || closing) return;
                    if (choice == WidgetCloseChoice.Minimize)
                    {
                        if (!HideToTray()) Notice("The notification area is unavailable. Retro Terminal is still open.");
                        return;
                    }
                }
                finally
                {
                    if (canMinimize && !inTray && !closing && !systemCloseQueued) Native.RetroLeaveTray(engine);
                }
                if (choice != WidgetCloseChoice.Quit) return;
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
