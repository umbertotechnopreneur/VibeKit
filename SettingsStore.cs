using System;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WaterRipple;

// Only user preferences are persisted. No native handles, runtime state or diagnostics.
public sealed class SettingsStore : IDisposable
{
    private const int SchemaVersion = 1;
    private const int MaximumFileBytes = 64 * 1024;
    private readonly object sync = new();
    private readonly Action<Exception>? onError;
    private AppSettings? observed;
    private bool disposed;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
    };

    public SettingsStore(string? path = null, Action<Exception>? onError = null)
    {
        FilePath = Path.GetFullPath(path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VibeWare", "WaterRipple", "settings-ripple.json"));
        this.onError = onError;
    }

    public string FilePath { get; }

    public AppSettings Load()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            try
            {
                if (!File.Exists(FilePath)) return new AppSettings();
                using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length < 1 || stream.Length > MaximumFileBytes)
                    throw new InvalidDataException("The settings file exceeds the supported size or is empty.");
                var document = JsonSerializer.Deserialize<SettingsDocument>(stream, JsonOptions);
                if (document is null || document.Version != SchemaVersion || document.Settings is null)
                    throw new InvalidDataException("The settings file has an unsupported schema version.");
                return document.Settings.ToModel();
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                // Loading never changes a corrupt or incompatible source file.
                Report(error);
                return new AppSettings();
            }
        }
    }

    public void Attach(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (ReferenceEquals(observed, settings)) return;
            if (observed is not null) observed.PropertyChanged -= SettingsChanged;
            observed = settings;
            observed.PropertyChanged += SettingsChanged;
        }
    }

    private void SettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        lock (sync)
        {
            if (disposed || observed is null) return;
            string temporary = FilePath + ".pending";
            try
            {
                string? directory = Path.GetDirectoryName(FilePath);
                if (string.IsNullOrEmpty(directory)) throw new IOException("The settings directory is unavailable.");
                Directory.CreateDirectory(directory);
                var document = new SettingsDocument { Version = SchemaVersion, Settings = SettingsValues.FromModel(observed) };
                // A sibling replacement keeps each committed document complete on the same volume.
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, document, JsonOptions);
                    stream.Flush(flushToDisk: true);
                }
                if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null);
                else File.Move(temporary, FilePath);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                Report(error);
            }
            finally
            {
                // A failed replacement must not leave accumulating temporary documents.
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private void Report(Exception error)
    {
        // Error reporting must not unwind a bound property setter.
        try { onError?.Invoke(error); }
        catch (Exception) { }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            if (observed is not null) observed.PropertyChanged -= SettingsChanged;
            observed = null;
        }
    }

    private sealed class SettingsDocument
    {
        public int Version { get; set; }
        public SettingsValues? Settings { get; set; }
    }

    // Separate storage shape prevents deserialization order from applying a preset last.
    private sealed class SettingsValues
    {
        public int PresetIndex { get; set; } = 1;
        public double Strength { get; set; } = 9;
        public double WaveSize { get; set; } = 100;
        public double Speed { get; set; } = 150;
        public double Duration { get; set; } = 1.5;
        public double AnimationFps { get; set; } = 60;
        public double CaptureFps { get; set; } = 30;
        public bool UseAcrylic { get; set; } = true;
        public bool ChromaticEnabled { get; set; } = true;
        public double ChromaticSeparation { get; set; } = 18;
        public double ChromaticBlend { get; set; } = 45;
        public bool CrestEnabled { get; set; }
        public bool RipplesEnabled { get; set; }
        public bool LiquidEnabled { get; set; }
        public double CrestIntensity { get; set; } = 50;
        public double RipplesIntensity { get; set; } = 50;
        public double LiquidIntensity { get; set; } = 50;

        public AppSettings ToModel() => new()
        {
            PresetIndex = PresetIndex,
            Strength = Strength,
            WaveSize = WaveSize,
            Speed = Speed,
            Duration = Duration,
            AnimationFps = AnimationFps,
            CaptureFps = CaptureFps,
            UseAcrylic = UseAcrylic,
            ChromaticEnabled = ChromaticEnabled,
            ChromaticSeparation = ChromaticSeparation,
            ChromaticBlend = ChromaticBlend,
            CrestEnabled = CrestEnabled,
            RipplesEnabled = RipplesEnabled,
            LiquidEnabled = LiquidEnabled,
            CrestIntensity = CrestIntensity,
            RipplesIntensity = RipplesIntensity,
            LiquidIntensity = LiquidIntensity,
        };

        public static SettingsValues FromModel(AppSettings model) => new()
        {
            PresetIndex = model.PresetIndex,
            Strength = model.Strength,
            WaveSize = model.WaveSize,
            Speed = model.Speed,
            Duration = model.Duration,
            AnimationFps = model.AnimationFps,
            CaptureFps = model.CaptureFps,
            UseAcrylic = model.UseAcrylic,
            ChromaticEnabled = model.ChromaticEnabled,
            ChromaticSeparation = model.ChromaticSeparation,
            ChromaticBlend = model.ChromaticBlend,
            CrestEnabled = model.CrestEnabled,
            RipplesEnabled = model.RipplesEnabled,
            LiquidEnabled = model.LiquidEnabled,
            CrestIntensity = model.CrestIntensity,
            RipplesIntensity = model.RipplesIntensity,
            LiquidIntensity = model.LiquidIntensity,
        };
    }
}
