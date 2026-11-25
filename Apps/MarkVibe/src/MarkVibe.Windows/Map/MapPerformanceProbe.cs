/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; opt-in bounded native map timing diagnostics
 * Human guidance: measure large synthetic graphs, camera responsiveness and memory before optimizing
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using System.Diagnostics;
using System.Numerics;

namespace MarkVibe.Windows;

/// <summary>Collects bounded UI-thread draw timings only for explicitly opted-in local performance runs.</summary>
internal sealed class MapPerformanceProbe
{
    private const int Capacity = 32;
    private readonly double[] _draw = new double[Capacity];
    private readonly double[] _interval = new double[Capacity];
    private readonly float[] _scales = new float[Capacity];
    private readonly float[] _translationX = new float[Capacity];
    private readonly float[] _translationY = new float[Capacity];
    private int _count;
    private string _stage = "";
    private long _previous;
    private long _allocated;
    private int _nodes;
    private int _edges;

    /// <summary>Enables numeric diagnostics in the existing local app log without recording document content or paths.</summary>
    internal static bool Enabled { get; } = Environment.GetEnvironmentVariable("MARKVIBE_MAP_PERFORMANCE") == "1";

    /// <summary>Returns no collector and allocates no sample buffers during ordinary application use.</summary>
    internal static MapPerformanceProbe? Create() => Enabled ? new() : null;

    /// <summary>Excludes the idle time between separate gestures from callback intervals.</summary>
    internal void ResetInterval() => _previous = 0;

    /// <summary>Records one completed draw; intervals describe callbacks during a gesture, not GPU presentation or FPS.</summary>
    internal void Record(string stage, long started, long allocatedBefore, int nodes, int edges,
        float scale, Vector2 translation)
    {
        var finished = Stopwatch.GetTimestamp();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        if (stage != _stage)
        {
            Flush();
            _stage = stage;
            _previous = 0;
        }
        _nodes = nodes;
        _edges = edges;
        _draw[_count] = Stopwatch.GetElapsedTime(started, finished).TotalMilliseconds;
        _scales[_count] = scale;
        _translationX[_count] = translation.X;
        _translationY[_count] = translation.Y;
        _interval[_count] = _previous == 0 ? 0 : Stopwatch.GetElapsedTime(_previous, started).TotalMilliseconds;
        _previous = started;
        _allocated += allocated;
        _count++;
        if (_count == Capacity)
        {
            Flush();
        }
    }

    /// <summary>Publishes a bounded numeric batch outside the measured draw body and releases its accumulated totals.</summary>
    internal void Flush()
    {
        if (_count == 0)
        {
            return;
        }
        DesktopLog.Write("info", "map.performance", new
        {
            stage = _stage,
            nodes = _nodes,
            edges = _edges,
            drawMs = _draw[.._count],
            intervalMs = _interval[.._count],
            scale = _scales[.._count],
            translationX = _translationX[.._count],
            translationY = _translationY[.._count],
            allocatedBytes = _allocated
        });
        _count = 0;
        _allocated = 0;
    }
}
