using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        ApplySize();
        CheckAlerts();
        if (next.CodexPath != previous.CodexPath || next.TimeZoneId != previous.TimeZoneId)
            _ = RefreshAsync();
    }

    private static async Task AddStartupPreferenceAsync(StackPanel content)
    {
        var startup = new CheckBox { Content = "Start with Windows", IsEnabled = false };
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
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(runPath);
                bool registered = string.Equals(key?.GetValue(valueName) as string, command, StringComparison.OrdinalIgnoreCase);
                using var approved = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");
                var approval = approved?.GetValue(valueName) as byte[];
                bool disabledByUser = registered && approval is { Length: > 0 } && (approval[0] & 1) != 0;
                startup.IsChecked = registered && !disabledByUser;
                startup.IsEnabled = !disabledByUser;
                hint.Text = disabledByUser ? "Re-enable OpenAI Usage in Windows Settings > Apps > Startup." : "Applies immediately when changed.";
            }
            catch (Exception registryError) { hint.Text = "Could not read the Windows startup setting."; Diagnostics.Record("startup.failed", registryError.GetType().Name); return; }
            startup.Click += (_, _) =>
            {
                try
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(runPath);
                    if (startup.IsChecked == true) key.SetValue(valueName, command);
                    else key.DeleteValue(valueName, false);
                    hint.Text = "Applies immediately when changed.";
                }
                catch (Exception registryError)
                {
                    startup.IsChecked = startup.IsChecked != true;
                    hint.Text = "Could not change the Windows startup setting.";
                    Diagnostics.Record("startup.failed", registryError.GetType().Name);
                }
            };
            return;
        }

        void ShowState(StartupTaskState state)
        {
            startup.IsChecked = state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
            startup.IsEnabled = state is StartupTaskState.Enabled or StartupTaskState.Disabled;
            hint.Text = state switch
            {
                StartupTaskState.DisabledByUser => "Re-enable OpenAI Usage in Windows Settings > Apps > Startup.",
                StartupTaskState.DisabledByPolicy or StartupTaskState.EnabledByPolicy => "Managed by Windows policy.",
                _ => "Applies immediately when changed."
            };
        }

        ShowState(task.State);
        startup.Click += async (_, _) =>
        {
            startup.IsEnabled = false;
            var state = task.State;
            try
            {
                if (startup.IsChecked == true && state == StartupTaskState.Disabled)
                    state = await task.RequestEnableAsync();
                else if (startup.IsChecked == false && state == StartupTaskState.Enabled)
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
        var taskbar = new CheckBox { Content = "Show in taskbar", IsChecked = settings.ShowInTaskbar };
        var bigSize = new CheckBox { Content = "Larger widget (+25%)", IsChecked = settings.BigSize };
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
        var showResets = new CheckBox { Content = "Show available resets", IsChecked = settings.ShowAvailableResets };
        var showCredits = new CheckBox { Content = "Show remaining credits", IsChecked = settings.ShowCredits };
        var showEstimatedTime = new CheckBox { Content = "Show estimated time left", IsChecked = settings.ShowEstimatedTime };
        var enableDetailsBar = new CheckBox { Content = "Enable usage details bar", IsChecked = settings.EnableDetailsBar };
        var alert50 = new CheckBox { Content = "50% remaining", IsChecked = settings.Alert50 };
        var alert20 = new CheckBox { Content = "20% remaining", IsChecked = settings.Alert20 };
        var alert5 = new CheckBox { Content = "5% remaining", IsChecked = settings.Alert5 };
        var alertSound = new CheckBox { Content = "Play a sound for alerts", IsChecked = settings.AlertSound };
        var creditsThreshold = new TextBox
        {
            Header = "Credit alert threshold",
            PlaceholderText = "Off",
            Text = settings.CreditsAlertThreshold?.ToString(CultureInfo.CurrentCulture) ?? "",
            Margin = new(0, 6, 0, 0)
        };
        var thresholdHint = Text("Leave blank to turn off credit alerts.", 12);
        var thresholdError = Text("Enter a whole number from 0 to 2147483647.", 12);
        thresholdError.Foreground = WarningText;
        thresholdError.TextWrapping = TextWrapping.Wrap;
        thresholdError.Visibility = Visibility.Collapsed;
        var pathError = Text("Choose a Codex executable.", 12);
        pathError.Foreground = WarningText;
        pathError.Visibility = Visibility.Collapsed;

        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(executable);
        content.Children.Add(pathError);
        content.Children.Add(timezone);
        content.Children.Add(Text("Uses your existing Codex ChatGPT sign-in.", 12));

        var columns = new Grid { ColumnSpacing = 24, Margin = new(0, 8, 0, 0) };
        columns.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var windowOptions = new StackPanel { Spacing = 4 };
        windowOptions.Children.Add(Text("Widget", 16, true));
        windowOptions.Children.Add(labelPosition);
        windowOptions.Children.Add(showResets);
        windowOptions.Children.Add(showCredits);
        windowOptions.Children.Add(showEstimatedTime);
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
        footer.Children.Add(Text("Changes are saved automatically.", 12));
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
        void BindCheck(CheckBox check, Action<WidgetSettings, bool> edit) =>
            check.Click += (_, _) => Apply(next => edit(next, check.IsChecked == true));
        BindCheck(taskbar, (next, value) => next.ShowInTaskbar = value);
        BindCheck(bigSize, (next, value) => next.BigSize = value);
        BindCheck(showResets, (next, value) => next.ShowAvailableResets = value);
        BindCheck(showCredits, (next, value) => next.ShowCredits = value);
        BindCheck(showEstimatedTime, (next, value) => next.ShowEstimatedTime = value);
        BindCheck(enableDetailsBar, (next, value) => next.EnableDetailsBar = value);
        BindCheck(alert50, (next, value) => next.Alert50 = value);
        BindCheck(alert20, (next, value) => next.Alert20 = value);
        BindCheck(alert5, (next, value) => next.Alert5 = value);
        BindCheck(alertSound, (next, value) => next.AlertSound = value);
        try
        {
            await ShowDialogAsync("OpenAI Usage · Preferences", content, closeButtonText: null, width: 620, height: 590, footer: footer);
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

}
