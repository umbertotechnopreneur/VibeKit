using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using System.Collections.ObjectModel;
using System.Globalization;
using Windows.ApplicationModel;

namespace OpenAIUsage.Windows;

public sealed partial class MainWindow
{
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
            ApplyOpacity(next.WindowOpacityPercent);

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
        var startup = Switch("Start with Windows", false);
        startup.IsEnabled = false;
        var hint = Text("Available with the MSIX installation.", 12);
        content.Children.Add(startup);
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
        if (exitPending) return;
        ShowWindow();
        if (activeDialog is not null) return;
        var executable = new TextBox { Header = "Codex executable", Text = settings.CodexPath };
        var timezone = new ComboBox { Header = "Time zone", HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = TimeZoneInfo.GetSystemTimeZones(), DisplayMemberPath = "DisplayName", SelectedValuePath = "Id",
            SelectedValue = settings.TimeZoneId };
        var taskbar = Switch("Show in taskbar", settings.ShowInTaskbar);
        var bigSize = Switch($"Larger widget (+{LargerWidgetPercent}%)", settings.BigSize);
        var opacity = new ComboBox
        {
            Header = "Window opacity",
            ItemsSource = WindowOpacityOptions.Select(percent => $"{percent}%").ToArray(),
            SelectedIndex = Array.IndexOf(WindowOpacityOptions, settings.WindowOpacityPercent)
        };
        (string Id, string Text)[] labelPositions = [("above", "Above values"), ("below", "Below values"), ("hidden", "Hidden")];
        var labelPosition = new ComboBox
        {
            Header = "Field labels",
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
            Header = "Credit alert threshold",
            PlaceholderText = "Example: 1000",
            Text = settings.CreditsAlertThreshold?.ToString(CultureInfo.CurrentCulture) ?? "",
            Margin = new(0, 6, 0, 0)
        };
        var thresholdHint = Text("Send a notification when remaining credits reach this number or fall below it. Enter a whole number of credits, such as 1000. An empty field disables credit alerts.", 12);
        var thresholdError = Text("Enter a whole number from 0 to 2147483647.", 12);
        thresholdError.Foreground = WarningText;
        thresholdError.TextWrapping = TextWrapping.Wrap;
        thresholdError.Visibility = Visibility.Collapsed;
        var pathError = Text("Choose a Codex executable.", 12);
        pathError.Foreground = WarningText;
        pathError.Visibility = Visibility.Collapsed;

        // The native scrollbar overlays the viewport. Reserve its gutter before adding the content gap.
        const double scrollbarGutter = 14;
        var content = new StackPanel { Spacing = 10, Padding = new(0, 0, scrollbarGutter + 8, 0) };
        var connection = new Grid { ColumnSpacing = 24 };
        connection.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        connection.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var executableSettings = new StackPanel { Spacing = 4, Children = { executable, pathError } };
        connection.Children.Add(executableSettings);
        Grid.SetColumn(timezone, 1);
        connection.Children.Add(timezone);
        content.Children.Add(connection);
        content.Children.Add(Text("Uses your existing Codex ChatGPT sign-in.", 12));

        var columns = new Grid { ColumnSpacing = 24, Margin = new(0, 8, 0, 0) };
        columns.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var windowOptions = new StackPanel { Spacing = 4 };
        windowOptions.Children.Add(Text("Widget", 16, true));
        windowOptions.Children.Add(labelPosition);
        windowOptions.Children.Add(enableDetailsBar);
        windowOptions.Children.Add(bigSize);
        windowOptions.Children.Add(opacity);
        var desktopHeading = Text("Desktop", 16, true);
        desktopHeading.Margin = new(0, 10, 0, 0);
        windowOptions.Children.Add(desktopHeading);
        windowOptions.Children.Add(taskbar);
        await AddStartupPreferenceAsync(windowOptions);
        if (closed || exitPending || activeDialog is not null) return;
        columns.Children.Add(windowOptions);

        var notifications = new StackPanel { Spacing = 4 };
        notifications.Children.Add(Text("Alerts", 16, true));
        notifications.Children.Add(alert50);
        notifications.Children.Add(alert20);
        notifications.Children.Add(alert5);
        notifications.Children.Add(alertSound);
        notifications.Children.Add(creditsThreshold);
        notifications.Children.Add(thresholdHint);
        notifications.Children.Add(thresholdError);
        Grid.SetColumn(notifications, 1);
        columns.Children.Add(notifications);
        content.Children.Add(columns);

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
        enableDetailsBar.Toggled += (_, _) =>
        {
            Apply(next => next.EnableDetailsBar = enableDetailsBar.IsOn);
            SetDetailsOpen(enableDetailsBar.IsOn);
        };
        BindCheck(alert50, (next, value) => next.Alert50 = value);
        BindCheck(alert20, (next, value) => next.Alert20 = value);
        BindCheck(alert5, (next, value) => next.Alert5 = value);
        BindCheck(alertSound, (next, value) => next.AlertSound = value);
        content.Children.Add(Text("Widget modules", 16, true));
        content.Children.Add(Text("Drag the grip to change the order. Use the switch to show or hide a module.", 12));
        content.Children.Add(BuildModuleList(Apply));
        try
        {
            await ShowDialogAsync("OpenAI Usage · Preferences", content, closeButtonText: null,
                width: 620, height: 590, footer: footer, showScrollbarOnHover: true);
            if (!exitPending && !closed)
                await settingsWriter.FlushAsync();
        }
        catch (IOException failure)
        {
            settingsError = failure.Message;
            Tick();
        }
        finally
        {
            if (settingsStatus == saveStatus) settingsStatus = null;
        }
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
