using System.Net;
using System.Text.Json.Serialization;

namespace VibeKit.StudioControls.Core;

public sealed record ObsConnectionSettings(
    string Host = "127.0.0.1",
    int Port = 4455,
    [property: JsonIgnore] string Password = "")
{
    public Uri Endpoint
    {
        get
        {
            Validate();
            return new UriBuilder("ws", Host, Port).Uri;
        }
    }

    public void Validate()
    {
        bool local = string.Equals(Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            (IPAddress.TryParse(Host, out IPAddress? address) && IPAddress.IsLoopback(address));
        if (!local) throw new ArgumentException("OBS must use a loopback address on this computer.", nameof(Host));
        if (Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(Port));
        if (Password is null) throw new ArgumentNullException(nameof(Password));
    }

    public override string ToString() => $"OBS {Host}:{Port} (password redacted)";
}

public sealed record ScreenFramingState(string SceneName, long ItemId, double Zoom, double X, double Y);
public sealed record VignetteState(bool Enabled, double Strength);
public sealed record ScreenPreset(double Zoom = 1, double X = 0, double Y = 0) : IValidatedSettings
{
    public void Validate()
    {
        if (!double.IsFinite(Zoom) || Zoom is < 1 or > 4 ||
            !double.IsFinite(X) || X is < -100 or > 100 || !double.IsFinite(Y) || Y is < -100 or > 100)
            throw new ArgumentException("A screen preset needs zoom 1–4 and positions -100–100.");
    }
}

public sealed record StudioStatus
{
    public string SceneName { get; init; } = "Unknown scene";
    public bool IsRecording { get; init; }
    public bool IsPaused { get; init; }
    public string Timecode { get; init; } = "";
    public string? OutputPath { get; init; }
    public bool ConnectionAvailable { get; init; } = true;
    public bool IsFresh { get; init; } = true;
    public bool ActionSucceeded { get; init; }
    public bool BlurAvailable { get; init; }
    public bool BlurEnabled { get; init; }
    public int BlurIntensity { get; init; } = 3;
    public ScreenFramingState? Screen { get; init; }
    public IReadOnlyDictionary<string, VignetteState> Vignettes { get; init; } = new Dictionary<string, VignetteState>();
    public IReadOnlyDictionary<string, bool> AudioMutes { get; init; } = new Dictionary<string, bool>();
    public IReadOnlyDictionary<string, bool> AudioAvailable { get; init; } = new Dictionary<string, bool>();
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public interface IValidatedSettings
{
    void Validate();
}

public sealed record ProjectSettings : IValidatedSettings
{
    public string Name { get; init; } = "Software demo";
    public string Eyebrow { get; init; } = "UMBERTO’S LAB";
    public string Subtitle { get; init; } = "A closer look.";
    public string Description { get; init; } = "";
    public string[] Links { get; init; } = ["", "", ""];
    public string Logo { get; init; } = "";
    [JsonPropertyName("vibeware")] public bool VibeWare { get; init; }
    [JsonPropertyName("sound_enabled")] public bool SoundEnabled { get; init; } = true;
    [JsonPropertyName("sound_volume")] public double SoundVolume { get; init; } = 18;
    public string Chapter { get; init; } = "One detail at a time";
    [JsonPropertyName("chapter_description")] public string ChapterDescription { get; init; } = "";
    [JsonPropertyName("compare_a")] public string CompareA { get; init; } = "Before";
    [JsonPropertyName("compare_b")] public string CompareB { get; init; } = "After";

    public void Validate()
    {
        ValidateText(Name, 100, nameof(Name), required: true);
        ValidateText(Eyebrow, 70, nameof(Eyebrow));
        ValidateText(Subtitle, 180, nameof(Subtitle));
        ValidateText(Description, 600, nameof(Description));
        ValidateText(Chapter, 100, nameof(Chapter));
        ValidateText(ChapterDescription, 180, nameof(ChapterDescription));
        ValidateText(CompareA, 60, nameof(CompareA));
        ValidateText(CompareB, 60, nameof(CompareB));
        if (Links is null || Links.Length > 3) throw new ArgumentException("Use up to three project links.", nameof(Links));
        foreach (string link in Links) ValidateText(link, 150, nameof(Links));
        if (Logo is null || Logo.Length > 32767) throw new ArgumentException("Invalid local logo path.", nameof(Logo));
        if (!double.IsFinite(SoundVolume) || SoundVolume is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(SoundVolume), "Sound volume must be between 0 and 100.");
    }

    private static void ValidateText(string? value, int maximum, string name, bool required = false)
    {
        if (value is null || value.Length > maximum || (required && string.IsNullOrWhiteSpace(value)))
            throw new ArgumentException($"{name} must contain {(required ? "1" : "0")}–{maximum} characters.", name);
    }
}

public class ObsException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class ObsRequestException(string requestType, int? code, string detail = "OBS rejected the request")
    : ObsException($"{requestType}: {detail} (code {code?.ToString() ?? "unknown"}).")
{
    public string RequestType { get; } = requestType;
    public int? Code { get; } = code;
}

public sealed class ObsConnectionException(string message, bool requestSent = false, Exception? inner = null)
    : ObsException(message, inner)
{
    public bool RequestSent { get; } = requestSent;
}

public sealed class ProjectApplyException(string message, Exception? inner = null) : ObsException(message, inner)
{
    public bool SavedLocally => true;
}
