namespace VibeKit.StudioControls.Core;

/// <summary>One explicit recording gesture, pinned to the OBS connection and consumed at most once.</summary>
public sealed class RecordCountdown(long generation, string scene, TimeProvider? clock = null)
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly long created = (clock ?? TimeProvider.System).GetTimestamp();
    private long? started;
    private int consumed;

    public long Generation { get; } = generation;
    public string Scene { get; } = scene;
    public bool IsPending => Volatile.Read(ref consumed) == 0;
    public bool Cancel() => Interlocked.Exchange(ref consumed, 1) == 0;

    public bool Begin()
    {
        if (!IsPending || started.HasValue || clock.GetElapsedTime(created) > TimeSpan.FromSeconds(5)) return false;
        started = clock.GetTimestamp();
        return true;
    }

    internal void Claim()
    {
        if (started is not long start || clock.GetElapsedTime(start).TotalSeconds is < 3 or > 5 ||
            Interlocked.Exchange(ref consumed, 1) != 0)
            throw new InvalidOperationException("Recording countdown cancelled.");
    }
}

public sealed record RecordingTarget(string Collection, string? SceneUuid);
