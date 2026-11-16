using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel;

namespace OpenAIUsage.Windows;

public sealed partial class MainWindow
{
    private void SaveSettings() => settingsWriter.Queue(settings);

    private void UpdateSettingsStatus()
    {
        if (settingsStatus is null) return;
        settingsStatus.Text = settingsError ?? "";
        settingsStatus.Visibility = settingsStatus.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
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
        var alert50 = new CheckBox { Content = "50% remaining", IsChecked = settings.Alert50 };
        var alert20 = new CheckBox { Content = "20% remaining", IsChecked = settings.Alert20 };
        var alert5 = new CheckBox { Content = "5% remaining", IsChecked = settings.Alert5 };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(executable);
        content.Children.Add(timezone);
        content.Children.Add(Text("Uses your existing Codex ChatGPT sign-in.", 12));

        var columns = new Grid { ColumnSpacing = 24, Margin = new(0, 8, 0, 0) };
        columns.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var windowOptions = new StackPanel { Spacing = 4 };
        windowOptions.Children.Add(Text("Widget", 16, true));
        windowOptions.Children.Add(taskbar);
        windowOptions.Children.Add(bigSize);
        await AddStartupPreferenceAsync(windowOptions);
        windowOptions.Children.Add(opacity);
        columns.Children.Add(windowOptions);

        var notifications = new StackPanel { Spacing = 4 };
        notifications.Children.Add(Text("Notify", 16, true));
        notifications.Children.Add(alert50);
        notifications.Children.Add(alert20);
        notifications.Children.Add(alert5);
        Grid.SetColumn(notifications, 1);
        columns.Children.Add(notifications);
        content.Children.Add(columns);

        var error = Text("", 12);
        error.Foreground = WarningText;
        error.Visibility = Visibility.Collapsed;
        content.Children.Add(error);
        settingsStatus = error;
        UpdateSettingsStatus();
        var retry = new Button { Content = "Save settings" };
        retry.Click += (_, _) => SaveSettings();
        content.Children.Add(retry);
        void Apply()
        {
            try
            {
                var next = new WidgetSettings
                {
                    CodexPath = string.IsNullOrWhiteSpace(executable.Text) ? settings.CodexPath : executable.Text.Trim(),
                    TimeZoneId = timezone.SelectedValue as string ?? settings.TimeZoneId,
                    AlwaysOnTop = settings.AlwaysOnTop,
                    ShowInTaskbar = taskbar.IsChecked == true,
                    BigSize = bigSize.IsChecked == true,
                    WindowOpacityPercent = opacity.SelectedIndex >= 0
                        ? WindowOpacityOptions[opacity.SelectedIndex] : settings.WindowOpacityPercent,
                    Alert50 = alert50.IsChecked == true, Alert20 = alert20.IsChecked == true, Alert5 = alert5.IsChecked == true,
                    SelectedWindow = settings.SelectedWindow
                };
                bool opacityChanged = next.WindowOpacityPercent != settings.WindowOpacityPercent;
                if (opacityChanged) ApplyOpacity(next.WindowOpacityPercent);
                next.Validate();
                bool refreshNeeded = next.CodexPath != settings.CodexPath || next.TimeZoneId != settings.TimeZoneId;
                settings = next;
                SaveSettings();
                error.Text = string.IsNullOrWhiteSpace(executable.Text) ? "Choose a Codex executable." : "";
                ApplyTaskbarVisibility();
                ApplySize();
                CheckAlerts();
                if(refreshNeeded)_=RefreshAsync();
            }
            catch (Exception ex) { error.Text = ex.Message; }
            error.Visibility = error.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        executable.TextChanged += (_, _) => Apply();
        timezone.SelectionChanged += (_, _) => Apply();
        taskbar.Click += (_, _) => Apply();
        bigSize.Click += (_, _) => Apply();
        opacity.SelectionChanged += (_, _) => Apply();
        alert50.Click += (_, _) => Apply(); alert20.Click += (_, _) => Apply(); alert5.Click += (_, _) => Apply();
        try { await ShowDialogAsync("OpenAI Usage · Preferences", content, width: 620, height: 510); }
        finally { settingsStatus = null; }
    }

}
