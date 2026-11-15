using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WaterRipple;

public sealed class PipelineParameter
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public JsonElement Default { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
    public double Step { get; set; }
    public string? WindowsProperty { get; set; }
    public string? WindowsTitle { get; set; }
    public double WindowsScale { get; set; } = 1;
    public double? WindowsMin { get; set; }
    public double? WindowsMax { get; set; }
    public bool IsBoolean => Default.ValueKind is JsonValueKind.True or JsonValueKind.False;
}
public sealed class PipelineFilterPreset
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public Dictionary<string, JsonElement> Values { get; set; } = new();
}
public sealed class PipelineGroup
{
    public string Title { get; set; } = "";
    public string? Enabled { get; set; }
    public string[] Keys { get; set; } = [];
    public PipelineFilterPreset[] Presets { get; set; } = [];
}
public sealed class Pipeline : ObservableModel
{
    public string Id { get; set; } = "";
    private string name = "";
    public string Name { get => name; set => SetProperty(ref name, value); }
    public Dictionary<string, JsonElement> Values { get; set; } = new();
    [JsonIgnore]
    public bool IsBuiltIn => Id.StartsWith("builtin:", StringComparison.Ordinal);
    public override string ToString() => Name;
}
public sealed class PipelineCatalog
{
    public int Version { get; set; }
    public PipelineParameter[] Parameters { get; set; } = [];
    public PipelineGroup[] Groups { get; set; } = [];
    public Pipeline[] Presets { get; set; } = [];
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    public static PipelineCatalog Load()
    {
        using var stream = typeof(PipelineCatalog).Assembly.GetManifestResourceStream("WaterRipple.pipeline-catalog.json")!;
        return JsonSerializer.Deserialize<PipelineCatalog>(stream, Json)!;
    }
}

// Owns pipeline selection, editing and persistence. Views only render this state and invoke actions.
public sealed class PipelineModel : ObservableModel, IDisposable
{
    private sealed class Document
    {
        public int Version { get; set; } = 1;
        public string? SelectedId { get; set; }
        public List<Pipeline> Pipelines { get; set; } = new();
    }
    private readonly AppSettings settings;
    private readonly string path;
    private bool applying, writable = true;
    private string error = "";
    private Pipeline? selected;
    public PipelineCatalog Catalog { get; } = PipelineCatalog.Load();
    public ObservableCollection<Pipeline> Items { get; } = new();
    public string Error { get => error; private set => SetProperty(ref error, value); }
    public Pipeline? Selected => selected;
    public string CurrentName => selected?.Name ?? "Custom";
    public string FilePath => path;
    public void ReportError(string message) => Error = message;

    public PipelineModel(AppSettings settings, string? path = null)
    {
        this.settings = settings;
        this.path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VibeWare", "Ripple", "pipelines.json");
        foreach (var preset in Catalog.Presets)
        {
            preset.Id = "builtin:" + preset.Id;
            preset.Values = Complete(preset.Values);
            Items.Add(preset);
        }
        if (File.Exists(this.path))
        {
            try
            {
                var document = Read(this.path);
                foreach (var pipeline in document.Pipelines) Items.Add(pipeline);
                selected = Items.FirstOrDefault(p => p.Id == document.SelectedId && Matches(p));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            { writable = false; Error = "Pipeline library could not be loaded. The source file was preserved: " + ex.Message; }
        }
        settings.PropertyChanged += SettingsChanged;
    }
    public string Summary(Pipeline pipeline) => string.Join(", ", Catalog.Groups.Where(g => g.Enabled is null || pipeline.Values[g.Enabled].GetBoolean()).Select(g => g.Title));
    public int FilterPresetIndex(PipelineGroup group) => Array.FindIndex(group.Presets, preset => preset.Values.All(pair =>
    {
        var p = Catalog.Parameters.First(p => p.Key == pair.Key);
        return Math.Abs((double)Property(p).GetValue(settings)! / p.WindowsScale - pair.Value.GetDouble()) < .001;
    })) + 1;
    public void ApplyFilterPreset(PipelineFilterPreset preset) => Run(() =>
    {
        foreach (var pair in preset.Values)
        {
            var p = Catalog.Parameters.First(p => p.Key == pair.Key);
            Property(p).SetValue(settings, pair.Value.GetDouble() * p.WindowsScale);
        }
    });
    public void Select(Pipeline pipeline) => Run(() =>
    {
        if (!Items.Contains(pipeline)) throw new InvalidDataException("Pipeline is unavailable.");
        applying = true;
        try
        {
            foreach (var parameter in Catalog.Parameters.Where(p => p.WindowsProperty is not null))
            {
                var value = pipeline.Values[parameter.Key];
                Property(parameter).SetValue(settings, parameter.IsBoolean ? (object)value.GetBoolean() : value.GetDouble() * parameter.WindowsScale);
            }
            selected = pipeline;
            NotifySelection();
            Save();
        }
        finally { applying = false; }
    });
    public void Create(string name) => Run(() =>
    {
        ValidateName(name);
        EnsureCapacity();
        var pipeline = new Pipeline { Id = Guid.NewGuid().ToString("N"), Name = name.Trim(), Values = Capture(selected?.Values) };
        Items.Add(pipeline); selected = pipeline; NotifySelection(); Save();
    });
    public void Duplicate(Pipeline pipeline) => Run(() =>
    {
        EnsureCapacity();
        string name = UniqueName(pipeline.Name + " copy");
        var copy = new Pipeline { Id = Guid.NewGuid().ToString("N"), Name = name, Values = new(pipeline.Values) };
        Items.Add(copy); Select(copy);
    });
    public void Rename(Pipeline pipeline, string name) => Run(() =>
    {
        Editable(pipeline); ValidateName(name, pipeline.Id); pipeline.Name = name.Trim();
        NotifySelection(); Save();
    });
    public void Delete(Pipeline pipeline) => Run(() =>
    {
        Editable(pipeline); Items.Remove(pipeline);
        if (selected == pipeline) { selected = null; NotifySelection(); }
        Save();
    });
    public void Import(string source) => Run(() =>
    {
        var document = Read(source);
        var additions = document.Pipelines.Select(p => new Pipeline { Id = Guid.NewGuid().ToString("N"), Name = p.Name, Values = p.Values }).ToList();
        if (Items.Count(p => !p.IsBuiltIn) + additions.Count > 64) throw new InvalidDataException("A library supports up to 64 saved pipelines.");
        foreach (var pipeline in additions) { pipeline.Name = UniqueName(pipeline.Name); Items.Add(pipeline); }
        Save();
    });
    public void Export(string destination) => Run(() => Write(destination, new Document { Pipelines = Items.Where(p => !p.IsBuiltIn).ToList() }));
    private void SettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (applying || !Catalog.Parameters.Any(p => p.WindowsProperty == args.PropertyName)) return;
        Run(() =>
        {
            if (selected is { IsBuiltIn: false }) { selected.Values = Capture(selected.Values); Save(); }
            else if (selected is not null && !Matches(selected)) { selected = null; Save(); }
            NotifySelection();
        });
    }
    private Dictionary<string, JsonElement> Capture(Dictionary<string, JsonElement>? previous)
    {
        var values = Complete(previous ?? new());
        foreach (var parameter in Catalog.Parameters.Where(p => p.WindowsProperty is not null))
        {
            object value = Property(parameter).GetValue(settings)!;
            values[parameter.Key] = parameter.IsBoolean ? JsonSerializer.SerializeToElement((bool)value) : JsonSerializer.SerializeToElement((double)value / parameter.WindowsScale);
        }
        return values;
    }
    private bool Matches(Pipeline pipeline)
    {
        foreach (var p in Catalog.Parameters.Where(p => p.WindowsProperty is not null))
        {
            var actual = Property(p).GetValue(settings)!;
            var stored = pipeline.Values[p.Key];
            if (p.IsBoolean) { if ((bool)actual != stored.GetBoolean()) return false; }
            else if (Math.Abs((double)actual / p.WindowsScale - Math.Clamp(stored.GetDouble(), p.WindowsMin ?? p.Min, p.WindowsMax ?? p.Max)) > .001) return false;
        }
        return true;
    }
    private static PropertyInfo Property(PipelineParameter p) => typeof(AppSettings).GetProperty(p.WindowsProperty!)!;
    private Dictionary<string, JsonElement> Complete(Dictionary<string, JsonElement> values) => Catalog.Parameters.ToDictionary(p => p.Key, p => values.GetValueOrDefault(p.Key, p.Default));
    private Document Read(string source)
    {
        if (new FileInfo(source).Length > 512 * 1024) throw new InvalidDataException("Pipeline library exceeds 512 KiB.");
        var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(source), PipelineCatalog.Json);
        if (document is null || document.Version != 1 || document.Pipelines is null || document.Pipelines.Count > 64) throw new InvalidDataException("Unsupported pipeline library.");
        var ids = new HashSet<string>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pipeline in document.Pipelines)
        {
            if (pipeline is null || string.IsNullOrWhiteSpace(pipeline.Id) || pipeline.Id.Length > 80 || pipeline.IsBuiltIn || !ids.Add(pipeline.Id) || string.IsNullOrWhiteSpace(pipeline.Name) || pipeline.Name.Length > 80 || !names.Add(pipeline.Name.Trim()) || pipeline.Values is null)
                throw new InvalidDataException("Invalid or duplicate pipeline name or identifier.");
            pipeline.Name = pipeline.Name.Trim();
            foreach (var pair in pipeline.Values)
            {
                var p = Catalog.Parameters.FirstOrDefault(p => p.Key == pair.Key) ?? throw new InvalidDataException("Unknown pipeline parameter: " + pair.Key);
                if (p.IsBoolean ? pair.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) : pair.Value.ValueKind != JsonValueKind.Number || !pair.Value.TryGetDouble(out var number) || !double.IsFinite(number) || number < Math.Min(p.Min, p.WindowsMin ?? p.Min) || number > Math.Max(p.Max, p.WindowsMax ?? p.Max))
                    throw new InvalidDataException("Invalid pipeline parameter: " + pair.Key);
            }
            pipeline.Values = Complete(pipeline.Values);
        }
        return document;
    }
    private void Save()
    {
        if (!writable) throw new IOException("Repair or move the unreadable pipeline library before saving.");
        Write(path, new Document { SelectedId = selected?.Id, Pipelines = Items.Where(p => !p.IsBuiltIn).ToList() });
    }
    private static void Write(string destination, Document document)
    {
        string full = Path.GetFullPath(destination); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temporary = full + ".pending";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(document, PipelineCatalog.Json)); File.Move(temporary, full, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private void ValidateName(string name, string? except = null)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80 || Items.Any(p => p.Id != except && p.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Choose a unique name of 1–80 characters.");
    }
    private string UniqueName(string name)
    {
        name = name[..Math.Min(name.Length, 70)]; string result = name; int suffix = 2;
        while (Items.Any(p => p.Name.Equals(result, StringComparison.OrdinalIgnoreCase))) result = name + " " + suffix++;
        return result;
    }
    private void EnsureCapacity() { if (Items.Count(p => !p.IsBuiltIn) >= 64) throw new InvalidDataException("A library supports up to 64 saved pipelines."); }
    private static void Editable(Pipeline pipeline) { if (pipeline.IsBuiltIn) throw new InvalidDataException("Duplicate a built-in pipeline to edit it."); }
    private void NotifySelection() { RaisePropertyChanged(nameof(Selected)); RaisePropertyChanged(nameof(CurrentName)); }
    private void Run(Action action)
    {
        try { Error = ""; action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { Error = ex.Message; }
        RaisePropertyChanged(nameof(Items));
    }
    public void Dispose() => settings.PropertyChanged -= SettingsChanged;
}
