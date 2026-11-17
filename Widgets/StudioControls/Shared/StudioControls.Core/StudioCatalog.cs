using System.Text.Json;

namespace VibeKit.StudioControls.Core;

public sealed record SceneDefinition(string Name, string Title, string Description);
public sealed record CardSourceDefinition(string SourceName, string FileName);
public sealed record VignetteDefinition(string Key, string SourceName, string TargetSource, string Label);
public sealed record LayoutRectangle(int X, int Y, int Width, int Height);

public sealed record StudioCatalog
{
    public int CanvasWidth { get; init; } = 2560;
    public int CanvasHeight { get; init; } = 1440;
    public string CameraSource { get; init; } = "Logitech Brio";
    public string ScreenSource { get; init; } = "ASUS ProArt";
    public string HardwareSource { get; init; } = "Sony";
    public string BlurFilter { get; init; } = "Background blur";
    public string VignetteFilter { get; init; } = "Vignette intensity";
    public string BrandingSource { get; init; } = "VibeWare branding";
    public string IntroSoundSource { get; init; } = "Lab intro sound";
    public string[] AudioInputs { get; init; } = ["Mic/Aux", "Desktop Audio"];
    public LayoutRectangle OutroCameraBounds { get; init; } = new(112, 350, 1464, 824);
    public LayoutRectangle[] ComparisonBounds { get; init; } = [new(80, 330, 1160, 900), new(1320, 330, 1160, 900)];
    public SceneDefinition[] Scenes { get; init; } = [];
    public CardSourceDefinition[] CardSources { get; init; } = [];
    public VignetteDefinition[] Vignettes { get; init; } = [];

    public static StudioCatalog Default { get; } = LoadEmbedded();

    private static StudioCatalog LoadEmbedded()
    {
        using Stream stream = typeof(StudioCatalog).Assembly.GetManifestResourceStream("StudioControls.Catalog.json")
            ?? throw new InvalidOperationException("The studio catalog is missing.");
        return JsonSerializer.Deserialize<StudioCatalog>(stream, SettingsStore.JsonOptions)
            ?? throw new InvalidDataException("The studio catalog is empty.");
    }
}
