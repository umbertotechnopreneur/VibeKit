using System.Globalization;
using System.IO;
using System.Security;
using Microsoft.Win32;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Automation;
using Serilog;
using Tools.Widgets;
using Windows.Graphics;
using Windows.UI;

namespace RetroStudio;
internal sealed class MainWindow : Window
{
    private readonly nint engine;
    private readonly Expander settingsSection;
    private readonly Grid root = new();
    private readonly Button effectButton = new(), tryButton = new();
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock effectState = new(), feedback = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly Slider[] sliders = new Slider[18];
    private readonly ComboBox palette = new(), mask = new(), preset = new();
    private readonly ToggleSwitch persistence = new(), random = new();
    private readonly CheckBox[] types = new CheckBox[4];
    private readonly ColorPicker color = new() { IsAlphaEnabled = false, IsAlphaSliderVisible = false, IsAlphaTextInputVisible = false };
    private FrameworkElement colorSection = null!;
    private bool syncing, closing, closePrompt, saveFailed, inTray;
    private string? displayedEffect;
    private string? loggedStatus;
    private readonly TextBlock effectLabel = new() { VerticalAlignment=VerticalAlignment.Center };
    private readonly Microsoft.UI.Xaml.Shapes.Ellipse effectLed = new() { Width=8, Height=8, StrokeThickness=1, HorizontalAlignment=HorizontalAlignment.Center, VerticalAlignment=VerticalAlignment.Center };
    private readonly Border effectGlow = new() { Width=14, Height=14, Opacity=0, IsHitTestVisible=false };
    private readonly Microsoft.UI.Xaml.Media.Animation.Storyboard effectPulse = new();
    private readonly Windows.UI.ViewManagement.UISettings accessibility = new();
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
    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromArgb(255,r,g,b));
    private static Style GetStyle(string key) => (Style)Application.Current.Resources[key];
    private static TextBlock Text(string text, int size=14, bool secondary=false)
    {
        var label = new TextBlock { Text=text, FontSize=size, TextWrapping=TextWrapping.Wrap };
        if (secondary) label.Style = GetStyle("SecondaryTextStyle");
        return label;
    }
    private static StackPanel Stack(double spacing=12) => new() { Spacing=spacing };
    private static Grid Columns(FrameworkElement left, FrameworkElement right)
    {
        var row=new Grid { ColumnSpacing=16 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width=new GridLength(1,GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
        row.Children.Add(left); Grid.SetColumn(right,1); row.Children.Add(right); return row;
    }
    private static Border Card(UIElement child) => new() { Child=child, Padding=new Thickness(16), CornerRadius=new CornerRadius(8), Style=GetStyle("CardStyle"), BorderThickness=new Thickness(1) };
    private static Expander Section(string title, string description, UIElement content, ToggleSwitch? toggle=null)
    {
        var heading=Stack(3); heading.Margin=new Thickness(0,12,0,12);
        heading.Children.Add(Text(title,15)); heading.Children.Add(Text(description,12,secondary:true));
        UIElement header=heading;
        if(toggle is not null)
        {
            toggle.MinWidth=0;
            toggle.Width=40;
            toggle.OnContent=null;
            toggle.OffContent=null;
            toggle.VerticalAlignment=VerticalAlignment.Center;
            AutomationProperties.SetName(toggle,title);
            ToolTipService.SetToolTip(toggle,title);
            header=Columns(heading,toggle);
        }
        var expander=new Expander { Header=header, Content=content, HorizontalAlignment=HorizontalAlignment.Stretch, HorizontalContentAlignment=HorizontalAlignment.Stretch };
        AutomationProperties.SetName(expander,title);
        return expander;
    }
    private FrameworkElement ScrollSections(UIElement content)
    {
        var scroll=new ScrollViewer { Content=content, Padding=new Thickness(0,16,8,8), HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled, VerticalScrollBarVisibility=ScrollBarVisibility.Auto, HorizontalContentAlignment=HorizontalAlignment.Stretch };
        ScrollBarBehavior.HoverScrollBar(scroll,scroll,root);
        return scroll;
    }
    private void Notice(string message) { feedback.Text=message; feedback.Visibility=Visibility.Visible; }

    public MainWindow()
    {
        Title="Retro Terminal · VibeWare";
        root.Style = GetStyle("WindowSurfaceStyle");
        var initialTheme = ReadPreference("WindowTheme", 0, 0, 2);
        ApplyTheme(initialTheme);
        Content=root;
        double scale=Native.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this))/96.0;
        var workArea=DisplayArea.GetFromWindowId(AppWindow.Id,DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new SizeInt32(Math.Min((int)(780*scale),workArea.Width-48),Math.Min((int)(860*scale),workArea.Height-48)));
        engine=Native.RetroCreate(WinRT.Interop.WindowNative.GetWindowHandle(this));
        if(engine==0) throw new InvalidOperationException("Retro Terminal could not initialize safely.");
        root.Padding=new Thickness(24,20,24,12); root.RowSpacing=14;
        foreach(var height in new[]{GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto}) root.RowDefinitions.Add(new RowDefinition { Height=height });
        var title=Stack(3); title.Children.Add(Text("Retro Terminal",26)); title.Children.Add(Text("CRT color, texture and motion for your desktop.",13,secondary:true));
        root.Children.Add(title);

        var power=Stack(10); var powerText=Stack(3); powerText.Children.Add(Text("CRT effect",16)); effectState.FontSize=13; effectState.Style=GetStyle("SecondaryTextStyle"); powerText.Children.Add(effectState);
        effectButton.MinWidth=120; effectButton.VerticalAlignment=VerticalAlignment.Center;
        var indicator=new Grid { Width=14, Height=14, VerticalAlignment=VerticalAlignment.Center };
        indicator.Children.Add(effectGlow); indicator.Children.Add(effectLed);
        effectGlow.Loaded+=(_,_)=> {
            var compositor=Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(effectGlow).Compositor;
            var shadow=compositor.CreateDropShadow(); shadow.Color=Color.FromArgb(255,255,42,42); shadow.BlurRadius=9; shadow.Opacity=.8f;
            var visual=compositor.CreateSpriteVisual(); visual.Size=new System.Numerics.Vector2(8,8); visual.Offset=new System.Numerics.Vector3(3,3,0);
            visual.Brush=compositor.CreateColorBrush(Color.FromArgb(255,205,40,40)); visual.Shadow=shadow;
            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetElementChildVisual(effectGlow,visual);
        };
        var pulseAnimation=new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { From=.3, To=.85, Duration=new Duration(TimeSpan.FromSeconds(1.3)), AutoReverse=true, RepeatBehavior=Microsoft.UI.Xaml.Media.Animation.RepeatBehavior.Forever, EnableDependentAnimation=true };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(pulseAnimation,effectGlow);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(pulseAnimation,"Opacity"); effectPulse.Children.Add(pulseAnimation);
        var effectContent=new StackPanel { Orientation=Orientation.Horizontal, Spacing=8, HorizontalAlignment=HorizontalAlignment.Center };
        effectContent.Children.Add(indicator); effectContent.Children.Add(effectLabel); effectButton.Content=effectContent;
        effectButton.Click += (_, _) =>
        {
            if (Native.RetroRunning(engine) != 0 || Native.RetroRecoveryPending(engine) != 0)
                Native.RetroStop(engine);
            else Native.RetroStart(engine);
            RefreshStatus();
        };
        power.Children.Add(Columns(powerText,effectButton));
        power.Children.Add(new Border { Height=1, Style=GetStyle("DividerStyle") });
        var emergency=Stack(2); emergency.Children.Add(Text("Emergency stop · Ctrl + Alt + F11",13)); power.Children.Add(emergency);
        var powerCard=Card(power); Grid.SetRow(powerCard,1); root.Children.Add(powerCard);

        var sections = Stack(10);
        var effect=Stack(10); var presets=Stack(12); var settings=Stack(12); var diagnostics=Stack(12);
        var effectSection = Section("Effect", "Palette, monitor surface and glitches", effect);
        sections.Children.Add(effectSection);
        sections.Children.Add(Section("Presets", "Built-in looks and personal presets", presets));
        settingsSection = Section("Settings", "Appearance and behavior", settings);
        sections.Children.Add(settingsSection);
        sections.Children.Add(Section("Diagnostics", "Live renderer status", diagnostics));
        foreach (Expander section in sections.Children)
        {
            section.Expanding += (_, _) =>
            {
                foreach (Expander other in sections.Children)
                    if (other != section) other.IsExpanded = false;
            };
        }
        var scroll = ScrollSections(sections);
        Grid.SetRow(scroll,2);
        root.Children.Add(scroll);
        preset.Header="Preset"; preset.HorizontalAlignment=HorizontalAlignment.Stretch;
        foreach(var name in new[]{"Custom","Clean terminal","Worn terminal","Arcade","My preset 1","My preset 2","My preset 3"}) preset.Items.Add(name);
        preset.SelectedIndex=0;
        preset.SelectionChanged+=(_,_)=> {
            if(syncing || preset.SelectedIndex<=0) return;
            if(Native.RetroPreset(engine,preset.SelectedIndex)==0) { Notice("This personal preset is empty. Save it in Presets first."); SetCustom(); }
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

        presets.Children.Add(Text("Built-in looks",18)); presets.Children.Add(Text("Apply a starting point, then adjust it in Effect.",13,secondary:true));
        for(int i=1;i<=3;i++) { int index=i; var apply=new Button { Content="Apply", VerticalAlignment=VerticalAlignment.Center }; apply.Click+=(_,_)=>ApplyPreset(index,effectSection); presets.Children.Add(Card(Columns(Text(new[]{"Clean terminal","Worn terminal","Arcade"}[i-1],15),apply))); }
        presets.Children.Add(Text("Personal presets",18)); presets.Children.Add(Text("Save stores the current settings in that slot, replacing its previous look.",13,secondary:true));
        for(int i=1;i<=3;i++) {
            int slot=i; var actions=new StackPanel { Orientation=Orientation.Horizontal, Spacing=8 };
            var apply=new Button { Content="Apply" }; apply.Click+=(_,_)=>ApplyPreset(slot+3,effectSection);
            var save=new Button { Content="Save current" }; save.Click+=(_,_)=> { if(Native.RetroSavePreset(engine,slot)==0) Notice("Could not save the personal preset."); else { syncing=true; preset.SelectedIndex=slot+3; syncing=false; Notice("Saved to My preset "+slot+"."); } };
            actions.Children.Add(apply); actions.Children.Add(save); presets.Children.Add(Card(Columns(Text("My preset "+slot,15),actions)));
        }
        var theme = new ComboBox { Header = "Theme", ItemsSource = new[] { "System", "Light", "Dark" }, SelectedIndex = initialTheme };
        theme.SelectionChanged += (_, _) =>
        {
            if (theme.SelectedIndex < 0) return;
            ApplyTheme(theme.SelectedIndex);
            SavePreference("WindowTheme", theme.SelectedIndex);
        };
        settings.Children.Add(theme);
        var gpuJokes = new ToggleSwitch
        {
            Header = "GPU joke notification at startup",
            IsOn = ReadPreference("ShowGpuJokes", 1, 0, 1) == 1
        };
        gpuJokes.Toggled += (_, _) =>
        {
            if (Native.RetroSetGpuJokes(engine, gpuJokes.IsOn ? 1 : 0) == 0)
                Notice("Could not save the GPU jokes preference.");
        };
        var jokes = Stack(8);
        jokes.Children.Add(gpuJokes);
        uint jokeIndex = 0;
        var joke = Text(Native.GpuJoke(jokeIndex), 13, secondary: true);
        var nextJoke = new Button { Content = "Next joke" };
        nextJoke.Click += (_, _) => joke.Text = Native.GpuJoke(++jokeIndex);
        jokes.Children.Add(Columns(joke, nextJoke));
        settings.Children.Add(Card(jokes));
        var behavior=Stack(8); behavior.Children.Add(Text("Startup & stopping",16)); behavior.Children.Add(Text("CRT starts off. Turn it on from this window when you need it. Closing the app stops capture and releases the effect.",13,secondary:true)); settings.Children.Add(Card(behavior));
        var protection = Stack(8);
        protection.Children.Add(Text("Performance protection", 16));
        protection.Children.Add(Text("CRT pauses when the desktop stops responding or rendering is repeatedly slow.", 13, secondary: true));
        var autoRestart = new ToggleSwitch
        {
            Header = "Restart automatically after a performance pause",
            IsOn = ReadPreference("AutoRestart", 1, 0, 1) == 1
        };
        var recoveryDelay = new NumberBox
        {
            Header = "Minimum recovery delay (seconds)", Minimum = 30, Maximum = 300,
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
        protection.Children.Add(autoRestart);
        protection.Children.Add(recoveryDelay);
        protection.Children.Add(Text("Restart requires 30 seconds of stable CPU, memory and desktop responsiveness. CRT attempts recovery twice per manual start.", 13, secondary: true));
        settings.Children.Add(Card(protection));
        var display=Stack(8); display.Children.Add(Text("Target display",16)); display.Children.Add(Text("The effect uses the monitor containing this window when it starts. Move the window before turning CRT on to choose another monitor.",13,secondary:true)); settings.Children.Add(Card(display));
        diagnostics.Children.Add(Text("Live renderer status",18)); diagnostics.Children.Add(Card(status));
        diagnostics.Children.Add(Text("Presentation counts and CPU submission time describe the renderer. GPU frame time is not measured.",13,secondary:true));
        feedback.Style = GetStyle("SecondaryTextStyle");
        var footer = Stack(6);
        footer.Children.Add(feedback);
        footer.Children.Add(WidgetFooter.Create("Retro Terminal", async () => await ShowAboutAsync()));
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); Save(); };
        Refresh();
        RefreshStatus();
        statusTimer.Tick += PollStatus;
        statusTimer.Start();
        AppWindow.Closing+=OnClosing;
    }

    private const string PreferencesPath = @"Software\VibeWare\RetroTerminal";

    private static int ReadPreference(string name, int fallback, int minimum, int maximum)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PreferencesPath);
            return key?.GetValue(name) is int value && value >= minimum && value <= maximum ? value : fallback;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return fallback;
        }
    }

    private void ApplyTheme(int index) => root.RequestedTheme = index switch
    {
        1 => ElementTheme.Light,
        2 => ElementTheme.Dark,
        _ => ElementTheme.Default
    };

    private void SavePreference(string name, int value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(PreferencesPath);
            key.SetValue(name, value, RegistryValueKind.DWord);
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
    private void ApplyPreset(int index,Expander effectSection)
    {
        if(Native.RetroPreset(engine,index)==0) { Notice("This personal preset is empty. Save the current settings to it first."); return; }
        syncing=true; preset.SelectedIndex=index; syncing=false; Refresh(); Save();
        effectSection.IsExpanded = true;
        effectSection.StartBringIntoView();
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
        string label = running ? "Effect on" : pending ? "Cancel restart" : "Effect off";
        if(displayedEffect!=label) {
            displayedEffect=label; effectPulse.Stop();
            effectLabel.Text=label;
            effectLed.Fill=running?Brush(255,75,76):Brush(65,58,60);
            effectLed.Stroke=running?Brush(255,164,151):Brush(148,132,133);
            effectGlow.Opacity=running?.45:0;
            if(running && accessibility.AnimationsEnabled) effectPulse.Begin();
            string action = running ? "Stop CRT effect" : pending ? "Cancel automatic restart" : "Turn on CRT effect";
            AutomationProperties.SetName(effectButton, action);
            ToolTipService.SetToolTip(effectButton, action);
        }
        var b=new StringBuilder(1024); Native.RetroStatus(engine,b,b.Capacity); status.Text=b.ToString();
        var summary = status.Text.Split('\n', 2)[0];
        effectState.Text = running ? "CRT effect is active" : summary;
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
                settingsSection.IsExpanded = true;
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

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if(closing) return;
        args.Cancel=true;
        await CloseOrHideAsync(fromTray: false);
    }

    private async Task CloseOrHideAsync(bool fromTray)
    {
        if(closePrompt) return;
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
                if (choice == ContentDialogResult.Primary)
                {
                    if (Native.RetroKeepInTray(engine) != 0)
                    {
                        inTray = true;
                        AppWindow.Hide();
                        Log.Information("Control window minimized to the notification area");
                    }
                    else Notice("The notification area is unavailable. Retro Terminal is still open.");
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
                if(choice==ContentDialogResult.None) return;
                if(choice==ContentDialogResult.Primary && Native.RetroSave(engine)==0) { Notice(SaveFailure); return; }
            }
            closing=true;
            statusTimer.Stop();
            effectPulse.Stop();
            Native.RetroDestroy(engine);
            Close();
        }
        finally { closePrompt=false; }
    }
}
