#if DEBUG
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace WaterRipple;

internal static class ResourceChecks
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(2);

    internal static int Run()
    {
        try
        {
            CheckWarningThresholds();
            Console.WriteLine("PASS memory threshold and hysteresis");
            CheckIndependentSampling();
            Console.WriteLine("PASS sampler/notice threads, warning deduplication and cancellation");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL resource guard: " + error.Message);
            return 1;
        }
    }

    private static void CheckWarningThresholds()
    {
        var latch = new MemoryWarningLatch();
        Require(!latch.Observe(0), "Zero memory unexpectedly warned.");
        Require(MemoryWarningLatch.LimitBytes == 500_000_000, "The memory threshold is not 500 MB.");
        Require(!latch.Observe(MemoryWarningLatch.LimitBytes - 1), "Warning fired below 500 MB.");
        Require(latch.Observe(MemoryWarningLatch.LimitBytes), "Exactly 500 MB did not warn.");
        Require(!latch.Observe(MemoryWarningLatch.LimitBytes + 1), "A high episode warned twice.");
        Require(!latch.Observe(MemoryWarningLatch.RearmBytes + 1), "An intermediate value warned.");
        Require(!latch.Observe(MemoryWarningLatch.LimitBytes), "The latch rearmed above 400 MB.");
        Require(!latch.Observe(MemoryWarningLatch.RearmBytes), "A low-memory recovery warned.");
        Require(latch.Observe(MemoryWarningLatch.LimitBytes), "Recovery to 400 MB failed to rearm.");
        Require(!latch.Observe(MemoryWarningLatch.LimitBytes), "The second episode warned repeatedly.");
    }

    private static void CheckIndependentSampling()
    {
        int callerThreadId = Environment.CurrentManagedThreadId;
        int sampleCount = 0, warningCount = 0, invalidWarning = 0;
        long requestedBytes = 0;
        Thread? samplerThread = null;
        var warningThreads = new ConcurrentQueue<Thread>();
        using var releaseFirstNotice = new ManualResetEventSlim(false);
        using var firstNoticeEntered = new ManualResetEventSlim(false);

        ResourceSnapshot Sample()
        {
            Volatile.Write(ref samplerThread, Thread.CurrentThread);
            Interlocked.Increment(ref sampleCount);
            long bytes = Interlocked.Read(ref requestedBytes);
            return new ResourceSnapshot(bytes, bytes / 2, 12.5, DateTimeOffset.UtcNow);
        }

        void Warn(ResourceSnapshot snapshot)
        {
            warningThreads.Enqueue(Thread.CurrentThread);
            if (snapshot.PrivateBytes < MemoryWarningLatch.LimitBytes)
                Interlocked.Exchange(ref invalidWarning, 1);
            int index = Interlocked.Increment(ref warningCount);
            if (index == 1)
            {
                firstNoticeEntered.Set();
                // An unanswered notice must not stop independent resource sampling.
                releaseFirstNotice.Wait(WaitLimit);
            }
        }

        using var monitor = new ResourceMonitor(Sample, Warn, TimeSpan.FromMilliseconds(5));
        try
        {
            WaitUntil(() => Volatile.Read(ref sampleCount) >= 2, "The sampler did not start.");
            Thread worker = Volatile.Read(ref samplerThread)!;
            Require(worker.ManagedThreadId != callerThreadId, "Sampling ran on the caller/UI thread.");
            Require(worker.GetApartmentState() == ApartmentState.MTA, "The sampler was not MTA.");
            Require(monitor.Snapshot.CpuPercent == 12.5, "The immutable snapshot lost the supplied CPU reading.");
            Require(monitor.Snapshot.SampledAt != DateTimeOffset.MinValue, "No successful sample was published.");

            Interlocked.Exchange(ref requestedBytes, MemoryWarningLatch.LimitBytes);
            Require(firstNoticeEntered.Wait(WaitLimit), "The 500 MB sample did not queue a warning.");
            Require(warningThreads.TryPeek(out var firstWarning), "No warning thread was observed.");
            Thread noticeThread = firstWarning ?? throw new InvalidOperationException("No warning thread was observed.");
            Require(noticeThread.ManagedThreadId != callerThreadId && noticeThread.ManagedThreadId != worker.ManagedThreadId,
                "The warning blocked the caller or sampler thread.");
            Require(noticeThread.GetApartmentState() == ApartmentState.STA, "The warning was not STA.");

            int countAtNotice = Volatile.Read(ref sampleCount);
            WaitUntil(() => Volatile.Read(ref sampleCount) >= countAtNotice + 4,
                "An unanswered warning stopped the sampler.");
            Require(Volatile.Read(ref warningCount) == 1, "A persistent high-memory episode queued duplicate warnings.");
            Require(monitor.Snapshot.PrivateBytes == MemoryWarningLatch.LimitBytes,
                "The published private-memory value was incorrect.");
            Require(monitor.Snapshot.WorkingSetBytes == MemoryWarningLatch.LimitBytes / 2,
                "The published working-set value was incorrect.");

            // Recovery and another spike while the first notice is open must not open a second one.
            SetMemoryAndWait(MemoryWarningLatch.RearmBytes);
            SetMemoryAndWait(MemoryWarningLatch.LimitBytes + 1);
            Require(Volatile.Read(ref warningCount) == 1, "Memory flapping opened concurrent warning dialogs.");
            releaseFirstNotice.Set();
            WaitUntil(() => !noticeThread.IsAlive, "The released warning thread did not finish.");
            int countAfterNotice = Volatile.Read(ref sampleCount);
            WaitUntil(() => Volatile.Read(ref sampleCount) >= countAfterNotice + 4,
                "Sampling stopped after a warning closed.");
            Require(Volatile.Read(ref warningCount) == 1, "Closing a warning retriggered the same high episode.");

            SetMemoryAndWait(0);
            SetMemoryAndWait(MemoryWarningLatch.LimitBytes);
            WaitUntil(() => Volatile.Read(ref warningCount) == 2,
                "A fresh high-memory episode did not produce a new warning.");
            Require(Volatile.Read(ref invalidWarning) == 0, "The warning received a below-threshold snapshot.");

            monitor.Dispose();
            monitor.Dispose();
            WaitUntil(() => !worker.IsAlive, "Idempotent disposal did not stop the sampler within two seconds.");

            void SetMemoryAndWait(long bytes)
            {
                Interlocked.Exchange(ref requestedBytes, bytes);
                WaitUntil(() => monitor.Snapshot.PrivateBytes == bytes,
                    "The sampler did not publish the requested fake memory value.");
                // Publication precedes latch evaluation. Observe later iterations before
                // changing state so this test cannot race a pending threshold decision.
                int publishedAtCount = Volatile.Read(ref sampleCount);
                WaitUntil(() => Volatile.Read(ref sampleCount) >= publishedAtCount + 2,
                    "The sampler did not finish processing the fake memory transition.");
            }
        }
        finally
        {
            releaseFirstNotice.Set();
            monitor.Dispose();
            // Bounded cleanup belongs to this pre-UI test runner, never to the application monitor.
            Volatile.Read(ref samplerThread)?.Join(WaitLimit);
            foreach (var notice in warningThreads) notice.Join(WaitLimit);
        }
    }

    private static void WaitUntil(Func<bool> condition, string failure)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed >= WaitLimit) throw new InvalidOperationException(failure);
            Thread.Sleep(2);
        }
    }

    private static void Require(bool condition, string failure)
    {
        if (!condition) throw new InvalidOperationException(failure);
    }
}
#endif
