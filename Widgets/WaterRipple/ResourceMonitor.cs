using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace WaterRipple;

internal sealed record ResourceSnapshot(
    long PrivateBytes,
    long WorkingSetBytes,
    double CpuPercent,
    DateTimeOffset SampledAt)
{
    internal static readonly ResourceSnapshot Empty = new(0, 0, 0, DateTimeOffset.MinValue);
}

// One warning per high-memory episode, rearmed only after a substantial recovery.
internal sealed class MemoryWarningLatch
{
    internal const long LimitBytes = 500_000_000L;
    internal const long RearmBytes = LimitBytes * 4 / 5;
    private bool armed = true;

    internal bool Observe(long privateBytes)
    {
        if (privateBytes < 0) throw new ArgumentOutOfRangeException(nameof(privateBytes));
        // Rearm only after private memory falls to 400 MB or less.
        if (privateBytes <= RearmBytes)
        {
            armed = true;
            return false;
        }
        if (!armed || privateBytes < LimitBytes) return false;
        armed = false;
        return true;
    }
}

// Owns process sampling for the app lifetime, independently of the XAML dispatcher.
internal sealed class ResourceMonitor : IDisposable
{
    private readonly object lifetimeSync = new();
    private readonly ManualResetEventSlim stop = new(false);
    private readonly Func<ResourceSnapshot>? customSampler;
    private readonly Action<ResourceSnapshot> warning;
    private readonly TimeSpan sampleInterval;
    private readonly MemoryWarningLatch warningLatch = new();
    private readonly Thread thread;
    private ResourceSnapshot snapshot = ResourceSnapshot.Empty;
    private int disposed, warningActive;
    private bool threadFinished;

    // Starts immediately, samples once, then samples every five seconds by default.
    // Injected callbacks allow threshold and cancellation tests without OS dialogs.
    internal ResourceMonitor(
        Func<ResourceSnapshot>? sampler = null,
        Action<ResourceSnapshot>? warning = null,
        TimeSpan? sampleInterval = null)
    {
        this.sampleInterval = sampleInterval ?? TimeSpan.FromSeconds(5);
        if (this.sampleInterval <= TimeSpan.Zero || this.sampleInterval.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(sampleInterval));
        customSampler = sampler;
        this.warning = warning ?? ShowNativeWarning;
        thread = new Thread(Run) { IsBackground = true, Name = "Water Ripple resource monitor" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    internal ResourceSnapshot Snapshot => Volatile.Read(ref snapshot);

    public void Dispose()
    {
        // The sampling thread owns final disposal; never wait on it from the UI.
        lock (lifetimeSync)
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0 && !threadFinished) stop.Set();
        }
    }

    private void Run()
    {
        Process? process = null;
        try
        {
            if (customSampler is null) process = Process.GetCurrentProcess();
            double previousCpuSeconds = process?.TotalProcessorTime.TotalSeconds ?? 0;
            long previousTimestamp = Stopwatch.GetTimestamp();
            while (Volatile.Read(ref disposed) == 0)
            {
                try
                {
                    ResourceSnapshot current;
                    if (customSampler is not null)
                    {
                        current = customSampler();
                    }
                    else
                    {
                        process!.Refresh();
                        long timestamp = Stopwatch.GetTimestamp();
                        double cpuSeconds = process.TotalProcessorTime.TotalSeconds;
                        double wallSeconds = (timestamp - previousTimestamp) / (double)Stopwatch.Frequency;
                        double cpu = wallSeconds > 0
                            ? (cpuSeconds - previousCpuSeconds) / wallSeconds / Environment.ProcessorCount * 100
                            : 0;
                        previousCpuSeconds = cpuSeconds;
                        previousTimestamp = timestamp;
                        current = new ResourceSnapshot(
                            process.PrivateMemorySize64,
                            process.WorkingSet64,
                            Math.Clamp(cpu, 0, 100),
                            DateTimeOffset.UtcNow);
                    }
                    if (Volatile.Read(ref disposed) != 0) break;
                    Volatile.Write(ref snapshot, current);
                    if (warningLatch.Observe(current.PrivateBytes)) QueueWarning(current);
                }
                catch (Exception)
                {
                    // Keep the last successful sample if an OS counter becomes unavailable.
                    // Monitoring must not terminate the app or create persistent diagnostics.
                }
                if (stop.Wait(sampleInterval)) break;
            }
        }
        catch (Exception)
        {
            // Process-counter initialization can fail during application shutdown.
        }
        finally
        {
            process?.Dispose();
            lock (lifetimeSync)
            {
                threadFinished = true;
                stop.Dispose();
            }
        }
    }

    private void QueueWarning(ResourceSnapshot current)
    {
        if (Interlocked.CompareExchange(ref warningActive, 1, 0) != 0) return;
        var notice = new Thread(() =>
        {
            try
            {
                if (Volatile.Read(ref disposed) == 0) warning(current);
            }
            catch (Exception)
            {
                // A failed notice must not bring down the application or its sampler.
            }
            finally { Volatile.Write(ref warningActive, 0); }
        }) { IsBackground = true, Name = "Water Ripple memory warning" };
        try
        {
            notice.SetApartmentState(ApartmentState.STA);
            notice.Start();
        }
        catch
        {
            Volatile.Write(ref warningActive, 0);
            throw;
        }
    }

    private static void ShowNativeWarning(ResourceSnapshot current)
    {
        string text = string.Format(CultureInfo.InvariantCulture,
            "Water Ripple is using at least 500 MB of private process memory.\n\n"
            + "Current private memory: {0:N0} MB.\n\n"
            + "Disable the screen effect to reduce resource use. You can also reduce the capture "
            + "and animation limits or close the app. This could be a memory leak; "
            + "please report it and include the recent application logs.",
            current.PrivateBytes / 1_000_000d);
        _ = MessageBox(IntPtr.Zero, text, "Water Ripple memory warning", 0x00040000 | 0x00010000 | 0x30);
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr owner, string text, string caption, uint flags);
}
