namespace WaterRipple;

// This managed value type is not marshalled. It documents the existing native call
// and keeps UI percentages/DPI conversions out of the frame lifecycle code.
internal readonly record struct RenderParameters
{
    internal int ScreenX { get; init; }
    internal int ScreenY { get; init; }
    internal int WidthPixels { get; init; }
    internal int HeightPixels { get; init; }
    internal float WidthDips { get; init; }
    internal float HeightDips { get; init; }
    internal float StrengthDips { get; init; }
    internal float BandDips { get; init; }
    internal float SpeedDipsPerSecond { get; init; }
    internal float LifetimeSeconds { get; init; }
    internal float ChromaticSeparationRatio { get; init; }
    internal float ChromaticBlendRatio { get; init; }
    internal float CrestRatio { get; init; }
    internal float RipplesRatio { get; init; }
    internal float LiquidRatio { get; init; }
    internal bool CaptureEnabled { get; init; }
    internal bool CaptureDue { get; init; }

    internal static RenderParameters From(AppSettings settings, GpuCapabilities bounds, float rasterizationScale,
        bool captureEnabled, bool captureDue) => new()
    {
        ScreenX = bounds.Left,
        ScreenY = bounds.Top,
        WidthPixels = bounds.Width,
        HeightPixels = bounds.Height,
        WidthDips = bounds.Width / rasterizationScale,
        HeightDips = bounds.Height / rasterizationScale,
        StrengthDips = (float)settings.Strength,
        // Preserve the historic UI-to-shader band scale, also used by the portable catalog.
        BandDips = (float)(settings.WaveSize * .35),
        SpeedDipsPerSecond = (float)settings.Speed,
        LifetimeSeconds = (float)settings.Duration,
        ChromaticSeparationRatio = EnabledRatio(settings.ChromaticEnabled, settings.ChromaticSeparation),
        ChromaticBlendRatio = (float)(settings.ChromaticBlend / 100),
        CrestRatio = EnabledRatio(settings.CrestEnabled, settings.CrestIntensity),
        RipplesRatio = EnabledRatio(settings.RipplesEnabled, settings.RipplesIntensity),
        LiquidRatio = EnabledRatio(settings.LiquidEnabled, settings.LiquidIntensity),
        CaptureEnabled = captureEnabled,
        CaptureDue = captureEnabled && captureDue,
    };

    private static float EnabledRatio(bool enabled, double percent) => enabled ? (float)(percent / 100) : 0;
}
