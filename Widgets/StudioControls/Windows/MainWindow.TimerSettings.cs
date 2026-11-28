using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace VibeKit.StudioControls.Windows;

public sealed partial class MainWindow
{
    private bool CanStartFreeTimer => !PlaybackActive && recordingCue is null && !sendingScene
        && !workflowDialogOpen && !editingLibrary && !rendering;

    private async Task EditTimerSettingsAsync()
    {
        if (timerEditor is not null || workflowDialogOpen || closed || recordingCue is not null) return;
        var editor = new TimerSettingsEditor(this);
        timerEditor = editor;
        try
        {
            // Put configuration on the control monitor. The detached timer remains readable.
            Activate();
            await editor.Dialog.ShowAsync();
            if (!closed && timerWindow != null && editor.StartRequested && CanStartFreeTimer)
            {
                player.StartFree(settings.Timer.FreeSeconds);
                RefreshPlayback();
            }
        }
        catch (Exception error) { if (!closed) Fail("timer.settings", error); }
        finally { timerEditor = null; }
    }

    private sealed class TimerSettingsEditor
    {
        private readonly MainWindow owner;
        private readonly ComboBox size = new() { MinWidth = 114 };
        private readonly ToggleSwitch showNext, showTotal, showNote, showObs, recordCountdown, endWarning;
        private readonly NumberBox warningSeconds, freeSeconds;
        private readonly TextBlock error = Text("");
        internal Button FreeStart { get; } = new();
        internal ContentDialog Dialog { get; }
        internal StackPanel Sections { get; } = Column(8);
        internal bool StartRequested { get; private set; }

        internal TimerSettingsEditor(MainWindow owner)
        {
            this.owner = owner;
            var prefs = owner.settings.Timer;
            string T(string key) => owner.localization.Text(key);
            size.Items.Add(T("Compact"));
            size.Items.Add(T("Large"));
            size.SelectedIndex = prefs.Large ? 1 : 0;
            var display = Column(6);
            display.Children.Add(Setting(size, "Timer size", "Choose Compact or Large for the separate presenter window."));
            showNext = AddSwitch(display, "Show next scene",
                "Show the upcoming scene beside the current one on the presenter timer.", prefs.ShowNext);
            showTotal = AddSwitch(display, "Show total remaining",
                "Add a second countdown for the time left in the entire workflow.", prefs.ShowTotal);
            showNote = AddSwitch(display, "Show scene note",
                "Display the note from the current workflow step on the presenter timer.", prefs.ShowNote);
            showObs = AddSwitch(display, "Show OBS status",
                "Display the latest connection and recording status reported by OBS on the timer.", prefs.ShowObs);
            Sections.Children.Add(Section(T("Display"), display, true));

            var signals = Column(6);
            recordCountdown = AddSwitch(signals, "Recording countdown",
                "Wait three seconds after Start recording. The presenter sees a light for each second.", prefs.RecordCountdown);
            endWarning = AddSwitch(signals, "End warning",
                "Light the timer signal shortly before the current countdown ends.", prefs.EndWarning);
            warningSeconds = AddSeconds(signals, "Warning (seconds)",
                "Choose how many seconds before the end the light turns on, from 1 to 60.", prefs.WarningSeconds, 60);
            warningSeconds.IsEnabled = endWarning.IsOn;
            endWarning.Toggled += (_, _) => warningSeconds.IsEnabled = endWarning.IsOn;
            Sections.Children.Add(Section(T("Signals"), signals, false));

            var free = Column(6);
            freeSeconds = AddSeconds(free, "Duration (seconds)",
                "Set the length of a standalone countdown. Start timer runs it without changing OBS scenes.", prefs.FreeSeconds, 86400);
            FreeStart.Content = T("Start timer");
            Help(FreeStart, T(ActionDescriptions["Start timer"]));
            FreeStart.IsEnabled = owner.CanStartFreeTimer;
            free.Children.Add(FreeStart);
            Sections.Children.Add(Section(T("Free timer"), free, false));
            error.Visibility = Visibility.Collapsed;
            Sections.Children.Add(error);
            var scroll = new ScrollViewer { Content = Sections, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = Math.Max(185, Math.Min(620, owner.root.ActualHeight - 185)) };
            Dialog = owner.Dialog("Timer settings", scroll);
            Dialog.PrimaryButtonClick += (_, args) => args.Cancel = !Apply();
            FreeStart.Click += (_, _) =>
            {
                if (!owner.CanStartFreeTimer || !Apply()) return;
                StartRequested = true;
                Dialog.Hide();
            };
        }

        private Grid Setting(Control input, string label, string help) =>
            WithTrailing(FieldLabel(input, owner.localization.Text(label), owner.localization.Text(help)), input);

        private ToggleSwitch AddSwitch(StackPanel group, string key, string help, bool value)
        {
            var toggle = new ToggleSwitch { IsOn = value, OnContent = "", OffContent = "", MinWidth = 44 };
            group.Children.Add(Setting(toggle, key, help));
            return toggle;
        }

        private NumberBox AddSeconds(StackPanel group, string key, string help, int value, int maximum)
        {
            var box = SecondsBox(value);
            box.Maximum = maximum;
            box.Width = 112;
            group.Children.Add(Setting(box, key, help));
            return box;
        }

        private bool Apply()
        {
            var previous = owner.settings.Timer;
            try
            {
                int warn = ReadSeconds(warningSeconds);
                if (warn > warningSeconds.Maximum) throw new ArgumentException("Enter a whole number of seconds.");
                var draft = new TimerSettings(size.SelectedIndex == 1, showNext.IsOn, showTotal.IsOn,
                    showNote.IsOn, showObs.IsOn, recordCountdown.IsOn, endWarning.IsOn, warn, ReadSeconds(freeSeconds));
                owner.settings.Timer = draft;
                LocalSettings.Save(owner.settings);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                owner.settings.Timer = previous;
                error.Text = owner.localization.Text(ex.Message);
                error.Visibility = Visibility.Visible;
                return false;
            }
            if (owner.timerWindow is { } window && previous.Large != owner.settings.Timer.Large)
                owner.timerFace?.ApplySize(window, owner.settings.Timer.Large);
            owner.RefreshPlayback();
            return true;
        }
    }
}
