using System.Text.Json;

namespace VibeKit.StudioControls.Core;

public sealed class WorkflowTransitionRecoveryException(Exception error)
    : InvalidOperationException("Check the destination scene's transition override in OBS. Restoration was not confirmed.", error);

public sealed partial class StudioService
{
    // Access only under operations. Explicit preflight binds one run to the current OBS collection.
    private sealed record WorkflowSession(string Collection, string InitialScene, long Generation, bool WasRecording,
        Dictionary<string, string> SceneIds);
    private WorkflowSession? workflowSession;

    public Task<IReadOnlySet<string>> GetWorkflowScenesAsync(CancellationToken token = default, IEnumerable<string>? requiredScenes = null,
        IEnumerable<string>? transitions = null) =>
        RunAsync<IReadOnlySet<string>>(async deadline =>
        {
            workflowSession = null;
            string collection = await CurrentCollectionAsync(deadline).ConfigureAwait(false);
            JsonElement response = await CallAsync("GetSceneList", null, deadline).ConfigureAwait(false);
            var sceneIds = response.GetProperty("scenes").EnumerateArray().ToDictionary(
                scene => scene.GetProperty("sceneName").GetString() ?? throw new InvalidDataException("Invalid scene list."),
                scene => JsonRead.String(scene, "sceneUuid"));
            var names = sceneIds.Keys.ToHashSet(StringComparer.Ordinal);
            string[] requested = transitions?.Where(choice => choice != "default").Distinct().ToArray() ?? [];
            if (requested.Length > 0)
            {
                var available = await WorkflowTransitionsAsync(deadline).ConfigureAwait(false);
                foreach (string choice in requested) ResolveTransition(available, choice);
            }
            JsonElement status = await CallAsync("GetRecordStatus", null, deadline).ConfigureAwait(false);
            if (requiredScenes?.Contains("Comparison A-B") == true && names.Contains("Comparison A-B"))
                await CheckComparisonAsync(deadline).ConfigureAwait(false);
            if (status.GetProperty("outputPaused").GetBoolean())
                throw new InvalidOperationException("Recording is paused.");
            workflowSession = new(collection, response.GetProperty("currentProgramSceneName").GetString()
                ?? throw new InvalidDataException("Invalid active scene."), client.ConnectionGeneration,
                status.GetProperty("outputActive").GetBoolean(), sceneIds);
            return names;
        }, token);

    private async Task<string> CurrentCollectionAsync(CancellationToken token) =>
        (await CallAsync("GetSceneCollectionList", null, token).ConfigureAwait(false))
            .GetProperty("currentSceneCollectionName").GetString() ?? throw new InvalidDataException("Invalid scene collection.");

    private async Task<Dictionary<string, string>> WorkflowTransitionsAsync(CancellationToken token)
    {
        JsonElement response = await CallAsync("GetSceneTransitionList", null, token).ConfigureAwait(false);
        return response.GetProperty("transitions").EnumerateArray().ToDictionary(
            item => item.GetProperty("transitionName").GetString() ?? throw new InvalidDataException("Invalid transition name."),
            item => item.GetProperty("transitionKind").GetString() ?? throw new InvalidDataException("Invalid transition kind."));
    }

    private static string ResolveTransition(Dictionary<string, string> available, string transition)
    {
        if (transition is "cut" or "fade")
            foreach (var item in available)
                if (item.Value == transition + "_transition") return item.Key;
        throw new InvalidOperationException("The selected workflow transition is unavailable in OBS.");
    }

    private void CheckWorkflowConnection(WorkflowSession session)
    {
        if (session.Generation != client.ConnectionGeneration) throw new InvalidOperationException("OBS connection changed.");
    }

    private async Task CheckWorkflowSessionAsync(WorkflowSession session, CancellationToken token)
    {
        CheckWorkflowConnection(session);
        if (await CurrentCollectionAsync(token).ConfigureAwait(false) != session.Collection)
            throw new InvalidOperationException("The OBS scene collection changed.");
    }

    private async Task<bool> CheckWorkflowInputAsync(WorkflowSession session, string expected, CancellationToken token)
    {
        await CheckWorkflowSessionAsync(session, token).ConfigureAwait(false);
        JsonElement status = await CallAsync("GetRecordStatus", null, token).ConfigureAwait(false);
        bool recording = status.GetProperty("outputActive").GetBoolean();
        if (status.GetProperty("outputPaused").GetBoolean() || (session.WasRecording && !recording))
            throw new InvalidOperationException("Recording was paused or stopped.");
        if (!await IsCurrentSceneAsync(expected, token).ConfigureAwait(false))
            throw new InvalidOperationException("The active OBS scene changed. Playback stopped.");
        CheckWorkflowConnection(session);
        return recording;
    }

    public Task SetWorkflowSceneAsync(string scene, string? expectedCurrent, CancellationToken token = default,
        string transition = "default")
    {
        if (!catalog.Scenes.Any(item => item.Name == scene)) throw new ArgumentException("Unknown workflow scene.");
        return RunAsync(async deadline =>
        {
            var session = workflowSession ?? throw new InvalidOperationException("Check the workflow scenes before playback.");
            workflowSession = null; // Require explicit preflight after an uncertain outcome.
            string expected = expectedCurrent ?? session.InitialScene;
            if (scene == "Comparison A-B")
            {
                await CheckWorkflowSessionAsync(session, deadline).ConfigureAwait(false);
                await CheckComparisonAsync(deadline).ConfigureAwait(false);
            }
            bool recording = await CheckWorkflowInputAsync(session, expected, deadline).ConfigureAwait(false);
            if (scene != expected)
            {
                // Prefer the preflight identity; a replacement scene may reuse the display name.
                Dictionary<string, string?> target = session.SceneIds.GetValueOrDefault(scene) is { Length: > 0 } uuid
                    ? new() { ["sceneUuid"] = uuid } : new() { ["sceneName"] = scene };
                if (transition == "default")
                    await CallAsync("SetCurrentProgramScene", target, deadline).ConfigureAwait(false);
                else
                    recording = await SwitchWithTransitionAsync(session with { WasRecording = recording }, target,
                        expected, transition, deadline).ConfigureAwait(false);
            }
            if (!await IsCurrentSceneAsync(scene, deadline).ConfigureAwait(false))
                throw new InvalidOperationException("OBS did not confirm the requested scene.");
            workflowSession = session with { WasRecording = recording };
            return true;
        }, token);
    }

    private async Task<bool> SwitchWithTransitionAsync(WorkflowSession session,
        Dictionary<string, string?> target, string expected, string transition, CancellationToken token)
    {
        var available = await WorkflowTransitionsAsync(token).ConfigureAwait(false);
        string chosen = ResolveTransition(available, transition);
        async Task<string?> ReadOverrideAsync(CancellationToken ct) =>
            (await CallAsync("GetSceneSceneTransitionOverride", target, ct).ConfigureAwait(false))
                .GetProperty("transitionName").GetString();
        Task WriteOverrideAsync(string? name, CancellationToken ct)
        {
            CheckWorkflowConnection(session);
            var fields = new Dictionary<string, string?>(target) { ["transitionName"] = name };
            return CallAsync("SetSceneSceneTransitionOverride", fields, ct);
        }
        async Task<bool> SwitchAsync()
        {
            bool recording = await CheckWorkflowInputAsync(session, expected, token).ConfigureAwait(false);
            await CallAsync("SetCurrentProgramScene", target, token).ConfigureAwait(false);
            return recording;
        }

        await CheckWorkflowSessionAsync(session, token).ConfigureAwait(false);
        string? previous = await ReadOverrideAsync(token).ConfigureAwait(false);
        if (previous == chosen) return await SwitchAsync().ConfigureAwait(false);
        if (previous is not null && !available.ContainsKey(previous))
            throw new InvalidOperationException("Check the destination scene's existing transition override in OBS.");
        try { await WriteOverrideAsync(chosen, token).ConfigureAwait(false); }
        catch (ObsConnectionException error) when (error.RequestSent)
        { throw new WorkflowTransitionRecoveryException(error); }
        try
        {
            return await SwitchAsync().ConfigureAwait(false);
        }
        finally
        {
            // A manual stop cancels the switch, but still permits restoring our own override.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await CheckWorkflowSessionAsync(session, cleanup.Token).ConfigureAwait(false);
                if (await ReadOverrideAsync(cleanup.Token).ConfigureAwait(false) == chosen)
                {
                    await WriteOverrideAsync(previous, cleanup.Token).ConfigureAwait(false);
                    if (await ReadOverrideAsync(cleanup.Token).ConfigureAwait(false) != previous)
                        throw new InvalidDataException("The transition override changed.");
                }
            }
            catch (Exception error) when (IsStatusFailure(error))
            { throw new WorkflowTransitionRecoveryException(error); }
        }
    }
}
