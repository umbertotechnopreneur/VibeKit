using System.Text.Json;
using System.Text.RegularExpressions;

namespace WorkspaceRepo;

public sealed record Repository(string Name, string Url, string RelativePath);
public sealed class Catalog
{
    public int SchemaVersion { get; set; } = 1;
    public List<Repository> Repositories { get; set; } = [];
}
public sealed class Preferences
{
    public string Root { get; set; } = "";
    public List<string> SavedRoots { get; set; } = [];
    public List<string> SelectedPaths { get; set; } = [];
    public bool CloneSubmodules { get; set; } = true;
}
public static class Storage
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    public static T Read<T>(string path) where T : new() => File.Exists(path)
        ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Empty JSON document.") : new T();
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
        if (File.Exists(path)) File.Copy(path, path + ".backup-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff"));
        File.Move(temp, path, true);
    }
}
public static class Safety
{
    public static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    public static bool SamePath(string a, string b) => string.Equals(Full(a), Full(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    public static void NoLinks(string path)
    {
        for (DirectoryInfo? part = new(Full(path)); part != null; part = part.Parent)
            if (part.Exists && (part.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Symbolic links/junctions are not supported in workspace paths: {part.FullName}");
    }
    public static string Destination(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) throw new InvalidDataException("Choose an absolute workspace root.");
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new InvalidDataException("Repository folders must be relative.");
        var segments = relative.Replace('\\', '/').Split('/');
        if (segments.Any(s => string.IsNullOrWhiteSpace(s) || s is "." or ".." || s.EndsWith('.') || s.EndsWith(' ') || s.Any(c => char.IsControl(c) || "<>:\"|?*".Contains(c)) ||
            Regex.IsMatch(s, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$", RegexOptions.IgnoreCase)))
            throw new InvalidDataException("Unsafe relative folder in catalog.");
        var fullRoot = Full(root);
        var target = Full(Path.Combine(fullRoot, Path.Combine(segments)));
        if (!target.StartsWith(fullRoot + (Path.EndsInDirectorySeparator(fullRoot) ? "" : Path.DirectorySeparatorChar), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Repository folder escapes the workspace root.");
        NoLinks(target);
        return target;
    }
    public static void ValidateCatalog(Catalog catalog, string root)
    {
        if (catalog.SchemaVersion != 1 || catalog.Repositories == null) throw new InvalidDataException("Unsupported catalog schema.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var repo in catalog.Repositories)
        {
            if (repo == null || string.IsNullOrWhiteSpace(repo.Name) || repo.Name.Any(char.IsControl)) throw new InvalidDataException("Repository name is missing or invalid.");
            ValidateUrl(repo.Url);
            if (!seen.Add(Destination(root, repo.RelativePath))) throw new InvalidDataException("Two catalog entries target the same folder.");
        }
    }
    public static void ValidateUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Any(char.IsControl) || url.StartsWith('-')) throw new InvalidDataException("Invalid Git URL.");
        if (Regex.IsMatch(url, @"^git@[a-zA-Z0-9.-]+:[a-zA-Z0-9_./-]+$")) return;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "ssh" && !string.IsNullOrWhiteSpace(uri.Host) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) &&
            (uri.Scheme == "https" ? string.IsNullOrEmpty(uri.UserInfo) : uri.UserInfo == "git")) return;
        // Local remotes are useful for offline testing and LAN folder catalogs.
        if (Path.IsPathFullyQualified(url) && Directory.Exists(url)) return;
        throw new InvalidDataException("Use HTTPS or git@host SSH URLs without embedded credentials, or an existing absolute local repository path.");
    }
    public static string CanonicalUrl(string url)
    {
        url = url.Trim().TrimEnd('/');
        if (url.StartsWith("git@", StringComparison.Ordinal) && url.Contains(':')) url = "https://" + url[4..].Replace(':', '/');
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "ssh")
            url = uri.Host.ToLowerInvariant() + (uri.IsDefaultPort ? "" : ":" + uri.Port) + uri.AbsolutePath;
        return url.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? url[..^4] : url;
    }
    public static bool SameRemote(string a, string b) => string.Equals(CanonicalUrl(a), CanonicalUrl(b), StringComparison.Ordinal);
    public static string Redact(string text)
    {
        text = Regex.Replace(text, @"(https?://)[^\s/@]+@", "$1***@");
        text = Regex.Replace(text, @"(?i)([?&](?:token|access_token|key|password)=)[^\s&]+", "$1***");
        text = Regex.Replace(text, @"\b(?:gh[pousr]_[A-Za-z0-9_]+|github_pat_[A-Za-z0-9_]+)\b", "***");
        return new string(text.Where(c => !char.IsControl(c) || c is '\r' or '\n' or '\t').ToArray());
    }
}
public sealed record GitResult(int Code, string Output, string Error)
{
    public bool Ok => Code == 0;
    public string Detail => Safety.Redact((Output + "\n" + Error).Trim());
    public void Require() { if (!Ok) throw new InvalidOperationException(Detail); }
}
/// <summary>Adapts the shared Git execution policy to the workspace's cancellation and progress handling.</summary>
public sealed class Git(params string[] configuration)
{
    public static readonly CancellationTokenSource Stop = new();
    public async Task<GitResult> Run(string? directory, params string[] args) => await Run(directory, args, null);
    public Task<GitResult> Run(string? directory, IEnumerable<string> args, Action<string>? line)
    {
        Action<string>? report = line is null ? null : text => line(Safety.Redact(text));
        return GitExecution.Run(directory, args, Stop.Token, TimeSpan.FromMinutes(10), report, report, configuration);
    }
    public async Task<string> Value(string dir, params string[] args) { var r = await Run(dir, args); r.Require(); return r.Output.Trim(); }
    public async Task<bool> Ancestor(string dir, string a, string b)
    {
        var r = await Run(dir, "merge-base", "--is-ancestor", a, b);
        if (r.Code > 1) r.Require();
        return r.Ok;
    }
}
public sealed record RepoState(string Path, string Branch, string RemoteBranch, bool Dirty, string Blocked, string Status);
public sealed record SyncResult(string Name, bool Success, string Message, string Stash = "")
{
    public bool Skipped { get; init; }
}
public sealed record BranchCandidate(string Name, string Sha);
public sealed class RepositoryService(Git git)
{
    public async Task<RepoState> Inspect(Repository repo, string root)
    {
        var path = Safety.Destination(root, repo.RelativePath);
        if (File.Exists(path)) return new(path, "", "", false, "A file occupies the destination.", "Blocked");
        if (!Directory.Exists(path)) return new(path, "", "", false, "", "Missing — clone");
        if (!File.Exists(Path.Combine(path, ".git")) && !Directory.Exists(Path.Combine(path, ".git")))
            return new(path, "", "", false, "Existing folder is not a Git working tree; it will not be overwritten.", "Blocked");
        var top = await git.Run(path, "rev-parse", "--show-toplevel");
        if (!top.Ok || !Safety.SamePath(top.Output.Trim(), path)) return new(path, "", "", false, "Destination is not the repository root.", "Blocked");
        var origin = await git.Run(path, "remote", "get-url", "origin");
        if (!origin.Ok || !Safety.SameRemote(origin.Output.Trim(), repo.Url)) return new(path, "", "", false, "Origin differs from the catalog (or is missing).", "Blocked");
        foreach (var marker in new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "rebase-merge", "rebase-apply", "BISECT_LOG", "sequencer" })
        {
            var markerPath = await git.Value(path, "rev-parse", "--git-path", marker);
            if (!Path.IsPathFullyQualified(markerPath)) markerPath = Path.Combine(path, markerPath);
            if (File.Exists(markerPath) || Directory.Exists(markerPath)) return new(path, "", "", false, $"Git operation in progress: {marker}. Finish it manually.", "Blocked");
        }
        var statusResult = await git.Run(path, "status", "--porcelain=v2", "--untracked-files=normal", "--ignore-submodules=none");
        if (!statusResult.Ok) return new(path, "", "", false, statusResult.Detail, "Blocked");
        var status = statusResult.Output.Trim();
        var dirty = status.Length > 0;
        if (status.Split('\n').Any(l => l.StartsWith("u "))) return new(path, "", "", dirty, "Unresolved conflicts. Resolve them manually.", "Blocked");
        if (status.Split('\n').Where(l => l.StartsWith("1 ") || l.StartsWith("2 ")).Any(l => { var f = l.Split(' '); return f.Length > 2 && f[2].StartsWith('S') && f[2] != "S..."; }))
            return new(path, "", "", dirty, "Submodule changes require manual attention; a parent stash cannot protect them.", "Blocked");
        var branchResult = await git.Run(path, "symbolic-ref", "--quiet", "--short", "HEAD");
        if (!branchResult.Ok) return new(path, "", "", dirty, "Detached HEAD or unborn branch. Choose a branch manually.", "Blocked");
        var branch = branchResult.Output.Trim();
        var tracking = (await git.Value(path, "for-each-ref", "--format=%(upstream:remotename)|%(upstream:remoteref)", "refs/heads/" + branch)).Split('|');
        if (tracking.Length != 2 || tracking[0] != "origin" || !tracking[1].StartsWith("refs/heads/"))
            return new(path, branch, "", dirty, "No upstream on origin. Configure tracking manually.", "Blocked");
        return new(path, branch, tracking[1][11..], dirty, "", dirty ? "Local changes" : "Clean — pull");
    }
    /// <summary>Updates only the canonical origin tracking refs used by catalog operations.</summary>
    public Task<GitResult> Fetch(string path, Action<string>? progress = null) => git.Run(path,
        ["fetch", "--progress", "--atomic", "--prune", "--no-prune-tags", "--no-tags", "--no-recurse-submodules",
            "--refmap=", "origin", "+refs/heads/*:refs/remotes/origin/*"], progress);

    public async Task<SyncResult> Sync(Repository repo, string root, string dirtyAction, bool submodules,
        Action<string>? progress = null, Action<string>? stage = null)
    {
        stage?.Invoke("Inspecting local state");
        var s = await Inspect(repo, root);
        if (s.Blocked.Length > 0) return new(repo.Name, false, s.Blocked);
        if (s.Status.StartsWith("Missing"))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(s.Path)!);
            stage?.Invoke("Cloning");
            var cloned = await git.Run(null, ["clone", "--progress", "--origin", "origin", "--", repo.Url, s.Path], progress);
            if (!cloned.Ok) return new(repo.Name, false, "Clone failed; partial output preserved. " + cloned.Detail);
            if (!submodules) return new(repo.Name, true, "Cloned");
            stage?.Invoke("Initializing submodules");
            var initialized = await git.Run(s.Path, ["submodule", "update", "--init", "--recursive", "--progress"], progress);
            return new(repo.Name, initialized.Ok, initialized.Ok ? "Cloned" : "Cloned; submodule setup failed. " + initialized.Detail);
        }
        if (s.Dirty && dirtyAction == "skip") return new(repo.Name, false, "Skipped — local changes preserved.") { Skipped = true };
        stage?.Invoke("Fetching origin");
        var fetched = await Fetch(s.Path, progress);
        if (!fetched.Ok) return new(repo.Name, false, "Fetch failed. " + fetched.Detail);
        // Recheck after network I/O: another tool may have changed the branch or worktree.
        stage?.Invoke("Checking the worktree after fetch");
        var fresh = await Inspect(repo, root);
        if (fresh.Blocked.Length > 0 || fresh.Branch != s.Branch || fresh.RemoteBranch != s.RemoteBranch || (!s.Dirty && fresh.Dirty))
            return new(repo.Name, false, "Repository changed during preflight; run again. " + fresh.Blocked);
        var target = "refs/remotes/origin/" + fresh.RemoteBranch;
        var targetResult = await git.Run(s.Path, "rev-parse", "--verify", target);
        if (!targetResult.Ok) return new(repo.Name, false, "Upstream branch was deleted. Choose a new upstream manually.");
        var head = await git.Value(s.Path, "rev-parse", "HEAD"); var remoteHead = targetResult.Output.Trim();
        if (head == remoteHead) return new(repo.Name, true, fresh.Dirty ? "Up to date — local changes preserved" : "Up to date");
        if (await git.Ancestor(s.Path, remoteHead, head)) return new(repo.Name, true, "Local branch is ahead — no push performed");
        if (!await git.Ancestor(s.Path, head, remoteHead)) return new(repo.Name, false, "Diverged history (or shallow boundary). Resolve manually; no merge/rebase attempted.");
        string stash = "";
        if (fresh.Dirty && dirtyAction == "stash")
        {
            stage?.Invoke("Saving a recovery stash");
            var oldStash = await git.Run(s.Path, "rev-parse", "--verify", "refs/stash");
            var saved = await git.Run(s.Path, "stash", "push", "--include-untracked", "--message", "Repo recovery " + DateTime.UtcNow.ToString("O"));
            if (!saved.Ok) return new(repo.Name, false, "Stash failed. " + saved.Detail);
            stash = await git.Value(s.Path, "rev-parse", "refs/stash");
            if (oldStash.Ok && stash == oldStash.Output.Trim()) return new(repo.Name, false, "No new recovery stash was created; update stopped.");
            var afterStash = await Inspect(repo, root);
            if (afterStash.Dirty || afterStash.Blocked.Length > 0) return new(repo.Name, false, "Worktree is still dirty after stash. Update stopped; recovery stash retained.", stash);
            // The recovery stash belongs to the checkout approved before the update.
            if (afterStash.Branch != fresh.Branch || afterStash.RemoteBranch != fresh.RemoteBranch ||
                await git.Value(s.Path, "rev-parse", "HEAD") != head)
                return new(repo.Name, false, "Checkout changed after stash. Update stopped; recovery stash retained.", stash);
        }
        else if (fresh.Dirty && dirtyAction != "pull") return new(repo.Name, false, "Skipped — choose how to handle local changes.") { Skipped = true };
        try
        {
            stage?.Invoke("Fast-forwarding");
            var merged = await git.Run(s.Path, "-c", "merge.autostash=false", "merge", "--ff-only", "--no-overwrite-ignore", "--no-edit", remoteHead);
            if (stash.Length > 0)
            {
                stage?.Invoke("Restoring local changes");
                var restored = await git.Run(s.Path, "stash", "apply", "--index", stash);
                if (!restored.Ok) return new(repo.Name, false, "Recovery stash could not be restored cleanly. Resolve conflicts manually. Stash retained. " + restored.Detail, stash);
            }
            return new(repo.Name, merged.Ok, merged.Ok ? stash.Length > 0 ? "Pulled — changes restored; recovery stash retained" : "Pulled (fast-forward)" : "Pull refused; changes preserved. " + merged.Detail, stash);
        }
        catch (OperationCanceledException) when (stash.Length > 0)
        { throw new OperationCanceledException($"Cancelled in {s.Path}. Recovery stash {stash} is retained; inspect the worktree before restoring it."); }
        catch (Exception e) when (stash.Length > 0)
        { return new(repo.Name, false, "Update interrupted; recovery stash retained. " + e.Message, stash); }
    }
    public async Task<string> DefaultBranch(string path)
    {
        var r = await git.Run(path, "ls-remote", "--symref", "origin", "HEAD"); r.Require();
        var line = r.Output.Split('\n').FirstOrDefault(l => l.StartsWith("ref: refs/heads/") && l.TrimEnd().EndsWith("\tHEAD"));
        if (line == null) throw new InvalidOperationException("Remote default branch could not be verified; branch deletion is disabled.");
        return line.Split('\t')[0][16..].Trim();
    }
    public static bool Protected(string branch, string defaultBranch) => branch == defaultBranch || new[] { "main", "master", "develop", "development", "dev", "trunk", "production", "release" }.Contains(branch, StringComparer.OrdinalIgnoreCase) || branch.StartsWith("release/", StringComparison.OrdinalIgnoreCase);
    public async Task<HashSet<string>> CheckedOut(string path)
    {
        var list = await git.Value(path, "worktree", "list", "--porcelain");
        return list.Split('\n').Where(l => l.StartsWith("branch refs/heads/")).Select(l => l[18..].Trim()).ToHashSet(StringComparer.Ordinal);
    }
    public async Task<List<BranchCandidate>> Candidates(string path, bool remote, string defaultBranch)
    {
        var busy = await CheckedOut(path);
        var prefix = remote ? "refs/remotes/origin/" : "refs/heads/";
        var lines = (await git.Value(path, "for-each-ref", "--format=%(refname)|%(objectname)|%(symref)", prefix)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<BranchCandidate>();
        foreach (var line in lines)
        {
            var fields = line.Trim().Split('|');
            if (fields.Length != 3 || fields[2].Length > 0) continue;
            var name = fields[0][prefix.Length..];
            if (name == "HEAD" || Protected(name, defaultBranch) || busy.Contains(name)) continue;
            if (await git.Ancestor(path, fields[1], "refs/remotes/origin/" + defaultBranch)) result.Add(new(name, fields[1]));
        }
        return result;
    }
    public async Task<GitResult> DeleteBranch(string path, BranchCandidate candidate, bool remote)
    {
        if (remote)
        {
            var fetchUrl = await git.Value(path, "remote", "get-url", "origin");
            var pushUrls = (await git.Value(path, "remote", "get-url", "--push", "--all", "origin")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (pushUrls.Length != 1 || !Safety.SameRemote(pushUrls[0].Trim(), fetchUrl)) return new(1, "", "Push URL differs from fetch URL; remote deletion disabled.");
        }
        var defaultBranch = await DefaultBranch(path);
        if (Protected(candidate.Name, defaultBranch) || (await CheckedOut(path)).Contains(candidate.Name)) return new(1, "", "Protected or checked-out branch.");
        var candidates = await Candidates(path, remote, defaultBranch);
        if (!candidates.Contains(candidate)) return new(1, "", "Branch changed or is no longer eligible; refresh the list.");
        if (remote) return await git.Run(path, "push", "--force-with-lease=refs/heads/" + candidate.Name + ":" + candidate.Sha, "origin", ":refs/heads/" + candidate.Name);
        // -d adds Git's own merged-branch guard. Never use -D.
        return await git.Run(path, "branch", "-d", "--", candidate.Name);
    }
}
