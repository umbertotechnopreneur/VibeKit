using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

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
    private readonly Func<Action, IDisposable>? scheduleSave;
    private string? expectedContents;
    private Checkpoint? pending;
    private IDisposable? saveTimer;
    private bool disposed;
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

    private sealed record Checkpoint(Pipeline[] Items, Pipeline? Selected, Dictionary<string, JsonElement> Settings,
        (Pipeline Item, string Name, Dictionary<string, JsonElement> Values)[] Entries);

    public PipelineModel(AppSettings settings, string? path = null, Func<Action, IDisposable>? scheduleSave = null)
    {
        this.settings = settings;
        this.path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VibeWare", "Ripple", "pipelines.json");
        var context = SynchronizationContext.Current;
        this.scheduleSave = scheduleSave ?? (context is null ? null : callback => new SaveDelay(context, callback));
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
                expectedContents = ReadContents(this.path);
                var document = Parse(expectedContents ?? throw new IOException("Pipeline library disappeared while loading."));
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
        applying = true;
        try
        {
            foreach (var pair in preset.Values)
            {
                var p = Catalog.Parameters.First(p => p.Key == pair.Key);
                Property(p).SetValue(settings, pair.Value.GetDouble() * p.WindowsScale);
            }
        }
        finally { applying = false; }
        if (selected is { IsBuiltIn: false }) selected.Values = Capture(selected.Values);
        else if (selected is not null && !Matches(selected)) selected = null;
        Save(); NotifySelection();
    });
    public void Select(Pipeline pipeline) => Run(() => Apply(pipeline));
    private void Apply(Pipeline pipeline)
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
    }
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
        Items.Add(copy); Apply(copy);
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
        if (disposed || applying || !Catalog.Parameters.Any(p => p.WindowsProperty == args.PropertyName)) return;
        if (selected is null || (selected.IsBuiltIn && Matches(selected))) return;
        pending ??= Snapshot() with { Settings = new(selected.Values) };
        if (!selected.IsBuiltIn) selected.Values = Capture(selected.Values);
        else selected = null;
        Error = "";
        saveTimer?.Dispose(); saveTimer = null;
        if (scheduleSave is null) Flush();
        else saveTimer = scheduleSave(() => { saveTimer = null; Flush(); });
        NotifySelection();
    }
    public bool Flush()
    {
        saveTimer?.Dispose(); saveTimer = null;
        if (pending is null) return true;
        var checkpoint = pending; pending = null;
        try { Save(); Error = ""; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { Restore(checkpoint); Error = ex.Message; NotifySelection(); RaisePropertyChanged(nameof(Items)); return false; }
        NotifySelection(); RaisePropertyChanged(nameof(Items)); return true;
    }
    private Checkpoint Snapshot() => new(Items.ToArray(), selected, Capture(null),
        Items.Select(p => (p, p.Name, new Dictionary<string, JsonElement>(p.Values))).ToArray());
    private void Restore(Checkpoint checkpoint)
    {
        applying = true;
        try
        {
            foreach (var (item, name, values) in checkpoint.Entries) { item.Name = name; item.Values = values; }
            Items.Clear(); foreach (var item in checkpoint.Items) Items.Add(item);
            selected = checkpoint.Selected;
            foreach (var p in Catalog.Parameters.Where(p => p.WindowsProperty is not null))
            {
                var value = checkpoint.Settings[p.Key];
                Property(p).SetValue(settings, p.IsBoolean ? (object)value.GetBoolean() : value.GetDouble() * p.WindowsScale);
            }
        }
        finally { applying = false; }
        NotifySelection();
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
        return Parse(ReadContents(source) ?? throw new FileNotFoundException("Pipeline library was not found."));
    }
    private static string? ReadContents(string source)
    {
        try
        {
            using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 512 * 1024) throw new InvalidDataException("Pipeline library exceeds 512 KiB.");
            using var reader = new StreamReader(stream);
            var contents = reader.ReadToEnd();
            if (System.Text.Encoding.UTF8.GetByteCount(contents) > 512 * 1024) throw new InvalidDataException("Pipeline library exceeds 512 KiB.");
            return contents;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    private Document Parse(string contents)
    {
        var document = JsonSerializer.Deserialize<Document>(contents, PipelineCatalog.Json);
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
        string full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using var locked = new FileStream(full + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (ReadContents(full) != expectedContents)
            throw new IOException("Pipeline library changed in another window. Reopen this panel before saving.");
        expectedContents = Write(full, new Document { SelectedId = selected?.Id, Pipelines = Items.Where(p => !p.IsBuiltIn).ToList() });
    }
    private static string Write(string destination, Document document)
    {
        string full = Path.GetFullPath(destination); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temporary = full + "." + Guid.NewGuid().ToString("N") + ".pending";
        string contents = JsonSerializer.Serialize(document, PipelineCatalog.Json);
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false), leaveOpen: true);
                writer.Write(contents); writer.Flush(); stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, full, true);
            return contents;
        }
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
        if (!Flush()) return;
        var checkpoint = Snapshot();
        try { Error = ""; action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { Restore(checkpoint); Error = ex.Message; }
        RaisePropertyChanged(nameof(Items));
    }
    public void Dispose()
    {
        if (disposed) return;
        Flush(); disposed = true;
        settings.PropertyChanged -= SettingsChanged;
    }
    private sealed class SaveDelay : IDisposable
    {
        private readonly Timer timer;
        private int cancelled;
        public SaveDelay(SynchronizationContext context, Action callback)
        {
            timer = new Timer(_ => context.Post(_ => Fire(callback), null), null, Timeout.Infinite, Timeout.Infinite);
            timer.Change(250, Timeout.Infinite);
        }
        private void Fire(Action callback)
        { if (Interlocked.Exchange(ref cancelled, 1) == 0) { timer.Dispose(); callback(); } }
        public void Dispose() { Interlocked.Exchange(ref cancelled, 1); timer.Dispose(); }
    }
}
