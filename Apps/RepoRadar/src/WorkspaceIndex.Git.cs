using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace WorkspaceRepo;

public sealed partial class WorkspaceIndex
{
    static string DisplayRemote(string raw)
    {
        if (raw.Any(char.IsControl)) return "";
        if (Path.IsPathFullyQualified(raw)) return Redact(raw);
        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "ssh" or "file")
        {
            if (uri.Scheme == "file") return uri.IsFile ? Redact(uri.LocalPath) : "";
            // Remove all embedded userinfo, query and fragment before persisting or rendering.
            return Redact(uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped));
        }
        return Regex.IsMatch(raw, @"^(?:[A-Za-z0-9_.-]+@)?[A-Za-z0-9_.-]+:[A-Za-z0-9_./~-]+$") ? raw : "";
    }
    static string Redact(string value)
    {
        value = Regex.Replace(value, @"(?i)(https?://)[^/\s@]+@", "$1");
        value = Regex.Replace(value, @"(?i)(ssh://)[^/\s@]+:[^/\s@]*@", "$1");
        value = Regex.Replace(value, @"(?i)(https?://[^\s?#]+)[?#][^\s]*", "$1");
        value = Regex.Replace(value, @"\b(gh[pousr]_|github_pat_)[A-Za-z0-9_]+\b", "***");
        return new string(value.Where(c => !char.IsControl(c)).ToArray());
    }

    static async Task<GitResult> RunGit(string path, IEnumerable<string> args, CancellationToken token,
        Action<string>? outputLine = null, Action<string>? errorLine = null, int timeoutSeconds = 120)
    {
        var result = await GitExecution.Run(path, args, token, TimeSpan.FromSeconds(timeoutSeconds), outputLine, errorLine)
            .ConfigureAwait(false);
        return result with { Output = result.Output.TrimEnd('\r', '\n'), Error = result.Error.TrimEnd('\r', '\n') };
    }

    static async Task<IndexedRepo> Inspect(string path, IndexedRepo? old, bool fetch, ConcurrentDictionary<string, SemaphoreSlim> gitDirectories,
        Action<string, int?> activity, CancellationToken token)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("The indexed folder is missing.");
        var top = await RunGit(path, ["rev-parse", "--show-toplevel"], token);
        if (top.Code != 0 || !Paths.Equals(RootPath(path), RootPath(top.Output)))
            throw new InvalidDataException("Git cannot open this working tree. Check its ownership and .git entry.");
        var policy = await GitExecution.ReadRepositoryPolicy(path, token);
        if (policy.LocalBlock.Length > 0) throw new InvalidDataException(policy.LocalBlock);
        bool hasNetworkHelper = policy.NetworkBlock.Length > 0;
        var branchResult = await RunGit(path, ["symbolic-ref", "--quiet", "HEAD"], token);
        var branch = branchResult.Code == 0 && branchResult.Output.StartsWith("refs/heads/", StringComparison.Ordinal)
            ? branchResult.Output[11..] : "";
        var foundRemote = branch.Length == 0 ? null : await RunGit(path, ["config", "--get", "branch." + branch + ".remote"], token);
        var remote = foundRemote is { Code: 0 } ? foundRemote.Output : "origin";
        string[] remotes = (await RunGit(path, ["remote"], token)).Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (foundRemote is not { Code: 0 } && !remotes.Contains("origin") && remotes.Length == 1) remote = remotes[0];
        // Ref names and CLI options must not be confused with an arbitrary URL or switch.
        var usableRemote = remotes.Contains(remote) && !remote.StartsWith('-') && !remote.Any(char.IsControl);
        var urlResult = usableRemote ? await RunGit(path, ["remote", "get-url", "--", remote], token) : null;
        var url = urlResult is { Code: 0 } ? DisplayRemote(urlResult.Output) : "";
        var unsupportedRemote = urlResult is { Code: 0 } && url.Length == 0;
        // Overlapping remote namespaces cannot be pruned independently with the canonical wildcard refspec.
        var refComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        bool overlappingRemote = usableRemote && remotes.Any(other => other != remote &&
            (other.Equals(remote, refComparison) || other.StartsWith(remote + "/", refComparison) ||
                remote.StartsWith(other + "/", refComparison)));
        var remoteVerified = old?.RemoteUrl == url && url.Length > 0 && old.VerifiedBranches.Count > 0 ? old.RemoteVerifiedUtc : "";
        var refreshFailed = fetch && (unsupportedRemote || hasNetworkHelper || overlappingRemote);
        Dictionary<string, string>? fetched = null;
        if (fetch && !refreshFailed && usableRemote && url.Length > 0)
        {
            var common = await RunGit(path, ["rev-parse", "--path-format=absolute", "--git-common-dir"], token);
            refreshFailed = common.Code != 0 || common.Output.Length == 0;
            if (!refreshFailed)
            {
                var gate = gitDirectories.GetOrAdd(RootPath(common.Output), _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(token);
                try
                {
                    activity("Git fetch · " + remote, null);
                    fetched = await FetchBranchTips(path, remote, activity, token);
                    refreshFailed = fetched == null;
                }
                catch (IOException) when (!token.IsCancellationRequested) { refreshFailed = true; }
                finally { gate.Release(); }
            }
        }
        activity("Git status · checking working tree", null);
        bool dirty = false, hasCounts = false;
        var changedFiles = new List<WorktreeChange>();
        int changesOmitted = 0;
        string head = "", upstream = "", observedBranch = "";
        int ahead = 0, behind = 0;
        var status = await RunGit(path, ["status", "--porcelain=v2", "--branch", "--ahead-behind", "--no-renames",
            "--untracked-files=normal", "--ignore-submodules=dirty"], token, line =>
        {
            if (line.StartsWith("# branch.oid ")) head = line[13..].Trim();
            else if (line.StartsWith("# branch.head ")) observedBranch = line[14..] is "(detached)" ? "" : line[14..];
            else if (line.StartsWith("# branch.upstream ")) upstream = line[18..].Trim();
            else if (line.StartsWith("# branch.ab "))
            {
                var terms = line[12..].Split(' ');
                hasCounts = terms.Length == 2 && int.TryParse(terms[0].TrimStart('+'), out ahead) &&
                    int.TryParse(terms[1].TrimStart('-'), out behind);
            }
            else if (line.Length > 1 && line[0] != '#')
            {
                dirty = true;
                if (changedFiles.Count >= 100) { changesOmitted++; return; }
                int fields = line[0] switch { '1' => 9, 'u' => 11, '?' => 2, _ => 0 };
                if (fields == 0) return;
                var entry = line.Split(' ', fields);
                if (entry.Length == fields) changedFiles.Add(new(fields == 2 ? "??" : entry[1], entry[^1]));
            }
        });
        if (status.Code != 0) throw new InvalidDataException("Git status failed. Check permissions or an active repository operation.");
        var inventory = new BranchInventory();
        var refs = await RunGit(path, ["for-each-ref",
            "--format=%(refname)%09%(symref)%09%(upstream)%09%(upstream:remotename)%09%(upstream:remoteref)%09%(upstream:track,nobracket)%09%(objectname)%09%(upstream:short)",
            "refs/heads", "refs/remotes"], token, inventory.Read);
        if (refs.Code != 0) throw new InvalidDataException("Git branch references could not be read.");
        bool checkoutChanged = branch != observedBranch;
        branch = observedBranch;
        var currentRef = inventory.Locals.FirstOrDefault(local => local.Name == branch);
        if (currentRef != null) checkoutChanged |= currentRef.ObjectId != head || currentRef.UpstreamShort != upstream;
        if (fetched != null)
        {
            var latestUrl = await RunGit(path, ["remote", "get-url", "--", remote], token);
            checkoutChanged |= latestUrl.Code != 0 || latestUrl.Output != urlResult?.Output;
        }
        List<BranchComparison> comparisons = fetched != null && !checkoutChanged
            ? await CompareBranches(path, branch, head, remote, fetched, inventory, token) : [];
        var active = comparisons.FirstOrDefault(comparison => comparison.Branch == branch && !comparison.Gone);
        if (fetch || checkoutChanged)
        {
            ahead = active?.Ahead ?? -1;
            behind = active?.Behind ?? -1;
            hasCounts = active != null;
        }
        bool exactFetch = fetched != null && !checkoutChanged;
        string checkedUtc = DateTime.UtcNow.ToString("O");
        if (exactFetch && comparisons.Count > 0) remoteVerified = checkedUtc;
        HashSet<string> configuredTracking = exactFetch && inventory.Locals.Any(local => local.Upstream.Length == 0)
            ? await ReadTrackingConfiguration(path, token) : [];
        var branchPresence = fetched != null && exactFetch
            ? inventory.ObservePresence(remote, checkedUtc, fetched, configuredTracking) : null;
        var issues = inventory.Describe(branch, comparisons, exactFetch, remotes, configuredTracking);
        activity("Fingerprinting Git metadata", null);
        var metadata = await Fingerprint(path, branch, upstream, token);
        var details = new List<string>();
        if (dirty) details.Add("Uncommitted or untracked files");
        if (ahead > 0) details.Add($"{ahead} commit(s) ahead");
        if (behind > 0) details.Add($"{behind} commit(s) behind {upstream}");
        if (issues.Summary.Length > 0) details.Add(issues.Summary);
        if (unsupportedRemote) details.Add("Unsupported remote transport");
        else if (url.Length == 0) details.Add("No tracked remote");
        if (branch.Length == 0) details.Add("Detached HEAD");
        else if (upstream.Length == 0) details.Add("No upstream for " + branch);
        else if (!hasCounts) details.Add("Exact upstream comparison unavailable");
        if (fetch && hasNetworkHelper) details.Add("Repository-local transport or credential helper. Refresh manually");
        else if (fetch && overlappingRemote) details.Add("Remote names share a tracking namespace. Refresh manually");
        else if (refreshFailed && !unsupportedRemote) details.Add("Fetch failed. Check network and Git authentication");
        else if (checkoutChanged) details.Add("Checkout or remote changed while checking. Refresh to compare");
        else if (url.Length > 0 && (!fetch || !usableRemote)) details.Add(remoteVerified.Length == 0 ? "Remote unverified. Refresh to compare" : "Using cached remote refs");
        var unknown = url.Length == 0 || branch.Length == 0 || !hasCounts || refreshFailed;
        var extra = inventory.HasExtras(remote);
        string health = dirty || ahead > 0 ? "Red" : refreshFailed ? "Unknown" : behind > 0 ? "Purple" :
            issues.MissingUpstream ? "Amber" : unknown ? "Unknown" : extra ? "Amber" : active != null ? "Green" : "Unknown";
        if (details.Count == 0) details.Add(branch + " matches " + upstream);
        return new IndexedRepo
        {
            Path = path, RemoteUrl = url, Branch = branch.Length == 0 ? "(detached)" : branch,
            HeadSha = head == "(initial)" ? "" : head, Ahead = hasCounts ? ahead : -1, Behind = hasCounts ? behind : -1, Dirty = dirty,
            LocalBranches = inventory.LocalCount, RemoteBranches = inventory.RemoteCount, Health = health,
            BranchIssue = issues.Summary, BranchIssues = issues.Details,
            LocalBranchRefs = inventory.LocalRefs, RemoteBranchRefs = inventory.RemoteRefs,
            WorktreeChanges = changedFiles, WorktreeChangesOmitted = changesOmitted,
            Reason = string.Join(" · ", details), LastCheckedUtc = DateTime.UtcNow.ToString("O"), RemoteVerifiedUtc = remoteVerified,
            VerifiedRemote = exactFetch ? remote : "", VerifiedThisInspection = exactFetch,
            VerifiedBranches = comparisons,
            RemoteBranchPresence = branchPresence,
            HasUnverifiedTracking = inventory.Locals.Count(b => b.Upstream.Length > 0 || configuredTracking.Contains(b.Name)) > comparisons.Count,
            Files = metadata.Files, MetadataCrc32 = metadata.Crc32, MetadataSha256 = metadata.Sha256
        };
    }
}
