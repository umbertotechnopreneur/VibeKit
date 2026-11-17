using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VibeKit.StudioControls.Windows;

public sealed record Framing(double Zoom = 1, double X = 0, double Y = 0);
public sealed record CameraFraming(int Zoom, int Pan, int Tilt);
public sealed class PanelSettings
{
    public string Theme { get; set; } = "Light";
    public string Language { get; set; } = "en";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 4455;
    public string ProtectedPassword { get; set; } = "";
    public string CameraId { get; set; } = "";
    public Dictionary<string, Framing> ScreenPresets { get; set; } = new()
    {
        ["Overview"] = new(), ["Detail"] = new(2), ["Close-up"] = new(3)
    };
    public Dictionary<string, CameraFraming> CameraPresets { get; set; } = new();
}

public static class LocalSettings
{
    internal static string? VerificationDirectory { get; set; }
    public static string DirectoryPath => VerificationDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VibeKit", "StudioControls");
    public static string AssetsPath => Path.Combine(DirectoryPath, "Cards");
    public static string ProjectPath => Path.Combine(AssetsPath, "project.json");
    private static string SettingsPath => Path.Combine(DirectoryPath, "panel.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static PanelSettings Load(out string? warning)
    {
        warning = null;
        try
        {
            if (!File.Exists(SettingsPath)) return new();
            if (new FileInfo(SettingsPath).Length > 64 * 1024) throw new JsonException("Settings exceed the size limit.");
            var value = JsonSerializer.Deserialize<PanelSettings>(File.ReadAllText(SettingsPath))
                ?? throw new JsonException("Empty settings");
            if (value.Theme is not ("Light" or "Dark" or "System")) value.Theme = "Light";
            if (!VibeKit.StudioControls.Core.StudioTranslations.Languages.Any(item => item.Code == value.Language)) value.Language = "en";
            if (value.Port is < 1 or > 65535) value.Port = 4455;
            value.ScreenPresets ??= new PanelSettings().ScreenPresets;
            value.CameraPresets ??= new();
            foreach (string key in value.CameraPresets.Keys.ToArray())
                if (value.CameraPresets[key] is null) value.CameraPresets.Remove(key);
            foreach (var key in value.ScreenPresets.Keys.ToArray())
            {
                var frame = value.ScreenPresets[key];
                if (frame is null || !double.IsFinite(frame.Zoom) || !double.IsFinite(frame.X) || !double.IsFinite(frame.Y))
                    value.ScreenPresets.Remove(key);
                else value.ScreenPresets[key] = new(Math.Clamp(frame.Zoom, 1, 4), Math.Clamp(frame.X, -100, 100), Math.Clamp(frame.Y, -100, 100));
            }
            foreach (var pair in new PanelSettings().ScreenPresets) value.ScreenPresets.TryAdd(pair.Key, pair.Value);
            return value;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Diagnostics.Record("settings.load", error);
            warning = "Settings could not be read. Defaults are shown; the original file is preserved until you save.";
            return new();
        }
    }

    public static void Save(PanelSettings settings) => WriteAtomic(SettingsPath, JsonSerializer.SerializeToUtf8Bytes(settings, Options));

    public static string ProtectPassword(string password)
    {
        if (password.Length == 0) return "";
        byte[] bytes = Encoding.UTF8.GetBytes(password);
        try { return Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static string ReadPassword(PanelSettings settings)
    {
        if (string.IsNullOrEmpty(settings.ProtectedPassword)) return "";
        byte[] bytes = ProtectedData.Unprotect(Convert.FromBase64String(settings.ProtectedPassword), null, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static void WriteAtomic(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
