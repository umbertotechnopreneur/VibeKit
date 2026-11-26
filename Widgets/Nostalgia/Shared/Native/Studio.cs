// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Serilog;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Tools.Widgets;
using static Tools.Widgets.EffectStudioLayout;

namespace VibeWare.EffectsStudio;

internal sealed partial class Studio : Window
{
    private readonly Grid root = new();
    private readonly ToggleSwitch effect = new();
    private readonly TextBlock status = new(), diagnosticState = SettingsHint(""), diagnosticGroup = SettingsHint("");
    private readonly ComboBox theme = new() { ItemsSource = new[] { "System", "Light", "Dark" } };
    private readonly ToggleSwitch acrylic = new();
    private readonly InfoBar error = new() { Severity = InfoBarSeverity.Warning, IsClosable = true };
    private readonly Native.Notification notification;
    private readonly StudioPages pages;
    private readonly DispatcherQueueTimer timer;
    private readonly WidgetTrayIcon tray;
    private readonly WidgetCloseDialog closeDialog = new();
    private bool syncing, quitting;
    internal bool HasTray => tray.IsVisible;

    internal Studio(bool startup)
    {
        Title = "VibeWare – " + Product;
        string iconPath = Path.Combine(AppContext.BaseDirectory, Identifier + ".ico");
        AppWindow.SetIcon(iconPath);
        Initialize(this, root, Product);
        notification = OnNotification;
        if (Native.WidgetStart(WinRT.Interop.WindowNative.GetWindowHandle(this), startup ? 1 : 0, notification) == 0)
            throw new InvalidOperationException(Native.Status);

        syncing = true;
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        root.Children.Add(StatusBar(status, effect, error));
        var settings = StudioSectionPage("Settings");
        settings.Children.Add(AppearanceGroup("Enable or disable " + Product + ".", effect, theme, acrylic));
        ReadAppearance();
        AddSettings(settings);
        pages = StandardPages(BuildEffectPage(), BuildProfilePage(), settings, BuildDiagnostics());
        Grid.SetRow(pages, 1);
        root.Children.Add(pages);
        foreach (var scroll in pages.Scrollers) ScrollBarBehavior.HoverScrollBar(scroll, scroll, root);
        pages.Select("Settings");

        var footer = FooterBar(FooterContent(), WidgetFooter.Create(Product, ShowAbout));
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        effect.Toggled += (_, _) => Change(() => Native.WidgetEnable(effect.IsOn ? 1 : 0));
        var menu = new WidgetTrayMenu(this, Reveal, () => { pages.Select("Settings"); Reveal(); }, ShowAbout,
            Quit,
            () => { Native.WidgetRead(out var state); return (state.Active != 0, true); },
            enabled => Change(() => Native.WidgetEnable(enabled ? 1 : 0)), AddTrayCommands);
        tray = new WidgetTrayIcon(this, Product, iconPath, menu, Reveal);
        try { tray.Show(); }
        catch (System.ComponentModel.Win32Exception)
        {
            error.Message = "The notification area is unavailable. Keep this window open to use the effect.";
            error.IsOpen = true;
        }
        AppWindow.Closing += OnClosing;
        timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += (_, _) => Refresh();
        timer.Start();
        Closed += (_, _) =>
        {
            quitting = true;
            timer.Stop();
            Native.WidgetStop();
            tray.Dispose();
        };
        syncing = false;
        Refresh();
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (quitting) return;
        args.Cancel = true;
        if (closeDialog.FocusIfOpen(this) || root.XamlRoot is null) return;
        try
        {
            var choice = await closeDialog.ChooseAsync(this, Product, HasTray);
            if (quitting) return;
            if (choice == WidgetCloseChoice.Quit) Quit();
            else if (choice == WidgetCloseChoice.Minimize)
            {
                if (HasTray) sender.Hide();
                else
                {
                    error.Message = "The notification area is unavailable. The window is still open.";
                    error.IsOpen = true;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            if (!quitting)
            {
                Log.Warning(ex, "Could not display the close dialog");
                error.Message = "Could not show the close options.";
                error.IsOpen = true;
            }
        }
    }

    private void Quit()
    {
        if (quitting || closeDialog.RequestQuitIfOpen()) return;
        quitting = true;
        Close();
    }

    internal void Reveal()
    {
        if (quitting) return;
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Refresh();
        Activate();
    }

    private void Refresh()
    {
        syncing = true;
        try
        {
            Native.WidgetRead(out var state);
            effect.IsOn = state.Active != 0;
            string detail = Native.Status;
            if (status.Text != detail)
            {
                status.Text = diagnosticState.Text = detail;
                Log.Information("Effect status {Status}", detail);
            }
            diagnosticGroup.Text = state.Mixed != 0 ? "Mixed or unavailable" : state.AllEnabled != 0 ? "All enabled" : "All stopped";
            RefreshProduct(state.Active != 0);
        }
        finally { syncing = false; }
    }

    private void Change(Action action)
    {
        if (syncing || quitting) return;
        action();
        Refresh();
    }

    private void OnNotification(nint message)
    {
        string text = Marshal.PtrToStringUni(message) ?? "";
        DispatcherQueue.TryEnqueue(() =>
        {
            if (quitting || !HasTray) return;
            try { tray.Notify(Product, text, silent: true); }
            catch (System.ComponentModel.Win32Exception) { Log.Warning("The notification area could not display the effect notice"); }
        });
    }

    private FrameworkElement BuildDiagnostics()
    {
        var page = StudioSectionPage("Diagnostics");
        page.Children.Add(RowGroup("Status",
            PreferenceRow("Effect", diagnosticState, new TextBlock()),
            PreferenceRow("All open effects", diagnosticGroup, new TextBlock())));
        page.Children.Add(Card(DiagnosticAboutActions.Create(Identifier, Product)));
        return page;
    }

    private void ReadAppearance()
    {
        string path = @"HKEY_CURRENT_USER\Software\VibeWare\" + Identifier + @"\Studio";
        int Read(string name, int initialValue, int maximum) => Registry.GetValue(path, name, null) switch
        {
            null => initialValue,
            int value when value >= 0 && value <= maximum => value,
            _ => throw new InvalidDataException($"Preference '{name}' must be an integer between 0 and {maximum}.")
        };
        theme.SelectedIndex = Read("Theme", 0, 2);
        acrylic.IsOn = Read("Acrylic", 1, 1) != 0;
        void Apply()
        {
            root.RequestedTheme = theme.SelectedIndex switch { 1 => ElementTheme.Light, 2 => ElementTheme.Dark, _ => ElementTheme.Default };
            root.ClearValue(Grid.BackgroundProperty);
            try
            {
                SystemBackdrop = acrylic.IsOn ? new DesktopAcrylicBackdrop() : null;
                if (SystemBackdrop is not null) root.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            }
            catch (COMException) { SystemBackdrop = null; }
        }
        void Save(string name, int value)
        {
            Apply();
            try { Registry.SetValue(path, name, value, RegistryValueKind.DWord); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                error.Message = "Could not save the appearance setting.";
                error.IsOpen = true;
            }
        }
        Apply();
        theme.SelectionChanged += (_, _) => Save("Theme", theme.SelectedIndex);
        acrylic.Toggled += (_, _) => Save("Acrylic", acrylic.IsOn ? 1 : 0);
    }

    private static Button Button(string text, Action action)
    {
        var button = new Button { Content = text };
        button.Click += (_, _) => action();
        return button;
    }

    private static StackPanel Footnote(string glyph, TextBlock label)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        line.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        label.FontSize = 12;
        label.TextWrapping = TextWrapping.Wrap;
        line.Children.Add(label);
        return line;
    }

    private static void OpenLink(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();

    private void ShowAbout() => WidgetAboutWindow.Show(this, new WidgetIdentity(
        Identifier, Product, typeof(Studio).Assembly.GetName().Version!, Description, Identifier + ".ico",
        "https://github.com/umbertotechnopreneur/VibeKit/tree/main/Widgets/Nostalgia/" + Identifier, "MIT License"));

}
