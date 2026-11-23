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
        Console.WriteLine("PASS managed/native ABI layout, capture flags and render parameter units");
    }
}
