using System.Text.Json;
using WaterRipple;

internal static class PipelinePersistenceChecks
{
    internal static void Run(string directory)
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        string path = Path.Combine(directory, "transactions.json");
        var settings = new AppSettings();
        using var model = new PipelineModel(settings, path);
        using var stale = new PipelineModel(new AppSettings(), path);
        model.Create("Saved"); var saved = model.Selected!;
        string original = File.ReadAllText(path);
        stale.Create("Must not overwrite");
        Check(stale.Error.Length > 0 && stale.Selected is null && !stale.Items.Any(p => p.Name == "Must not overwrite") &&
            File.ReadAllText(path) == original, "Stale first creation overwrote another model or failed to roll back.");
        string import = Path.Combine(directory, "transaction-import.json");
        File.WriteAllText(import, """{"version":1,"pipelines":[{"id":"fixture","name":"Imported","values":{}}]}""");
        using (var locked = new FileStream(path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            foreach (var action in new Action[] { () => model.Rename(saved, "Unsaved"), () => model.Delete(saved),
                () => model.Create("Unsaved"), () => model.Duplicate(saved), () => model.Import(import), () => model.Select(model.Items[0]) })
            {
                action();
                Check(model.Error.Length > 0 && model.Selected == saved && saved.Name == "Saved" &&
                    model.Items.Count == model.Catalog.Presets.Length + 1 && File.ReadAllText(path) == original &&
                    settings.Strength == saved.Values["strength"].GetDouble(), "Failed transaction changed memory, selection, settings or disk.");
            }
        }
        var slots = new List<Slot>();
        IDisposable Schedule(Action action) { var slot = new Slot(action); slots.Add(slot); return slot; }
        var deferredSettings = new AppSettings();
        using var deferred = new PipelineModel(deferredSettings, path, Schedule);
        for (int i = 0; i < 120; i++) deferredSettings.Strength = 1 + i / 100d;
        Check(File.ReadAllText(path) == original && slots.Count(slot => !slot.Cancelled) == 1 &&
            deferred.Selected!.Values["strength"].GetDouble() == 2.19, "Slider changes did not stay live with one pending save.");
        slots.Last().Fire();
        Check(File.ReadAllText(path) != original && deferred.Flush(), "Deferred save did not commit.");
        using (var locked = new FileStream(path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            deferredSettings.Strength = 8;
            Check(!deferred.Flush() && deferredSettings.Strength == 2.19 && deferred.Selected!.Values["strength"].GetDouble() == 2.19,
                "Deferred failure did not restore the last committed pipeline and settings.");
        }
        using (var concurrent = new PipelineModel(new AppSettings { Strength = 2.19 }, path))
        {
            deferredSettings.Strength = 9;
            concurrent.Rename(concurrent.Items.Last(), "External");
            string external = File.ReadAllText(path);
            Check(concurrent.Error == "" && !deferred.Flush() && File.ReadAllText(path) == external,
                "Deferred save overwrote an external change.");
        }
        using (var closing = new PipelineModel(new AppSettings { Strength = 2.19 }, path, Schedule))
        {
            closing.Select(closing.Items.Last());
            // No native dispatcher is required to verify the final write on disposal.
        }
        var closingSettings = new AppSettings();
        var final = new PipelineModel(closingSettings, path, Schedule);
        final.Select(final.Items.Last()); closingSettings.Strength = 6; final.Dispose();
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Check(document.RootElement.GetProperty("pipelines")[0].GetProperty("values").GetProperty("strength").GetDouble() == 6 &&
            slots.All(slot => slot.Cancelled), "Disposal did not flush and cancel the pending save.");
        Console.WriteLine("PASS C# pipeline conflicts, rollback, coalesced changes, deferred errors and final flush");
    }
    private sealed class Slot(Action callback) : IDisposable
    {
        internal bool Cancelled { get; private set; }
        internal void Fire() { if (!Cancelled) { Cancelled = true; callback(); } }
        public void Dispose() => Cancelled = true;
    }
}
