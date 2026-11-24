using System.Text.Json;

namespace VibeKit.StudioControls.Core;

/// <summary>Serializes complete user actions; optional capabilities cannot hide recording status.</summary>
public sealed partial class StudioService(IObsClient client, StudioCatalog? catalog = null)
{
    private readonly StudioCatalog catalog = catalog ?? StudioCatalog.Default;
    private readonly SemaphoreSlim operations = new(1, 1);
    private StudioStatus lastStatus = new();

    public Task<StudioStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        RunAsync(ReadStatusAsync, cancellationToken, 10);

    public Task<StudioStatus> SetSceneAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!catalog.Scenes.Any(scene => scene.Name == name)) throw new ArgumentException("Choose a scene from the studio catalog.", nameof(name));
        return RunAsync(async token =>
        {
            if (name == "Comparison A-B") await PrepareComparisonAsync(name, token).ConfigureAwait(false);
            await CallAsync("SetStudioModeEnabled", new { studioModeEnabled = false }, token).ConfigureAwait(false);
            await CallAsync("SetCurrentProgramScene", new { sceneName = name }, token).ConfigureAwait(false);
            return await AfterActionAsync("Change scene", state => state with { SceneName = name, Screen = null }, token).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<RecordingTarget> GetRecordingTargetAsync(RecordCountdown cue, CancellationToken token = default) =>
        RunAsync(deadline => ReadRecordingTargetAsync(cue, deadline), token);

    private async Task<RecordingTarget> ReadRecordingTargetAsync(RecordCountdown cue, CancellationToken token)
    {
        if (!cue.IsPending || client.ConnectionGeneration != cue.Generation)
            throw new InvalidOperationException("Recording countdown cancelled.");
        string collection = await CurrentCollectionAsync(token).ConfigureAwait(false);
        JsonElement scene = await CallAsync("GetCurrentProgramScene", null, token).ConfigureAwait(false);
        if (scene.GetProperty("currentProgramSceneName").GetString() != cue.Scene)
            throw new InvalidOperationException("The recording target changed.");
        JsonElement status = await CallAsync("GetRecordStatus", null, token).ConfigureAwait(false);
        if (status.GetProperty("outputActive").GetBoolean() || status.GetProperty("outputPaused").GetBoolean())
            throw new InvalidOperationException("OBS is already recording.");
        if (client.ConnectionGeneration != cue.Generation)
            throw new InvalidOperationException("OBS connection changed.");
        return new(collection, JsonRead.String(scene, "currentProgramSceneUuid"));
    }

    public Task<StudioStatus> StartAfterCountdownAsync(RecordCountdown cue, RecordingTarget originalTarget,
        CancellationToken cancellationToken = default) => RunAsync(async token =>
    {
        try
        {
            if (await ReadRecordingTargetAsync(cue, token).ConfigureAwait(false) != originalTarget)
                throw new InvalidOperationException("The recording target changed.");
            cue.Claim();
            return await ChangeRecordingAsync(true, token).ConfigureAwait(false);
        }
        finally { cue.Cancel(); }
    }, cancellationToken);

    public Task<StudioStatus> SetRecordingAsync(bool start, CancellationToken cancellationToken = default) =>
        RunAsync(token => ChangeRecordingAsync(start, token), cancellationToken);

    private async Task<StudioStatus> ChangeRecordingAsync(bool start, CancellationToken token)
    {
        JsonElement response = await CallAsync(start ? "StartRecord" : "StopRecord", null, token).ConfigureAwait(false);
        var warnings = new List<string>();
        bool transportFailed = false;
        if (start)
        {
            try
            {
                JsonElement scene = await CallAsync("GetCurrentProgramScene", null, token).ConfigureAwait(false);
                if (JsonRead.String(scene, "currentProgramSceneName") == "Intro")
                    await CallAsync("TriggerMediaInputAction", new
                    {
                        inputName = catalog.IntroSoundSource,
                        mediaAction = "OBS_WEBSOCKET_MEDIA_INPUT_ACTION_RESTART"
                    }, token).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsStatusFailure(exception))
            {
                transportFailed = exception is ObsConnectionException or OperationCanceledException;
                warnings.Add("Recording started; the optional intro sound could not be restarted.");
            }
        }
        string? outputPath = start ? null : JsonRead.String(response, "outputPath");
        return await AfterActionAsync(start ? "Start recording" : "Stop recording",
            state => state with { IsRecording = start, IsPaused = false }, token,
            warnings, string.IsNullOrWhiteSpace(outputPath) ? null : outputPath, transportFailed).ConfigureAwait(false);
    }

    public Task<StudioStatus> SetPausedAsync(bool paused, CancellationToken cancellationToken = default) => RunAsync(async token =>
    {
        await CallAsync(paused ? "PauseRecord" : "ResumeRecord", null, token).ConfigureAwait(false);
        return await AfterActionAsync(paused ? "Pause recording" : "Resume recording",
            state => state with { IsRecording = true, IsPaused = paused }, token).ConfigureAwait(false);
    }, cancellationToken);

    public Task<StudioStatus> SetAudioMutedAsync(string inputName, bool muted, CancellationToken cancellationToken = default)
    {
        if (!catalog.AudioInputs.Contains(inputName, StringComparer.Ordinal)) throw new ArgumentException("Unknown studio audio input.", nameof(inputName));
        return RunAsync(async token =>
        {
            await CallAsync("SetInputMute", new { inputName, inputMuted = muted }, token).ConfigureAwait(false);
            return await AfterActionAsync("Change audio mute", state => state with
            {
                AudioMutes = CopyWith(state.AudioMutes, inputName, muted),
                AudioAvailable = CopyWith(state.AudioAvailable, inputName, true)
            }, token).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<StudioStatus> SetBlurEnabledAsync(bool enabled, CancellationToken cancellationToken = default) => RunAsync(async token =>
    {
        await CallAsync("SetSourceFilterEnabled", new { sourceName = catalog.CameraSource, filterName = catalog.BlurFilter, filterEnabled = enabled }, token).ConfigureAwait(false);
        return await AfterActionAsync("Change background blur", state => state with { BlurEnabled = enabled, BlurAvailable = true }, token).ConfigureAwait(false);
    }, cancellationToken);

    public Task<StudioStatus> SetBlurIntensityAsync(int intensity, CancellationToken cancellationToken = default)
    {
        // This plugin interprets zero as background removal; OFF has a separate control.
        int bounded = Math.Clamp(intensity, 1, 20);
        return RunAsync(async token =>
        {
            await CallAsync("SetSourceFilterSettings", new
            {
                sourceName = catalog.CameraSource, filterName = catalog.BlurFilter,
                filterSettings = new { blur_background = bounded }, overlay = true
            }, token).ConfigureAwait(false);
            return await AfterActionAsync("Change blur intensity", state => state with { BlurIntensity = bounded, BlurAvailable = true }, token).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<StudioStatus> SetVignetteAsync(string key, bool enabled, double strength, CancellationToken cancellationToken = default)
    {
        VignetteDefinition definition = catalog.Vignettes.FirstOrDefault(item => item.Key == key)
            ?? throw new ArgumentException("Unknown vignette target.", nameof(key));
        double bounded = Math.Round(ScreenFraming.Bounded(strength, 0, 100));
        return RunAsync(async token =>
        {
            await CallAsync("SetSourceFilterSettings", new
            {
                sourceName = definition.SourceName, filterName = catalog.VignetteFilter,
                filterSettings = new { opacity = enabled ? bounded / 100 : 0, lab_enabled = enabled, lab_strength = bounded },
                overlay = true
            }, token).ConfigureAwait(false);
            return await AfterActionAsync("Change vignette", state => state with
            {
                Vignettes = CopyWith(state.Vignettes, key, new VignetteState(enabled, bounded))
            }, token).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<bool> SetScreenFramingAsync(string sceneName, long itemId, double zoom, double x, double y, CancellationToken cancellationToken = default)
    {
        // Validate before contacting OBS, including NaN/infinity from callers other than the sliders.
        ScreenFraming.Bounded(zoom, 1, 4); ScreenFraming.Bounded(x, -100, 100); ScreenFraming.Bounded(y, -100, 100);
        return RunAsync(async token =>
        {
            if (!await IsCurrentSceneAsync(sceneName, token).ConfigureAwait(false)) return false;
            JsonElement items = await CallAsync("GetSceneItemList", new { sceneName }, token).ConfigureAwait(false);
            JsonElement? match = items.GetProperty("sceneItems").EnumerateArray()
                .Where(item => item.GetProperty("sceneItemId").GetInt64() == itemId && JsonRead.String(item, "sourceName") == catalog.ScreenSource)
                .Select(item => (JsonElement?)item).FirstOrDefault();
            if (match is null) return false;
            IReadOnlyDictionary<string, int> crop = ScreenFraming.CalculateCrop(match.Value.GetProperty("sceneItemTransform"), zoom, x, y);
            if (!await IsCurrentSceneAsync(sceneName, token).ConfigureAwait(false)) return false;
            await CallAsync("SetSceneItemTransform", new { sceneName, sceneItemId = itemId, sceneItemTransform = crop }, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    public Task<StudioStatus> ApplyProjectSettingsAsync(ProjectSettings settings, string assetsDirectory, CancellationToken cancellationToken = default)
    {
        settings.Validate();
        string directory = Path.GetFullPath(assetsDirectory);
        var cards = catalog.CardSources.Select(card => (card.SourceName, Path: Path.Combine(directory, card.FileName))).ToArray();
        if (cards.Any(card => !File.Exists(card.Path))) throw new FileNotFoundException("Render all six project cards before applying them to OBS.");
        return RunAsync(async token =>
        {
            var warnings = new List<string>();
            try
            {
                foreach (var card in cards)
                    await CallAsync("SetInputSettings", new { inputName = card.SourceName, inputSettings = new { file = card.Path }, overlay = true }, token).ConfigureAwait(false);
                string branding = Path.Combine(directory, "VibeWare-logo.png");
                if (File.Exists(branding))
                    await OptionalProjectAsync("VibeWare artwork", () => CallAsync("SetInputSettings", new { inputName = catalog.BrandingSource, inputSettings = new { file = branding }, overlay = true }, token), warnings).ConfigureAwait(false);
                JsonElement scenes = await CallAsync("GetSceneList", null, token).ConfigureAwait(false);
                foreach (JsonElement scene in scenes.GetProperty("scenes").EnumerateArray())
                {
                    string sceneName = scene.GetProperty("sceneName").GetString()!;
                    JsonElement items = await CallAsync("GetSceneItemList", new { sceneName }, token).ConfigureAwait(false);
                    foreach (JsonElement item in items.GetProperty("sceneItems").EnumerateArray())
                        if (JsonRead.String(item, "sourceName") == catalog.BrandingSource)
                            await CallAsync("SetSceneItemEnabled", new { sceneName, sceneItemId = item.GetProperty("sceneItemId").GetInt64(), sceneItemEnabled = settings.VibeWare }, token).ConfigureAwait(false);
                }
                string audio = Path.Combine(directory, "Lab-intro.wav");
                if (File.Exists(audio))
                    await OptionalProjectAsync("Intro audio file", () => CallAsync("SetInputSettings", new { inputName = catalog.IntroSoundSource, inputSettings = new { local_file = audio, is_local_file = true }, overlay = true }, token), warnings).ConfigureAwait(false);
                await OptionalProjectAsync("Intro audio mute", () => CallAsync("SetInputMute", new { inputName = catalog.IntroSoundSource, inputMuted = !settings.SoundEnabled }, token), warnings).ConfigureAwait(false);
                await OptionalProjectAsync("Intro audio volume", () => CallAsync("SetInputVolume", new { inputName = catalog.IntroSoundSource, inputVolumeMul = settings.SoundVolume / 100 }, token), warnings).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsStatusFailure(exception))
            {
                throw new ProjectApplyException("Project cards are saved locally; the OBS update is incomplete. Apply again after checking the connection and scene collection.", exception);
            }
            return await AfterActionAsync("Apply project", state => state, token, warnings).ConfigureAwait(false);
        }, cancellationToken, 30);
    }

    private async Task<StudioStatus> ReadStatusAsync(CancellationToken token)
    {
        JsonElement recording = await CallAsync("GetRecordStatus", null, token).ConfigureAwait(false);
        JsonElement scene = await CallAsync("GetCurrentProgramScene", null, token).ConfigureAwait(false);
        var warnings = new List<string>();
        bool connected = true;
        var status = new StudioStatus
        {
            SceneName = scene.GetProperty("currentProgramSceneName").GetString() ?? "Unknown scene",
            IsRecording = recording.GetProperty("outputActive").GetBoolean(),
            IsPaused = JsonRead.Boolean(recording, "outputPaused"),
            Timecode = JsonRead.String(recording, "outputTimecode")
        };
        async Task OptionalAsync(string label, Func<Task> operation)
        {
            if (!connected) return;
            try { await operation().ConfigureAwait(false); }
            catch (Exception exception) when (IsStatusFailure(exception))
            {
                if (exception is ObsConnectionException or OperationCanceledException) connected = false;
                warnings.Add($"{label} unavailable.");
            }
        }
        await OptionalAsync("Background blur", async () =>
        {
            JsonElement filter = await GetFilterAsync(catalog.CameraSource, catalog.BlurFilter, token).ConfigureAwait(false);
            int intensity = filter.TryGetProperty("filterSettings", out JsonElement settings) ? (int)JsonRead.Number(settings, "blur_background", 3) : 3;
            status = status with { BlurAvailable = true, BlurEnabled = JsonRead.Boolean(filter, "filterEnabled"), BlurIntensity = Math.Clamp(intensity, 1, 20) };
        }).ConfigureAwait(false);
        var mutes = new Dictionary<string, bool>();
        var available = catalog.AudioInputs.ToDictionary(name => name, _ => false);
        foreach (string inputName in catalog.AudioInputs)
            await OptionalAsync(inputName, async () =>
            {
                JsonElement response = await CallAsync("GetInputMute", new { inputName }, token).ConfigureAwait(false);
                mutes[inputName] = response.GetProperty("inputMuted").GetBoolean();
                available[inputName] = true;
            }).ConfigureAwait(false);
        await OptionalAsync("Screen framing", async () =>
        {
            JsonElement response = await CallAsync("GetSceneItemList", new { sceneName = status.SceneName }, token).ConfigureAwait(false);
            JsonElement[] screens = response.GetProperty("sceneItems").EnumerateArray()
                .Where(item => JsonRead.String(item, "sourceName") == catalog.ScreenSource).ToArray();
            if (screens.Length != 1) return;
            ScreenPreset? frame = ScreenFraming.Read(screens[0].GetProperty("sceneItemTransform"));
            if (frame is not null) status = status with
            {
                Screen = new ScreenFramingState(status.SceneName, screens[0].GetProperty("sceneItemId").GetInt64(), frame.Zoom, frame.X, frame.Y)
            };
        }).ConfigureAwait(false);
        var vignettes = new Dictionary<string, VignetteState>();
        foreach (VignetteDefinition definition in catalog.Vignettes)
            await OptionalAsync($"{definition.Label} vignette", async () =>
            {
                JsonElement filter = await GetFilterAsync(definition.SourceName, catalog.VignetteFilter, token).ConfigureAwait(false);
                JsonElement settings = filter.GetProperty("filterSettings");
                double strength = JsonRead.Number(settings, "lab_strength", Math.Round(JsonRead.Number(settings, "opacity", .25) * 100));
                vignettes[definition.Key] = new VignetteState(JsonRead.Boolean(settings, "lab_enabled", true), Math.Clamp(strength, 0, 100));
            }).ConfigureAwait(false);
        status = status with { ConnectionAvailable = connected, Warnings = warnings.ToArray(), AudioMutes = mutes, AudioAvailable = available, Vignettes = vignettes };
        lastStatus = status;
        return status;
    }

    private async Task<StudioStatus> AfterActionAsync(string action, Func<StudioStatus, StudioStatus> confirmed,
        CancellationToken token, IReadOnlyList<string>? warnings = null, string? outputPath = null, bool skipRefresh = false)
    {
        StudioStatus status;
        try
        {
            if (skipRefresh) throw new ObsConnectionException("The connection needs a new status check.");
            status = await ReadStatusAsync(token).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsStatusFailure(exception))
        {
            status = confirmed(lastStatus) with
            {
                IsFresh = false,
                ConnectionAvailable = exception is not (ObsConnectionException or OperationCanceledException),
                Warnings = [$"{action} succeeded; current status could not be refreshed."]
            };
        }
        // Saved paths belong to this result, never to a later unrelated action.
        lastStatus = status with { OutputPath = null, ActionSucceeded = false };
        return status with { ActionSucceeded = true, OutputPath = outputPath, Warnings = [.. status.Warnings, .. warnings ?? []] };
    }

    private sealed record ComparisonItem(JsonElement Item, int Left, int Right);

    private async Task<ComparisonItem[]> ComparisonCropsAsync(string sceneName, CancellationToken token)
    {
        JsonElement response = await CallAsync("GetSceneItemList", new { sceneName }, token).ConfigureAwait(false);
        JsonElement[] screens = response.GetProperty("sceneItems").EnumerateArray()
            .Where(item => JsonRead.String(item, "sourceName") == catalog.ScreenSource)
            .OrderBy(item => JsonRead.Number(item.GetProperty("sceneItemTransform"), "positionX")).ToArray();
        if (screens.Length != 2) throw new ObsRequestException("Comparison", null, "The comparison scene needs exactly two screen items");
        int[] widths = screens.Select(item => ScreenFraming.Dimension(item.GetProperty("sceneItemTransform"), "sourceWidth")).ToArray();
        if (widths.Any(width => width <= 1)) throw new ObsRequestException("Comparison", null, "The screen capture is not ready");
        return screens.Select((item, index) => new ComparisonItem(item,
            index == 0 ? 0 : widths[index] / 2,
            index == 0 ? widths[index] - widths[index] / 2 : 0)).ToArray();
    }

    private async Task CheckComparisonAsync(CancellationToken token)
    {
        foreach (var crop in await ComparisonCropsAsync("Comparison A-B", token).ConfigureAwait(false))
        {
            var transform = crop.Item.GetProperty("sceneItemTransform");
            if (JsonRead.Number(transform, "cropLeft") != crop.Left || JsonRead.Number(transform, "cropRight") != crop.Right ||
                JsonRead.Number(transform, "cropTop") != 0 || JsonRead.Number(transform, "cropBottom") != 0)
                throw new InvalidOperationException("Select Comparison A/B manually once to calibrate it before playback.");
        }
    }

    private async Task PrepareComparisonAsync(string sceneName, CancellationToken token)
    {
        foreach (var crop in await ComparisonCropsAsync(sceneName, token).ConfigureAwait(false))
        {
            await CallAsync("SetSceneItemTransform", new
            {
                sceneName, sceneItemId = crop.Item.GetProperty("sceneItemId").GetInt64(),
                sceneItemTransform = new { cropLeft = crop.Left, cropRight = crop.Right, cropTop = 0, cropBottom = 0 }
            }, token).ConfigureAwait(false);
        }
    }

    private async Task<JsonElement> GetFilterAsync(string sourceName, string filterName, CancellationToken token)
    {
        JsonElement response = await CallAsync("GetSourceFilterList", new { sourceName }, token).ConfigureAwait(false);
        foreach (JsonElement filter in response.GetProperty("filters").EnumerateArray())
            if (JsonRead.String(filter, "filterName") == filterName) return filter;
        throw new ObsRequestException("GetSourceFilterList", 600, "The requested filter is not installed");
    }

    private async Task<bool> IsCurrentSceneAsync(string name, CancellationToken token) =>
        JsonRead.String(await CallAsync("GetCurrentProgramScene", null, token).ConfigureAwait(false), "currentProgramSceneName") == name;

    private static async Task OptionalProjectAsync(string label, Func<Task<JsonElement>> action, List<string> warnings)
    {
        try { await action().ConfigureAwait(false); }
        catch (ObsRequestException) { warnings.Add($"{label} unavailable; other project settings were applied."); }
    }

    private Task<JsonElement> CallAsync(string requestType, object? data, CancellationToken token) => client.CallAsync(requestType, data, token);

    private async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken, int seconds = 15)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        await operations.WaitAsync(deadline.Token).ConfigureAwait(false);
        try { return await operation(deadline.Token).ConfigureAwait(false); }
        finally { operations.Release(); }
    }

    private static Dictionary<string, T> CopyWith<T>(IReadOnlyDictionary<string, T> values, string key, T value) => new(values) { [key] = value };
    private static bool IsStatusFailure(Exception exception) => exception is ObsException or OperationCanceledException or
        InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException;
}
