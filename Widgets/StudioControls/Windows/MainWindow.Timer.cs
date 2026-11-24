using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VibeKit.StudioControls.Core;

namespace VibeKit.StudioControls.Windows;

public sealed partial class MainWindow
{
    private readonly WorkflowPlayer player = new();
    private readonly DispatcherTimer workflowClock = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly TextBlock inlineTimer = Text("");
    private readonly Button rehearse = new() { Content = "Rehearse" };
    private readonly Button live = new() { Content = "Play in OBS" };
    private readonly Button pauseWorkflow = new(), nextWorkflow = new() { Content = "Next" }, stopWorkflow = new() { Content = "Stop workflow" };
    private readonly NativeLocalization timerLocalization = new();
    private Window? timerWindow;
    private PresenterFace? timerFace;
    private TimerSettingsEditor? timerEditor;
    private CancellationTokenSource? playbackCancellation;
    private bool checkingScenes, sendingScene;
    private string? lastConfirmedScene;
    private (WorkflowState State, bool Checking, bool Sending)? lastControlState;

    private bool PlaybackActive => checkingScenes || player.State is WorkflowState.Running or WorkflowState.Paused or WorkflowState.Switching;
    private bool LiveCheckActive => checkingScenes || (player.Mode == WorkflowMode.Live && PlaybackActive);

    private FrameworkElement BuildPlaybackPanel()
    {
        var panel = Column(8);
        var buttons = Row();
        rehearse.Click += async (_, _) => await StartWorkflowAsync(false);
        live.Click += async (_, _) => await StartWorkflowAsync(true);
        buttons.Children.Add(rehearse);
        buttons.Children.Add(live);
        WireTransport(buttons, pauseWorkflow, nextWorkflow, stopWorkflow);
        buttons.Children.Add(ActionButton("Open timer", () => { ShowTimer(); return Task.CompletedTask; }));
        panel.Children.Add(buttons); panel.Children.Add(inlineTimer);
        workflowClock.Tick += async (_, _) =>
        {
            player.Tick();
            await AdvanceWorkflowAsync();
        };
        return panel;
    }

    private void WireTransport(StackPanel row, Button hold, Button next, Button stopButton)
    {
        hold.Content = "Pause";
        next.Content = "Next";
        stopButton.Content = "Stop workflow";
        hold.Click += (_, _) =>
        {
            if (player.State == WorkflowState.Paused) player.Resume();
            else player.Pause();
            RefreshPlayback();
        };
        next.Click += async (_, _) => { player.Next(); await AdvanceWorkflowAsync(); };
        stopButton.Click += (_, _) => { if (!CancelRecordingCue()) StopWorkflow("Workflow stopped."); };
        row.Children.Add(hold); row.Children.Add(next); row.Children.Add(stopButton);
    }

    private void ShowTimer()
    {
        if (timerWindow is not null) { timerWindow.Activate(); return; }
        var window = new Window { Title = localization.Text("Timer") };
        timerWindow = window;
        var face = new PresenterFace(this);
        timerFace = face;
        window.Content = new ScrollViewer { Content = face.Body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        face.Body.RequestedTheme = root.RequestedTheme;
        face.Body.Language = localization.Language;
        timerLocalization.Language = localization.Language;
        timerLocalization.Attach(face.Body);
        long statusBinding = status.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => face.ShowObsStatus(status.Text));
        window.Closed += (_, _) =>
        {
            CancelRecordingCue();
            timerEditor?.Dialog.Hide();
            if (player.Mode == WorkflowMode.Free && PlaybackActive) StopWorkflow("Timer stopped.");
            status.UnregisterPropertyChangedCallback(TextBlock.TextProperty, statusBinding);
            face.Close();
            timerLocalization.Dispose();
            timerWindow = null;
            timerFace = null;
        };
        window.Activate();
        face.ApplySize(window, settings.Timer.Large);
        UpdateWorkflowControls();
        RefreshPlayback();
    }

    private async Task StartWorkflowAsync(bool useObs)
    {
        if (recordingCue is not null || CurrentWorkflow is not { Steps.Length: > 0 } flow || PlaybackActive || sendingScene || editingLibrary || workflowDialogOpen || rendering) return;
        CancellationToken token = lifetime.Token;
        try
        {
            if (useObs)
            {
                if (service is not { } connected || state?.ConnectionAvailable != true || stopPending || connecting || commandBusy || manualTakeoverPending) return;
                playbackCancellation?.Dispose();
                playbackCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                token = playbackCancellation.Token;
                lastConfirmedScene = null;
                checkingScenes = true;
                RefreshPlayback();
                await obsGate.WaitAsync(token);
                try
                {
                    token.ThrowIfCancellationRequested();
                    var available = await connected.GetWorkflowScenesAsync(token, flow.Steps.Select(step => step.Scene), flow.Steps.Select(step => step.Transition));
                    token.ThrowIfCancellationRequested();
                    var missing = flow.Steps.Select(step => step.Scene).Distinct().Except(available).ToArray();
                    if (missing.Length != 0) throw new InvalidOperationException(localization.Text("Missing scenes") + " · " + string.Join(", ", missing));
                }
                finally { obsGate.Release(); }
            }
            player.Start(flow, useObs);
            checkingScenes = false;
            ShowTimer();
            await AdvanceWorkflowAsync();
        }
        catch (Exception) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            Diagnostics.Record("workflow.start", error);
            StopWorkflow(localization.Text(error.Message));
            if (error is ObsConnectionException) Offline(error);
        }
        finally
        {
            checkingScenes = false;
            if (!closed) RefreshPlayback();
        }
    }

    private async Task AdvanceWorkflowAsync()
    {
        if (closed) return;
        if (sendingScene || player.State != WorkflowState.Switching || player.Current is not { } step ||
            playbackCancellation is not { } cancellation)
        { RefreshPlayback(); return; }
        sendingScene = true;
        RefreshPlayback();
        long ticket = player.Ticket;
        var token = cancellation.Token;
        try
        {
            await obsGate.WaitAsync(token);
            try
            {
                token.ThrowIfCancellationRequested();
                if (service is null || state?.ConnectionAvailable != true || stopPending)
                    throw new InvalidOperationException(localization.Text("OBS unavailable."));
                await service.SetWorkflowSceneAsync(step.Scene, lastConfirmedScene, token, step.Transition);
                token.ThrowIfCancellationRequested();
                if (player.Ticket == ticket)
                {
                    lastConfirmedScene = step.Scene;
                    player.Confirm(ticket);
                }
            }
            finally { obsGate.Release(); }
        }
        catch (WorkflowTransitionRecoveryException error)
        {
            Diagnostics.Record("workflow.transition", error);
            StopWorkflow(localization.Text(error.Message));
        }
        catch (Exception) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            Diagnostics.Record("workflow.scene", error);
            player.Fail(ticket, localization.Text("Scene change stopped") + " · " + SceneTitle(step.Scene) + " · " + localization.Text(error.Message));
            if (error is ObsConnectionException) Offline(error);
        }
        finally
        {
            sendingScene = false;
            if (!closed) RefreshPlayback();
        }
    }

    private void ObserveWorkflowStatus(StudioStatus result)
    {
        if (!LiveCheckActive) return;
        if (!result.ConnectionAvailable || !result.IsFresh) InterruptLivePlayback("OBS status is unconfirmed.");
        else if (state?.IsRecording == true && (!result.IsRecording || (state.IsPaused == false && result.IsPaused)))
            InterruptLivePlayback("Recording interrupted.");
        else if (lastConfirmedScene is not null && result.SceneName != lastConfirmedScene)
            InterruptLivePlayback("OBS scene changed manually.");
    }

    private void StopWorkflow(string reason)
    {
        player.Stop(reason);
        playbackCancellation?.Cancel();
        lastConfirmedScene = null;
        if (!closed) RefreshPlayback();
    }

    private void InterruptLivePlayback(string reason)
    {
        if (LiveCheckActive) StopWorkflow(reason);
    }

    private void UpdateWorkflowControls()
    {
        bool editable = workflowLibrary is not null && recordingCue is null && !PlaybackActive && !sendingScene && !editingLibrary && !rendering && !workflowDialogOpen;
        libraryButton.IsEnabled = targetButton.IsEnabled = paletteSurface.IsEnabled = editable;
        rehearse.IsEnabled = editable && CurrentWorkflow?.Steps.Length > 0;
        live.IsEnabled = rehearse.IsEnabled && service is not null && state?.ConnectionAvailable == true && !stopPending && !connecting && !manualTakeoverPending && !commandBusy;
        apply.IsEnabled = editable;
        if (timerEditor is not null) timerEditor.FreeStart.IsEnabled = CanStartFreeTimer;
        bool active = PlaybackActive;
        foreach (var button in new[] { pauseWorkflow, nextWorkflow, stopWorkflow })
            button.Visibility = active || button == stopWorkflow && recordingCue?.IsPending == true ? Visibility.Visible : Visibility.Collapsed;
        rehearse.Visibility = live.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        inlineTimer.Visibility = player.State == WorkflowState.Idle ? Visibility.Collapsed : Visibility.Visible;
        bool running = player.State == WorkflowState.Running;
        bool paused = player.State == WorkflowState.Paused;
        pauseWorkflow.Content = paused ? "Resume" : "Pause";
        pauseWorkflow.IsEnabled = running || paused;
        nextWorkflow.IsEnabled = running && player.Mode != WorkflowMode.Free;
        stopWorkflow.IsEnabled = PlaybackActive || recordingCue?.IsPending == true;
        timerFace?.UpdateTransport();
    }

    private void RefreshPlayback()
    {
        if (player.State == WorkflowState.Running) { if (!workflowClock.IsEnabled) workflowClock.Start(); }
        else workflowClock.Stop();
        var controls = (player.State, checkingScenes, sendingScene);
        if (lastControlState != controls)
        {
            UpdateWorkflowControls();
            lastControlState = controls;
        }
        HighlightWorkflowStep();
        string title = player.Current is { } current ? SceneTitle(current.Scene) : localization.Text("Timer");
        string digits = ClockText(player.RemainingSeconds);
        string mode = localization.Text(checkingScenes ? "Checking scenes…" : player.State switch
        {
            WorkflowState.Switching => "Switching scene…", WorkflowState.Finished => "Workflow finished.",
            WorkflowState.Stopped => player.Reason, WorkflowState.Idle => "Ready", WorkflowState.Paused => "Paused",
            _ => player.Mode switch { WorkflowMode.Rehearsal => "Rehearsal", WorkflowMode.Free => "Free timer", _ => "Play in OBS" }
        });
        string summary = mode;
        if (player.NextStep is { } next && PlaybackActive) summary += "  ·  " + localization.Text("Next") + " " + SceneTitle(next.Scene);
        if (player.Mode != WorkflowMode.Free && PlaybackActive) summary += "  ·  " + localization.Text("Remaining") + " " + ClockText(player.TotalRemainingSeconds);
        if (player.Mode == WorkflowMode.Live && player.State == WorkflowState.Finished)
            summary += "  ·  " + localization.Text(service is null || state?.ConnectionAvailable != true || !state.IsFresh
                ? "Check recording in OBS." : state.IsRecording ? "OBS is recording." : "OBS is not recording.");
        if (inlineTimer.Text != summary + "  ·  " + title + " " + digits) inlineTimer.Text = summary + "  ·  " + title + " " + digits;
        timerFace?.Present(mode);
        if (timerEditor is not null) timerEditor.FreeStart.IsEnabled = CanStartFreeTimer;
    }

    private async Task VerifyWorkflowUiAsync()
    {
        if (!UiSmoke.Enabled) throw new InvalidOperationException("Isolated verification required.");
        var tabs = root.Children.OfType<TabView>().Single();
        tabs.SelectedIndex = tabs.TabItems.Count - 1;
        root.UpdateLayout();
        await InsertStepAsync("Intro", 0);
        await InsertStepAsync("Thank you", 1);
        var flow = CurrentWorkflow!;
        await SaveWorkflowAsync(flow with { Steps = [flow.Steps[0], flow.Steps[1] with { Transition = "fade" }] });
        var saved = WorkflowLibrary.Load(LibraryPath, "");
        if (timeline.Children.Count != 3 || timeline.Children[1] is not FontIcon ||
            timeline.Children[2] is not Button last ||
            !Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(last).Contains(ClockText(15)) ||
            saved.Projects.Single().Workflows.Single().Steps[1].Transition != "fade" ||
            !saved.Projects.Single().Workflows.Single().Steps.Select(step => step.Scene)
            .SequenceEqual(new[] { "Intro", "Thank you" }))
            throw new InvalidOperationException("The workflow editor did not preserve the sequence.");
        var libraryDialog = new LibraryDialog(this, saved, saved.Projects.Single(), saved.Projects.Single().Workflows.Single());
        await UiSmoke.CheckLibraryDialogAsync(libraryDialog.Dialog, localization, () => libraryDialog.Draft);
        await StartWorkflowAsync(false);
        if (timerWindow is null || player.State != WorkflowState.Running || player.Index != 0 || highlightedIndex != 0)
            throw new InvalidOperationException("The detached rehearsal timer did not start.");
        player.Next();
        await AdvanceWorkflowAsync();
        if (player.Current?.Scene != "Thank you" || highlightedIndex != 1 || camera.IsValueCreated || service is not null || client is not null)
            throw new InvalidOperationException("Rehearsal did not stay offline.");
        await CheckPresenterAsync();
        StopWorkflow("Workflow stopped.");
        timerWindow.Close();
    }

    private void CloseWorkflow()
    {
        workflowClock.Stop();
        StopWorkflow("");
        playbackCancellation?.Dispose();
        timerWindow?.Close();
    }
}
