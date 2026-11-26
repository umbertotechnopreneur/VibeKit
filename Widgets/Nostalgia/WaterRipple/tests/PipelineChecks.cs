using System.Text.Json;
using WaterRipple;

if (OperatingSystem.IsWindows() && args.Length == 2 && args[0] == "--recovery-worker")
{
    RecoveryCoordinationChecks.Worker(args[1]);
    return;
}

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
string directory = Path.Combine(Path.GetTempPath(), "ripple-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    Check(SettingsChecks.Run() == 0, "Legacy preference persistence failed.");
    SettingsPersistenceChecks.Run(directory);
    CloseSequenceChecks.Run(directory);
    InteropChecks.Run();
    if (OperatingSystem.IsWindows()) RecoveryCoordinationChecks.Run();
    PipelinePersistenceChecks.Run(directory);
    string path = Path.Combine(directory, "pipelines.json");
    var settings = new AppSettings();
    using var model = new PipelineModel(settings, path);
    model.Select(model.Items.First(p => p.Name == "Calm water"));
    Check(settings.Strength == 7 && settings.WaveSize == 220, "Immediate application or platform clamping failed.");
    settings.Strength = 8;
    Check(model.Selected is null, "Edited built-in pipeline was still selected.");
    model.Create("My <water>");
    var custom = model.Selected!;
    settings.Strength = 12;
    settings.LiquidEnabled = true;
    var filter = model.Catalog.Groups[1];
    model.ApplyFilterPreset(filter.Presets[2]);
    Check(settings.ChromaticSeparation == 45 && model.FilterPresetIndex(filter) == 3, "Shared filter preset failed.");
    Check(custom.Values["strength"].GetDouble() == 12 && custom.Values["liquid-enabled"].GetBoolean(), "Saved pipeline did not update live.");
    using var loaded = new PipelineModel(settings, path);
    Check(loaded.Selected?.Name == "My <water>" && loaded.Selected.Values["strength"].GetDouble() == 12, "Pipeline persistence or selection restore failed.");
    bool nameChanged = false;
    custom.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(Pipeline.Name)) nameChanged = true; };
    model.Rename(custom, "Water"); Check(nameChanged, "Rename did not notify the bound view."); model.Duplicate(custom);
    Check(model.Selected?.Name == "Water copy", "Duplicate failed.");
    int updates = 0;
    model.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(PipelineModel.Items)) updates++; };
    model.Delete(model.Selected!);
    Check(updates > 0, "Delete did not notify the library view.");
    Check(model.Selected is null && !model.Items.Any(p => p.Name == "Water copy"), "Delete failed.");
    var before = model.Items.Count;
    model.Create("Water"); Check(model.Error.Length > 0 && before == model.Items.Count, "Duplicate names were accepted.");
    string invalid = Path.Combine(directory, "invalid.json");
    foreach (var values in new[] { "{\"strength\":null}", "{\"strength\":true}", "{\"strength\":1000}", "{\"liquid-enabled\":1}", "{\"unknown\":2}" })
    {
        File.WriteAllText(invalid, "{\"version\":1,\"pipelines\":[{\"id\":\"test\",\"name\":\"Test\",\"values\":" + values + "}]}");
        model.Import(invalid);
        Check(model.Error.Length > 0 && before == model.Items.Count, "Malformed import changed the library.");
    }
    string exchange = Environment.GetEnvironmentVariable("RIPPLE_EXCHANGE") ?? Path.Combine(directory, "exchange.json");
    if (File.Exists(exchange))
    {
        updates = 0; model.Import(exchange);
        Check(updates > 0, "Import did not notify the library view.");
        Check(model.Error == "" && model.Items.Any(p => p.Name == "From Linux" && p.Values["rings"].GetDouble() == 4), "Linux document import failed: " + model.Error);
        var linux = model.Items.First(p => p.Name == "From Linux");
        model.Select(linux); settings.Strength = 10;
        Check(linux.Values["rings"].GetDouble() == 4, "Linux-only parameter was lost.");
    }
    model.Export(exchange); Check(model.Error == "", "Export failed.");
    File.WriteAllText(path, "broken");
    using var corrupt = new PipelineModel(settings, path);
    corrupt.Create("Safe");
    Check(corrupt.Error.Length > 0 && File.ReadAllText(path) == "broken", "Corrupt library was overwritten.");
    Console.WriteLine("PASS Windows model: live application, CRUD, persistence, validation, corrupt input and portable exchange");
}
finally { Directory.Delete(directory, true); }
