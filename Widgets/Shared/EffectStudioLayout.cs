using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace Tools.Widgets;

internal static class EffectStudioLayout
{
    internal static void Initialize(Window window, Grid root, string title, string description)
    {
        root.Style = GetStyle("WindowSurfaceStyle");
        foreach (var height in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            root.RowDefinitions.Add(new RowDefinition { Height = height });
        AutomationProperties.SetName(root, title);
        window.Content = root;
        var accessibility = new Windows.UI.ViewManagement.AccessibilitySettings();
        void UpdateTitleBar()
        {
            if (!AppWindowTitleBar.IsCustomizationSupported()) return;
            // AccessibilitySettings.HighContrastChanged can reject event registration in
            // desktop sessions. Native caption styling must never prevent startup.
            string key = root.ActualTheme == ElementTheme.Dark ? "Dark" : "Light";
            try { if (accessibility.HighContrast) key = "HighContrast"; }
            catch (COMException) { /* WinUI still applies its own contrast resources. */ }
            if (!Application.Current.Resources.ThemeDictionaries.TryGetValue(key, out var dictionary) ||
                dictionary is not ResourceDictionary theme ||
                !theme.TryGetValue("StudioChromeBrush", out var chrome) || chrome is not SolidColorBrush backgroundBrush ||
                !theme.TryGetValue("StudioHeadingBrush", out var heading) || heading is not SolidColorBrush foregroundBrush) return;
            var foreground = key == "HighContrast" ? foregroundBrush.Color
                : root.ActualTheme == ElementTheme.Dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
            try
            {
                var titleBar = window.AppWindow.TitleBar;
                titleBar.BackgroundColor = titleBar.InactiveBackgroundColor = backgroundBrush.Color;
                titleBar.ButtonBackgroundColor = titleBar.ButtonInactiveBackgroundColor = backgroundBrush.Color;
                titleBar.ForegroundColor = titleBar.ButtonForegroundColor = foreground;
                titleBar.InactiveForegroundColor = titleBar.ButtonInactiveForegroundColor = foreground;
            }
            catch (COMException) { /* Keep the system caption if customization is unavailable. */ }
        }
        root.ActualThemeChanged += (_, _) => UpdateTitleBar();
        root.Loaded += (_, _) => UpdateTitleBar();
        window.Activated += (_, _) => UpdateTitleBar();
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(window)) / 96.0;
        var area = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        window.AppWindow.Resize(new SizeInt32(Math.Min((int)(720 * scale), area.Width - 48),
            Math.Min((int)(880 * scale), area.Height - 48)));
    }

    internal static Border StatusBar(TextBlock description, ToggleSwitch effect, UIElement? recovery = null,
        Func<bool>? isActive = null)
    {
        var state = new TextBlock { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var dot = new TextBlock { Text = "●", FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        void Update()
        {
            bool active = isActive?.Invoke() ?? effect.IsOn;
            state.Text = active ? "Effect: Active" : effect.IsOn ? "Effect: Waiting" : "Effect: Stopped";
            dot.Style = GetStyle(active ? "StudioActiveStatusStyle" : "SecondaryTextStyle");
        }
        effect.Toggled += (_, _) => Update();
        Update();
        var leading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        leading.Children.Add(dot);
        leading.Children.Add(state);
        var line = new Grid { ColumnSpacing = 16 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.Children.Add(leading);
        description.FontSize = 12;
        description.Style = GetStyle("SecondaryTextStyle");
        description.TextWrapping = TextWrapping.NoWrap;
        description.TextTrimming = TextTrimming.CharacterEllipsis;
        description.VerticalAlignment = VerticalAlignment.Center;
        ToolTipService.SetToolTip(description, description.Text);
        description.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) =>
        {
            ToolTipService.SetToolTip(description, description.Text);
            if (isActive is not null) Update();
        });
        Grid.SetColumn(description, 1);
        line.Children.Add(description);
        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(line);
        if (recovery is not null) content.Children.Add(recovery);
        return new Border { Child = content, Padding = new Thickness(24, 8, 24, 8),
            BorderThickness = new Thickness(0, 0, 0, 1), Style = GetStyle("StudioChromeStyle") };
    }

    internal static Border FooterBar(FrameworkElement emergency, FrameworkElement brand)
    {
        emergency.VerticalAlignment = VerticalAlignment.Center;
        brand.VerticalAlignment = VerticalAlignment.Center;
        brand.HorizontalAlignment = HorizontalAlignment.Right;
        var row = Columns(emergency, brand);
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.SizeChanged += (_, args) =>
        {
            bool stacked = args.NewSize.Width < 500;
            row.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : GridLength.Auto;
            Grid.SetColumnSpan(emergency, stacked ? 2 : 1);
            Grid.SetRow(brand, stacked ? 1 : 0);
            Grid.SetColumn(brand, stacked ? 0 : 1);
            Grid.SetColumnSpan(brand, stacked ? 2 : 1);
        };
        return new Border { Child = row, Padding = new Thickness(24, 6, 24, 6),
            BorderThickness = new Thickness(0, 1, 0, 0), Style = GetStyle("StudioChromeStyle") };
    }

    internal sealed class EmergencyShortcutRow : Grid
    {
        private readonly TextBlock status = SettingsHint("");
        internal ToggleSwitch Toggle { get; } = new()
        {
            MinWidth = 0, Width = 40, OffContent = null, OnContent = null,
            VerticalAlignment = VerticalAlignment.Center
        };

        internal EmergencyShortcutRow(string keys, string actionName)
        {
            var heading = new Grid { ColumnSpacing = 8 };
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            heading.Children.Add(new FontIcon { Glyph = "\uE765", FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            var label = new TextBlock { Text = $"Emergency shortcut: {keys}", FontSize = 12, TextWrapping = TextWrapping.Wrap };
            SetColumn(label, 1);
            heading.Children.Add(label);

            var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(heading);
            text.Children.Add(status);
            AutomationProperties.SetName(Toggle, actionName);
            AutomationProperties.SetAutomationId(Toggle, "EmergencyShortcut");
            Children.Add(Columns(text, Toggle));
        }

        internal void SetStatus(string message, bool registered)
        {
            status.Text = message;
            status.Visibility = registered ? Visibility.Collapsed : Visibility.Visible;
            AutomationProperties.SetHelpText(Toggle, message);
            ToolTipService.SetToolTip(Toggle, message);
        }
    }

    internal static StackPanel SettingsPage()
    {
        var page = new StackPanel { Spacing = 16 };
        page.Children.Add(new TextBlock { Text = "Settings", FontSize = 26, FontWeight = FontWeights.SemiBold });
        return page;
    }

    internal static StackPanel RowGroup(string title, params FrameworkElement[] rows)
    {
        var heading = new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold,
            Style = GetStyle("StudioSectionHeadingStyle"), TextWrapping = TextWrapping.Wrap };
        var group = new StackPanel { Spacing = 8 };
        group.Children.Add(heading);
        var body = new StackPanel();
        foreach (var row in rows)
        {
            if (body.Children.Count > 0)
                body.Children.Add(new Border { Height = 1, Style = GetStyle("DividerStyle") });
            body.Children.Add(row);
        }
        group.Children.Add(new Border { Child = body, CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1), Style = GetStyle("StudioRowGroupStyle") });
        return group;
    }

    internal static Grid PreferenceRow(string label, string description, FrameworkElement control, string? controlHint = null) =>
        PreferenceRow(label, SettingsHint(description), control, controlHint);

    internal static Grid PreferenceRow(string label, TextBlock description, FrameworkElement control, string? controlHint = null)
    {
        var row = new Grid { Padding = new Thickness(16, 8, 16, 8), ColumnSpacing = 20, MinHeight = 52 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(232) });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = label, FontSize = 14, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap });
        description.FontSize = 12;
        description.TextWrapping = TextWrapping.Wrap;
        description.Style = GetStyle("SecondaryTextStyle");
        text.Children.Add(description);
        row.Children.Add(text);
        var field = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        control.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(control, label);
        AutomationProperties.SetAutomationId(control, "Setting" + label.Replace(" ", ""));
        if (control is ToggleSwitch toggle)
        {
            toggle.MinWidth = 0;
            toggle.HorizontalAlignment = HorizontalAlignment.Right;
            toggle.OnContent = new TextBlock { Text = "On", MinWidth = 24 };
            toggle.OffContent = new TextBlock { Text = "Off", MinWidth = 24 };
        }
        else if (control is ComboBox)
        {
            control.MinWidth = 0;
            control.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        else control.HorizontalAlignment = HorizontalAlignment.Right;
        field.Children.Add(control);
        if (controlHint is not null) field.Children.Add(SettingsHint(controlHint));
        Grid.SetColumn(field, 1);
        row.Children.Add(field);
        row.SizeChanged += (_, args) =>
        {
            bool stacked = args.NewSize.Width < 440;
            row.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(args.NewSize.Width < 560 ? 200 : 232);
            Grid.SetColumnSpan(text, stacked ? 2 : 1);
            Grid.SetColumn(field, stacked ? 0 : 1);
            Grid.SetRow(field, stacked ? 1 : 0);
            Grid.SetColumnSpan(field, stacked ? 2 : 1);
            field.Margin = new Thickness(0, stacked ? 8 : 0, 0, 0);
        };
        return row;
    }

    internal static Grid SliderRow(string label, string description, Slider slider, TextBlock value)
    {
        var row = new Grid { Padding = new Thickness(16, 8, 16, 2) };
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = label, FontSize = 14, FontWeight = FontWeights.SemiBold });
        text.Children.Add(SettingsHint(description));
        value.FontSize = 14;
        value.FontWeight = FontWeights.SemiBold;
        value.VerticalAlignment = VerticalAlignment.Top;
        row.Children.Add(Columns(text, value));
        slider.HorizontalAlignment = HorizontalAlignment.Stretch;
        slider.MinWidth = 0;
        AutomationProperties.SetName(slider, label);
        AutomationProperties.SetAutomationId(slider, "Setting" + label.Replace(" ", ""));
        Grid.SetRow(slider, 1);
        row.Children.Add(slider);
        return row;
    }

    internal sealed record StudioPage(string Title, string Glyph, FrameworkElement Content, Action? OnSelected = null);

    internal sealed class StudioPages : Grid
    {
        private readonly NavigationView navigation;
        private readonly Dictionary<string, (NavigationViewItem Item, ScrollViewer Scroll, StudioPage Page)> pages = new();
        internal IEnumerable<ScrollViewer> Scrollers
        {
            get { foreach (var entry in pages.Values) yield return entry.Scroll; }
        }

        internal StudioPages(params StudioPage[] definitions)
        {
            navigation = new NavigationView
            {
                PaneDisplayMode = NavigationViewPaneDisplayMode.Top,
                IsSettingsVisible = false, IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
                IsPaneToggleButtonVisible = false, AlwaysShowHeader = false, IsTitleBarAutoPaddingEnabled = false,
                Style = GetStyle("StudioNavigationStyle")
            };
            AutomationProperties.SetAutomationId(navigation, "StudioNavigation");
            var host = new Grid();
            navigation.Content = host;
            foreach (var page in definitions)
            {
                var item = new NavigationViewItem { Content = page.Title, Tag = page.Title,
                    Icon = new FontIcon { Glyph = page.Glyph }, Margin = new Thickness(4, 0, 4, 0) };
                AutomationProperties.SetAutomationId(item, "Navigate" + page.Title);
                var scroll = new ScrollViewer { Content = page.Content, Padding = new Thickness(24, 16, 24, 16),
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed };
                AutomationProperties.SetAutomationId(scroll, page.Title + "Scroll");
                pages.Add(page.Title, (item, scroll, page));
                host.Children.Add(scroll);
                navigation.MenuItems.Add(item);
            }
            navigation.SelectionChanged += (_, args) =>
            {
                if (args.SelectedItem is not NavigationViewItem selected || selected.Tag is not string title) return;
                foreach (var entry in pages.Values)
                    entry.Scroll.Visibility = entry.Item == selected ? Visibility.Visible : Visibility.Collapsed;
                // Pages and their controls stay mounted. Selecting a preset never navigates or steals focus.
                pages[title].Page.OnSelected?.Invoke();
            };
            Children.Add(navigation);
        }

        internal void Select(string title)
        {
            if (pages.TryGetValue(title, out var entry) && !ReferenceEquals(navigation.SelectedItem, entry.Item))
                navigation.SelectedItem = entry.Item;
        }
    }

    internal static Grid Columns(FrameworkElement left, FrameworkElement right)
    {
        var row = new Grid { ColumnSpacing = 16 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(left);
        Grid.SetColumn(right, 1);
        row.Children.Add(right);
        return row;
    }

    internal static ComboBox ApplyToPicker() => new()
    {
        Header = "Apply to", ItemsSource = new[] { "Wallpaper and windows", "Windows only", "Wallpaper only" }
    };

    internal static TextBlock SettingsHint(string text) => new()
    {
        Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Style = GetStyle("SecondaryTextStyle")
    };

    internal static Grid SettingsRow(string label, Control control)
    {
        AutomationProperties.SetName(control, label);
        control.HorizontalAlignment = HorizontalAlignment.Right;
        control.VerticalAlignment = VerticalAlignment.Center;
        var text = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        return Columns(text, control);
    }

    internal static Border Card(UIElement content) => new()
    {
        Child = content,
        Padding = new Thickness(16),
        CornerRadius = new CornerRadius(8),
        Style = GetStyle("CardStyle"),
        BorderThickness = new Thickness(1)
    };

    internal static Expander Section(string title, string description, UIElement content, ToggleSwitch? toggle = null)
    {
        var heading = Heading(title, description, 15, 12);
        heading.Margin = new Thickness(0, 12, 0, 12);
        UIElement header = heading;
        if (toggle is not null)
        {
            toggle.MinWidth = 0;
            toggle.Width = 40;
            toggle.OnContent = null;
            toggle.OffContent = null;
            toggle.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetName(toggle, title);
            ToolTipService.SetToolTip(toggle, title);
            header = Columns(heading, toggle);
        }
        var section = new Expander
        {
            Header = header, Content = content, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(section, title);
        return section;
    }

    internal static Expander JokeSection(Window window, bool enabled, Action<bool> setEnabled, Func<uint, string> getCaption)
    {
        var toggle = new ToggleSwitch { Header = "Show GPU jokes", IsOn = enabled };
        var timing = new TextBlock
        {
            Text = "One notification 2 minutes after startup.",
            Style = GetStyle("SecondaryTextStyle"),
            TextWrapping = TextWrapping.Wrap
        };
        var caption = new TextBlock { Text = getCaption(0), TextWrapping = TextWrapping.Wrap };
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(toggle);
        content.Children.Add(timing);
        content.Children.Add(caption);
        var section = Section("Joke", "GPU excuses", content);
        var timer = window.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(12);
        uint index = 0;

        FrameworkElement? page = null;
        long visibilityToken = 0;
        bool CanRotate() => section.IsLoaded && section.IsExpanded && toggle.IsOn &&
            (page is null || page.Visibility == Visibility.Visible) && window.AppWindow.IsVisible &&
            window.AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        void UpdateRotation()
        {
            caption.Visibility = toggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
            if (!CanRotate()) timer.Stop();
            else if (!timer.IsRunning) timer.Start();
        }

        timer.Tick += (_, _) =>
        {
            if (CanRotate()) caption.Text = getCaption(++index);
            else timer.Stop();
        };
        toggle.Toggled += (_, _) =>
        {
            setEnabled(toggle.IsOn);
            UpdateRotation();
        };
        section.Loaded += (_, _) =>
        {
            for (DependencyObject? ancestor = VisualTreeHelper.GetParent(section); ancestor is not null;
                 ancestor = VisualTreeHelper.GetParent(ancestor))
            {
                if (ancestor is not ScrollViewer scroll) continue;
                page = scroll;
                visibilityToken = page.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => UpdateRotation());
                break;
            }
            UpdateRotation();
        };
        section.Unloaded += (_, _) =>
        {
            timer.Stop();
            page?.UnregisterPropertyChangedCallback(UIElement.VisibilityProperty, visibilityToken);
            page = null;
        };
        section.Expanding += (_, _) => UpdateRotation();
        section.Collapsed += (_, _) => timer.Stop();
        window.AppWindow.Changed += (_, _) => UpdateRotation();
        window.Closed += (_, _) => timer.Stop();
        UpdateRotation();
        return section;
    }

    private static StackPanel Heading(string title, string description, int titleSize, int descriptionSize)
    {
        var heading = new StackPanel { Spacing = 3 };
        heading.Children.Add(new TextBlock { Text = title, FontSize = titleSize, TextWrapping = TextWrapping.Wrap });
        heading.Children.Add(new TextBlock
        {
            Text = description, FontSize = descriptionSize, TextWrapping = TextWrapping.Wrap,
            Style = GetStyle("SecondaryTextStyle")
        });
        return heading;
    }

    private static Style GetStyle(string key) => (Style)Application.Current.Resources[key];
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
}
