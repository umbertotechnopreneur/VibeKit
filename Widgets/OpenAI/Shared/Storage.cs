using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenAIUsage;

public sealed class Storage
{
    public const int MaximumSettingsBytes = 64 * 1024;
    private const int MaximumHistoryBytes = 8 * 1024 * 1024;
    private const int MaximumHistorySamples = 65_536;
    private const long MaximumTimestamp = 253402300799;
    public string DirectoryPath { get; }
    public string SettingsPath => Path.Combine(DirectoryPath, "settings.json");
    public Storage(string? directory = null)
    {
        DirectoryPath = directory ?? (OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAIUsage")
            : Path.Combine(Environment.GetEnvironmentVariable("XDG_STATE_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state"), "openai-usage"));
        Directory.CreateDirectory(DirectoryPath);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public WidgetSettings LoadSettings()
    {
        if (!File.Exists(SettingsPath)) return new();
        using var stream = File.OpenRead(SettingsPath);
        if (stream.Length > MaximumSettingsBytes)
            throw new InvalidDataException("The settings file exceeds the supported size.");
        // The allocation and read stay bounded even if another process grows the open file.
        var contents = new byte[(int)stream.Length];
        stream.ReadExactly(contents);
        if (stream.ReadByte() != -1) throw new InvalidDataException("The settings file changed while reading.");
        var settings = JsonSerializer.Deserialize<WidgetSettings>(contents, JsonOptions.Default)
            ?? throw new InvalidDataException("The settings file must contain an object.");
        settings.Validate();
        return settings;
    }

    public void SaveSettings(WidgetSettings settings)
    {
        settings.Validate();
        using var guard = new FileStream(SettingsPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        string? preserved = null;
        try { LoadSettings(); }
        catch (Exception error) when (IsInvalidSettings(error))
        {
            // Move instead of copying: even an oversized invalid file costs no duplicate storage.
            preserved = SettingsPath + ".invalid-" + Guid.NewGuid().ToString("N");
            File.Move(SettingsPath, preserved);
        }
        try { AtomicWrite(SettingsPath, settings, MaximumSettingsBytes); }
        catch
        {
            if (preserved is not null && !File.Exists(SettingsPath)) File.Move(preserved, SettingsPath);
            throw;
        }
    }

    public SettingsForEditing LoadSettingsForEditing()
    {
        try { return new(LoadSettings(), null); }
        catch (Exception error) when (IsInvalidSettings(error))
        {
            return new(new(), "Saved settings are invalid. Defaults are shown for editing; save to recover. " +
                "The original file will be preserved beside the settings file.");
        }
    }

    private static bool IsInvalidSettings(Exception error) => error is
        JsonException or InvalidDataException or ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException;

    public List<Sample> Record(string identity, QuotaWindow window, string plan, long now)
    {
        if (string.IsNullOrWhiteSpace(identity) || string.IsNullOrWhiteSpace(plan) ||
            !double.IsFinite(window.Used) || window.Used is < 0 or > 100 || now is <= 0 or > MaximumTimestamp ||
            window.ResetsAt is <= 0 or > MaximumTimestamp)
            throw new InvalidDataException("The quota observation is invalid; local history was preserved.");
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity + "|" + window.Key + "|" + plan)))[..32];
        string path = Path.Combine(DirectoryPath, key + ".history.json");
        using var locked = new FileStream(Path.Combine(DirectoryPath, key + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        List<Sample> history = ReadHistory(path, plan);
        history.RemoveAll(s => s.At < now - 16 * 86400 || s.At > now);
        history.RemoveAll(s => s.At == now);
        history.Add(new(now, window.Used, window.ResetsAt, plan));
        if (history.Count > MaximumHistorySamples)
            throw new InvalidDataException("Local history exceeds the supported sample count; the source file was preserved.");
        AtomicWrite(path, history, MaximumHistoryBytes);
        return history;
    }

    private static List<Sample> ReadHistory(string path, string plan)
    {
        FileStream stream;
        try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException) { return []; }
        using (stream)
        {
            if (stream.Length is < 1 or > MaximumHistoryBytes)
                throw new InvalidDataException("Local history has an unsupported size; the source file was preserved.");
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > MaximumHistorySamples)
                throw new InvalidDataException("Local history must be a bounded array of observations; the source file was preserved.");
            var result = new List<Sample>(root.GetArrayLength());
            var timestamps = new HashSet<long>();
            foreach (var value in root.EnumerateArray())
            {
                var sample = ParseSample(value, plan);
                if (!timestamps.Add(sample.At))
                    throw new InvalidDataException("Local history contains duplicate timestamps; the source file was preserved.");
                result.Add(sample);
            }
            return result;
        }
    }

    private static Sample ParseSample(JsonElement value, string expectedPlan)
    {
        const string invalid = "Local history contains an invalid observation; the source file was preserved.";
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException(invalid);
        long observedAt = 0, resetsAt = 0;
        double percentage = 0;
        bool validTimestamp = value.TryGetProperty("at", out var timestamp) &&
            timestamp.ValueKind == JsonValueKind.Number && timestamp.TryGetInt64(out observedAt) &&
            observedAt is > 0 and <= MaximumTimestamp;
        bool validPercentage = value.TryGetProperty("used", out var used) &&
            used.ValueKind == JsonValueKind.Number && used.TryGetDouble(out percentage) &&
            double.IsFinite(percentage) && percentage is >= 0 and <= 100;
        bool validReset = value.TryGetProperty("reset", out var reset) &&
            (reset.ValueKind == JsonValueKind.Null || (reset.ValueKind == JsonValueKind.Number &&
                reset.TryGetInt64(out resetsAt) && resetsAt is > 0 and <= MaximumTimestamp));
        bool validPlan = value.TryGetProperty("plan", out var plan) &&
            plan.ValueKind == JsonValueKind.String && plan.GetString() == expectedPlan;
        if (!validTimestamp || !validPercentage || !validReset || !validPlan)
            throw new InvalidDataException(invalid);
        return new(observedAt, percentage, reset.ValueKind == JsonValueKind.Null ? null : resetsAt, expectedPlan);
    }

    private static void AtomicWrite<T>(string path, T data, long? maximumBytes = null)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                JsonSerializer.Serialize(stream, data, JsonOptions.Default);
                if (maximumBytes is { } limit && stream.Length > limit)
                    throw new InvalidDataException("Local history exceeds the supported size; the source file was preserved.");
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed record SettingsForEditing(WidgetSettings Settings, string? RecoveryMessage);
