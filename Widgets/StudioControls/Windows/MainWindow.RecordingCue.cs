using VibeKit.StudioControls.Core;

namespace VibeKit.StudioControls.Windows;

public sealed partial class MainWindow
{
    private RecordCountdown? recordingCue;
    private CancellationTokenSource? recordingCueDelay;
    private int RecordingCueSeconds { get; set; }

    private bool CancelRecordingCue()
    {
        if (recordingCue?.Cancel() != true) return false;
        recordingCueDelay?.Cancel();
        RecordingCueSeconds = 0;
        if (!closed)
        {
            notice.Text = localization.Text("Recording countdown cancelled.");
            UpdateEnabled(state?.ConnectionAvailable == true);
            RefreshPlayback();
        }
        return true;
    }

    private async Task ManualRecordAsync()
    {
        if (recordingCue is not null) { CancelRecordingCue(); return; }
        if (closed || connecting || stopPending || commandBusy || client is null || service is not { } connected ||
            state is not { ConnectionAvailable: true, IsFresh: true, IsRecording: false, IsPaused: false }) return;
        if (!settings.Timer.RecordCountdown)
        {
            await CommandAsync("record.start", token => connected.SetRecordingAsync(true, token));
            return;
        }

        var cue = new RecordCountdown(client.ConnectionGeneration, state.SceneName);
        using var delay = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        recordingCueDelay = delay;
        recordingCue = cue;
        UpdateEnabled(true);
        bool acquired = false;
        try
        {
            acquired = await obsGate.WaitAsync(2000, delay.Token);
            if (!acquired) throw new InvalidOperationException("An OBS request is finishing. Try again in a moment.");
            var target = await connected.GetRecordingTargetAsync(cue, lifetime.Token);
            obsGate.Release();
            acquired = false;
            delay.Token.ThrowIfCancellationRequested();
            if (!cue.Begin()) throw new InvalidOperationException("Recording countdown cancelled.");
            RecordingCueSeconds = 3;
            ShowTimer();
            for (int seconds = 3; seconds >= 1; seconds--)
            {
                RecordingCueSeconds = seconds;
                RefreshPlayback();
                await Task.Delay(1000, delay.Token);
            }
            // Keep red visible while the worker verifies the original OBS target.
            acquired = await obsGate.WaitAsync(2000, delay.Token);
            if (!acquired || closed || connecting || stopPending || service != connected)
                throw new InvalidOperationException("Recording countdown cancelled.");
            SetState(await connected.StartAfterCountdownAsync(cue, target, lifetime.Token));
        }
        catch (ObsConnectionException error) { if (!closed) Offline(error); }
        catch (Exception) when (delay.IsCancellationRequested) { }
        catch (Exception error) { if (!closed) Fail("record.countdown", error); }
        finally
        {
            if (acquired) obsGate.Release();
            recordingCue = null;
            recordingCueDelay = null;
            RecordingCueSeconds = 0;
            if (!closed) { UpdateEnabled(state?.ConnectionAvailable == true); RefreshPlayback(); }
        }
    }
}
