using System;

namespace WaterRipple;

// All deadlines use the session's monotonic clock. Idle sessions still service
// capture and the optional CRT lease without polling every few milliseconds.
internal sealed class FrameSchedule
{
    private const double MaintenanceInterval = .100;
    private double nextFrame = -1, nextCapture = -1;

    internal void Rendered(double now, double animationFps) =>
        nextFrame = Advance(nextFrame, now, 1 / animationFps);

    internal bool CaptureDue(double now, double captureFps)
    {
        if (now < nextCapture) return false;
        nextCapture = Advance(nextCapture, now, 1 / captureFps);
        return true;
    }

    internal void BeginAnimation() => nextFrame = -1;

    internal void RefreshCapture() => nextCapture = -1;

    internal double DelayUntilWork(double now, bool animated, double inspectionUntil, bool retryPresent)
    {
        double due = now + MaintenanceInterval;
        if (animated)
            return Math.Max(0, Math.Min(due, nextFrame) - now);

        // A busy swap chain can still need to show the final clean frame.
        if (retryPresent) due = Math.Min(due, nextFrame);
        if (inspectionUntil > now)
            due = Math.Min(due, inspectionUntil);
        else
            due = Math.Min(due, nextCapture);
        return Math.Max(0, due - now);
    }

    private static double Advance(double deadline, double now, double interval)
    {
        // Do not replay missed ticks or advance a future deadline on an early wake.
        if (deadline > now) return deadline;
        return now - deadline < interval ? deadline + interval : now + interval;
    }
}
