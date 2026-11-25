using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace OpenAIUsage.Windows;

public sealed partial class MainWindow
{
    private const double MenuButtonWidth = 34;
    private readonly SolidColorBrush menuAccent = new();
    private readonly List<(FontIcon Icon, uint Light, uint Dark)> menuIcons = [];
    private readonly Flyout widgetMenu = new() { ShouldConstrainToRootBounds = false };
    private readonly StackPanel commands = new() { Spacing = 2, Width = 218 };
    private readonly StackPanel dockCommands = new() { Spacing = 2, Width = 218, Visibility = Visibility.Collapsed };
    private readonly TextBlock menuWarning = Text("", 12);
    private Button menuButton = null!, pinCommand = null!, undockCommand = null!;
    private ToggleSwitch compactMode = null!;
    private TextBlock pinCaption = null!;
    private FontIcon pinIcon = null!;
    private bool syncingMenu;

    private void BuildWidgetMenu()
    {
        var dots = new FontIcon { Glyph = "\uE712", FontSize = 24, Foreground = menuAccent, IsHitTestVisible = false };
        menuButton = Button(dots, "Widget menu");
        menuButton.Width = MenuButtonWidth;
        menuButton.Height = 30;
        menuButton.MinWidth = menuButton.MinHeight = 0;
        menuButton.Padding = new(0);
        menuButton.Margin = new(TileSpacing, 0, 0, 0);
        menuButton.VerticalAlignment = VerticalAlignment.Top;
        AutomationProperties.SetAutomationId(menuButton, "WidgetMenu");
        menuButton.Flyout = widgetMenu;
        widgetMenu.Content = new Grid { Children = { commands, dockCommands } };

        pinIcon = MenuIcon("\uE718", 0x9A5104, 0xFFBB67);
        pinCaption = Text("Pin on top", 14);
        pinCommand = MenuCommand("Pin on top", pinIcon, () =>
            UpdateSettings(next => next.AlwaysOnTop = !next.AlwaysOnTop), pinCaption);
        commands.Children.Add(pinCommand);

        compactMode = new ToggleSwitch
        {
            OnContent = null, OffContent = null, MinWidth = 0, Width = 42,
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(compactMode, "Compact mode");
        AutomationProperties.SetAutomationId(compactMode, "CompactModeSwitch");
        compactMode.Toggled += (_, _) =>
        {
            if (!syncingMenu) SetDetailsOpen(!compactMode.IsOn);
        };
        var compactRow = MenuRow(MenuIcon("\uE8A9", 0x5C41A3, 0xC7AAFF), Text("Compact mode", 14), compactMode);
        compactRow.Padding = new(8, 0, 8, 0);
        commands.Children.Add(compactRow);

        var anchor = MenuCommand("Dock", MenuIcon("\uE8A7", 0x08726A, 0x68DBC4), () => ShowDockMenu(true),
            closeMenu: false, trailing: new FontIcon { Glyph = "\uE76C", FontSize = 12, Foreground = SecondaryText });
        commands.Children.Add(anchor);
        commands.Children.Add(MenuCommand("Settings", MenuIcon("\uE713", 0x2167A5, 0x8CCCFF), () => _ = ShowSettingsAsync()));
        commands.Children.Add(MenuCommand("About", MenuIcon("\uE946", 0x496F21, 0xAFDC78), () => _ = ShowAboutAsync()));
        commands.Children.Add(MenuCommand("VibeWare", MenuIcon("\uE8A1", 0x984479, 0xF3A6D8), () => OpenLink(ManifestoUrl)));
        commands.Children.Add(MenuCommand("Exit", MenuIcon("\uE8BB", 0xA9313B, 0xFF8F98), ExitApplication));

        var back = MenuCommand("Back", MenuIcon("\uE72B", 0x49627D, 0xB0C7E3), () => ShowDockMenu(false), closeMenu: false);
        dockCommands.Children.Add(back);
        dockCommands.Children.Add(MenuCommand("Top", MenuIcon("\uE74A", 0x2167A5, 0x8CCCFF), () => DockWidget(bottom: false)));
        dockCommands.Children.Add(MenuCommand("Bottom", MenuIcon("\uE74B", 0x5C41A3, 0xC7AAFF), () => DockWidget(bottom: true)));
        undockCommand = MenuCommand("Undock", MenuIcon("\uE77A", 0x984479, 0xF3A6D8), UndockWidget);
        dockCommands.Children.Add(undockCommand);

        menuWarning.Foreground = WarningText;
        menuWarning.Margin = new(8, 4, 8, 0);
        menuWarning.Visibility = Visibility.Collapsed;
        commands.Children.Add(menuWarning);
        widgetMenu.Opening += (_, _) =>
        {
            RefreshMenuState();
            UpdateMenuStatus();
        };
        widgetMenu.Closed += (_, _) => ShowDockMenu(false, focus: false);
        ApplyMenuTheme(root.RequestedTheme == ElementTheme.Dark);

        void ShowDockMenu(bool show, bool focus = true)
        {
            commands.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
            dockCommands.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (focus && Microsoft.UI.Xaml.Input.FocusManager.FindFirstFocusableElement(show ? dockCommands : commands) is Control first)
                first.Focus(FocusState.Programmatic);
        }
    }

    private FontIcon MenuIcon(string glyph, uint light, uint dark)
    {
        var icon = new FontIcon { Glyph = glyph, FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
        menuIcons.Add((icon, light, dark));
        return icon;
    }

    private void ApplyMenuTheme(bool dark)
    {
        menuAccent.Color = systemSettings.GetColorValue(global::Windows.UI.ViewManagement.UIColorType.Accent);
        foreach (var (icon, light, darkColor) in menuIcons)
            icon.Foreground = new SolidColorBrush(Rgb(dark ? darkColor : light));
    }

    private Button MenuCommand(string label, FontIcon icon, Action action, TextBlock? caption = null,
        bool closeMenu = true, FrameworkElement? trailing = null)
    {
        var button = Button(MenuRow(icon, caption ?? Text(label, 14), trailing), label);
        ToolTipService.SetToolTip(button, null);
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.Padding = new(8, 0, 8, 0);
        button.Click += (_, _) =>
        {
            if (closeMenu) widgetMenu.Hide();
            action();
        };
        return button;
    }

    private static Grid MenuRow(FontIcon icon, TextBlock label, FrameworkElement? trailing)
    {
        var row = new Grid { ColumnSpacing = 10, MinHeight = 36 };
        row.ColumnDefinitions.Add(new() { Width = new(20) });
        row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        label.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(icon);
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        if (trailing is not null)
        {
            Grid.SetColumn(trailing, 2);
            row.Children.Add(trailing);
        }
        return row;
    }

    private void RefreshMenuState()
    {
        if (compactMode is null) return;
        syncingMenu = true;
        compactMode.IsOn = IsCompact;
        syncingMenu = false;
        pinCaption.Text = settings.AlwaysOnTop ? "Unpin" : "Pin on top";
        pinIcon.Glyph = settings.AlwaysOnTop ? "\uE77A" : "\uE718";
        AutomationProperties.SetName(pinCommand, pinCaption.Text);
        pinCommand.IsEnabled = !IsDocked;
        undockCommand.IsEnabled = IsDocked;
        pinCommand.Opacity = IsDocked ? .5 : 1;
        undockCommand.Opacity = IsDocked ? 1 : .5;
        widgetMenu.Placement = DockedAtBottom ? FlyoutPlacementMode.Top : FlyoutPlacementMode.Bottom;
    }

    private void UpdateMenuStatus()
    {
        string? warning = dockError ?? settingsError ?? (!state.Connected ? state.Status : null);
        if (string.IsNullOrWhiteSpace(warning) && !state.Connected) warning = "Check the Codex connection in Settings.";
        warning ??= "";
        if (menuWarning.Text == warning) return;
        menuWarning.Text = warning;
        menuWarning.Visibility = warning.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(menuButton, warning.Length == 0 ? "Widget menu" : warning);
        AutomationProperties.SetHelpText(menuButton, warning);
    }
}
