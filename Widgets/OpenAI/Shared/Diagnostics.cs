using System.Text.Json;

namespace OpenAIUsage;

public static class Diagnostics
{
    private static readonly object Sync = new();
    public static string DirectoryPath => localDirectory ?? Path.Combine(new Storage().DirectoryPath, "logs");
    // Windows supplies a privacy-filtered Serilog writer. The core fallback is used by other hosts.
    private static Action<string, long?>? recorder;
    private static string? localDirectory;

    public static void UseRecorder(Action<string, long?> writer, string? logDirectory = null)
    {
        localDirectory = logDirectory;
        recorder = writer;
    }

    public static void Record(string kind, string message, long? bytes = null)
    {
        if (recorder is { } write)
        {
            write(kind, bytes);
            return;
        }
        WriteLocal(kind, message, bytes);
    }

    internal static void RecordMemoryLimit(long peakBytes, JobMemorySnapshot memorySnapshot)
    {
        const string kind = "guard.memory-limit";
        WriteLocal(kind, "The widget and its children exceeded 1 GB.", peakBytes, memorySnapshot);
        recorder?.Invoke(kind, peakBytes);
    }

    private static void WriteLocal(string kind, string message, long? bytes, JobMemorySnapshot? memorySnapshot = null)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(DirectoryPath);
                var path = Path.Combine(DirectoryPath, "app.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Move(path, path + ".1", true);
                var atUtc = DateTimeOffset.UtcNow;
                message = message.Length > 600 ? message[..600] : message;
                string line = memorySnapshot is null
                    ? JsonSerializer.Serialize(new { atUtc, kind, message, bytes }, JsonOptions.Compact)
                    : JsonSerializer.Serialize(new
                    {
                        atUtc, kind, message, bytes, limitBytes = WindowsMemoryGuard.LimitBytes, memorySnapshot
                    }, JsonOptions.Compact);
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
