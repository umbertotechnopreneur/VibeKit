using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace WaterRipple;

public abstract class ObservableModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        RaisePropertyChanged(propertyName);
        return true;
    }

    protected void RaisePropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

// The sole source of user preferences. All consumers observe the same instance.
public sealed class AppSettings : ObservableModel
{
    private bool chromaticEnabled = true, emergencyShortcutEnabled = true;
    private bool useAcrylic, crestEnabled, ripplesEnabled, liquidEnabled;
    private bool autoRecover = true, showGpuJokes = true;
    private double strength = 9, waveSize = 100, speed = 150, duration = 1.5;
    private double animationFps = 60, captureFps = 30;
    private double chromaticSeparation = 18, chromaticBlend = 45;
    private double crestIntensity = 50, ripplesIntensity = 50, liquidIntensity = 50;
    private int presetIndex = 1, themeIndex, targetIndex;
    private string targetDisplay = "";
    private IReadOnlyList<string>? targetDisplays;
    public static IReadOnlyList<string> PresetNames { get; } = Array.AsReadOnly(new[]
    {
        "Gentle", "Water drop", "Deep water", "Shockwave", "Dew", "Pond", "Raindrop", "Glass", "Jelly", "Echo"
    });

    public double Strength { get => strength; set => SetProperty(ref strength, Number(value, 0, 25, 9)); }
    public double WaveSize { get => waveSize; set => SetProperty(ref waveSize, Number(value, 30, 220, 100)); }
    public double Speed { get => speed; set => SetProperty(ref speed, Number(value, 40, 350, 150)); }
    public double Duration { get => duration; set => SetProperty(ref duration, Number(value, 0.4, 3, 1.5)); }
    public double AnimationFps { get => animationFps; set => SetProperty(ref animationFps, Number(value, 15, 120, 60)); }
    public double CaptureFps { get => captureFps; set => SetProperty(ref captureFps, Number(value, 5, 60, 30)); }
    public bool EmergencyShortcutEnabled { get => emergencyShortcutEnabled; set => SetProperty(ref emergencyShortcutEnabled, value); }
    public bool ShowGpuJokes { get => showGpuJokes; set => SetProperty(ref showGpuJokes, value); }
    public bool AutoRecover { get => autoRecover; set => SetProperty(ref autoRecover, value); }
    public int ThemeIndex { get => themeIndex; set => SetProperty(ref themeIndex, value is >= 0 and <= 2 ? value : 0); }
    public int TargetIndex { get => targetIndex; set => SetProperty(ref targetIndex, value is >= 0 and <= 2 ? value : 0); }
    public string TargetDisplay { get => targetDisplay; set => SetProperty(ref targetDisplay, value ?? ""); }
    public IReadOnlyList<string>? TargetDisplays
    {
        get => targetDisplays;
        set
        {
            var normalized = value?.Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if (targetDisplays is not null && normalized is not null &&
                targetDisplays.SequenceEqual(normalized, StringComparer.OrdinalIgnoreCase)) return;
            SetProperty(ref targetDisplays, normalized is null ? null : Array.AsReadOnly(normalized));
        }
    }
    public bool UseAcrylic { get => useAcrylic; set => SetProperty(ref useAcrylic, value); }
    public bool ChromaticEnabled { get => chromaticEnabled; set => SetProperty(ref chromaticEnabled, value); }
    public double ChromaticSeparation { get => chromaticSeparation; set => SetProperty(ref chromaticSeparation, Number(value, 0, 100, 18)); }
    public double ChromaticBlend { get => chromaticBlend; set => SetProperty(ref chromaticBlend, Number(value, 0, 100, 45)); }
    public bool CrestEnabled { get => crestEnabled; set => SetProperty(ref crestEnabled, value); }
    public bool RipplesEnabled { get => ripplesEnabled; set => SetProperty(ref ripplesEnabled, value); }
    public bool LiquidEnabled { get => liquidEnabled; set => SetProperty(ref liquidEnabled, value); }
    public double CrestIntensity { get => crestIntensity; set => SetProperty(ref crestIntensity, Number(value, 0, 100, 50)); }
    public double RipplesIntensity { get => ripplesIntensity; set => SetProperty(ref ripplesIntensity, Number(value, 0, 100, 50)); }
    public double LiquidIntensity { get => liquidIntensity; set => SetProperty(ref liquidIntensity, Number(value, 0, 100, 50)); }

    public int PresetIndex
    {
        get => presetIndex;
        set
        {
            int normalized = Math.Clamp(value, 0, PresetNames.Count - 1);
            if (SetProperty(ref presetIndex, normalized)) ApplyPresetValues(normalized);
        }
    }

    public void ApplyPreset(int index)
    {
        int normalized = Math.Clamp(index, 0, PresetNames.Count - 1);
        if (presetIndex != normalized) PresetIndex = normalized;
        else ApplyPresetValues(normalized);
    }

    private void ApplyPresetValues(int index)
    {
        var catalog = PipelineCatalog.Load();
        var preset = System.Linq.Enumerable.First(catalog.Presets, p => p.Name == PresetNames[index]);
        Strength = preset.Values["strength"].GetDouble();
        WaveSize = preset.Values["wavelength"].GetDouble() / .35;
        Speed = preset.Values["speed"].GetDouble();
        Duration = preset.Values["lifetime"].GetDouble();
    }

    private static double Number(double value, double minimum, double maximum, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

}

// Transient application data is observable but never serialized to settings.json.
public sealed class AppState : ObservableModel
{
    private bool effectEnabled, isScreenEffectActive, captureAllowed, canApply, isClosing;
    private bool screenOverlayVisible, isCaptureValid;
    private bool isClosePromptOpen, isInTray, isExiting;
    private string overlayPhase = "Inactive";
    private string persistenceError = "";
    private string statusMessage = "", metricsText = "Waiting for frames";
    private string resourcesText = "Reading process resources…", capabilityText = "Checking the display GPU…", gpuName = "";
    private ulong dedicatedVramBytes, localGpuUsageBytes, localGpuBudgetBytes;
    private double cpuPercent;
    private long privateRamBytes, workingSetBytes;
    private int activeWaveCount;

    private bool isSettingsOpen, isCaptureConsentOpen, startWithWindows, canManageStartup;
    private string emergencyShortcutStatus = "Starting emergency shortcut…";
    public string EmergencyShortcutStatus { get => emergencyShortcutStatus; set => SetProperty(ref emergencyShortcutStatus, value); }
    private string startupStatus = "Reading Windows startup setting…";
    public bool IsCaptureConsentOpen { get => isCaptureConsentOpen; set => SetProperty(ref isCaptureConsentOpen, value); }
    public bool IsSettingsOpen { get => isSettingsOpen; set => SetProperty(ref isSettingsOpen, value); }
    public bool StartWithWindows { get => startWithWindows; set => SetProperty(ref startWithWindows, value); }
    public bool CanManageStartup { get => canManageStartup; set => SetProperty(ref canManageStartup, value); }
    public string StartupStatus { get => startupStatus; set => SetProperty(ref startupStatus, value); }

    public bool EffectEnabled { get => effectEnabled; set => SetProperty(ref effectEnabled, value); }
    public bool IsScreenEffectActive
    {
        get => isScreenEffectActive;
        set => SetProperty(ref isScreenEffectActive, value);
    }
    public bool CaptureAllowed
    {
        get => captureAllowed;
        set => SetProperty(ref captureAllowed, value);
    }
    public bool CanApply { get => canApply; set => SetProperty(ref canApply, value); }
    public bool ScreenOverlayVisible { get => screenOverlayVisible; set => SetProperty(ref screenOverlayVisible, value); }
    public bool IsCaptureValid { get => isCaptureValid; set => SetProperty(ref isCaptureValid, value); }
    public bool IsClosePromptOpen { get => isClosePromptOpen; set => SetProperty(ref isClosePromptOpen, value); }
    public bool IsInTray { get => isInTray; set => SetProperty(ref isInTray, value); }
    public bool IsExiting { get => isExiting; set => SetProperty(ref isExiting, value); }
    public string OverlayPhase { get => overlayPhase; set => SetProperty(ref overlayPhase, value ?? ""); }
    public string PersistenceError { get => persistenceError; set => SetProperty(ref persistenceError, value ?? ""); }
    public bool IsClosing
    {
        get => isClosing;
        set => SetProperty(ref isClosing, value);
    }
    public string StatusMessage { get => statusMessage; set => SetProperty(ref statusMessage, value ?? ""); }
    public string MetricsText { get => metricsText; set => SetProperty(ref metricsText, value ?? ""); }
    public string ResourcesText { get => resourcesText; set => SetProperty(ref resourcesText, value ?? ""); }
    public string CapabilityText { get => capabilityText; set => SetProperty(ref capabilityText, value ?? ""); }
    public string GpuName { get => gpuName; set => SetProperty(ref gpuName, value ?? ""); }
    public ulong DedicatedVramBytes { get => dedicatedVramBytes; set => SetProperty(ref dedicatedVramBytes, value); }
    public ulong LocalGpuUsageBytes { get => localGpuUsageBytes; set => SetProperty(ref localGpuUsageBytes, value); }
    public ulong LocalGpuBudgetBytes { get => localGpuBudgetBytes; set => SetProperty(ref localGpuBudgetBytes, value); }
    public double CpuPercent { get => cpuPercent; set => SetProperty(ref cpuPercent, double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0); }
    public long PrivateRamBytes { get => privateRamBytes; set => SetProperty(ref privateRamBytes, Math.Max(0, value)); }
    public long WorkingSetBytes { get => workingSetBytes; set => SetProperty(ref workingSetBytes, Math.Max(0, value)); }
    public int ActiveWaveCount { get => activeWaveCount; set => SetProperty(ref activeWaveCount, Math.Max(0, value)); }

}

public sealed class StudioModel
{
    public StudioModel(AppSettings settings)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        State = new AppState();
        Pipelines = new PipelineModel(settings);
    }
    public AppSettings Settings { get; }
    public AppState State { get; }
    public PipelineModel Pipelines { get; }
}
