using System.Text.Json;

namespace WorkspaceRepo;

public sealed record IndexedRepo
{
    public required string Path { get; init; }
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : Path;
    public string RemoteUrl { get; init; } = "";
    public string Branch { get; init; } = "";
    public string HeadSha { get; init; } = "";
    public int Ahead { get; init; } = -1;
    public int Behind { get; init; } = -1;
    public int LocalBranches { get; init; }
    public int RemoteBranches { get; init; }
    public string BranchIssue { get; init; } = "";
    public IReadOnlyList<string> BranchIssues { get; init; } = [];
    public IReadOnlyList<string> LocalBranchRefs { get; init; } = [];
    public IReadOnlyList<string> RemoteBranchRefs { get; init; } = [];
    public IReadOnlyList<WorktreeChange> WorktreeChanges { get; init; } = [];
    public int WorktreeChangesOmitted { get; init; }
    public bool Dirty { get; init; }
    public string Health { get; init; } = "Unknown";
    public string Reason { get; init; } = "";
    public string LastCheckedUtc { get; init; } = "";
    public string RemoteVerifiedUtc { get; init; } = "";
    public string VerifiedRemote { get; init; } = "";
    public bool? VerifiedThisInspection { get; init; }
    public IReadOnlyList<BranchComparison> VerifiedBranches { get; init; } = [];
    public RemoteBranchPresence? RemoteBranchPresence { get; init; }
    public bool HasUnverifiedTracking { get; init; }
    public string MetadataSha256 { get; init; } = "";
    public string MetadataCrc32 { get; init; } = "";
    public IReadOnlyList<MetadataDigest> Files { get; init; } = [];
}

public sealed record MetadataDigest(string File, long Bytes, string Crc32, string Sha256);
public sealed record WorktreeChange(string Status, string Path);
/// <summary>A local branch compared with the exact tracking object returned by the remote.</summary>
public sealed record BranchComparison(string Branch, string Upstream, string LocalObject, string RemoteObject,
    int Ahead, int Behind, bool Gone = false);
/// <summary>Branch membership on the checked remote, independent of commit comparisons.</summary>
public sealed record RemoteBranchPresence(string Remote, string CheckedUtc, IReadOnlyList<string> LocalOnly,
    IReadOnlyList<string>? RemoteOnly);
public enum ScanPhase { Discovery, Inspection, Saving }

public sealed record ScanActivity(string Path, string Action, int Directories, int Repositories,
    int Completed, int Total, int? Percent, ScanPhase Phase = ScanPhase.Discovery,
    string? DiscoveredRepository = null, IndexedRepo? InspectedRepository = null);

/// <summary>One complete snapshot per scan root. An interrupted scan never replaces the previous snapshot.</summary>
public sealed partial class WorkspaceIndex
{
    readonly string databasePath;
    static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    static readonly HashSet<string> Omitted = new(Paths)
    {
        ".git", ".svn", ".hg", ".vs", ".nuget", ".cache", ".codex", ".venv",
        "node_modules", "obj", "__pycache__", "artifacts"
    };
    static readonly HashSet<string> WindowsOmitted = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "$RECYCLE.BIN", "System Volume Information", "WindowsApps", "Windows", "Program Files", "Program Files (x86)"
    };

    public WorkspaceIndex(string databasePath)
    {
        this.databasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(this.databasePath)!);
        using var db = new IndexDatabase(this.databasePath);
        db.Execute("CREATE TABLE IF NOT EXISTS repo_index_roots (root TEXT PRIMARY KEY, completed_utc TEXT NOT NULL)");
        db.Execute("CREATE TABLE IF NOT EXISTS repo_index_entries (root TEXT NOT NULL, path TEXT NOT NULL, snapshot TEXT NOT NULL, PRIMARY KEY(root,path))");
    }

    static string RootPath(string root)
    {
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("Choose an absolute scan folder.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }
    static string RootKey(string root) => OperatingSystem.IsWindows() ? RootPath(root).ToUpperInvariant() : RootPath(root);

    public bool HasSnapshot(string root)
    {
        using var db = new IndexDatabase(databasePath);
        return db.Query("SELECT root FROM repo_index_roots WHERE root=?", RootKey(root)).Count != 0;
    }

    public List<IndexedRepo> Load(string root)
    {
        using var db = new IndexDatabase(databasePath);
        var entries = db.Query("SELECT snapshot FROM repo_index_entries WHERE root=? ORDER BY path COLLATE NOCASE", RootKey(root));
        return entries.Select(s => JsonSerializer.Deserialize<IndexedRepo>(s)
            ?? throw new InvalidDataException("Invalid repository snapshot.")).ToList();
    }

    void Save(string root, IEnumerable<IndexedRepo> entries, CancellationToken token)
    {
        var key = RootKey(root);
        using var db = new IndexDatabase(databasePath);
        db.Execute("BEGIN IMMEDIATE");
        try
        {
            db.Execute("DELETE FROM repo_index_entries WHERE root=?", key);
            foreach (var item in entries)
            {
                token.ThrowIfCancellationRequested();
                db.Execute("INSERT INTO repo_index_entries(root,path,snapshot) VALUES (?,?,?)",
                    key, item.Path, JsonSerializer.Serialize(item));
            }
            db.Execute("INSERT OR REPLACE INTO repo_index_roots VALUES(?,?)", key, DateTime.UtcNow.ToString("O"));
            token.ThrowIfCancellationRequested();
            db.Execute("COMMIT");
        }
        catch
        {
            db.Execute("ROLLBACK");
            throw;
        }
    }

    public Task<List<IndexedRepo>> ScanAsync(string root, bool refreshRemotes,
        IProgress<ScanActivity>? progress, CancellationToken token) =>
        Task.Run(() => ScanCore(RootPath(root), refreshRemotes, progress, token), token);

    async Task<List<IndexedRepo>> ScanCore(string root, bool refresh, IProgress<ScanActivity>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var rootInfo = new DirectoryInfo(root);
        if (!rootInfo.Exists) throw new DirectoryNotFoundException(root);
        if (rootInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Choose the physical scan folder instead of a link or junction.");
        var previous = Load(root);
        var directories = new Stack<string>();
        var repos = new HashSet<string>(Paths);
        var inaccessible = new List<string>();
        int visited = 0;
        directories.Push(root);
        var throttle = System.Diagnostics.Stopwatch.StartNew();
        long nextDisplay = 0;
        void Report(string path, string action, bool force = false, string? discoveredRepository = null)
        {
            if (force || throttle.ElapsedMilliseconds >= nextDisplay)
            {
                progress?.Report(new(path, action, visited, repos.Count, 0, 0, null,
                    DiscoveredRepository: discoveredRepository));
                nextDisplay = throttle.ElapsedMilliseconds + 70;
            }
        }
        while (directories.TryPop(out var current))
        {
            token.ThrowIfCancellationRequested();
            visited++;
            Report(current, "Scanning folders");
            try
            {
                foreach (var entry in new DirectoryInfo(current).EnumerateFileSystemInfos())
                {
                    token.ThrowIfCancellationRequested();
                    if (Paths.Equals(entry.Name, ".git"))
                    {
                        if (!entry.Attributes.HasFlag(FileAttributes.ReparsePoint) && repos.Add(current))
                            Report(current, "Scanning folders", force: true, discoveredRepository: current);
                        continue;
                    }
                    if (entry is DirectoryInfo && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint) && !SkipDirectory(current, entry.Name))
                        directories.Push(entry.FullName);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (Paths.Equals(current, root)) throw;
                inaccessible.Add(current);
                Report(current, "Folder inaccessible", true);
            }
        }
        Report(root, "Discovery complete", true);
        var sorted = repos.Order(Paths).ToList();
        var rows = await InspectAll(sorted, previous, refresh, visited, progress, token).ConfigureAwait(false);
        // Preserve older observations in unreadable trees with an explicit stale label.
        foreach (var old in previous)
            if (!repos.Contains(old.Path) && inaccessible.Any(dir => Paths.Equals(dir, old.Path) ||
                old.Path.StartsWith(Path.TrimEndingDirectorySeparator(dir) + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
                rows.Add(old with { Health = "Unknown", VerifiedThisInspection = false, VerifiedBranches = [], RemoteBranchPresence = null,
                    Reason = "Folder inaccessible during this scan. Showing cached metadata." });
        progress?.Report(new(root, "Saving repository scan", visited, repos.Count, repos.Count, repos.Count, null, ScanPhase.Saving));
        Save(root, rows, token);
        progress?.Report(new(root, $"Index saved · {inaccessible.Count} inaccessible folders", visited, repos.Count,
            repos.Count, repos.Count, 100, ScanPhase.Saving));
        return rows;
    }

    static bool SkipDirectory(string parent, string name)
    {
        if (Omitted.Contains(name)) return true;
        if (OperatingSystem.IsWindows()) return WindowsOmitted.Contains(name);
        return parent == "/" && (name is "proc" or "sys" or "dev" or "run");
    }

    public Task<List<IndexedRepo>> RefreshAsync(string root, IProgress<ScanActivity>? progress, CancellationToken token) =>
        Task.Run(async () =>
        {
            var previous = Load(root);
            if (!HasSnapshot(root)) throw new InvalidOperationException("Scan this root before refreshing its remotes.");
            var rows = await InspectAll(previous.Select(r => r.Path).ToList(), previous, true, 0, progress, token).ConfigureAwait(false);
            Save(root, rows, token);
            return rows;
        }, token);

    internal async Task<List<IndexedRepo>> InspectModifiedAsync(string root, IEnumerable<string> changedPaths)
    {
        var previous = Load(root);
        var changed = changedPaths.ToHashSet(Paths);
        var targets = previous.Select(repo => repo.Path).Where(changed.Contains).ToList();
        if (targets.Count == 0) return previous;
        // A confirmed batch only invalidates observations for its own worktrees.
        var inspected = await InspectAll(targets, previous, false, 0, null, CancellationToken.None).ConfigureAwait(false);
        var latest = inspected.ToDictionary(repo => repo.Path, Paths);
        var rows = previous.Select(repo => latest.GetValueOrDefault(repo.Path, repo)).ToList();
        Save(root, rows, CancellationToken.None);
        return rows;
    }

    async Task<List<IndexedRepo>> InspectAll(List<string> paths, List<IndexedRepo> previous, bool refresh, int visited,
        IProgress<ScanActivity>? progress, CancellationToken token)
    {
        var known = previous.ToDictionary(r => r.Path, Paths);
        var rows = new IndexedRepo[paths.Count];
        int completed = 0;
        var progressGate = new object();
        var gitDirectories = new System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>(Paths);
        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, paths.Count), new ParallelOptions
            {
                CancellationToken = token, MaxDegreeOfParallelism = 6
            }, async (i, scanToken) =>
            {
                string path = paths[i];
                known.TryGetValue(path, out var old);
                void Activity(string action, int? percent) => Publish(action, percent, null);
                void Publish(string action, int? percent, IndexedRepo? inspected)
                {
                    if (progress == null) return;
                    lock (progressGate)
                        progress.Report(new(path, action, visited, paths.Count, Volatile.Read(ref completed), paths.Count, percent,
                            ScanPhase.Inspection, InspectedRepository: inspected));
                }
                Activity("Reading Git metadata", null);
                try { rows[i] = await Inspect(path, old, refresh, gitDirectories, Activity, scanToken).ConfigureAwait(false); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
                    or InvalidDataException or ArgumentException or System.Security.SecurityException or System.Security.Cryptography.CryptographicException)
                {
                    rows[i] = (old ?? new IndexedRepo { Path = path }) with
                    { Health = "Unknown", VerifiedThisInspection = false, VerifiedBranches = [], RemoteBranchPresence = null,
                        Reason = Redact(e.Message), LastCheckedUtc = DateTime.UtcNow.ToString("O") };
                }
                Interlocked.Increment(ref completed);
                Publish("Repository checked", null, rows[i]);
            }).ConfigureAwait(false);
        }
        finally
        {
            foreach (var gate in gitDirectories.Values) gate.Dispose();
        }
        return rows.ToList();
    }
}
