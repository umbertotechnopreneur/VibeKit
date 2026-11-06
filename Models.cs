using System;
using System.Collections.Generic;
using System.ComponentModel;
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
    private bool useAcrylic = true;
    private double strength = 9, waveSize = 100, speed = 150, duration = 1.5;
    private double animationFps = 60, captureFps = 30;
    private int presetIndex = 1;
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
    public bool UseAcrylic { get => useAcrylic; set => SetProperty(ref useAcrylic, value); }

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
        var values = index switch
        {
            0 => (Strength: 4d, Size: 85d, Speed: 100d, Duration: 1.7),
            2 => (Strength: 14d, Size: 150d, Speed: 90d, Duration: 2.5),
            3 => (Strength: 20d, Size: 65d, Speed: 260d, Duration: 1d),
            4 => (Strength: 2.5d, Size: 50d, Speed: 160d, Duration: .8d),
            5 => (Strength: 6d, Size: 180d, Speed: 80d, Duration: 2.8d),
            6 => (Strength: 11d, Size: 55d, Speed: 210d, Duration: 1.1d),
            7 => (Strength: 3.5d, Size: 40d, Speed: 300d, Duration: .65d),
            8 => (Strength: 18d, Size: 190d, Speed: 70d, Duration: 2.7d),
            9 => (Strength: 12d, Size: 210d, Speed: 130d, Duration: 3d),
            _ => (Strength: 9d, Size: 100d, Speed: 150d, Duration: 1.5)
        };
        Strength = values.Strength;
        WaveSize = values.Size;
        Speed = values.Speed;
        Duration = values.Duration;
    }

    private static double Number(double value, double minimum, double maximum, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

}

// Transient application data is observable but never serialized to settings.json.
public sealed class AppState : ObservableModel
{
    private bool isScreenEffectActive, isCaptureLive = true, captureAllowed, canApply, isClosing;
    private bool screenOverlayVisible, isCaptureValid;
    private bool isClosePromptOpen, isInTray, isExiting;
    private string overlayPhase = "Inactive";
    private string persistenceError = "";
    private string statusMessage = "", captureMessage = "Starting…", metricsText = "Waiting for frames";
    private string resourcesText = "Reading process resources…", capabilityText = "Checking the display GPU…", gpuName = "";
    private ulong dedicatedVramBytes, localGpuUsageBytes, localGpuBudgetBytes;
    private double cpuPercent;
    private long privateRamBytes, workingSetBytes;
    private int activeWaveCount;

    private bool isSettingsOpen, isCaptureConsentOpen, startWithWindows, canManageStartup;
    private string startupStatus = "Reading Windows startup setting…";
    public bool IsCaptureConsentOpen { get => isCaptureConsentOpen; set => SetProperty(ref isCaptureConsentOpen, value); }
    public bool IsSettingsOpen { get => isSettingsOpen; set => SetProperty(ref isSettingsOpen, value); }
    public bool StartWithWindows { get => startWithWindows; set => SetProperty(ref startWithWindows, value); }
    public bool CanManageStartup { get => canManageStartup; set => SetProperty(ref canManageStartup, value); }
    public string StartupStatus { get => startupStatus; set => SetProperty(ref startupStatus, value); }

    public bool IsScreenEffectActive
    {
        get => isScreenEffectActive;
        set
        {
            if (!SetProperty(ref isScreenEffectActive, value)) return;
            RaisePropertyChanged(nameof(ApplyButtonText));
            RaisePropertyChanged(nameof(CanFreeze));
            RaisePropertyChanged(nameof(ScreenModeText));
        }
    }
    public bool IsCaptureLive
    {
        get => isCaptureLive;
        set { if (SetProperty(ref isCaptureLive, value)) RaisePropertyChanged(nameof(FreezeButtonText)); }
    }
    public bool CaptureAllowed
    {
        get => captureAllowed;
        set { if (SetProperty(ref captureAllowed, value)) RaisePropertyChanged(nameof(CanFreeze)); }
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
        set { if (SetProperty(ref isClosing, value)) RaisePropertyChanged(nameof(CanFreeze)); }
    }
    public string StatusMessage { get => statusMessage; set => SetProperty(ref statusMessage, value ?? ""); }
    public string CaptureMessage { get => captureMessage; set => SetProperty(ref captureMessage, value ?? ""); }
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
    public int ActiveWaveCount { get => activeWaveCount; set => SetProperty(ref activeWaveCount, Math.Clamp(value, 0, 8)); }
    public string ApplyButtonText => IsScreenEffectActive ? "Stop screen effect" : "Apply to screen";
    public string FreezeButtonText => IsCaptureLive ? "Freeze desktop" : "Refresh background";
    public bool CanFreeze => CaptureAllowed && !IsScreenEffectActive && !IsClosing;
    public string ScreenModeText => IsScreenEffectActive ? "Screen effect" : "Studio preview";

}

public sealed class StudioModel
{
    public StudioModel(AppSettings settings)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        State = new AppState();
    }
    public AppSettings Settings { get; }
    public AppState State { get; }
}
