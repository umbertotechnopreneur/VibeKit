#if DEBUG
using System;
using System.IO;
namespace WaterRipple;
internal static class SettingsChecks
{
    internal static int Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "WaterRipple-settings-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        try
        {
            using var store = new SettingsStore(path);
            var model = store.Load(); store.Attach(model);
            model.ApplyPreset(2); model.Strength = 7.5; model.Duration = 1.2; model.UseAcrylic = false;
            using var reader = new SettingsStore(path);
            var loaded = reader.Load();
            if (loaded.PresetIndex != 2 || loaded.Strength != 7.5 || loaded.Duration != 1.2 || loaded.UseAcrylic)
                throw new Exception("Immediate persistence did not preserve custom preset values.");
            model.Strength = double.NaN; model.CaptureFps = 999;
            if (!double.IsFinite(model.Strength) || model.CaptureFps != 60) throw new Exception("Invalid settings were not clamped.");
            File.WriteAllText(path, "invalid");
            reader.Load();
            if (File.ReadAllText(path) != "invalid") throw new Exception("Corrupt input was overwritten during load.");
            Console.WriteLine("PASS immediate ripple settings persistence, custom presets, clamps and corrupt input preservation");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
#endif
