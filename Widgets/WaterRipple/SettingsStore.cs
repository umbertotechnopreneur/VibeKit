using System;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace WaterRipple;

// Only user preferences are persisted. No native handles, runtime state or diagnostics.
public sealed class SettingsStore : IDisposable, IAsyncDisposable
{
    private const int SchemaVersion = 1;
    private const int MaximumFileBytes = 64 * 1024;
    private readonly object sync = new();
    private readonly Action<Exception>? onError;
    private readonly Action? onSaved;
    private readonly Timer saveTimer;
    private readonly int saveDelayMilliseconds;
    private AppSettings? observed;
    private SettingsDocument? pending, failed;
    private Task<bool> writerTask = Task.FromResult(true);
    private Task? disposalTask;
    private long saveAfter;
    private bool disposed, writerRunning, flushRequested, writable = true;
    private Exception? lastError;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
    };

    public SettingsStore(string? path = null, Action<Exception>? onError = null,
        Action? onSaved = null, TimeSpan? saveDelay = null)
    {
        FilePath = Path.GetFullPath(path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VibeWare", "WaterRipple", "settings-ripple.json"));
        this.onError = onError;
        this.onSaved = onSaved;
        saveDelayMilliseconds = checked((int)(saveDelay ?? TimeSpan.FromMilliseconds(250)).TotalMilliseconds);
        if (saveDelayMilliseconds < 1) throw new ArgumentOutOfRangeException(nameof(saveDelay));
        saveTimer = new Timer(SaveWhenQuiet, null, Timeout.Infinite, Timeout.Infinite);
    }

    public string FilePath { get; }
    public Exception? LastError { get { lock (sync) return lastError; } }

    private void SaveWhenQuiet(object? state)
    {
        lock (sync)
        {
            if (disposed) return;
            long remaining = saveAfter - Environment.TickCount64;
            if (!flushRequested && remaining > 0) saveTimer.Change((int)remaining, Timeout.Infinite);
            else StartWriter();
        }
    }

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
                // A fallback model must not overwrite the unreadable source on its first edit.
                writable = false;
                lastError = error;
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
            if (disposed || !writable || observed is null) return;
            // Snapshot bound properties on their owning thread. The worker never reads the UI model.
            pending = new SettingsDocument { Version = SchemaVersion, Settings = SettingsValues.FromModel(observed) };
            saveAfter = Environment.TickCount64 + saveDelayMilliseconds;
            saveTimer.Change(saveDelayMilliseconds, Timeout.Infinite);
        }
    }

    public Task<bool> FlushAsync()
    {
        lock (sync)
        {
            if (disposed) return writerTask;
            if (!writable) return Task.FromResult(false);
            pending ??= failed; // Explicit flush retries a failed snapshot; the timer does not loop on errors.
            flushRequested = true;
            saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
            StartWriter();
            return writerTask;
        }
    }

    // Called with sync held. There is at most one active writer and one latest pending snapshot.
    private void StartWriter()
    {
        if (writerRunning) return;
        if (pending is null) { flushRequested = false; return; }
        writerRunning = true;
        writerTask = Task.Run(WritePending);
    }

    private bool WritePending()
    {
        while (true)
        {
            SettingsDocument document;
            lock (sync)
            {
                document = pending!;
                pending = null;
            }
            Exception? failure = null;
            try { Write(document); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            { failure = error; }
            lock (sync)
            {
                lastError = failure;
                failed = failure is null ? null : document;
            }
            if (failure is not null) Report(failure);
            else
            {
                // Notifications may originate on this worker; callers dispatch UI updates themselves.
                try { onSaved?.Invoke(); }
                catch (Exception) { }
            }
            lock (sync)
            {
                if (flushRequested && pending is not null) continue;
                writerRunning = false;
                flushRequested = false;
                if (pending is not null && !disposed)
                    saveTimer.Change((int)Math.Max(1, saveAfter - Environment.TickCount64), Timeout.Infinite);
                return failure is null;
            }
        }
    }

    private void Write(SettingsDocument document)
    {
        string temporary = FilePath + ".pending";
        try
        {
            string? directory = Path.GetDirectoryName(FilePath);
            if (string.IsNullOrEmpty(directory)) throw new IOException("The settings directory is unavailable.");
            Directory.CreateDirectory(directory);
            // A sibling replacement keeps each committed document complete on the same volume.
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, document, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null);
            else File.Move(temporary, FilePath);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void Report(Exception error)
    {
        // Error reporting must not unwind a bound property setter.
        try { onError?.Invoke(error); }
        catch (Exception) { }
    }

    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposalTask is not null) return new ValueTask(disposalTask);
            disposed = true;
            if (observed is not null) observed.PropertyChanged -= SettingsChanged;
            observed = null;
            saveTimer.Dispose();
            if (writable) pending ??= failed;
            flushRequested = true;
            StartWriter();
            disposalTask = writerTask;
            return new ValueTask(disposalTask);
        }
    }

    // Non-UI callers retain deterministic disposal; the application awaits DisposeAsync on exit.
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

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
        public bool EmergencyShortcutEnabled { get; set; } = true;
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
            EmergencyShortcutEnabled = EmergencyShortcutEnabled,
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
            EmergencyShortcutEnabled = model.EmergencyShortcutEnabled,
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
