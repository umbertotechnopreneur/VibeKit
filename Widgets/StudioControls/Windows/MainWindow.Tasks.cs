using Microsoft.UI.Xaml.Controls;
using VibeKit.StudioControls.Core;

namespace VibeKit.StudioControls.Windows;

public sealed partial class MainWindow
{
    private bool cameraSuspended;

    private async Task ConnectAsync()
    {
        if (connecting || commandBusy || stopPending || closed) return;
        connecting = true;
        connect.IsEnabled = false;
        try
        {
            var connection = new ObsConnectionSettings(host.Text.Trim(), checked((int)port.Value), password.Password);
            connection.Validate();
            await DisconnectAsync();
            settings.Host = connection.Host; settings.Port = connection.Port;
            settings.ProtectedPassword = LocalSettings.ProtectPassword(connection.Password);
            LocalSettings.Save(settings);
            client = new ObsClient(connection);
            await client.ConnectAsync(lifetime.Token);
            service = new StudioService(client);
            SetState(await service.GetStatusAsync(lifetime.Token));
            if (state?.ConnectionAvailable == true)
            {
                polling.Start();
                if (state.Warnings.Count == 0) notice.Text = "Connected. Recording starts only when you press Start recording.";
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { Offline(error); }
        finally
        {
            connecting = false;
            if (!closed) { connect.IsEnabled = true; UpdateEnabled(service is not null && state?.ConnectionAvailable == true); }
        }
    }

    private async Task DisconnectAsync()
    {
        polling.Stop(); effectsTimer.Stop(); pendingEffects.Clear();
        if (service is not null && !await obsGate.WaitAsync(0, lifetime.Token))
            throw new InvalidOperationException("Wait for the current OBS action before disconnecting.");
        bool ownsGate = service is not null;
        try
        {
            service = null;
            if (client is not null) await client.DisposeAsync();
            client = null;
            if (!closed)
            {
                status.Text = state?.IsRecording == true ? "Disconnected · OBS may still be recording. Check OBS." : "Not connected · choose Connect when OBS is ready.";
                UpdateEnabled(false);
            }
        }
        finally { if (ownsGate) obsGate.Release(); }
    }

    private async Task PollAsync()
    {
        if (closed || pollBusy || connecting || stopPending || service is null || !await obsGate.WaitAsync(0, lifetime.Token)) return;
        pollBusy = true;
        try { SetState(await service.GetStatusAsync(lifetime.Token)); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { Offline(error); }
        finally { pollBusy = false; obsGate.Release(); }
    }

    private async Task CommandAsync(string operation, Func<CancellationToken, Task<StudioStatus>> action)
    {
        if (closed || service is null || stopPending) return;
        if (!await obsGate.WaitAsync(0, lifetime.Token))
        {
            notice.Text = "An OBS request is finishing. Try again in a moment.";
            return;
        }
        commandBusy = true;
        // A deliberate command wins over an older, not-yet-sent slider gesture.
        pendingEffects.Clear(); effectsTimer.Stop();
        UpdateEnabled(state?.ConnectionAvailable == true);
        try
        {
            SetState(await action(lifetime.Token));
            if (!closed && state?.Warnings.Count == 0) notice.Text = state?.OutputPath is { Length: > 0 } saved ? "Video saved: " + saved : "";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (ObsConnectionException error) { Offline(error); }
        catch (Exception error) { Fail(operation, error); }
        finally
        {
            commandBusy = false; obsGate.Release();
            if (!closed) UpdateEnabled(service is not null && state?.ConnectionAvailable == true);
        }
    }

    private async Task StopRecordingAsync()
    {
        if (closed || stopPending || service is null || state?.ConnectionAvailable != true) return;
        stopPending = true;
        pendingEffects.Clear(); effectsTimer.Stop();
        stop.Content = "Stop requested…";
        UpdateEnabled(true);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        bool acquired = false;
        try
        {
            // One explicit Stop may wait for the bounded request already in flight.
            // Polls and unsent slider gestures cannot get ahead of it.
            await obsGate.WaitAsync(deadline.Token);
            acquired = true;
            if (service is null || state?.ConnectionAvailable != true)
                throw new ObsConnectionException("Stop was not sent because OBS disconnected. Check OBS before reconnecting.");
            SetState(await service.SetRecordingAsync(false, deadline.Token));
            if (!closed && state?.OutputPath is { Length: > 0 } saved) notice.Text = "Video saved: " + saved;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (ObsConnectionException error) { Offline(error); }
        catch (Exception error) { Fail("record.stop", error); }
        finally
        {
            if (acquired) obsGate.Release();
            stopPending = false;
            if (!closed) { stop.Content = "■ Stop and save"; UpdateEnabled(service is not null && state?.ConnectionAvailable == true); }
        }
    }

    private void QueueEffect(string key, Func<CancellationToken, Task> action)
    {
        if (closed || syncing || stopPending || service is null || state?.ConnectionAvailable != true) return;
        pendingEffects[key] = action;
        effectsTimer.Start();
    }

    private async Task FlushEffectsAsync()
    {
        if (closed || stopPending || service is null || state?.ConnectionAvailable != true) { effectsTimer.Stop(); pendingEffects.Clear(); return; }
        if (pendingEffects.Count == 0) { effectsTimer.Stop(); return; }
        if (!await obsGate.WaitAsync(0, lifetime.Token)) return;
        // At most one request chain per tick; values overwrite their keyed slot.
        var (key, action) = pendingEffects.First();
        pendingEffects.Remove(key);
        try { await action(lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (ObsConnectionException error) { Offline(error); }
        catch (Exception error) { Fail("effect", error); }
        finally { obsGate.Release(); }
    }

    private void SetState(StudioStatus result)
    {
        if (closed) return;
        if (result.SceneName != state?.SceneName) pendingEffects.Remove("screen");
        state = result;
        if (!result.ConnectionAvailable)
        {
            polling.Stop(); effectsTimer.Stop(); pendingEffects.Clear();
        }
        status.Text = (result.IsRecording ? result.IsPaused ? "Ⅱ Paused " : "● Recording " : "Ready · not recording ")
            + result.Timecode + " · " + result.SceneName + (result.IsFresh ? "" : " · status refresh pending");
        if (!result.ConnectionAvailable) status.Text += " · connection lost; check OBS";
        pause.Content = result.IsPaused ? "Resume" : "Pause";
        notice.Text = string.Join(" · ", result.Warnings);
        syncing = true;
        try
        {
            if (!pendingEffects.ContainsKey("blur")) blur.IsOn = result.BlurEnabled;
            if (!pendingEffects.ContainsKey("blur-intensity")) blurIntensity.Value = result.BlurIntensity;
            if (result.Screen is { } screen && !pendingEffects.ContainsKey("screen"))
            { screenZoom.Value = screen.Zoom; screenX.Value = screen.X; screenY.Value = screen.Y; }
            foreach (var (key, controls) in vignettes)
                if (result.Vignettes.TryGetValue(key, out var vignette) && !pendingEffects.ContainsKey("vignette-" + key))
                { controls.Toggle.IsOn = vignette.Enabled; controls.Slider.Value = vignette.Strength; }
        }
        finally { syncing = false; }
        foreach (var (name, button) in audioButtons)
            button.Content = (name == "Mic/Aux" ? "Microphone" : "Desktop audio") + " · "
                + (result.AudioMutes.TryGetValue(name, out bool muted) ? muted ? "MUTED" : "ON" : "unavailable");
        ApplyTheme();
        UpdateEnabled(result.ConnectionAvailable);
    }

    private void UpdateEnabled(bool connected)
    {
        bool ready = connected && !commandBusy && !connecting && !stopPending;
        foreach (var button in sceneButtons.Values) button.IsEnabled = ready;
        record.IsEnabled = ready && state?.IsRecording != true;
        stop.IsEnabled = connected && !connecting && !stopPending && state?.IsRecording == true;
        pause.IsEnabled = ready && state?.IsRecording == true;
        foreach (var (name, button) in audioButtons)
            button.IsEnabled = ready && state?.AudioAvailable.GetValueOrDefault(name) == true;
        blur.IsEnabled = blurIntensity.IsEnabled = ready && state?.BlurAvailable == true;
        screenZoom.IsEnabled = ready && state?.Screen is not null;
        screenX.IsEnabled = screenY.IsEnabled = screenZoom.IsEnabled && screenZoom.Value > 1;
        foreach (var (key, controls) in vignettes)
            controls.Toggle.IsEnabled = controls.Slider.IsEnabled = ready && state?.Vignettes.ContainsKey(key) == true;
        apply.IsEnabled = !rendering;
    }

    private void Offline(Exception error)
    {
        Diagnostics.Record("obs.connection", error);
        if (closed) return;
        polling.Stop(); effectsTimer.Stop(); pendingEffects.Clear();
        if (state is not null) state = state with { ConnectionAvailable = false, IsFresh = false };
        status.Text = state is null ? "Waiting for OBS · open OBS with WebSocket enabled, then Connect."
            : "OBS connection lost · recording state unconfirmed. Check OBS, then reconnect.";
        notice.Text = error.Message;
        foreach (var (name, button) in audioButtons) button.Content = name + " · offline";
        UpdateEnabled(false);
    }

    private async Task RefreshCamerasAsync()
    {
        if (cameraBusy || cameraSuspended) return;
        await RunCameraAsync(async token =>
        {
            var devices = await camera.Value.ListAsync(token);
            if (closed || cameraSuspended) return;
            cameras.ItemsSource = devices;
            cameras.SelectedItem = devices.FirstOrDefault(device => device.Id == settings.CameraId) ?? devices.FirstOrDefault();
            cameraNotice.Text = devices.Count == 0 ? "No camera found." : "Choose a camera, then Open controls. OBS selects its video source separately.";
        });
    }

    private async Task OpenCameraAsync()
    {
        if (cameraBusy || cameraSuspended) return;
        if (cameras.SelectedItem is not CameraDevice device) { await RefreshCamerasAsync(); return; }
        pendingCamera.Clear(); cameraTimer.Stop();
        await RunCameraAsync(async token =>
        {
            var values = await camera.Value.OpenAsync(device.Id, token);
            if (closed || cameraSuspended) return;
            settings.CameraId = device.Id; SavePreferences(); SetCameraState(values);
        });
    }

    private async Task CameraFrameAsync(CameraFraming? frame)
    {
        if (cameraBusy || cameraSuspended) return;
        pendingCamera.Clear(); cameraTimer.Stop();
        await RunCameraAsync(async token => SetCameraState(await camera.Value.FrameAsync(frame, token)));
    }

    private async Task FlushCameraAsync()
    {
        if (closed || cameraSuspended || pendingCamera.Count == 0) { cameraTimer.Stop(); return; }
        if (cameraBusy) return;
        var (property, value) = pendingCamera.First();
        pendingCamera.Remove(property);
        await RunCameraAsync(async token => SetCameraState(await camera.Value.SetAsync(property, value, token)));
    }

    private async Task RunCameraAsync(Func<CancellationToken, Task> operation)
    {
        cameraBusy = true;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        try
        {
            Task task = operation(request.Token);
            await task.WaitAsync(TimeSpan.FromSeconds(5), lifetime.Token);
        }
        catch (TimeoutException error)
        {
            request.Cancel();
            cameraSuspended = true;
            pendingCamera.Clear(); cameraTimer.Stop();
            foreach (var slider in cameraSliders.Values) slider.IsEnabled = false;
            cameraNotice.Text = "Camera driver timed out. Controls are paused; reconnect the camera and reopen this panel. OBS controls remain available.";
            Diagnostics.Record("camera.timeout", error);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            pendingCamera.Clear(); cameraTimer.Stop();
            cameraNotice.Text = "Camera controls unavailable: " + error.Message;
            Diagnostics.Record("camera.controls", error);
        }
        finally { cameraBusy = false; }
    }

    private void SetCameraState(IReadOnlyDictionary<CameraProperty, CameraValue> values)
    {
        if (closed || cameraSuspended) return;
        cameraValues = values;
        syncing = true;
        try
        {
            foreach (var (property, slider) in cameraSliders)
            {
                bool available = values.TryGetValue(property, out var value) && value.Manual;
                slider.IsEnabled = available;
                if (!available) continue;
                slider.Minimum = value!.Minimum; slider.Maximum = value.Maximum; slider.StepFrequency = value.Step;
                if (!pendingCamera.ContainsKey(property)) slider.Value = value.Value;
            }
        }
        finally { syncing = false; }
        cameraNotice.Text = values.Count == 0 ? "This camera does not expose native pan, tilt or zoom." : "Native camera controls ready. Unsupported adjustments are disabled.";
    }

    private void Shutdown()
    {
        if (closed) return;
        closed = true; lifetime.Cancel();
        polling.Stop(); effectsTimer.Stop(); cameraTimer.Stop(); smokeTimer?.Stop();
        pendingEffects.Clear(); pendingCamera.Clear();
        systemSettings.ColorValuesChanged -= SystemThemeChanged;
        if (camera.IsValueCreated) camera.Value.Dispose();
        if (client is not null) _ = CloseClientAsync(client);
    }

    private static async Task CloseClientAsync(ObsClient connection)
    {
        try { await connection.DisposeAsync(); }
        catch (Exception error) { Diagnostics.Record("obs.close", error); }
    }
}
