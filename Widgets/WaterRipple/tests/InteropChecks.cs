using System.Runtime.InteropServices;
using WaterRipple;

internal static class InteropChecks
{
    internal static void Run()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        Check(Marshal.SizeOf<NativeWave>() == 16 && Marshal.SizeOf<NativeStats>() == 32 &&
            Marshal.SizeOf<GpuCapabilities>() == 304 && Marshal.SizeOf<GpuMemory>() == 32,
            "Managed ABI size differs from the native static assertions.");
        Check(Marshal.OffsetOf<NativeStats>(nameof(NativeStats.Status)).ToInt32() == 24,
            "Capture status moved in the native statistics ABI.");
        Check(!new NativeStats { Status = CaptureStatus.ValidCapture | CaptureStatus.PresentBusy }.HasPresentedCapture &&
            new NativeStats { Status = CaptureStatus.VisibleCapture | CaptureStatus.Clipped }.HasPresentedCapture,
            "Capture validity accepts a frame that has not been presented.");
        var delegated = new NativeStats { Status = CaptureStatus.ValidCapture | CaptureStatus.CrtPresentation };
        Check(delegated.HasCrtPresentation && !delegated.HasPresentedCapture &&
            !new NativeStats { Status = CaptureStatus.CrtPresentation }.HasCrtPresentation,
            "CRT presentation must validate capture while leaving the local surface hidden.");
        var settings = new AppSettings { WaveSize = 100, ChromaticSeparation = 40, CrestEnabled = false, CrestIntensity = 80 };
        var bounds = new GpuCapabilities { Left = -1920, Width = 1920, Height = 1080 };
        var parameters = RenderParameters.From(settings, bounds, 1.5f, captureEnabled: false, captureDue: true);
        Check(parameters.ScreenX == -1920 && parameters.WidthPixels == 1920 && parameters.WidthDips == 1280 &&
            parameters.HeightDips == 720 && parameters.BandDips == 35 && parameters.ChromaticSeparationRatio == .4f &&
            parameters.CrestRatio == 0 && !parameters.CaptureDue,
            "DPI, filter units or paused-capture arguments changed at the native boundary.");
        var schedule = new FrameSchedule();
        schedule.Rendered(1, 60);
        Check(schedule.CaptureDue(1, 30) && !schedule.CaptureDue(1.02, 30),
            "Capture should wait for its own deadline.");
        Check(Math.Abs(schedule.DelayUntilWork(1, false, 0, false) - 1d / 30) < .0001,
            "Idle ripple should wake for capture without waking at the animation rate.");
        Check(Math.Abs(schedule.DelayUntilWork(1, true, 0, false) - 1d / 60) < .0001,
            "Live waves should still use the animation cadence.");
        Check(Math.Abs(schedule.DelayUntilWork(1, false, 1.5, true) - 1d / 60) < .0001,
            "A busy final presentation must be retried before the next maintenance tick.");
        Check(Math.Abs(schedule.DelayUntilWork(1, false, 1.5, false) - .1) < .0001 &&
            Math.Abs(schedule.DelayUntilWork(1.45, false, 1.5, false) - .05) < .0001,
            "A frozen capture should service maintenance and wake at the end of inspection.");
        schedule.Rendered(3, 60);
        Check(schedule.CaptureDue(3, 30) && !schedule.CaptureDue(3, 30) &&
            schedule.DelayUntilWork(3, true, 0, false) > 0, "A delayed render should not replay past deadlines.");
        schedule.RefreshCapture();
        Check(schedule.CaptureDue(3, 30), "Leaving inspection should capture immediately.");
        Console.WriteLine("PASS managed/native ABI layout, capture flags, render parameters and one-shot deadlines");
    }
}
