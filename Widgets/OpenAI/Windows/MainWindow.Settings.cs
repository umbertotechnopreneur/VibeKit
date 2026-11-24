using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using System.Collections.ObjectModel;
using System.Globalization;
using Windows.ApplicationModel;
using global::Windows.Graphics;

namespace OpenAIUsage.Windows;

public sealed partial class MainWindow
{
    private FrameworkElement? settingsPane;
    private SizeInt32? settingsRestoreSize;

    private void ApplyWidgetOpacity(int percent)
    {
        ApplyOpacity(settingsPane is null ? percent : 100);
        viewport.Opacity = settingsPane is null ? 1 : percent / 100d;
    }

    private async Task HideSettingsAsync()
    {
        if (settingsPane is null) return;
        root.Children.Remove(settingsPane);
        settingsPane = null;
        settingsStatus = null;
        var restore = settingsRestoreSize;
        settingsRestoreSize = null;
        RequestWidgetFit(restore);
        try
        {
            ApplyWidgetOpacity(settings.WindowOpacityPercent);
            if (!exitPending && !closed)
                await settingsWriter.FlushAsync();
        }
        catch (Exception failure) when (failure is IOException or System.ComponentModel.Win32Exception)
        {
            settingsError = failure.Message;
            Tick();
        }
        QueueLayout();
    }

    private void UpdateSettingsStatus()
    {
        if (settingsStatus is null) return;
        settingsStatus.Text = settingsError ?? "";
        settingsStatus.Visibility = settingsStatus.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateSettings(Action<WidgetSettings> edit)
    {
        var previous = settings;
        var next = previous.Copy();
        edit(next);
        next.Validate();
        if (next.WindowOpacityPercent != previous.WindowOpacityPercent)
            ApplyWidgetOpacity(next.WindowOpacityPercent);

        settings = next;
        settingsWriter.Queue(next);
        ApplyTaskbarVisibility();
        ApplyTopmost();
        ApplySize();
        CheckAlerts();
        if (next.CodexPath != previous.CodexPath || next.TimeZoneId != previous.TimeZoneId)
            _ = RefreshAsync();
    }

    private static async Task AddStartupPreferenceAsync(StackPanel content)
    {
        var startup = Switch("Start on sign-in", false);
        startup.IsEnabled = false;
        var hint = Text("Available with the MSIX installation.", 12);
        content.Children.Add(SettingsRow("Start on sign-in", startup));
        content.Children.Add(hint);

        StartupTask task;
        try { task = await StartupTask.GetAsync(StartupTaskId); }
        catch (Exception error)
        {
            Diagnostics.Record("startup.unavailable", error.GetType().Name);
            const string runPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
            const string valueName = "OpenAIUsage";
            string? executable = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executable) || Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                hint.Text = "Startup is unavailable when running through dotnet.";
                return;
            }
            string command = "\"" + executable + "\"";
            bool registered;
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(runPath);
                registered = string.Equals(key?.GetValue(valueName) as string, command, StringComparison.OrdinalIgnoreCase);
                using var approved = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");
                var approval = approved?.GetValue(valueName) as byte[];
                bool disabledByUser = registered && approval is { Length: > 0 } && (approval[0] & 1) != 0;
                startup.IsOn = registered && !disabledByUser;
                startup.IsEnabled = !disabledByUser;
                hint.Text = disabledByUser ? "Re-enable OpenAI Usage in Windows Settings > Apps > Startup." : "";
            }
            catch (Exception registryError) { hint.Text = "Could not read the Windows startup setting."; Diagnostics.Record("startup.failed", registryError.GetType().Name); return; }
            bool updatingStartup = false;
            startup.Toggled += (_, _) =>
            {
                if (updatingStartup) return;
                try
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(runPath);
                    if (startup.IsOn == true) key.SetValue(valueName, command);
                    else key.DeleteValue(valueName, false);
                    registered = startup.IsOn;
                    hint.Text = "";
                }
                catch (Exception registryError)
                {
                    updatingStartup = true;
                    startup.IsOn = registered;
                    updatingStartup = false;
                    hint.Text = "Could not change the Windows startup setting.";
                    Diagnostics.Record("startup.failed", registryError.GetType().Name);
                }
            };
            return;
        }

        void ShowState(StartupTaskState state)
        {
            startup.IsOn = state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
            startup.IsEnabled = state is StartupTaskState.Enabled or StartupTaskState.Disabled;
            hint.Text = state switch
            {
                StartupTaskState.DisabledByUser => "Re-enable OpenAI Usage in Windows Settings > Apps > Startup.",
                StartupTaskState.DisabledByPolicy or StartupTaskState.EnabledByPolicy => "Managed by Windows policy.",
                _ => ""
            };
        }

        ShowState(task.State);
        startup.Toggled += async (_, _) =>
        {
            if (!startup.IsEnabled) return;
            startup.IsEnabled = false;
            var state = task.State;
            try
            {
                if (startup.IsOn == true && state == StartupTaskState.Disabled)
                    state = await task.RequestEnableAsync();
                else if (startup.IsOn == false && state == StartupTaskState.Enabled)
                {
                    task.Disable();
                    state = task.State;
                }
                ShowState(state);
            }
            catch (Exception error)
            {
                ShowState(state);
                hint.Text = "Could not change the Windows startup setting.";
                Diagnostics.Record("startup.failed", error.GetType().Name);
            }
        };
    }

    private async Task ShowSettingsAsync()
    {
        if (exitPending || closed) return;
        ShowWindow();
        if (settingsPane is not null || activeDialog is not null) return;
        var executable = new TextBox { Text = settings.CodexPath, MinWidth = 124, MaxWidth = 220 };
        var timezone = new ComboBox { MinWidth = 124, MaxWidth = 240,
            ItemsSource = TimeZoneInfo.GetSystemTimeZones(), DisplayMemberPath = "DisplayName", SelectedValuePath = "Id",
            SelectedValue = settings.TimeZoneId };
        var taskbar = Switch("Show in taskbar", settings.ShowInTaskbar);
        var bigSize = Switch($"Larger widget (+{LargerWidgetPercent}%)", settings.BigSize);
        var opacity = new ComboBox
        {
            ItemsSource = WindowOpacityOptions.Select(percent => $"{percent}%").ToArray(),
            SelectedIndex = Array.IndexOf(WindowOpacityOptions, settings.WindowOpacityPercent)
        };
        (string Id, string Text)[] labelPositions = [("above", "Above values"), ("below", "Below values"), ("hidden", "Hidden")];
        var labelPosition = new ComboBox
        {
            ItemsSource = labelPositions.Select(position => position.Text).ToArray(),
            SelectedIndex = Math.Max(0, Array.FindIndex(labelPositions, position => position.Id == settings.LabelPosition))
        };
        var enableDetailsBar = Switch("Enable usage details", settings.EnableDetailsBar);
        var alert50 = Switch("50% remaining", settings.Alert50);
        var alert20 = Switch("20% remaining", settings.Alert20);
        var alert5 = Switch("5% remaining", settings.Alert5);
        var alertSound = Switch("Play a sound for alerts", settings.AlertSound);
        var creditsThreshold = new TextBox
        {
            PlaceholderText = "Example: 1000",
            Text = settings.CreditsAlertThreshold?.ToString(CultureInfo.CurrentCulture) ?? "",
            MinWidth = 124
        };
        var thresholdError = Text("Enter a whole number from 0 to 2147483647.", 12);
        thresholdError.Foreground = WarningText;
        thresholdError.Visibility = Visibility.Collapsed;
        var pathError = Text("Choose a Codex executable.", 12);
        pathError.Foreground = WarningText;
        pathError.Visibility = Visibility.Collapsed;

        var content = new StackPanel { Spacing = 4, MaxWidth = 640 };
        var sections = new List<Expander>();

        StackPanel Section(string title, string subtitle, string icon)
        {
            var headerText = new StackPanel { Spacing = 2 };
            headerText.Children.Add(Text(title, 15, true));
            var description = Text(subtitle, 12);
            description.Foreground = SecondaryText;
            headerText.Children.Add(description);
            var headerContent = new Grid { ColumnSpacing = 12 };
            headerContent.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            headerContent.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            headerContent.Children.Add(new FontIcon { Glyph = icon, FontSize = 16 });
            Grid.SetColumn(headerText, 1);
            headerContent.Children.Add(headerText);
            var body = new StackPanel { Spacing = 10 };
            var expander = new Expander
            {
                Header = headerContent, Content = body, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            AutomationProperties.SetName(expander, title);
            expander.Expanding += (_, _) =>
            {
                foreach (var other in sections)
                    if (other != expander) other.IsExpanded = false;
            };
            sections.Add(expander);
            content.Children.Add(expander);
            return body;
        }

        var appearance = Section("Appearance", "Labels, usage details, size and opacity", "\uE790");
        appearance.Children.Add(SettingsRow("Field labels", labelPosition));
        appearance.Children.Add(SettingsRow("Enable usage details", enableDetailsBar));
        appearance.Children.Add(SettingsRow($"Larger widget (+{LargerWidgetPercent}%)", bigSize));
        appearance.Children.Add(SettingsRow("Window opacity", opacity));

        var fields = Section("Widget fields", "Choose what the widget shows and in which order", "\uE8FD");
        fields.Children.Add(Text("Drag to reorder. Use the switches to show or hide fields. Details appear in normal mode when usage details are enabled.", 12));

        var notifications = Section("Alerts", "Allowance and credit thresholds", "\uE7ED");
        notifications.Children.Add(SettingsRow("50% remaining", alert50));
        notifications.Children.Add(SettingsRow("20% remaining", alert20));
        notifications.Children.Add(SettingsRow("5% remaining", alert5));
        notifications.Children.Add(SettingsRow("Play a sound for alerts", alertSound));
        notifications.Children.Add(SettingsRow("Credit alert threshold", creditsThreshold,
            "Send an alert at or below this balance. Leave blank to disable."));
        notifications.Children.Add(thresholdError);

        var connection = Section("Codex connection", "Executable and time zone", "\uE753");
        connection.Children.Add(SettingsRow("Codex executable", executable, "Uses your existing Codex ChatGPT sign-in."));
        connection.Children.Add(pathError);
        connection.Children.Add(SettingsRow("Time zone", timezone));

        var desktop = Section("Desktop", "Taskbar visibility and startup", "\uE7F8");
        desktop.Children.Add(SettingsRow("Show in taskbar", taskbar));

        var footer = new StackPanel { Spacing = 4, Margin = new(24, 0, 24, 16) };
        var error = Text("", 12);
        error.Foreground = WarningText;
        error.Visibility = Visibility.Collapsed;
        footer.Children.Add(error);
        var saveStatus = Text("", 12);
        saveStatus.Foreground = WarningText;
        footer.Children.Add(saveStatus);
        settingsStatus = saveStatus;
        UpdateSettingsStatus();
        void Apply(Action<WidgetSettings> edit)
        {
            try
            {
                UpdateSettings(edit);
                error.Text = "";
            }
            catch (Exception ex) { error.Text = ex.Message; }
            error.Visibility = error.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        creditsThreshold.TextChanged += (_, _) =>
        {
            string input = creditsThreshold.Text.Trim();
            int? creditLimit = null;
            if (input.Length > 0)
            {
                if (!int.TryParse(input, NumberStyles.None, CultureInfo.CurrentCulture, out int value))
                {
                    thresholdError.Visibility = Visibility.Visible;
                    return;
                }
                creditLimit = value;
            }
            thresholdError.Visibility = Visibility.Collapsed;
            if (creditLimit != settings.CreditsAlertThreshold)
                Apply(next => next.CreditsAlertThreshold = creditLimit);
        };
        executable.TextChanged += (_, _) =>
        {
            string path = executable.Text.Trim();
            pathError.Visibility = path.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (path.Length > 0 && path != settings.CodexPath)
                Apply(next => next.CodexPath = path);
        };
        timezone.SelectionChanged += (_, _) =>
        {
            if (timezone.SelectedValue is string zone && zone != settings.TimeZoneId)
                Apply(next => next.TimeZoneId = zone);
        };
        opacity.SelectionChanged += (_, _) =>
        {
            if (opacity.SelectedIndex >= 0)
                Apply(next => next.WindowOpacityPercent = WindowOpacityOptions[opacity.SelectedIndex]);
        };
        labelPosition.SelectionChanged += (_, _) =>
        {
            if (labelPosition.SelectedIndex >= 0)
                Apply(next => next.LabelPosition = labelPositions[labelPosition.SelectedIndex].Id);
        };
        void BindCheck(ToggleSwitch control, Action<WidgetSettings, bool> edit) =>
            control.Toggled += (_, _) => Apply(next => edit(next, control.IsOn));
        BindCheck(taskbar, (next, value) => next.ShowInTaskbar = value);
        BindCheck(bigSize, (next, value) => next.BigSize = value);
        BindCheck(enableDetailsBar, (next, value) => next.EnableDetailsBar = value);
        BindCheck(alert50, (next, value) => next.Alert50 = value);
        BindCheck(alert20, (next, value) => next.Alert20 = value);
        BindCheck(alert5, (next, value) => next.Alert5 = value);
        BindCheck(alertSound, (next, value) => next.AlertSound = value);
        fields.Children.Add(BuildModuleList(Apply));
        footer.Children.Add(Text("Changes are saved automatically.", 12));
        var header = new Grid { Margin = new(24, 0, 24, 0), ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var title = Text("Settings", 16, true);
        title.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(title);
        var done = new Button { Content = "Done" };
        done.Click += async (_, _) => await HideSettingsAsync();
        Grid.SetColumn(done, 1);
        header.Children.Add(done);
        var pane = new Grid { Padding = new(0, 12, 0, 0), RowSpacing = 6 };
        pane.RowDefinitions.Add(new() { Height = GridLength.Auto });
        pane.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        pane.RowDefinitions.Add(new() { Height = GridLength.Auto });
        pane.Children.Add(header);
        var scroll = new ScrollViewer
        {
            Content = content, Margin = new(24, 8, 24, 0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetRow(scroll, 1);
        pane.Children.Add(scroll);
        Grid.SetRow(footer, 2);
        pane.Children.Add(footer);
        var panel = new Border { Child = pane, Margin = new(0, 6, 0, 0), CornerRadius = new(12),
            Background = SettingsPanelFill };
        Grid.SetRow(panel, 1);
        settingsRestoreSize = AppWindow.ClientSize;
        settingsPane = panel;
        root.Children.Add(panel);
        try { ApplyWidgetOpacity(settings.WindowOpacityPercent); }
        catch (System.ComponentModel.Win32Exception failure) { Diagnostics.Record("window.opacity.failed", failure.Message); }
        RequestWidgetFit();
        await AddStartupPreferenceAsync(desktop);
    }

    private static Grid SettingsRow(string title, Control input, string? subtitle = null)
    {
        var row = new Grid { ColumnSpacing = 12, MinHeight = 34 };
        row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var labels = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(Text(title, 14));
        if (subtitle is not null)
        {
            var description = Text(subtitle, 12);
            description.Foreground = SecondaryText;
            labels.Children.Add(description);
        }
        if (input is ToggleSwitch toggle)
            toggle.OffContent = toggle.OnContent = "";
        input.HorizontalAlignment = HorizontalAlignment.Right;
        input.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(input, title);
        row.Children.Add(labels);
        Grid.SetColumn(input, 1);
        row.Children.Add(input);
        return row;
    }

    private static ToggleSwitch Switch(string label, bool isOn)
    {
        var control = new ToggleSwitch { OnContent = label, OffContent = label, IsOn = isOn, MinWidth = 0 };
        AutomationProperties.SetName(control, label);
        return control;
    }

    private ListView BuildModuleList(Action<Action<WidgetSettings>> apply)
    {
        ListViewItem BuildItem(Module module)
        {
            var toggle = Switch(module.Label, ModuleEnabled(module.Key));
            toggle.OnContent = toggle.OffContent = "";
            toggle.HorizontalAlignment = HorizontalAlignment.Right;
            toggle.VerticalAlignment = VerticalAlignment.Center;
            toggle.Toggled += (_, _) => apply(next =>
            {
                switch (module.Key)
                {
                    case "credits": next.ShowCredits = toggle.IsOn; break;
                    case "estimate": next.ShowEstimatedTime = toggle.IsOn; break;
                    case "vouchers": next.ShowAvailableResets = toggle.IsOn; break;
                    default:
                        var hidden = (next.HiddenFields ?? []).Where(key => key != module.Key);
                        next.HiddenFields = toggle.IsOn ? hidden.ToArray() : hidden.Append(module.Key).ToArray();
                        break;
                }
            });
            var grip = Text("⠿", 23);
            grip.VerticalAlignment = VerticalAlignment.Center;
            grip.Foreground = SecondaryText;
            ToolTipService.SetToolTip(grip, "Drag to move " + module.Label);
            var description = Text(module.Description, 12);
            description.Foreground = SecondaryText;
            var fields = new StackPanel { Spacing = 2, Children = { Text(module.Label, 14), description } };
            var row = new Grid { ColumnSpacing = 12, Padding = new(0, 6, 0, 6) };
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.Children.Add(grip);
            Grid.SetColumn(fields, 1);
            row.Children.Add(fields);
            Grid.SetColumn(toggle, 2);
            row.Children.Add(toggle);
            var item = new ListViewItem
            {
                Content = row, Tag = module.Key,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            AutomationProperties.SetName(item, module.Label);
            return item;
        }

        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.None, CanDragItems = true,
            CanReorderItems = true, AllowDrop = true, MinHeight = 0
        };
        ScrollViewer.SetVerticalScrollMode(list, ScrollMode.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        AutomationProperties.SetName(list, "Widget module order");
        AttachRows();
        return list;

        void AttachRows()
        {
            var items = new ObservableCollection<ListViewItem>(OrderedModules().Select(BuildItem));
            list.ItemsSource = items;
            items.CollectionChanged += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                if (closed || list.ItemsSource != items || items.Count != modules.Length) return;
                string[] order = items.Select(item => (string)item.Tag).ToArray();
                if (order.SequenceEqual(OrderedModules().Select(module => module.Key))) return;
                apply(next => next.FieldOrder = order);
                // Discard the displaced WinUI drag container and render fresh rows in the new order.
                AttachRows();
            });
        }
    }
}
