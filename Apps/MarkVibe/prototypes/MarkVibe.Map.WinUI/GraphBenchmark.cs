/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; measure graph opening and continuous native drawing.
 * Human guidance: compare graph opening, pointer-centered zoom and node dragging.
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Xaml.Media;

namespace MarkVibe.Map.WinUI;

/// <summary>Summarizes intervals between actual graph draws for one three-second input trajectory.</summary>
public sealed record GraphFrameMetrics(
    int Draws,
    int Intervals,
    double FirstInputToDrawMs,
    double DurationMs,
    double P50Ms,
    double P95Ms,
    double MaximumMs,
    int Over16Point7Ms,
    int Over32Ms);

/// <summary>Captures local opening latency, graph dimensions and the two independently timed interactions.</summary>
public sealed record GraphBenchmarkResult(
    int Nodes,
    int Edges,
    string DraggedPath,
    double ViewportWidthDip,
    double ViewportHeightDip,
    double FitScale,
    double OpenToFirstDrawMs,
    double OpenToFirstCompositionMs,
    GraphFrameMetrics Zoom,
    GraphFrameMetrics Drag);

/// <summary>Feeds deterministic input through normal graph geometry and measures the resulting draws on the UI thread.</summary>
public static class GraphBenchmark
{
    private static readonly TimeSpan GestureDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Measures from the caller's map-opening timestamp through first draw and the next XAML composition callback.</summary>
    public static async Task<GraphBenchmarkResult> RunAsync(GraphCanvas canvas, long showRequestedTimestamp,
        CancellationToken cancellationToken = default)
    {
        var first = await canvas.InitialFrame.WaitAsync(FrameTimeout, cancellationToken);
        if (canvas.NodeCount == 0 || string.IsNullOrEmpty(canvas.SelectedPath))
        {
            throw new InvalidOperationException("The graph benchmark requires a selected document.");
        }
        var scale = canvas.CameraScale;
        var center = canvas.ViewportCenter;
        canvas.SelectCentralCard();
        var selectedPath = canvas.SelectedPath;
        var origin = canvas.SelectedPosition;
        GraphFrameMetrics zoom;
        GraphFrameMetrics drag;
        try
        {
            zoom = await MeasureAsync(canvas, progress =>
            {
                var sine = Math.Sin(Math.PI * progress);
                canvas.ZoomAt(center, scale * (float)(1 + .4 * sine * sine));
            }, cancellationToken);
            drag = await MeasureAsync(canvas, progress =>
            {
                var phase = 2 * Math.PI * progress;
                var offset = new Vector2((float)(120 * Math.Sin(phase)), (float)(55 * Math.Sin(2 * phase)));
                canvas.DragSelected(origin, offset);
            }, cancellationToken);
        }
        finally
        {
            // Keep the saved view and geometry usable even if the window cancels the benchmark.
            if (!canvas.IsDisposed && selectedPath == canvas.SelectedPath)
            {
                canvas.ZoomAt(center, scale);
                canvas.DragSelected(origin, Vector2.Zero);
            }
        }
        return new GraphBenchmarkResult(canvas.NodeCount, canvas.EdgeCount, selectedPath,
            canvas.ActualWidth, canvas.ActualHeight, scale, ElapsedMs(showRequestedTimestamp, first.Draw),
            ElapsedMs(showRequestedTimestamp, first.Composition), zoom, drag);
    }

    /// <summary>Drives one update per composition opportunity and records only frames that painted new gesture input.</summary>
    private static async Task<GraphFrameMetrics> MeasureAsync(GraphCanvas canvas, Action<double> update,
        CancellationToken cancellationToken)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var draws = new List<long>(320);
        long started = 0;
        bool inputPending = false;
        bool finalInput = false;

        // Start on the first composition callback so setup and the transition between gestures are excluded.
        void OnComposition(object? sender, object args)
        {
            if (finalInput)
            {
                return;
            }
            var now = Stopwatch.GetTimestamp();
            if (started == 0)
            {
                started = now;
            }
            var progress = Math.Min(1, Stopwatch.GetElapsedTime(started, now) / GestureDuration);
            finalInput = progress >= 1;
            inputPending = true;
            try
            {
                update(progress);
            }
            catch (Exception exception)
            {
                finalInput = true;
                completed.TrySetException(exception);
            }
        }

        // A composition callback without new canvas content cannot add a measurement.
        void OnDraw(long timestamp)
        {
            if (!inputPending)
            {
                return;
            }
            inputPending = false;
            draws.Add(timestamp);
            if (finalInput)
            {
                completed.TrySetResult();
            }
        }

        canvas.FrameDrawn += OnDraw;
        CompositionTarget.Rendering += OnComposition;
        try
        {
            await completed.Task.WaitAsync(FrameTimeout, cancellationToken);
        }
        finally
        {
            canvas.FrameDrawn -= OnDraw;
            CompositionTarget.Rendering -= OnComposition;
        }
        if (draws.Count < 2)
        {
            throw new InvalidOperationException("Too few graph draws to measure frame intervals.");
        }
        var intervals = new double[draws.Count - 1];
        for (var index = 0; index < intervals.Length; index++)
        {
            intervals[index] = ElapsedMs(draws[index], draws[index + 1]);
        }
        Array.Sort(intervals);
        return new GraphFrameMetrics(draws.Count, intervals.Length, ElapsedMs(started, draws[0]),
            ElapsedMs(started, draws[^1]), Percentile(intervals, .5), Percentile(intervals, .95),
            intervals[^1], intervals.Count(value => value > 16.7), intervals.Count(value => value > 32));
    }

    /// <summary>Returns the nearest-rank percentile of the ascending millisecond samples.</summary>
    private static double Percentile(double[] sorted, double fraction) =>
        sorted[(int)Math.Ceiling(sorted.Length * fraction) - 1];

    /// <summary>Converts two monotonic timestamps from the same process into elapsed milliseconds.</summary>
    private static double ElapsedMs(long start, long end) => Stopwatch.GetElapsedTime(start, end).TotalMilliseconds;
}
