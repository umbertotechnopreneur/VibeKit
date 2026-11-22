global using static WorkspaceRepo.WorkspaceViewModel;

using System.Diagnostics;

namespace WorkspaceRepo;

internal enum WorkspaceTab { Dashboard, Repositories, Branches, Remotes }

internal sealed record WorkspaceTableRow(string Path, string Name, string Location, string Branch, string Status, string Counts);

/// <summary>Projects one observation into the shared ordering, filters and rows without invoking Git or the terminal.</summary>
internal sealed class WorkspaceViewModel
{
    static readonly string[] HealthOrder = ["Green", "Both", "Purple", "Push", "Gone", "Red", "Amber", "Unknown"];
    internal static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    public List<IndexedRepo> Scoped { get; private set; } = [];
    public List<IndexedRepo> Visible { get; private set; } = [];
    private HashSet<string> hiddenDuringScan = new(Paths);
    public IndexedRepo? Latest { get; private set; }

    public void Update(IReadOnlyList<IndexedRepo> source, bool includeHidden, bool pinFirst,
        IEnumerable<string> hiddenPaths, IEnumerable<string> pinnedPaths, string search, string filter, int sort,
        IReadOnlySet<string>? selectedOnly = null)
    {
        var pins = pinnedPaths.Distinct(Paths).Select((path, i) => (path, i)).ToDictionary(p => p.path, p => p.i, Paths);
        hiddenDuringScan = includeHidden ? new(Paths) : hiddenPaths.ToHashSet(Paths);
        Scoped = source.Where(r => !hiddenDuringScan.Contains(r.Path)).ToList();
        Latest = Scoped.Count == 0 ? null : Scoped.MaxBy(r => r.LastCheckedUtc);
        var candidates = selectedOnly == null ? Scoped.Where(r => InFilter(r, filter) &&
            (search.Length == 0 || r.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             r.RemoteUrl.Contains(search, StringComparison.OrdinalIgnoreCase) || r.Path.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             r.BranchIssues.Any(issue => issue.Contains(search, StringComparison.OrdinalIgnoreCase)))) :
            source.Where(r => selectedOnly.Contains(r.Path));
        var ordered = candidates.OrderBy(r => filter == "Overview" || sort == 0 ? Array.IndexOf(HealthOrder, DisplayStatus(r)) : 0);
        if (pinFirst) ordered = ordered.ThenBy(r => pins.GetValueOrDefault(r.Path, int.MaxValue));
        ordered = sort switch
        {
            2 => ordered.ThenBy(r => r.RemoteUrl, StringComparer.OrdinalIgnoreCase),
            3 => ordered.ThenBy(r => r.Path, Paths),
            _ => ordered
        };
        Visible = ordered.ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Path, Paths).ToList();
    }

    public int Count(string category, WorkspaceScanSnapshot? scan = null)
    {
        if (scan == null) return Scoped.Count(r => InFilter(r, category));
        int observed = Scoped.Count(r => !scan.Inspected.ContainsKey(r.Path) && InFilter(r, category)) +
            scan.Inspected.Values.Count(r => !hiddenDuringScan.Contains(r.Path) && InFilter(r, category));
        if (category != "Unknown") return observed;
        var known = Scoped.Select(r => r.Path).ToHashSet(Paths);
        return observed + scan.Discovered.Count(p => !known.Contains(p) &&
            !scan.Inspected.ContainsKey(p) && !hiddenDuringScan.Contains(p));
    }

    public IReadOnlyList<WorkspaceTableRow> Rows(WorkspaceTab tab, WorkspaceScanSnapshot? scan = null)
    {
        if (scan == null) return Visible.Select(r => Project(r, tab)).ToArray();
        var found = scan.Discovered.Where(p => !hiddenDuringScan.Contains(p)).ToArray();
        var inspected = scan.Inspected;
        var existing = Scoped.ToDictionary(r => r.Path, Paths);
        var result = new List<WorkspaceTableRow>();
        foreach (var path in Visible.Select(r => r.Path).Concat(Scoped.Select(r => r.Path)).Concat(found).Distinct(Paths))
        {
            if (inspected.TryGetValue(path, out var repo) || existing.TryGetValue(path, out repo))
                result.Add(Project(repo, tab));
            else result.Add(new(path, Path.GetFileName(path), path, "—", "Discovered", "—"));
        }
        return result;
    }

    internal static string RemoteName(IndexedRepo repo)
    {
        string value = repo.RemoteUrl.Trim().Replace('\\', '/').TrimEnd('/');
        if (value.Length == 0) return repo.Name;
        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) value = value[..^4];
        if (Path.IsPathFullyQualified(repo.RemoteUrl) || value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return value[(value.LastIndexOf('/') + 1)..];
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "ssh" or "https" or "http" or "git")
            return uri.AbsolutePath.Trim('/');
        var colon = value.IndexOf(':');
        return colon > 0 ? value[(colon + 1)..] : value;
    }

    internal static bool InFilter(IndexedRepo r, string category)
    {
        var alignment = RepositoryAlignment.Of(r);
        return category switch
        {
            "Overview" => true,
            "Sync" => alignment.Push || alignment.Pull,
            "Red" => alignment.LocalEdits,
            "LocalChanges" => alignment.LocalEdits || alignment.Push,
            "ExtraBranches" => r.LocalBranches > 1 || r.RemoteBranches > 1,
            "RemoteChanges" => alignment.Pull,
            "Push" => alignment.Push,
            "Purple" => alignment.Pull,
            "Amber" => r.BranchIssues.Count > 0 || r.LocalBranches > 1 || r.RemoteBranches > 1,
            "Gone" => alignment.Gone,
            "Unknown" => alignment.NeedsVerification,
            "Green" => alignment.Aligned,
            _ => false
        };
    }

    // A verified checkout with extra branches is still aligned, but the visible badge is amber.
    internal static string DisplayStatus(IndexedRepo r)
    {
        var alignment = RepositoryAlignment.Of(r);
        return alignment.Aligned && (r.LocalBranches > 1 || r.RemoteBranches > 1) ? "Amber" : alignment.Status;
    }

    private static WorkspaceTableRow Project(IndexedRepo r, WorkspaceTab tab)
    {
        var alignment = RepositoryAlignment.Of(r);
        var delta = alignment.Deltas.FirstOrDefault();
        string status = DisplayStatus(r);
        if (tab == WorkspaceTab.Branches && status == "Unknown" && (r.LocalBranches > 1 || r.RemoteBranches > 1))
            status = "Amber";
        string location = tab switch
        {
            WorkspaceTab.Remotes => r.RemoteUrl.Length == 0 ? "—" : r.RemoteUrl,
            WorkspaceTab.Branches => r.BranchIssue.Length == 0 ? $"{r.LocalBranches} local · {r.RemoteBranches} remote" : r.BranchIssue,
            _ => r.Path
        };
        string counts = delta != null ? $"↑{delta.Ahead}  ↓{delta.Behind}" :
            alignment.CurrentComparison is { } current ? $"↑{current.Ahead}  ↓{current.Behind}" : "—";
        return new(r.Path, RemoteName(r), location, delta?.Branch ?? (r.Branch.Length == 0 ? "—" : r.Branch), status, counts);
    }
}

internal sealed record WorkspaceScanSnapshot(ScanPhase Phase, ScanActivity? Activity, int Frame,
    IReadOnlyList<string> Discovered, IReadOnlyDictionary<string, IndexedRepo> Inspected,
    string Title, string Caption, string Path, string Counts, string Footer, bool Cancelling);

/// <summary>Collects progress across worker threads and provides a consistent copy for each screen frame.</summary>
internal sealed class WorkspaceScanSession(string root, bool refresh) : IProgress<ScanActivity>
{
    private readonly object gate = new();
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly List<string> discovered = [];
    private readonly Dictionary<string, IndexedRepo> inspected = new(WorkspaceViewModel.Paths);
    private ScanActivity? activity;
    private bool cancelling;
    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(started);

    public void Report(ScanActivity value)
    {
        lock (gate)
        {
            activity = value;
            if (value.DiscoveredRepository is { } path) discovered.Add(path);
            if (value.InspectedRepository is { } repo) inspected[repo.Path] = repo;
        }
    }

    public void RequestCancel() { lock (gate) cancelling = true; }

    public WorkspaceScanSnapshot Capture()
    {
        lock (gate)
        {
            var phase = activity?.Phase ?? (refresh ? ScanPhase.Inspection : ScanPhase.Discovery);
            string label = cancelling ? "Cancelling" : phase switch
            {
                ScanPhase.Discovery => "Scanning",
                ScanPhase.Saving => "Saving observations",
                _ => refresh ? "Checking remotes" : "Checking repositories"
            };
            string path = activity?.Path ?? root;
            string title = phase switch
            {
                ScanPhase.Discovery => "Scanning repositories",
                ScanPhase.Saving => "Saving repository scan",
                _ => refresh ? "Checking remotes" : "Checking repositories"
            };
            string caption = phase == ScanPhase.Discovery ? "Searching for Git repositories under:" : activity?.Action ?? "Reading Git metadata";
            string counts = phase == ScanPhase.Discovery
                ? $"{activity?.Directories ?? 0:N0} folders visited  /  {activity?.Repositories ?? 0:N0} repositories found"
                : $"{activity?.Completed ?? 0:N0} / {activity?.Total ?? 0:N0} repositories checked";
            return new(phase, activity, (int)(Elapsed.TotalSeconds * 8), discovered.ToArray(),
                new Dictionary<string, IndexedRepo>(inspected, WorkspaceViewModel.Paths),
                title, caption, path, counts, label + " · " + path, cancelling);
        }
    }
}
