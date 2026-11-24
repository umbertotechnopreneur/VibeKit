// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using Icon = System.Drawing.Icon;
using ColorTranslator = System.Drawing.ColorTranslator;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Tools.Widgets;
using Forms = System.Windows.Forms;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace VibeWare.Vignette;

internal static class Program
{
    internal static readonly WindowsSignIn SignIn = new("Vignette");

    [STAThread]
    private static void Main()
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Contains(WindowsSignIn.RemoveArgument)) { SignIn.Remove(); return; }
        bool startup = args.Contains(WindowsSignIn.StartArgument);
        using var activation = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\VibeWare.Vignette.Activate");
        using var singleton = new Mutex(false, @"Local\VibeWare.Vignette", out bool created);
        if (!created) { if (!startup) activation.Set(); return; }
        SetCurrentProcessExplicitAppUserModelID("biz.umbertogiacobbi.Vignette");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new Studio();
        app.SessionEnding += (_, _) => window.EndSession();
        var wait = ThreadPool.RegisterWaitForSingleObject(activation, (_, _) => window.Dispatcher.BeginInvoke(window.Reveal), null, -1, false);
        try
        {
            if (startup) { new WindowInteropHelper(window).EnsureHandle(); }
            else window.Reveal();
            app.Run();
        }
        finally { wait.Unregister(null); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string id);
}

internal sealed class Studio : Window
{
    private const string Site = "https://umbertogiacobbi.biz";
    private const int Hotkey = 0x5649;
    private const uint WindowShown = 0x8002, WindowReordered = 0x8004, SkipOwnProcess = 2;
    private static readonly Brush Violet = new SolidColorBrush(Color.FromRgb(114, 84, 169));
    private readonly SettingsStore store = new();
    private readonly IReadOnlyList<Pipeline> builtins = SettingsStore.Builtins();
    private Preferences preferences;
    private IReadOnlyList<DisplayTarget> displays = [];
    private readonly DesktopOverlay overlay = new();
    private readonly TextBlock status = Label("Stopped", 14);
    private readonly TextBlock message = Label("", 13);
    private readonly TextBlock description = Label("", 13);
    private readonly CheckBox effect = new() { Content = "Vignette effect", VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel monitorRows = new();
    private readonly ComboBox pipeline = new() { MinWidth = 310 };
    private readonly TextBox pipelineName = new() { MaxLength = 80, MinWidth = 260 };
    private readonly TextBox edgeColor = new() { MaxLength = 7, Width = 86 };
    private readonly Border swatch = new() { Width = 30, Height = 22, CornerRadius = new CornerRadius(4) };
    private readonly Slider[] sliders = new Slider[4];
    private readonly DispatcherTimer renderTimer = new() { Interval = TimeSpan.FromMilliseconds(55) };
    private readonly DispatcherTimer peerOrderTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer previewTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly Forms.NotifyIcon tray = new();
    private readonly Forms.ToolStripMenuItem trayEffect;
    private readonly WinEventCallback winEvent;
    private Window? about;
    private Button updatePipeline = null!, removePipeline = null!;
    private nint handle, orderHook;
    private bool syncing, closing, hotkeyRegistered, preview, dirty;
    private string? editId;

    internal Studio()
    {
        preferences = store.Load();
        Title = "Vignette";
        Width = 700; Height = 670; MinWidth = 520; MinHeight = 430;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = BitmapFrame.Create(new Uri(Path.Combine(AppContext.BaseDirectory, "Vignette.ico")));
        var root = new DockPanel { Margin = new Thickness(24) };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        header.Children.Add(Label("Vignette", 30, true));
        header.Children.Add(Label("Elliptical edge lighting and shading", 14));
        var commands = Horizontal();
        effect.Checked += (_, _) => { if (!syncing) SetEffect(true); };
        effect.Unchecked += (_, _) => { if (!syncing) SetEffect(false); };
        commands.Children.Add(effect);
        commands.Children.Add(ButtonFor("Try for 15 seconds", () =>
        {
            if (effect.IsChecked == true) return;
            SetEffect(true);
            if (effect.IsChecked == true) { preview = true; previewTimer.Start(); Render(); }
        }));
        commands.Children.Add(ButtonFor("Stop", () => SetEffect(false)));
        status.Foreground = Violet;
        header.Children.Add(commands);
        header.Children.Add(status);
        header.Children.Add(message);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        var pages = new TabControl();
        pages.Items.Add(Tab("Effect", BuildEffect()));
        pages.Items.Add(Tab("Pipelines", BuildPipelines()));
        pages.Items.Add(Tab("Settings", BuildSettings()));
        pages.Items.Add(Tab("About", BuildAboutPage()));
        root.Children.Add(pages);
        Content = root;
        ApplyTheme();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Preferences", null, (_, _) => { pages.SelectedIndex = 2; Reveal(); });
        menu.Items.Add("Open", null, (_, _) => Reveal());
        menu.Items.Add(new Forms.ToolStripSeparator());
        trayEffect = new Forms.ToolStripMenuItem("Effect", null, (_, _) => SetEffect(effect.IsChecked != true));
        menu.Items.Add(trayEffect);
        menu.Items.Add("Stop", null, (_, _) => SetEffect(false));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Mini Utilities", null, (_, _) => OpenLink(Site + "/mini-utilities/"));
        menu.Items.Add("About", null, (_, _) => ShowAbout());
        tray.Text = "Vignette";
        tray.Icon = new Icon(Path.Combine(AppContext.BaseDirectory, "Vignette.ico"));
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Reveal();
        tray.Visible = true;

        renderTimer.Tick += (_, _) => { renderTimer.Stop(); Render(); };
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); Flush(); };
        previewTimer.Tick += (_, _) => SetEffect(false);
        winEvent = OnPeerWindowChanged;
        peerOrderTimer.Tick += (_, _) =>
        {
            peerOrderTimer.Stop();
            try { overlay.EnsureAbovePeers(); }
            catch (Win32Exception error) { SetEffect(false, "Vignette stopped. " + error.Message); }
        };
        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle).AddHook(Messages);
            hotkeyRegistered = RegisterHotKey(handle, Hotkey, 0x4000 | 8 | 4, 0x79);
            if (!hotkeyRegistered) status.Text = "The emergency shortcut is already in use.";
        };
        SystemEvents.DisplaySettingsChanged += DisplaysChanged;
        SystemEvents.SessionSwitch += SessionChanged;
        RefreshDisplays();
        RefreshPipelines();
        SyncAppearance();
        if (store.Warning is { } warning) message.Text = warning;
    }

    internal void Reveal() { if (closing) return; Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate(); }

    private FrameworkElement BuildEffect()
    {
        var stack = Section();
        var colorRow = Horizontal();
        colorRow.Children.Add(Label("Edge color",14,true));
        colorRow.Children.Add(swatch);
        edgeColor.ToolTip = "RGB color, such as #000000 for darkening or #FFFFFF for brightening.";
        edgeColor.LostKeyboardFocus += (_, _) => CommitColor();
        edgeColor.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) CommitColor(); };
        colorRow.Children.Add(edgeColor);
        colorRow.Children.Add(ButtonFor("Choose color…", () =>
        {
            using var dialog = new Forms.ColorDialog { FullOpen = true,
                Color = ColorTranslator.FromHtml(preferences.Appearance.Color) };
            if (dialog.ShowDialog(new DialogOwner(handle)) == Forms.DialogResult.OK)
                Change(preferences.Appearance with { Color = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}" });
        }));
        stack.Children.Add(colorRow);
        stack.Children.Add(Label("Dark colors shade the edges. Light colors lift them.", 13));
        (string Name, double Min, double Max)[] specs =
            [("Intensity", 0, .85), ("Clear center", .1, .9), ("Falloff width", .08, .9), ("Ellipse aspect", .65, 1.55)];
        for (int i = 0; i < specs.Length; i++)
        {
            var (name, min, max) = specs[i];
            var caption = Label(name, 14, true);
            var slider = new Slider { Minimum = min, Maximum = max, TickFrequency = .01, IsSnapToTickEnabled = true };
            System.Windows.Automation.AutomationProperties.SetName(slider, name);
            int position = i;
            slider.ValueChanged += (_, _) =>
            {
                caption.Text = $"{name}  {slider.Value:F2}";
                if (syncing) return;
                var a = preferences.Appearance;
                double value = Math.Round(slider.Value, 2);
                Change(position switch
                {
                    0 => a with { Intensity = value }, 1 => a with { Radius = value },
                    2 => a with { Softness = value }, _ => a with { Aspect = value }
                }, sync: false);
            };
            sliders[i] = slider;
            stack.Children.Add(caption);
            stack.Children.Add(slider);
        }
        return stack;
    }

    private FrameworkElement BuildPipelines()
    {
        var stack = Section();
        stack.Children.Add(Label("Pipeline", 16, true));
        pipeline.DisplayMemberPath = "Name";
        pipeline.SelectionChanged += (_, _) =>
        {
            if (syncing || pipeline.SelectedItem is not Pipeline item) return;
            editId = item.Id;
            pipelineName.Text = builtins.Contains(item) ? "" : item.Name;
            Change(item.Appearance);
        };
        stack.Children.Add(pipeline);
        stack.Children.Add(description);
        stack.Children.Add(Label("Personal pipeline name", 14));
        stack.Children.Add(pipelineName);
        var row = Horizontal();
        row.Children.Add(ButtonFor("Save as", () => EditPipeline(null)));
        updatePipeline = ButtonFor("Update", () => EditPipeline(editId));
        removePipeline = ButtonFor("Remove", RemovePipeline);
        row.Children.Add(updatePipeline);
        row.Children.Add(removePipeline);
        stack.Children.Add(row);
        return stack;
    }

    private FrameworkElement BuildSettings()
    {
        var stack = Section();
        stack.Children.Add(Label("Monitors", 17, true));
        stack.Children.Add(monitorRows);
        var dark = new CheckBox { Content = "Dark theme", IsChecked = preferences.Dark, Margin = new Thickness(0, 10, 0, 8) };
        dark.Click += (_, _) => { preferences = preferences with { Dark = dark.IsChecked == true }; ApplyTheme(); ScheduleSave(); };
        stack.Children.Add(dark);
        var login = Program.SignIn.Read();
        var startup = new CheckBox { Content = "Start with Windows", IsChecked = login.Enabled, IsEnabled = !login.Blocked,
            ToolTip = "Open Vignette in the notification area at sign-in. The effect starts off." };
        startup.Click += (_, _) =>
        {
            try { Program.SignIn.SetEnabled(startup.IsChecked == true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { message.Text = error.Message; }
            startup.IsChecked = Program.SignIn.Read().Enabled;
        };
        stack.Children.Add(startup);
        stack.Children.Add(Label("Emergency stop · Windows + Shift + F10", 14, true));
        stack.Children.Add(Label("The gradient is composited over the desktop. It does not read screen pixels.",13));
        return stack;
    }

    private FrameworkElement BuildAboutPage()
    {
        var page = Section();
        page.Children.Add(ButtonFor("About Vignette", ShowAbout));
        page.Children.Add(ButtonFor("Mini Utilities", () => OpenLink(Site + "/mini-utilities/")));
        return page;
    }

    private void ShowAbout()
    {
        Reveal();
        if (about is { } existing) { existing.Activate(); return; }
        var dialog = new Window { Owner = this, Title = "About Vignette", Width = 585, Height = 540,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Icon = Icon };
        about = dialog;
        var body = Section();
        var mark = ButtonFor("", () => OpenLink(Site + "/vibeware/manifesto"));
        mark.Content = new Image { Source = BitmapFrame.Create(new Uri(Path.Combine(AppContext.BaseDirectory, "VibeWare.png"))),
            Width = 92, Height = 92 };
        mark.ToolTip = "VibeWare manifesto";
        mark.HorizontalAlignment = HorizontalAlignment.Center;
        System.Windows.Automation.AutomationProperties.SetName(mark, "VibeWare manifesto");
        body.Children.Add(mark);
        body.Children.Add(Label("Vignette", 28, true));
        body.Children.Add(Label("VibeWare · Version " + typeof(Studio).Assembly.GetName().Version?.ToString(3),14));
        body.Children.Add(Label("A film and CRT-inspired vignette for your desktop.",15));
        body.Children.Add(Label("I’m Umberto Giacobbi. I build playful effects that make the desktop a little more personal.",14));
        body.Children.Add(Label("Independent software, provided as-is without warranty.",13));
        var links = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var (label, url) in new[] { ("Website", Site), ("Source", "https://github.com/umbertotechnopreneur/VibeKit/tree/main/Widgets/Vignette"),
            ("Terms", Site + "/terms/"), ("Privacy", Site + "/privacy/"), ("MIT license", "https://opensource.org/license/mit") })
            links.Children.Add(ButtonFor(label, () => OpenLink(url)));
        body.Children.Add(links);
        var close = ButtonFor("Close", dialog.Close);
        close.IsCancel = true;
        body.Children.Add(close);
        dialog.Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        dialog.Closed += (_, _) => about = null;
        dialog.ShowDialog();
    }

    private void CommitColor()
    {
        string hex = edgeColor.Text.Trim().ToUpperInvariant();
        var updated = preferences.Appearance with { Color = hex };
        if (updated.Valid) Change(updated);
        else { edgeColor.Text = preferences.Appearance.Color; message.Text = "Enter a color in #RRGGBB format."; }
    }

    private void Change(Appearance value, bool sync = true)
    {
        if (!value.Valid) return;
        preferences = preferences with { Appearance = value };
        if (sync) SyncAppearance();
        else UpdatePipelineSelection();
        ScheduleSave();
        if (effect.IsChecked == true)
        {
            renderTimer.Stop();
            renderTimer.Start();
        }
    }

    private void SyncAppearance()
    {
        syncing = true;
        try
        {
            var value = preferences.Appearance;
            var values = new[] { value.Intensity, value.Radius, value.Softness, value.Aspect };
            edgeColor.Text = value.Color;
            swatch.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value.Color));
            for (int i=0; i<sliders.Length; i++) sliders[i].Value = values[i];
        }
        finally { syncing = false; }
        UpdatePipelineSelection();
    }

    private void RefreshPipelines()
    {
        syncing = true;
        try { pipeline.ItemsSource = builtins.Concat(preferences.Personal).ToArray(); }
        finally { syncing = false; }
        UpdatePipelineSelection();
    }

    private void UpdatePipelineSelection()
    {
        var item = builtins.Concat(preferences.Personal)
            .FirstOrDefault(p => p.Id == editId && p.Appearance == preferences.Appearance)
            ?? builtins.Concat(preferences.Personal).FirstOrDefault(p => p.Appearance == preferences.Appearance);
        syncing = true;
        try { pipeline.SelectedItem = item; }
        finally { syncing = false; }
        description.Text = item?.Description ?? (item?.Name ?? "Custom settings");
        updatePipeline.IsEnabled = removePipeline.IsEnabled = store.Writable && preferences.Personal.Any(p => p.Id == editId);
    }

    private void EditPipeline(string? id)
    {
        try
        {
            if (!store.Writable) throw new InvalidOperationException(store.Warning);
            if (id is null && preferences.Personal.Length >= 60) throw new InvalidOperationException("The personal library can hold 60 pipelines.");
            var existing = preferences.Personal.FirstOrDefault(item => item.Id == id);
            var value = Pipeline.Personal(id, pipelineName.Text.Length == 0 ? existing?.Name ?? "" : pipelineName.Text, preferences.Appearance);
            preferences = preferences with { Personal = [..preferences.Personal.Where(item => item.Id != id), value] };
            editId = value.Id;
            RefreshPipelines();
            ScheduleSave();
        }
        catch (InvalidOperationException error) { message.Text = error.Message; }
    }

    private void RemovePipeline()
    {
        var item = preferences.Personal.FirstOrDefault(p => p.Id == editId);
        if (item is null || MessageBox.Show(this, $"Remove “{item.Name}”?", "Remove personal pipeline",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        preferences = preferences with { Personal = preferences.Personal.Where(p => p.Id != item.Id).ToArray() };
        editId = null;
        pipelineName.Clear();
        RefreshPipelines();
        ScheduleSave();
    }

    private void DisplaysChanged(object? sender, EventArgs args) => Dispatcher.BeginInvoke(() =>
    {
        if (closing) return;
        SetEffect(false, "Display layout changed. Enable the effect to resume.");
        RefreshDisplays();
    });

    private void SessionChanged(object sender, SessionSwitchEventArgs args)
    {
        if (args.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
            Dispatcher.BeginInvoke(() => SetEffect(false, "Stopped when the desktop session changed."));
    }

    private bool Selected(DisplayTarget target) => preferences.Screens is null || preferences.Screens.Any(target.Matches);

    private void RefreshDisplays()
    {
        displays = DisplayTarget.GetConnected();
        monitorRows.Children.Clear();
        if (displays.Count == 0) monitorRows.Children.Add(Label("No active displays found.", 13));
        foreach (var target in displays)
        {
            var row = new CheckBox { Content = $"{target.Label}  ·  {target.Details}", IsChecked = Selected(target), Margin = new Thickness(0, 5, 0, 5) };
            row.Click += (_, _) =>
            {
                var ids = new HashSet<string>(preferences.Screens ?? displays.SelectMany(d => d.Identifiers).ToArray(), StringComparer.OrdinalIgnoreCase);
                if (row.IsChecked == true) ids.UnionWith(target.Identifiers);
                else { ids.ExceptWith(target.Identifiers); ids.Remove(target.Device); }
                preferences = preferences with { Screens = ids.ToArray() };
                ScheduleSave();
                if (effect.IsChecked == true) Render();
            };
            monitorRows.Children.Add(row);
        }
    }

    private void SetEffect(bool enabled, string? stopReason = null)
    {
        if (enabled && !hotkeyRegistered) { SetEffect(false, "The emergency shortcut is unavailable."); return; }
        syncing = true;
        try { effect.IsChecked = enabled; trayEffect.Checked = enabled; }
        finally { syncing = false; }
        if (enabled)
        {
            Render();
        }
        else
        {
            renderTimer.Stop();
            previewTimer.Stop();
            preview = false;
            TrackPeerOrder(false);
            overlay.Dispose();
            status.Text = stopReason ?? "Stopped";
        }
    }

    private void Render()
    {
        try
        {
            if (effect.IsChecked != true) return;
            int count = overlay.Apply(displays.Where(Selected), preferences.Appearance);
            TrackPeerOrder(overlay.HasSurfaces);
            status.Text = count == 0 ? "Select a display for the effect."
                : !overlay.HasSurfaces ? "The effect is transparent at zero intensity."
                : $"{(preview ? "Preview" : "Active")} · {count} {(count == 1 ? "display" : "displays")}";
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            SetEffect(false, "Vignette stopped. " + error.Message);
        }
    }

    private void TrackPeerOrder(bool active)
    {
        if (active)
        {
            if (orderHook == 0)
                orderHook = SetWinEventHook(WindowShown, WindowReordered, 0, winEvent, 0, 0, SkipOwnProcess);
        }
        else
        {
            peerOrderTimer.Stop();
            if (orderHook != 0) { UnhookWinEvent(orderHook); orderHook = 0; }
        }
    }

    private void OnPeerWindowChanged(nint hook, uint type, nint window, int objectId, int childId, uint thread, uint tick)
    {
        if (hook != orderHook || window == 0 || objectId != 0 || childId != 0 || peerOrderTimer.IsEnabled ||
            type is not (WindowShown or WindowReordered)) return;
        // A reorder may name the parent of the moved windows instead of a peer.
        if ((type == WindowReordered && window == GetDesktopWindow()) || overlay.IsPeerOnSelectedDisplay(window))
            peerOrderTimer.Start();
    }

    private nint Messages(nint window, int code, nint wp, nint lp, ref bool handled)
    {
        if (code == 0x0312 && wp == Hotkey) { SetEffect(false); handled = true; }
        return 0;
    }

    private void ScheduleSave() { if (!store.Writable) return; dirty = true; saveTimer.Stop(); saveTimer.Start(); }

    private void Flush()
    {
        if (!dirty) return;
        try { store.Save(preferences); dirty = false; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { message.Text = "Could not save settings. " + error.Message; }
    }

    internal void EndSession()
    {
        if (closing) return;
        closing = true;
        Flush();
        Release();
    }

    private void Quit()
    {
        if (closing) return;
        saveTimer.Stop();
        Flush();
        if (dirty && MessageBox.Show(this, "Settings could not be saved. Quit anyway?", "Vignette",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        closing = true;
        Release();
        Application.Current.Shutdown();
    }

    private void Release()
    {
        SetEffect(false);
        if (hotkeyRegistered) UnregisterHotKey(handle, Hotkey);
        SystemEvents.DisplaySettingsChanged -= DisplaysChanged;
        SystemEvents.SessionSwitch -= SessionChanged;
        tray.Visible = false;
        tray.Icon?.Dispose();
        tray.ContextMenuStrip?.Dispose();
        tray.Dispose();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!closing) { e.Cancel = true; Hide(); }
        base.OnClosing(e);
    }

    private void ApplyTheme()
    {
        Background = PreferencesBrush(preferences.Dark, "#19191F", "#F5F2F9");
        Foreground = PreferencesBrush(preferences.Dark, "#EEEAF4", "#2B2333");
        Resources[SystemColors.ControlBrushKey] = Background;
        Resources[SystemColors.ControlTextBrushKey] = Foreground;
        Resources[SystemColors.WindowBrushKey] = PreferencesBrush(preferences.Dark, "#24242B", "#FFFFFF");
        Resources[SystemColors.WindowTextBrushKey] = Foreground;
    }

    private static Brush PreferencesBrush(bool dark, string darkColor, string lightColor) =>
        SystemParameters.HighContrast ? SystemColors.WindowBrush : new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? darkColor : lightColor));

    private static TextBlock Label(string text, double size, bool bold = false) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 2, 0, 5), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal
    };

    private static StackPanel Section() => new() { Margin = new Thickness(16) };
    private static StackPanel Horizontal() => new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 6) };
    private static TabItem Tab(string name, FrameworkElement content) => new()
    {
        Header = name, Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
    };

    private static Button ButtonFor(string text, Action execute)
    {
        var button = new Button { Content = text, Margin = new Thickness(4, 0, 4, 5), Padding = new Thickness(12, 5, 12, 5) };
        button.Click += (_, _) => execute();
        return button;
    }

    private static void OpenLink(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private sealed class DialogOwner(nint window) : Forms.IWin32Window { public nint Handle { get; } = window; }
    private delegate void WinEventCallback(nint hook, uint evt, nint window, int obj, int child, uint thread, uint time);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint min, uint max, nint dll, WinEventCallback callback, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();
}
