using System.Text.Json;
using System.Text.Json.Serialization;

namespace VibeKit.StudioControls.Core;

public static class SettingsStore
{
    public const int MaximumFileBytes = 512 * 1024;
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static T Load<T>(string path) where T : new()
    {
        if (!File.Exists(path)) return new T();
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumFileBytes) throw new InvalidDataException("Settings file is too large.");
            byte[] bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) throw new InvalidDataException("Settings changed while being read.");
            T settings = JsonSerializer.Deserialize<T>(bytes, JsonOptions) ?? throw new InvalidDataException("Settings must be an object.");
            if (settings is IValidatedSettings validated) validated.Validate();
            return settings;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or EndOfStreamException)
        {
            throw new InvalidDataException("Settings could not be loaded. The original file was left unchanged.", error);
        }
    }

    public static async Task SaveAsync<T>(string path, T settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings is IValidatedSettings validated) validated.Validate();
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);
        if (data.Length > MaximumFileBytes) throw new InvalidDataException("Settings file is too large.");
        string destination = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        string staged = Path.Combine(directory, $".studio-settings-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream stream = new(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staged, destination, overwrite: true);
        }
        finally
        {
            File.Delete(staged);
        }
    }
}
