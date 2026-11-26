// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Tools.Widgets;
using Windows.System;
using Windows.UI;
using static Tools.Widgets.EffectStudioLayout;

namespace VibeWare.Vignette;

internal sealed partial class Studio
{
    private const string EmergencyKeys = "Windows + Shift + F10";
    private const string SettingsPageName = "Settings";
    private readonly Grid root = new();
    private readonly TextBlock status = new() { Text = "Vignette effect is not running." };
    private readonly InfoBar message = new() { Severity = InfoBarSeverity.Warning, IsClosable = true };
    private readonly TextBlock description = SettingsHint("Custom settings");
    private readonly ToggleSwitch effect = new();
    private readonly StackPanel monitorRows = new();
    private readonly TextBlock monitorHint = SettingsHint("");
    private readonly TextBlock emergencyStatus = SettingsHint("");
    private readonly TextBlock diagnosticShortcut = SettingsHint("");
    private readonly TextBlock diagnosticDisplays = SettingsHint("");
    private readonly StackPanel presetRows = new() { Spacing = 0 };
    private readonly Dictionary<string, (Border Row, TextBlock Title)> presetCards = new();
    private readonly StackPanel presetEditor = new() { Spacing = 8, Visibility = Visibility.Collapsed };
    private readonly ComboBox pipeline = new() { PlaceholderText = "Custom settings", DisplayMemberPath = nameof(Pipeline.Name) };
    private readonly TextBox pipelineName = new() { MaxLength = 80, PlaceholderText = "Pipeline name" };
    private readonly TextBox edgeColor = new() { MaxLength = 7, Width = 105, PlaceholderText = "#000000" };
    private readonly Border swatch = new() { Width = 22, Height = 18, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1) };
    private readonly Slider[] sliders = new Slider[4];
    private StudioPages pages = null!;
    private Button updatePipeline = null!;
    private Button removePipeline = null!;
    private bool dialogOpen;

    private void BuildUi()
    {
        Initialize(this, root, "Vignette", "A film and CRT-inspired vignette for your desktop.");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        root.Children.Add(StatusBar(status, effect, message, () => overlay.HasSurfaces));

        pages = new StudioPages(
            new StudioPage("Effect", "\uE790", BuildEffect()),
            new StudioPage("Presets", "\uE8F1", BuildPipelines()),
            new StudioPage(SettingsPageName, "\uE713", BuildSettings()),
            new StudioPage("Diagnostics", "\uE9D9", BuildDiagnostics()));
        Grid.SetRow(pages, 1);
        root.Children.Add(pages);
        foreach (var scroll in pages.Scrollers)
            RetroStudio.ScrollBarBehavior.HoverScrollBar(scroll, scroll, root);

        var shortcut = new StackPanel { Spacing = 2 };
        var keys = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        keys.Children.Add(new FontIcon { Glyph = "\uE765", FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        keys.Children.Add(new TextBlock { Text = $"Emergency shortcut: {EmergencyKeys}", FontSize = 12, TextWrapping = TextWrapping.Wrap });
        shortcut.Children.Add(keys);
        shortcut.Children.Add(emergencyStatus);
        var footer = FooterBar(shortcut, WidgetFooter.CreateCompact("Vignette", async () => await ShowAboutAsync()));
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        ShowSettings();
        ApplyTheme();
    }

    private void ShowSettings() => pages.Select(SettingsPageName);

    private FrameworkElement BuildEffect()
    {
        var page = CreatePage("Effect");
        AutomationProperties.SetName(edgeColor, "Edge color in hexadecimal");
        AutomationProperties.SetAutomationId(edgeColor, "EdgeColor");
        edgeColor.TextChanged += (_, _) =>
        {
            if (syncing) return;
            var updated = preferences.Appearance with { Color = ReadEdgeColor() };
            if (updated.Valid && updated != preferences.Appearance)
                Change(updated, sync: false);
        };
        edgeColor.LostFocus += (_, _) => CommitColor();
        edgeColor.KeyDown += (_, args) =>
        {
            if (args.Key != VirtualKey.Enter) return;
            CommitColor();
            args.Handled = true;
        };
        swatch.BorderBrush = new SolidColorBrush(Color.FromArgb(128, 128, 128, 128));
        var pickerLabel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        pickerLabel.Children.Add(swatch);
        pickerLabel.Children.Add(new TextBlock { Text = "Choose…", VerticalAlignment = VerticalAlignment.Center });
        var chooseColor = new Button { Content = pickerLabel };
        AutomationProperties.SetName(chooseColor, "Choose edge color");
        chooseColor.Click += async (_, _) => await ChooseColorAsync();
        var fields = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        fields.Children.Add(edgeColor);
        fields.Children.Add(chooseColor);
        page.Children.Add(RowGroup("Color", PreferenceRow("Edge color",
            "Dark colors shade the edges. Light colors brighten them.", fields)));

        (string Title, string Detail, double Minimum, double Maximum)[] specs =
        [
            ("Intensity", "How strongly the vignette tints the edges.", 0, .85),
            ("Clear center", "How much of the center stays unaffected.", .1, .9),
            ("Falloff width", "How gradually the tint builds from the clear center to the edges.", .08, .9),
            ("Ellipse aspect", "Stretches the clear area horizontally or vertically.", .65, 1.55)
        ];
        var rows = new FrameworkElement[specs.Length];
        for (int i = 0; i < specs.Length; i++)
        {
            int position = i;
            var spec = specs[i];
            var number = new TextBlock();
            var slider = new Slider { Minimum = spec.Minimum, Maximum = spec.Maximum, StepFrequency = .01 };
            number.Text = slider.Value.ToString("F2", CultureInfo.CurrentCulture);
            slider.ValueChanged += (_, args) =>
            {
                double value = Math.Round(args.NewValue, 2);
                number.Text = value.ToString("F2", CultureInfo.CurrentCulture);
                if (syncing) return;
                var current = preferences.Appearance;
                Change(position switch
                {
                    0 => current with { Intensity = value },
                    1 => current with { Radius = value },
                    2 => current with { Softness = value },
                    _ => current with { Aspect = value }
                }, sync: false);
            };
            sliders[i] = slider;
            rows[i] = SliderRow(spec.Title, spec.Detail, slider, number);
        }
        page.Children.Add(RowGroup("Gradient", rows));
        return page;
    }

    private async Task ChooseColorAsync()
    {
        var picker = new ColorPicker
        {
            Color = ReadColor(preferences.Appearance.Color),
            IsAlphaEnabled = false,
            IsAlphaSliderVisible = false,
            IsAlphaTextInputVisible = false
        };
        var choice = await ShowStudioDialogAsync(new ContentDialog
        {
            Title = "Choose edge color",
            Content = picker,
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        });
        if (choice == ContentDialogResult.Primary)
        {
            var color = picker.Color;
            Change(preferences.Appearance with { Color = $"#{color.R:X2}{color.G:X2}{color.B:X2}" });
        }
    }

    private static Color ReadColor(string value)
    {
        uint rgb = uint.Parse(value.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        return Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    private FrameworkElement BuildPipelines()
    {
        var page = new StackPanel { Spacing = 12 };
        pipeline.SelectionChanged += (_, _) =>
        {
            if (syncing || pipeline.SelectedItem is not Pipeline item) return;
            editId = item.Id;
            pipelineName.Text = builtins.Contains(item) ? "" : item.Name;
            Change(item.Appearance);
        };
        page.Children.Add(SettingsHint("Presets are saved sets of vignette settings."));
        var create = ActionButton("New preset", () => { pipelineName.Text = ""; presetEditor.Visibility = Visibility.Visible; pipelineName.Focus(FocusState.Programmatic); });
        create.IsEnabled = store.Writable;
        page.Children.Add(create);
        pipelineName.Header = "Preset name";
        AutomationProperties.SetAutomationId(pipelineName, "PersonalPipelineName");
        presetEditor.Children.Add(pipelineName);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(ActionButton("Save", () => { if (EditPipeline(null)) presetEditor.Visibility = Visibility.Collapsed; }));
        actions.Children.Add(ActionButton("Cancel", () => presetEditor.Visibility = Visibility.Collapsed));
        presetEditor.Children.Add(actions);
        page.Children.Add(presetEditor);
        updatePipeline = new Button();
        removePipeline = new Button();
        page.Children.Add(presetRows);
        page.Children.Add(SettingsHint("Library: " + store.FilePath));
        return page;
    }

    private void RefreshPresetRows()
    {
        var items = builtins.Concat(preferences.Personal).ToArray();
        foreach (var id in presetCards.Keys.Except(items.Select(p => p.Id)).ToArray())
        {
            presetRows.Children.Remove(presetCards[id].Row);
            presetCards.Remove(id);
        }
        for (int index = 0; index < items.Length; index++)
        {
            var item = items[index];
            if (!presetCards.TryGetValue(item.Id, out var card))
            {
                string id = item.Id;
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var label = new StackPanel { Spacing = 4 };
                var title = new TextBlock { FontSize = 18 };
                label.Children.Add(title);
                label.Children.Add(new TextBlock { Text = item.Description ?? "Personal vignette preset", FontSize = 12, TextWrapping = TextWrapping.Wrap });
                var open = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Padding = new Thickness(4, 9, 4, 9) };
                open.Click += (_, _) => { var current = builtins.Concat(preferences.Personal).FirstOrDefault(p => p.Id == id); if (current is not null) { editId = id; Change(current.Appearance); } };
                row.Children.Add(open);
                var menu = new MenuFlyout();
                var duplicate = new MenuFlyoutItem { Text = "Duplicate", IsEnabled = store.Writable };
                duplicate.Click += (_, _) => DuplicatePreset(id);
                menu.Items.Add(duplicate);
                if (!builtins.Any(p => p.Id == id))
                {
                    var update = new MenuFlyoutItem { Text = "Update with current settings", IsEnabled = store.Writable };
                    update.Click += (_, _) => { pipelineName.Text = ""; EditPipeline(id); };
                    menu.Items.Add(update);
                    var remove = new MenuFlyoutItem { Text = "Delete", IsEnabled = store.Writable };
                    remove.Click += async (_, _) => await RemovePipelineAsync(id);
                    menu.Items.Add(remove);
                }
                var more = new Button { Content = "\u22ee", Flyout = menu, VerticalAlignment = VerticalAlignment.Center,
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
                AutomationProperties.SetName(more, "Actions for " + item.Name);
                Grid.SetColumn(more, 1); row.Children.Add(more);
                card = (new Border { Child = row, BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = new SolidColorBrush(Color.FromArgb(60, 191, 187, 178)) }, title);
                presetCards.Add(id, card);
            }
            card.Title.Text = (item.Id == (pipeline.SelectedItem as Pipeline)?.Id ? "\u2713 " : "") + item.Name;
            int previous = presetRows.Children.IndexOf(card.Row);
            if (previous == index) continue;
            if (previous >= 0) presetRows.Children.RemoveAt(previous);
            presetRows.Children.Insert(index, card.Row);
        }
    }

    private FrameworkElement BuildSettings()
    {
        var page = SettingsPage();
        var theme = new ComboBox { ItemsSource = new[] { "System", "Light", "Dark" }, SelectedIndex = (int)preferences.Theme };
        theme.SelectionChanged += (_, _) =>
        {
            if (theme.SelectedIndex < 0) return;
            preferences = preferences.WithTheme((ThemeMode)theme.SelectedIndex);
            ApplyTheme();
            ScheduleSave();
        };
        var acrylic = new ToggleSwitch { IsOn = preferences.UseAcrylic };
        acrylic.Toggled += (_, _) =>
        {
            preferences = preferences with { UseAcrylic = acrylic.IsOn };
            ApplyTheme();
            ScheduleSave();
        };
        page.Children.Add(RowGroup("Appearance",
            PreferenceRow("Effect", "Enable or disable the vignette effect.", effect),
            PreferenceRow("Theme", "App theme.", theme),
            PreferenceRow("Acrylic background", "Use a translucent background for this window.", acrylic)));

        var monitors = new StackPanel { Spacing = 8 };
        monitors.Children.Add(RowGroup("Monitors", monitorRows));
        monitors.Children.Add(monitorHint);
        page.Children.Add(monitors);

        var startup = new ToggleSwitch();
        var startupDetail = SettingsHint("");
        bool readingStartup = false;
        void ReadStartup()
        {
            readingStartup = true;
            try
            {
                var state = App.SignIn.Read();
                startup.IsOn = state.Enabled;
                startup.IsEnabled = !state.Blocked;
                startupDetail.Text = state.Blocked ? "Windows Startup Apps has disabled this entry."
                    : state.Enabled ? "Open in the notification area at sign-in. Start the effect manually."
                    : "Open only when you launch it.";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                startup.IsEnabled = false;
                startupDetail.Text = "The Windows startup setting is unavailable.";
            }
            finally { readingStartup = false; }
        }
        ReadStartup();
        startup.Toggled += (_, _) =>
        {
            if (readingStartup) return;
            try { App.SignIn.SetEnabled(startup.IsOn); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
            {
                ShowMessage(error.Message);
            }
            ReadStartup();
        };
        page.Children.Add(RowGroup("Startup", PreferenceRow("Start with Windows", startupDetail, startup)));
        return page;
    }

    private void RefreshMonitorRows()
    {
        monitorRows.Children.Clear();
        if (displays.Count == 0)
            monitorRows.Children.Add(PreferenceRow("No monitors found", "Connect a display to select it.", new TextBlock()));
        foreach (var target in displays)
        {
            if (monitorRows.Children.Count > 0)
                monitorRows.Children.Add(new Border { Height = 1, Style = (Style)Application.Current.Resources["DividerStyle"] });
            var toggle = new ToggleSwitch { IsOn = Selected(target) };
            var row = PreferenceRow(target.Label, target.Details, toggle);
            AutomationProperties.SetName(toggle, $"{target.Label}, {target.Details}");
            AutomationProperties.SetAutomationId(toggle, $"Monitor-{target.Device}");
            toggle.Toggled += (_, _) =>
            {
                var ids = new HashSet<string>(
                    preferences.Screens ?? displays.SelectMany(display => display.Identifiers).ToArray(),
                    StringComparer.OrdinalIgnoreCase);
                if (toggle.IsOn) ids.UnionWith(target.Identifiers);
                else
                {
                    ids.ExceptWith(target.Identifiers);
                    ids.Remove(target.Device);
                }
                preferences = preferences with { Screens = ids.ToArray() };
                ScheduleSave();
                UpdateMonitorHint();
                if (effect.IsOn) Render();
            };
            monitorRows.Children.Add(row);
        }
        UpdateMonitorHint();
    }

    private void UpdateMonitorHint()
    {
        bool missing = preferences.Screens?.Any(id => !displays.Any(display => display.Matches(id))) == true;
        monitorHint.Text = missing ? "A selected monitor is disconnected. Its selection is saved."
            : "Choose where the effect runs when Effect is on.";
        diagnosticDisplays.Text = $"{displays.Count(Selected)} selected of {displays.Count} connected.";
    }

    private FrameworkElement BuildDiagnostics()
    {
        var page = CreatePage("Diagnostics");
        page.Children.Add(RowGroup("Desktop overlay",
            PreferenceRow("Displays", diagnosticDisplays, new TextBlock()),
            PreferenceRow("Emergency shortcut", diagnosticShortcut, new TextBlock()),
            PreferenceRow("Renderer", "The vignette is composited over the desktop without reading screen pixels.", new TextBlock())));
        return page;
    }

    private void UpdateEmergencyShortcut()
    {
        diagnosticShortcut.Text = hotkeyRegistered ? $"{EmergencyKeys} stops the effect."
            : "The emergency shortcut could not be registered.";
        emergencyStatus.Text = hotkeyRegistered ? "" : "Unavailable. The effect stays off.";
        emergencyStatus.Visibility = hotkeyRegistered ? Visibility.Collapsed : Visibility.Visible;
        effect.IsEnabled = hotkeyRegistered;
    }

    private void ShowMessage(string text)
    {
        message.Message = text;
        message.IsOpen = true;
    }

    private async Task ShowAboutAsync()
    {
        if (closing || dialogOpen) return;
        Reveal();
        var content = new StackPanel { Spacing = 10 };
        var badge = new HyperlinkButton
        {
            NavigateUri = new Uri(Site + "/vibeware/manifesto"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Content = new Image { Source = new BitmapImage(new Uri("ms-appx:///Assets/VibeWare.png")), Width = 84, Height = 84 }
        };
        AutomationProperties.SetName(badge, "VibeWare manifesto");
        content.Children.Add(badge);
        content.Children.Add(SettingsHint("VibeWare · Version " + typeof(Studio).Assembly.GetName().Version?.ToString(3)));
        content.Children.Add(new TextBlock
        {
            Text = "A film and CRT-inspired vignette for your desktop. Created by Umberto Giacobbi.",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(SettingsHint("Independent software, provided as-is without warranty."));
        foreach (var (title, address) in new[]
        {
            ("Website", Site),
            ("Mini Utilities", Site + "/mini-utilities/"),
            ("Source", "https://github.com/umbertotechnopreneur/VibeKit/tree/main/Widgets/Vignette"),
            ("Terms", Site + "/terms/"),
            ("Privacy", Site + "/privacy/"),
            ("MIT license", "https://opensource.org/license/mit")
        })
            content.Children.Add(new HyperlinkButton { Content = title, NavigateUri = new Uri(address), Padding = new Thickness(0, 2, 0, 2) });
        await ShowStudioDialogAsync(new ContentDialog
        {
            Title = "About Vignette",
            Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            CloseButtonText = "Close"
        });
    }

    private async Task<ContentDialogResult> ShowStudioDialogAsync(ContentDialog dialog)
    {
        if (dialogOpen || root.XamlRoot is not { } xamlRoot) return ContentDialogResult.None;
        dialogOpen = true;
        try
        {
            dialog.XamlRoot = xamlRoot;
            dialog.RequestedTheme = root.ActualTheme;
            return await dialog.ShowAsync();
        }
        finally { dialogOpen = false; }
    }

    private static StackPanel CreatePage(string title)
    {
        var page = new StackPanel { Spacing = 16 };
        var heading = new TextBlock { Text = title, FontSize = 26, FontWeight = FontWeights.SemiBold };
        page.Children.Add(heading);
        return page;
    }

    private static Button ActionButton(string name, Action execute)
    {
        var button = new Button { Content = name };
        button.Click += (_, _) => execute();
        return button;
    }
}
