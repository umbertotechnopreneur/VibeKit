using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Text.Json;
using VibeKit.StudioControls.Core;

namespace VibeKit.StudioControls.Windows;

public sealed partial class MainWindow
{
    private static readonly Uri DeckDownloads = new("https://github.com/umbertotechnopreneur/ulanzi-dock-vibeware/releases");
    private readonly DispatcherTimer deckClock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Guid deckInstance = Guid.NewGuid();
    private long deckRun;
    private bool deckLiveWorkflow;
    private DateTime deckObsConfirmedAt;
    private readonly CheckBox deckEnabled = new() { MinWidth = 28, Padding = new(0) };
    private readonly StackPanel deckOption = new() { Orientation = Orientation.Horizontal, Spacing = 4, Visibility = Visibility.Collapsed };
    private readonly StackPanel deckMissing = Column(4);
    private bool deckPublished, deckBusy;
    private DeckState? previousDeckState;
    private DateTime deckPublishedAt;
    private string? deckIoError;

    private sealed record DeckState(int Version, Guid Instance, long Run, long Ticket, int Seconds, string Next, bool Italian,
        bool Paused, bool Running, bool Recording, bool RecordingPaused, bool? MicMuted, bool PrivacyAvailable);
    private sealed record DeckAction(Guid Instance, long Run, long Ticket, string Action);

    private static string DeckDirectory => Path.Combine(
        LocalSettings.VerificationDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VibeKit"),
        "StudioDeck");
    private static string DeckStatePath => Path.Combine(DeckDirectory, "state.json");

    private UIElement BuildDeck()
    {
        var content = Column(8);
        var link = new HyperlinkButton { Content = "Download UlanziDeck", NavigateUri = DeckDownloads, Padding = new(0) };
        ActionHelp(link, "Download UlanziDeck");
        deckMissing.Children.Add(Text("Have a Ulanzi D200H? Connect it and open a compatible UlanziDeck controller."));
        deckMissing.Children.Add(link);
        deckEnabled.IsChecked = settings.UseUlanziDeck;
        deckOption.Children.Add(deckEnabled);
        deckOption.Children.Add(FieldLabel(deckEnabled, "Use it as a secondary monitor",
            "The wide display shows the next scene and the recording status confirmed by OBS."));
        deckEnabled.Checked += (_, _) => SetDeckEnabled(true);
        deckEnabled.Unchecked += (_, _) => SetDeckEnabled(false);
        content.Children.Add(deckOption);
        content.Children.Add(deckMissing);
        content.Children.Add(ActionButton("Learn more", ShowDeckHelpAsync));
        return Section("VibeWare · UlanziDeck D200H", content, settings.UseUlanziDeck);
    }

    private async Task ShowDeckHelpAsync()
    {
        var content = Column(10);
        content.Children.Add(new Image
        {
            Source = new BitmapImage(new Uri("ms-appx:///Assets/UlanziDeck/recording-preview.png")),
            MaxWidth = 660, MaxHeight = 350, Stretch = Stretch.Uniform
        });
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(content.Children[0], localization.Text("UlanziDeck recording layout"));
        content.Children.Add(Text(localization.Text("The middle row shows time left in the current scene. It refreshes at each scene change.")));
        content.Children.Add(Text(localization.Text("Use +10 s or +20 s for more time. Pause holds the scene while OBS keeps recording.")));
        content.Children.Add(Text(localization.Text("The wide display shows the next scene and the recording status confirmed by OBS.")));
        content.Children.Add(Text(localization.Text("Privacy switches to the privacy scene and ends the running workflow. Audio stays as set.")));
        var link = new HyperlinkButton { Content = localization.Text("Download UlanziDeck"), NavigateUri = DeckDownloads, Padding = new(0) };
        Help(link, localization.Text(ActionDescriptions["Download UlanziDeck"]));
        content.Children.Add(link);
        var dialog = Dialog("UlanziDeck recording layout", new ScrollViewer
        {
            Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = Math.Max(160, root.ActualHeight - 170)
        }, "Close", "");
        dialog.MinWidth = Math.Min(700, Math.Max(280, root.ActualWidth - 56));
        await dialog.ShowAsync();
    }

    private void InitializeDeck()
    {
        if (UiSmoke.Enabled) return;
        deckClock.Tick += async (_, _) => await UpdateDeckAsync();
        deckClock.Start();
    }

    private void SetDeckEnabled(bool enabled)
    {
        settings.UseUlanziDeck = enabled;
        SavePreferences();
        if (!enabled)
        {
            deckLiveWorkflow = false;
            deckInstance = Guid.NewGuid();
            ClearDeckState();
        }
    }

    private static bool FreshDeckFile(string path, TimeSpan limit)
    {
        var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
        return age >= TimeSpan.Zero && age <= limit;
    }

    private bool FreshDeckObs => state is { IsFresh: true, ConnectionAvailable: true } &&
        DateTime.UtcNow - deckObsConfirmedAt <= TimeSpan.FromSeconds(5);

    private async Task UpdateDeckAsync()
    {
        if (deckBusy || closed) return;
        deckBusy = true;
        try
        {
            bool available = FreshDeckFile(Path.Combine(DeckDirectory, "dock-v1"), TimeSpan.FromSeconds(3));
            deckOption.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
            deckMissing.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
            if (!available || !settings.UseUlanziDeck)
            {
                deckLiveWorkflow = false;
                ClearDeckState();
                return;
            }

            bool sceneActive = PlaybackActive && player.Current is not null;
            if (sceneActive && player.Mode == WorkflowMode.Live) deckLiveWorkflow = true;
            bool liveObs = deckLiveWorkflow && FreshDeckObs;
            bool recording = liveObs && state!.IsRecording;
            if (!sceneActive && !recording)
            {
                if (FreshDeckObs && state?.IsRecording == false) deckLiveWorkflow = false;
                ClearDeckState();
                return;
            }

            bool italian = localization.Language == "it";
            var snapshot = new DeckState(1, deckInstance, deckRun, player.Ticket,
                sceneActive ? player.RemainingSeconds : 0,
                sceneActive && player.NextStep is { } next ? italian ? SceneTitle(next.Scene) : next.Scene : "",
                italian, sceneActive && player.State == WorkflowState.Paused,
                sceneActive && player.CanExtendCurrent, recording, recording && state!.IsPaused,
                liveObs && state!.AudioAvailable.GetValueOrDefault("Mic/Aux")
                    ? state.AudioMutes.GetValueOrDefault("Mic/Aux") : null,
                liveObs);
            if (snapshot != previousDeckState || DateTime.UtcNow - deckPublishedAt >= TimeSpan.FromSeconds(1))
            {
                string staged = DeckStatePath + ".tmp";
                try
                {
                    File.WriteAllBytes(staged, JsonSerializer.SerializeToUtf8Bytes(snapshot, SettingsStore.JsonOptions));
                    File.Move(staged, DeckStatePath, overwrite: true);
                }
                finally { File.Delete(staged); }
                deckPublished = true;
                previousDeckState = snapshot;
                deckPublishedAt = DateTime.UtcNow;
            }
            foreach (string file in Directory.EnumerateFiles(DeckDirectory, "action-*.json").Order(StringComparer.Ordinal).Take(32))
            {
                DeckAction? action = TakeDeckAction(file);
                if (action?.Instance != deckInstance || action.Run != deckRun || action.Ticket != player.Ticket ||
                    closed || !settings.UseUlanziDeck) continue;
                await RunDeckActionAsync(action.Action);
            }
            deckIoError = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            if (deckIoError != error.GetType().Name)
            {
                deckIoError = error.GetType().Name;
                Diagnostics.Record("deck.exchange", error);
            }
        }
        finally { deckBusy = false; }
    }

    private static DeckAction? TakeDeckAction(string path)
    {
        DeckAction? action = null;
        try
        {
            if (new FileInfo(path).Length <= 1024 && FreshDeckFile(path, TimeSpan.FromSeconds(2)))
                action = JsonSerializer.Deserialize<DeckAction>(File.ReadAllBytes(path), SettingsStore.JsonOptions);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        return action;
    }

    private async Task RunDeckActionAsync(string action)
    {
        switch (action)
        {
            case "add10": player.ExtendCurrent(10); RefreshPlayback(); break;
            case "add20": player.ExtendCurrent(20); RefreshPlayback(); break;
            case "pause":
                if (player.State == WorkflowState.Paused) player.Resume(); else player.Pause();
                RefreshPlayback();
                break;
            case "next":
                player.Next();
                await AdvanceWorkflowAsync();
                break;
            case "mic" when deckLiveWorkflow && FreshDeckObs &&
                state is { IsFresh: true, ConnectionAvailable: true } status &&
                status.AudioAvailable.GetValueOrDefault("Mic/Aux"):
                await CommandAsync("audio.mute", token => service!.SetAudioMutedAsync("Mic/Aux",
                    !status.AudioMutes.GetValueOrDefault("Mic/Aux"), token));
                break;
            case "privacy" when deckLiveWorkflow && FreshDeckObs:
                bool takeover = LiveCheckActive;
                InterruptLivePlayback("Manual scene selection.");
                await CommandAsync("scene", token => service!.SetSceneAsync("Privacy", token), takeover);
                break;
            case "stop" when deckLiveWorkflow && FreshDeckObs &&
                state is { IsFresh: true, ConnectionAvailable: true, IsRecording: true }:
                await StopRecordingAsync();
                break;
        }
    }

    private void ClearDeckState()
    {
        if (!deckPublished) return;
        try
        {
            File.Delete(DeckStatePath);
            deckPublished = false;
            previousDeckState = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (deckIoError != error.GetType().Name)
            {
                deckIoError = error.GetType().Name;
                Diagnostics.Record("deck.clear", error);
            }
        }
    }
}
