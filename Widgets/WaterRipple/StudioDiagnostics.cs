using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.UI.Dispatching;
using Serilog;

namespace WaterRipple;

// Owns the resource sampler and its UI timer; the controller only manages effect/session actions.
internal sealed class StudioDiagnostics : IDisposable
{
    private const double BytesPerMebibyte = 1024 * 1024;
    private readonly AppState state;
    private readonly DispatcherQueueTimer timer;
    private readonly ResourceMonitor monitor;
    private GpuMemory memory;
    private double nextLogSeconds;
    private bool crtComposition;
    private bool disposed;

    internal StudioDiagnostics(AppState state, DispatcherQueue dispatcher)
    {
        this.state = state;
        monitor = new ResourceMonitor(warning: sample =>
        {
            Log.Warning("Private memory threshold reached Pid={Pid} Process={ProcessName} Bytes={PrivateBytes} WorkingSet={WorkingSet} LimitBytes={LimitBytes} SampledAtUtc={SampledAtUtc}",
                Environment.ProcessId, nameof(WaterRipple), sample.PrivateBytes, sample.WorkingSetBytes, MemoryWarningLatch.LimitBytes, sample.SampledAt);
            Native.MessageBox(IntPtr.Zero, "Water Ripple Studio is using at least 500 MB of private memory. This could be a memory leak. Please report it and include the recent application logs.\n\nStop the screen effect or close the app if memory keeps growing.", "Water Ripple Studio · memory", 0x50030);
        });
        timer = dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += UpdateResources;
        timer.Start();
    }

    internal void Observe(IReadOnlyList<ScreenSessionMetrics> sessions)
    {
        var largestGpuSample = sessions.MaxBy(metrics => metrics.Memory.LocalUsage);
        memory = largestGpuSample.Memory;
        bool composed = sessions.Any(metrics => (metrics.Native.Status & CaptureStatus.CrtComposition) != 0);
        if (composed != crtComposition)
        {
            crtComposition = composed;
            Log.Information("CRT composition {CompositionActive}", crtComposition);
        }
        // These are CPU submission times, not GPU execution times.
        bool delegated = sessions.Any(metrics => metrics.Native.HasCrtPresentation);
        string mode = sessions.All(metrics => metrics.Native.HasCrtPresentation) ? "Presented by Retro Terminal" :
            delegated ? "Local and Retro Terminal presentation" : crtComposition ? "Composed with CRT" : "Screen effect";
        string sizes = string.Join(" · ", sessions.Select(metrics => $"{metrics.Native.CaptureWidth} × {metrics.Native.CaptureHeight} px"));
        state.MetricsText = $"Mode\t{mode}\nLocal presents\t{sessions.Sum(metrics => metrics.Presents):F1} /s\nDesktop updates\t{sessions.Sum(metrics => metrics.Captures):F1} /s\nCapture submit\t{sessions.Max(metrics => metrics.Native.LastCaptureMs):F2} ms\nRender submit\t{sessions.Max(metrics => metrics.Native.LastRenderCpuMs):F2} ms\nCapture size\t{sizes}";
    }

    internal void ClearCapture()
    {
        memory = default;
        state.MetricsText = "Waiting for frames";
        if (crtComposition) Log.Information("CRT composition stopped");
        crtComposition = false;
    }

    private void UpdateResources(DispatcherQueueTimer sender, object args)
    {
        var sample = monitor.Snapshot;
        state.PrivateRamBytes = sample.PrivateBytes;
        state.WorkingSetBytes = sample.WorkingSetBytes;
        state.CpuPercent = sample.CpuPercent;
        state.LocalGpuUsageBytes = memory.LocalUsage;
        state.LocalGpuBudgetBytes = memory.LocalBudget;
        state.ResourcesText = $"Private RAM\t{sample.PrivateBytes / BytesPerMebibyte:F0} MiB\nWorking set\t{sample.WorkingSetBytes / BytesPerMebibyte:F0} MiB\nCPU\t{sample.CpuPercent:F1}%\nGPU local\t{memory.LocalUsage / BytesPerMebibyte:F0} / {memory.LocalBudget / BytesPerMebibyte:F0} MiB\nRAM warning\t500 MB";
        double nowSeconds = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        if (nowSeconds < nextLogSeconds) return;
        nextLogSeconds = nowSeconds + 60;
        Log.Information("ResourceSummary ScreenActive={ScreenActive} PrivateBytes={PrivateBytes} WorkingSetBytes={WorkingSetBytes} CPU={CPU} GpuLocalBytes={GpuLocalBytes} DroppedLogs={DroppedLogs}",
            state.IsScreenEffectActive, sample.PrivateBytes, sample.WorkingSetBytes, sample.CpuPercent, memory.LocalUsage, LoggingService.DroppedEvents);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer.Stop();
        timer.Tick -= UpdateResources;
        monitor.Dispose();
    }
}
