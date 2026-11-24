using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VibeKit.StudioControls.Core;

public sealed record WorkflowStep(string Id, string Scene, int Seconds, [property: JsonRequired] string Note = "");

public sealed record Workflow(string Id, string Name, int? TargetSeconds, WorkflowStep[] Steps)
{
    internal void Validate()
    {
        WorkflowLibrary.CheckId(Id);
        WorkflowLibrary.CheckName(Name);
        if (TargetSeconds.HasValue) WorkflowLibrary.CheckSeconds(TargetSeconds.Value);
        ArgumentNullException.ThrowIfNull(Steps);
        var ids = new HashSet<string>();
        foreach (WorkflowStep step in Steps)
        {
            ArgumentNullException.ThrowIfNull(step);
            WorkflowLibrary.CheckId(step.Id);
            if (!ids.Add(step.Id) || !StudioCatalog.Default.Scenes.Any(s => s.Name == step.Scene))
                throw new ArgumentException("Invalid workflow step.");
            WorkflowLibrary.CheckSeconds(step.Seconds);
            if (step.Note is null || step.Note.Length > 500) throw new ArgumentException("Invalid step note.");
        }
    }
}

public sealed record WorkflowProject(string Id, string Name, Workflow[] Workflows);

public sealed record WorkflowLibrary(int Version, string ActiveProjectId, WorkflowProject[] Projects) : IValidatedSettings
{
    private static readonly JsonSerializerOptions LibraryJson = new(SettingsStore.JsonOptions)
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false
    };

    public const string DefaultProjectId = "00000000-0000-0000-0000-000000000000";
    // All revisions produced by a UI edit ('with') share the last successful disk revision.
    private sealed class DiskRevision { public byte[]? Hash; }
    private readonly DiskRevision revision = new();
    public static string NewId() => Guid.NewGuid().ToString("D");

    public static WorkflowLibrary Create(string name) => new(1, DefaultProjectId,
        [new(DefaultProjectId, name, [new(NewId(), "Workflow 1", 180, [])])]);

    internal static void CheckId(string id)
    {
        if (!Guid.TryParseExact(id, "D", out Guid parsed) || parsed.ToString("D") != id)
            throw new ArgumentException("Invalid workflow library identifier.");
    }

    internal static void CheckName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100) throw new ArgumentException("Invalid workflow or project name.");
    }

    internal static void CheckSeconds(int seconds)
    {
        if (seconds is < 1 or > 86400) throw new ArgumentOutOfRangeException(nameof(seconds));
    }

    public void Validate()
    {
        if (Version != 1 || Projects is null || Projects.Length == 0)
            throw new ArgumentException("Unsupported or invalid workflow library.");
        var projects = new HashSet<string>();
        foreach (WorkflowProject project in Projects)
        {
            ArgumentNullException.ThrowIfNull(project);
            CheckId(project.Id);
            CheckName(project.Name);
            if (!projects.Add(project.Id) || project.Workflows is not { Length: > 0 })
                throw new ArgumentException("Invalid project.");
            var workflows = new HashSet<string>();
            foreach (Workflow workflow in project.Workflows)
            {
                ArgumentNullException.ThrowIfNull(workflow);
                workflow.Validate();
                if (!workflows.Add(workflow.Id)) throw new ArgumentException("Duplicate workflow ID.");
            }
        }
        if (!projects.Contains(ActiveProjectId)) throw new ArgumentException("Unknown active project.");
    }

    private static byte[]? Read(string path)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > SettingsStore.MaximumFileBytes) throw new InvalidDataException("Workflow library is too large.");
            byte[] data = new byte[checked((int)stream.Length)];
            stream.ReadExactly(data);
            return data;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public static WorkflowLibrary Load(string path, string initialProjectName)
    {
        byte[]? data = Read(path);
        if (data is null) return Create(initialProjectName);
        try
        {
            var library = JsonSerializer.Deserialize<WorkflowLibrary>(data, LibraryJson)
                ?? throw new InvalidDataException("Empty workflow library.");
            library.Validate();
            library.revision.Hash = SHA256.HashData(data);
            return library;
        }
        catch (Exception error) when (error is ArgumentException or JsonException)
        {
            throw new InvalidDataException("Workflow library could not be loaded.", error);
        }
    }

    public async Task SaveAsync(string path, CancellationToken token = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using FileStream writerLock = new(path + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        byte[]? existing = Read(path);
        if (existing is null ? revision.Hash is not null :
            revision.Hash is null || !SHA256.HashData(existing).AsSpan().SequenceEqual(revision.Hash))
            throw new IOException("The workflow library changed on disk. Reopen before editing.");
        await SettingsStore.SaveAsync(path, this, token).ConfigureAwait(false);
        revision.Hash = SHA256.HashData(Read(path)!);
    }
}
