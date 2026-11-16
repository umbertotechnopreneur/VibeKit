using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenAIUsage;

public sealed class Storage
{
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
        var settings = JsonSerializer.Deserialize<WidgetSettings>(File.ReadAllText(SettingsPath), JsonOptions.Default) ?? new();
        settings.Validate();
        return settings;
    }

    public void SaveSettings(WidgetSettings settings)
    {
        settings.Validate();
        AtomicWrite(SettingsPath, settings);
    }

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
                if (value.ValueKind != JsonValueKind.Object ||
                    !value.TryGetProperty("at", out var at) || at.ValueKind != JsonValueKind.Number || !at.TryGetInt64(out long seconds) || seconds is <= 0 or > MaximumTimestamp ||
                    !value.TryGetProperty("used", out var used) || used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out double percentage) || !double.IsFinite(percentage) || percentage is < 0 or > 100 ||
                    !value.TryGetProperty("reset", out var reset) || (reset.ValueKind != JsonValueKind.Null &&
                        (reset.ValueKind != JsonValueKind.Number || !reset.TryGetInt64(out long expiry) || expiry is <= 0 or > MaximumTimestamp)) ||
                    !value.TryGetProperty("plan", out var samplePlan) || samplePlan.ValueKind != JsonValueKind.String || samplePlan.GetString() != plan ||
                    !timestamps.Add(seconds))
                    throw new InvalidDataException("Local history contains an invalid observation; the source file was preserved.");
                result.Add(new(seconds, percentage, reset.ValueKind == JsonValueKind.Null ? null : reset.GetInt64(), plan));
            }
            return result;
        }
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
