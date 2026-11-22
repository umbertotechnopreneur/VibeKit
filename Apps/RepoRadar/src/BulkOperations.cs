using System.Text;

namespace WorkspaceRepo;

internal enum BulkAction { Prune, Pull, Commit, Push, CommitPush, Stash, StashPull }
internal sealed record BulkTarget(string Path, BulkAction Action, string Message = "", string? Remote = null,
    bool IncludeUntracked = false);
internal sealed record BulkPlan(IReadOnlyList<BulkPlanItem> Items);
internal sealed record BulkPlanItem(string Path, BulkAction Action, string Branch, string Remote, bool Ready,
    string Summary, IReadOnlyList<string> Details)
{
    internal BulkApproval? Approval { get; init; }
}
internal sealed record BulkResult(string Path, BulkAction Action, bool Success, bool Skipped, string Summary,
    string Stash = "");
internal sealed record BulkRef(string Name, string Oid);
internal sealed record BulkApproval(BulkTarget Target, string Directory, string CommonDirectory, string Branch,
    string Head, string Status, string Staged, string Remote, string Url, string Upstream, string RemoteTip,
    IReadOnlyList<BulkRef> Stale);

/// <summary>Previews selected actions and revalidates their checkout, input and destination before applying.</summary>
internal sealed class BulkOperations
{
    private static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly string[] Markers =
        ["MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "rebase-merge", "rebase-apply", "BISECT_LOG", "sequencer"];
    private static readonly TimeSpan ReviewTimeout = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan ApplyTimeout = TimeSpan.FromMinutes(2);
    private readonly string[] configuration;

    public BulkOperations(params string[] configuration) => this.configuration = configuration;

    public static string Label(BulkAction action) => action switch
    {
        BulkAction.Prune => "Prune tracking refs",
        BulkAction.Pull => "Pull (fast-forward)",
        BulkAction.Commit => "Commit staged files",
        BulkAction.Push => "Push only",
        BulkAction.CommitPush => "Commit → Push",
        BulkAction.Stash => "Stash only",
        BulkAction.StashPull => "Stash → Pull (keep stash)",
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    public async Task<BulkPlan> PlanAsync(IReadOnlyList<BulkTarget> targets, CancellationToken token)
    {
        var items = new List<BulkPlanItem>();
        foreach (var target in targets)
        {
            token.ThrowIfCancellationRequested();
            items.Add(await ReviewSafely(target, token));
        }
        BlockDuplicates(items, item => item.Approval!.Target.Path, Paths, "More than one action targets this checkout.");
        BlockDuplicates(items, item => IsPush(item.Action)
                ? CanonicalDestination(item.Approval!.Url) + "\n" + item.Approval.Upstream : null,
            StringComparer.Ordinal, "More than one checkout would push to this remote branch. Choose one.");
        BlockDuplicates(items, item => item.Action == BulkAction.Prune
                ? item.Approval!.CommonDirectory + "\n" + item.Remote : null,
            Paths, "Selected worktrees share these tracking refs. Choose one.");
        return new(items.AsReadOnly());
    }

    /// <summary>Stops between worktrees. Each started worktree reports its final outcome, including any preserved stash.</summary>
    public async Task<IReadOnlyList<BulkResult>> ApplyAsync(BulkPlan plan, CancellationToken stop,
        IProgress<BulkResult>? progress = null)
    {
        var results = new List<BulkResult>();
        foreach (var item in plan.Items)
        {
            BulkResult result;
            if (!item.Ready || item.Approval is not { } approved)
                result = new(item.Path, item.Action, false, true, item.Summary);
            else if (stop.IsCancellationRequested)
                result = new(item.Path, item.Action, false, true, "Stopped before applying this action.");
            else
            {
                BulkPlanItem current;
                try { current = await ReviewSafely(approved.Target, stop); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                {
                    current = Blocked(approved.Target, "Stopped before applying this action.");
                }
                result = !current.Ready || current.Approval is not { } observed || !SameApproval(approved, observed) ||
                         stop.IsCancellationRequested
                    ? new(item.Path, item.Action, false, true, stop.IsCancellationRequested
                        ? "Stopped before applying this action." : current.Ready
                            ? "Checkout or remote changed after review. Create a new plan."
                            : "Preflight changed: " + current.Summary)
                    : await ApplyItem(approved);
            }
            results.Add(result);
            progress?.Report(result);
        }
        return results.AsReadOnly();
    }

    private async Task<BulkPlanItem> ReviewSafely(BulkTarget target, CancellationToken token)
    {
        try { return await Review(target, token); }
        catch (Exception e) when (Expected(e)) { return Blocked(target, Safe(e.Message)); }
    }

    private async Task<BulkPlanItem> Review(BulkTarget target, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(target.Path)) return Blocked(target, "An absolute worktree path is required.");
        target = target with { Path = Safety.Full(target.Path) };
        Safety.NoLinks(target.Path);
        var path = target.Path;
        if (!Directory.Exists(path)) return Blocked(target, "Worktree path is missing.");
        string root = await Value(path, token, "rev-parse", "--show-toplevel");
        if (!Safety.SamePath(path, root)) return Blocked(target, "This path is not the root of the selected worktree.");
        string gitDirectory = Safety.Full(await Value(path, token, "rev-parse", "--absolute-git-dir"));
        string commonDirectory = Safety.Full(await Value(path, token, "rev-parse", "--path-format=absolute", "--git-common-dir"));
        Safety.NoLinks(gitDirectory);
        Safety.NoLinks(commonDirectory);

        var headResult = await Read(path, token, "rev-parse", "--verify", "HEAD");
        var branchResult = await Read(path, token, "symbolic-ref", "--quiet", "HEAD");
        string head = headResult.Ok ? headResult.Output.Trim() : "";
        string branchRef = branchResult.Ok ? branchResult.Output.Trim() : "";
        string branch = branchRef.StartsWith("refs/heads/", StringComparison.Ordinal) ? branchRef[11..] : "";
        if (target.Action != BulkAction.Prune && (!Oid(head) || branch.Length == 0))
            return Blocked(target, "Detached HEAD or unborn branch. Choose a branch first.", branch);

        bool worktreeAction = target.Action is not (BulkAction.Prune or BulkAction.Push);
        string status = "", staged = "";
        if (worktreeAction)
        {
            if (HasOperationInProgress(gitDirectory))
                return Blocked(target, "A Git operation is already in progress.", branch);
            status = await WorktreeStatus(path, token);
            if (StatusLines(status).Any(line => line.StartsWith("u ", StringComparison.Ordinal) || HasSubmoduleChanges(line)))
                return Blocked(target, "Unresolved conflict or submodule changes. Review manually.", branch);
        }

        var details = new List<string> { "Worktree: " + path };
        if (branch.Length > 0) details.Add("Branch: " + branch + " at " + head);
        if (target.Action is BulkAction.Commit or BulkAction.CommitPush)
        {
            if (string.IsNullOrWhiteSpace(target.Message) || target.Message.Contains('\0'))
                return Blocked(target, "Enter a commit message.", branch);
            staged = await StagedChanges(path, token);
            if (staged.Length == 0) return Blocked(target, "No staged changes.", branch);
            var signing = await Read(path, token, "config", "--bool", "--get", "commit.gpgsign");
            if (signing.Ok && signing.Output.Trim() == "true")
                return Blocked(target, "Commit signing is enabled. Commit in Git to sign interactively.", branch);
            if (signing.Code is not (0 or 1)) return Blocked(target, "Commit signing settings could not be read.", branch);
            if (!(await Read(path, token, "var", "GIT_AUTHOR_IDENT")).Ok ||
                !(await Read(path, token, "var", "GIT_COMMITTER_IDENT")).Ok)
                return Blocked(target, "Set a Git author and committer identity first.", branch);
            details.Add("Message: " + target.Message);
            details.AddRange(GitStagedPaths(staged).Select(file => "Staged: " + Display(file)));
        }

        if (target.Action == BulkAction.Pull && status.Length > 0)
            return Blocked(target, "Worktree has local changes. Use Stash → Pull or handle them manually.", branch);
        if (target.Action is BulkAction.Stash or BulkAction.StashPull)
        {
            var untracked = StatusLines(status).Where(line => line.StartsWith("? ", StringComparison.Ordinal)).ToArray();
            var tracked = StatusLines(status).Count(line => line.StartsWith("1 ", StringComparison.Ordinal));
            int included = tracked + (target.IncludeUntracked ? untracked.Length : 0);
            if (target.Action == BulkAction.Stash && included == 0)
                return Blocked(target, untracked.Length > 0 ? "Only untracked files. To stash them, select Include untracked." : "No local changes to stash.", branch);
            if (target.Action == BulkAction.StashPull && untracked.Length > 0 && !target.IncludeUntracked)
                return Blocked(target, "Untracked files would remain. Select Include untracked or handle them first.", branch);
            details.Add($"Stash tracked changes: {tracked}. Include untracked files: {(target.IncludeUntracked ? untracked.Length : 0)}.");
            details.Add("Any stash created remains available for manual application.");
        }

        string remote = "", url = "", upstream = "", remoteTip = "";
        IReadOnlyList<BulkRef> stale = Array.Empty<BulkRef>();
        if (target.Action is not (BulkAction.Commit or BulkAction.Stash))
        {
            var knownRemotes = Lines(await Value(path, token, "remote"));
            string[] tracking = branchRef.Length == 0 ? [] : (await Value(path, token, "for-each-ref",
                "--format=%(refname)%09%(upstream:remotename)%09%(upstream:remoteref)", branchRef))
                .Split('\n').Select(line => line.TrimEnd('\r'))
                .FirstOrDefault(line => line.StartsWith(branchRef + "\t", StringComparison.Ordinal))?.Split('\t') ?? [];
            string trackedRemote = tracking.Length == 3 ? tracking[1] : "";
            remote = target.Remote ?? (trackedRemote.Length > 0 ? trackedRemote : knownRemotes.Length == 1 ? knownRemotes[0] : "");
            if (remote.Length == 0 || remote[0] == '-' || !knownRemotes.Contains(remote, StringComparer.Ordinal))
                return Blocked(target, "Choose an explicit remote for this worktree.", branch);
            if (target.Action != BulkAction.Prune &&
                (trackedRemote != remote || tracking.Length != 3 || !Ref(tracking[2], "refs/heads/")))
                return Blocked(target, "The checked-out branch must track a branch on the selected remote.", branch, remote);
            upstream = target.Action == BulkAction.Prune ? "" : tracking[2];

            url = await ResolveUrl(path, remote, token);
            if (IsPush(target.Action) && url != await ResolveUrl(path, remote, token, push: true))
                return Blocked(target, "Fetch and push URLs differ. Verify this destination manually.", branch, remote);
            details.Add($"{remote}: {url}");
            var advertised = await Advertised(path, url, token);
            if (target.Action == BulkAction.Prune)
            {
                stale = await StaleRefs(path, remote, knownRemotes, advertised, token);
                if (stale.Count == 0) return Blocked(target, "No stale tracking refs for this remote.", branch, remote);
                details.AddRange(stale.Select(item => "Prune local tracking ref: " + item.Name + " at " + item.Oid));
            }
            else
            {
                if (!advertised.TryGetValue(upstream, out var advertisedTip))
                    return Blocked(target, "The upstream branch is missing on the remote.", branch, remote);
                remoteTip = advertisedTip;
                details.Add($"{branch} → {remote}/{upstream[11..]} at {remoteTip}");
                if (IsPush(target.Action))
                {
                    if (!await HasCommit(path, remoteTip, token))
                        return Blocked(target, "Fetch first so the outgoing commits can be verified.", branch, remote);
                    if (!await Ancestor(path, remoteTip, head, token))
                        return Blocked(target, "Push would not fast-forward the remote branch.", branch, remote);
                    if (head == remoteTip && target.Action == BulkAction.Push)
                        return Blocked(target, "Already up to date.", branch, remote);
                    string count = await Value(path, token, "rev-list", "--count", remoteTip + ".." + head);
                    details.Add("Existing outgoing commits: " + count + (target.Action == BulkAction.CommitPush ? " plus the new commit." : "."));
                }
                else if (head == remoteTip)
                {
                    if (target.Action == BulkAction.Pull || status.Length == 0)
                        return Blocked(target, "Already up to date.", branch, remote);
                    details.Add("Already up to date. Only the stash will be created.");
                }
                else if (await HasCommit(path, remoteTip, token))
                {
                    if (!await Ancestor(path, head, remoteTip, token))
                        return Blocked(target, "Pull would not fast-forward the local branch.", branch, remote);
                    details.Add("Fast-forward commits: " + await Value(path, token, "rev-list", "--count", head + ".." + remoteTip));
                }
                else
                {
                    details.Add("Fetch this verified branch and check fast-forward ancestry before changing the worktree.");
                }
            }
        }

        string summary = target.Action == BulkAction.Prune ? $"{stale.Count} local tracking refs to prune." : Label(target.Action);
        return new(target.Path, target.Action, branch, remote, true, summary, details.AsReadOnly())
        {
            Approval = new(target, gitDirectory, commonDirectory, branch, head, status, staged, remote, url, upstream,
                remoteTip, stale)
        };
    }

    private async Task<BulkResult> ApplyItem(BulkApproval item)
    {
        var target = item.Target;
        var path = target.Path;
        string stash = "", commit = "";
        try
        {
            // The prune transaction deletes only listed local refs with the exact approved original IDs.
            if (target.Action == BulkAction.Prune)
            {
                var input = new StringBuilder("start\n");
                foreach (var entry in item.Stale)
                    input.Append("option no-deref\ndelete ").Append(entry.Name).Append(' ').Append(entry.Oid).Append('\n');
                input.Append("prepare\ncommit\n");
                await Mutate(path, ["update-ref", "--stdin"], input.ToString());
                return Result(true, $"Pruned {item.Stale.Count} local tracking refs.");
            }

            if (target.Action is BulkAction.Pull or BulkAction.StashPull && item.Head != item.RemoteTip)
            {
                if (!await HasCommit(path, item.RemoteTip, CancellationToken.None))
                    await Mutate(path, ["fetch", "--no-tags", "--no-prune", "--no-write-fetch-head",
                        "--no-recurse-submodules", "--refmap=", "--", item.Url, item.Upstream]);
                await VerifyRemoteTip(item);
                if (!await HasCommit(path, item.RemoteTip, CancellationToken.None) ||
                    !await Ancestor(path, item.Head, item.RemoteTip, CancellationToken.None))
                    throw new InvalidOperationException("The verified update cannot fast-forward this branch.");
                await VerifyCheckout(item, item.Head, item.Status);
            }

            if (target.Action is BulkAction.Stash or BulkAction.StashPull && item.Status.Length > 0)
            {
                await VerifyCheckout(item, item.Head, item.Status);
                string title = "Repo Radar stash " + Guid.NewGuid().ToString("N");
                string[] args = target.IncludeUntracked
                    ? ["stash", "push", "--include-untracked", "--message", title]
                    : ["stash", "push", "--message", title];
                await Mutate(path, args);
                string log = await Value(path, CancellationToken.None, "reflog", "show", "--format=%H%x09%gs", "refs/stash");
                stash = Lines(log).Select(line => line.Split('\t', 2)).FirstOrDefault(fields =>
                    fields.Length == 2 && Oid(fields[0]) && fields[1].EndsWith(title, StringComparison.Ordinal))?[0] ?? "";
                if (stash.Length == 0) throw new InvalidOperationException("Git did not report the new stash. Inspect the stash list.");
            }

            if (target.Action is BulkAction.Pull or BulkAction.StashPull && item.Head != item.RemoteTip)
            {
                await VerifyCheckout(item, item.Head, "");
                await Mutate(path, ["-c", "merge.autostash=false", "merge", "--ff-only", "--no-overwrite-ignore",
                    "--no-edit", item.RemoteTip]);
            }

            if (target.Action is BulkAction.Commit or BulkAction.CommitPush)
            {
                await VerifyCheckout(item, item.Head, item.Status);
                if ((await StagedChanges(path, CancellationToken.None)) != item.Staged)
                    throw new InvalidOperationException("Staged contents changed. Create a new plan.");
                await Mutate(path, ["-c", "commit.cleanup=verbatim", "commit", "--message", target.Message]);
                commit = await Value(path, CancellationToken.None, "rev-parse", "--verify", "HEAD");
                if (!Oid(commit) || await Value(path, CancellationToken.None, "rev-parse", "--verify", commit + "^") != item.Head)
                    throw new InvalidOperationException("The new commit could not be verified. Inspect the branch.");
            }

            if (IsPush(target.Action))
            {
                string pushHead = commit.Length > 0 ? commit : item.Head;
                await VerifyCheckout(item, pushHead);
                await VerifyRemoteTip(item);
                if (!await Ancestor(path, item.RemoteTip, pushHead, CancellationToken.None))
                    throw new InvalidOperationException("Push would not fast-forward the remote branch.");
                await Mutate(path, ["push", "--porcelain", "--no-follow-tags", "--recurse-submodules=no", "--",
                    item.Url, pushHead + ":" + item.Upstream]);
            }

            return Result(true, target.Action switch
            {
                BulkAction.Stash => "Stash saved for manual application.",
                BulkAction.StashPull when item.Head == item.RemoteTip => "Already up to date. Stash saved for manual application.",
                BulkAction.StashPull when stash.Length > 0 => "Fast-forwarded. Stash saved for manual application.",
                BulkAction.StashPull or BulkAction.Pull => "Fast-forwarded.",
                BulkAction.Commit => "Committed " + Short(commit) + ".",
                BulkAction.CommitPush => "Committed and pushed " + Short(commit) + ".",
                BulkAction.Push => "Pushed " + Short(item.Head) + ".",
                _ => throw new ArgumentOutOfRangeException()
            });
        }
        catch (Exception e) when (Expected(e))
        {
            string retained = stash.Length > 0 ? "Stash retained. " : "";
            string committed = commit.Length > 0 && target.Action == BulkAction.CommitPush ? "Local commit " + Short(commit) + " retained. " : "";
            return Result(false, retained + committed + Safe(e.Message));
        }

        BulkResult Result(bool success, string summary) => new(path, target.Action, success, false, summary, stash);
    }

    private async Task VerifyCheckout(BulkApproval item, string expectedHead, string? expectedStatus = null)
    {
        string path = item.Target.Path;
        Safety.NoLinks(path);
        string root = await Value(path, CancellationToken.None, "rev-parse", "--show-toplevel");
        string directory = await Value(path, CancellationToken.None, "rev-parse", "--absolute-git-dir");
        if (!Safety.SamePath(path, root) || !Safety.SamePath(item.Directory, directory) ||
            await Value(path, CancellationToken.None, "symbolic-ref", "--quiet", "HEAD") != "refs/heads/" + item.Branch ||
            await Value(path, CancellationToken.None, "rev-parse", "--verify", "HEAD") != expectedHead ||
            expectedStatus != null && (HasOperationInProgress(directory) ||
                await WorktreeStatus(path, CancellationToken.None) != expectedStatus))
            throw new InvalidOperationException("The checkout changed after review. Create a new plan.");
    }

    private async Task VerifyRemoteTip(BulkApproval item)
    {
        string path = item.Target.Path;
        string branchRef = "refs/heads/" + item.Branch;
        string tracking = await Value(path, CancellationToken.None, "for-each-ref",
            "--format=%(refname)%09%(upstream:remotename)%09%(upstream:remoteref)", branchRef);
        if (!Lines(tracking).Contains($"{branchRef}\t{item.Remote}\t{item.Upstream}", StringComparer.Ordinal) ||
            await ResolveUrl(path, item.Remote, CancellationToken.None) != item.Url ||
            IsPush(item.Target.Action) && await ResolveUrl(path, item.Remote, CancellationToken.None, push: true) != item.Url ||
            !(await Advertised(path, item.Url, CancellationToken.None)).TryGetValue(item.Upstream, out string? tip) ||
            tip != item.RemoteTip)
            throw new InvalidOperationException("Remote destination or branch tip changed after review. Create a new plan.");
    }

    private async Task<IReadOnlyList<BulkRef>> StaleRefs(string path, string remote, IEnumerable<string> known,
        IReadOnlyDictionary<string, string> advertised, CancellationToken token)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var names = known.ToArray();
        string prefix = "refs/remotes/" + remote + "/";
        if (!Ref(prefix + "branch", "refs/remotes/") || names.Any(other => other != remote &&
            (other.Equals(remote, comparison) || remote.StartsWith(other + "/", comparison) || other.StartsWith(remote + "/", comparison))))
            throw new InvalidOperationException("Remote tracking namespaces overlap. Prune manually.");
        foreach (string name in names)
        {
            var mappings = await Read(path, token, "config", "--get-all", "remote." + name + ".fetch");
            string standard = "refs/heads/*:refs/remotes/" + name + "/*";
            if (mappings.Code == 1 && name != remote) continue;
            var entries = Lines(mappings.Output);
            if (!mappings.Ok || entries.Length != 1 || entries[0].TrimStart('+') != standard)
                throw new InvalidOperationException("Custom fetch mappings need a manual prune.");
        }
        string output = await Value(path, token, "for-each-ref", "--format=%(refname)%09%(objectname)%09%(symref)", prefix);
        var stale = new List<BulkRef>();
        foreach (string row in Lines(output))
        {
            var entry = row.Split('\t');
            if (entry.Length == 2) entry = [entry[0], entry[1], ""];
            if (entry.Length != 3 || !Ref(entry[0], prefix) || !Oid(entry[1]))
                throw new InvalidOperationException("Local tracking references could not be reviewed safely.");
            if (entry[2].Length == 0 && !advertised.ContainsKey("refs/heads/" + entry[0][prefix.Length..]))
                stale.Add(new(entry[0], entry[1]));
        }
        return stale.AsReadOnly();
    }

    private async Task<Dictionary<string, string>> Advertised(string path, string url, CancellationToken token)
    {
        string output = await Value(path, token, "ls-remote", "--refs", "--heads", "--", url);
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in Lines(output))
        {
            var parts = line.Split('\t');
            if (parts.Length != 2 || !Oid(parts[0]) || !Ref(parts[1], "refs/heads/") || !refs.TryAdd(parts[1], parts[0]))
                throw new InvalidOperationException("The remote branch listing could not be verified.");
        }
        return refs;
    }

    private async Task<string> ResolveUrl(string path, string remote, CancellationToken token, bool push = false)
    {
        var result = await Value(path, token, push ? ["remote", "get-url", "--push", "--all", "--", remote]
            : ["remote", "get-url", "--all", "--", remote]);
        var urls = Lines(result);
        if (urls.Length != 1) throw new InvalidOperationException("The selected remote must have one unambiguous URL.");
        Safety.ValidateUrl(urls[0]);
        return urls[0];
    }

    private async Task<string> WorktreeStatus(string path, CancellationToken token) =>
        (await Read(path, token, "status", "--porcelain=v2", "-z", "--no-renames", "--untracked-files=all",
            "--ignore-submodules=none")).RequireOutput();
    private async Task<string> StagedChanges(string path, CancellationToken token) =>
        (await Read(path, token, "diff", "--cached", "--raw", "-z", "--no-abbrev", "--no-renames", "--no-ext-diff",
            "--no-textconv", "--", ".")).RequireOutput();
    private async Task<bool> HasCommit(string path, string id, CancellationToken token) =>
        (await Read(path, token, "cat-file", "-e", id + "^{commit}")).Ok;
    private async Task<bool> Ancestor(string path, string a, string b, CancellationToken token)
    {
        var result = await Read(path, token, "merge-base", "--is-ancestor", a, b);
        if (result.Code > 1) result.Require();
        return result.Ok;
    }
    private Task<GitResult> Read(string path, CancellationToken token, params string[] args) =>
        GitExecution.Run(path, args, token, ReviewTimeout, trustedConfiguration: configuration);
    private async Task<string> Value(string path, CancellationToken token, params string[] args) =>
        (await Read(path, token, args)).RequireOutput().TrimEnd('\r', '\n');
    private async Task Mutate(string path, string[] args, string? input = null)
    {
        var result = await GitExecution.Run(path, args, CancellationToken.None, ApplyTimeout,
            trustedConfiguration: configuration, standardInput: input);
        result.Require();
    }

    private static BulkPlanItem Blocked(BulkTarget target, string reason, string branch = "", string remote = "") =>
        new(target.Path, target.Action, branch, remote, false, reason, ["Worktree: " + target.Path]);

    private static void BlockDuplicates(List<BulkPlanItem> items, Func<BulkPlanItem, string?> key,
        StringComparer comparer, string reason)
    {
        var duplicates = items.Select((item, i) => (Item: item, Index: i))
            .Where(pair => pair.Item.Ready).Select(pair => (pair.Index, Key: key(pair.Item)))
            .Where(pair => pair.Key != null).GroupBy(pair => pair.Key, comparer).Where(group => group.Count() > 1)
            .SelectMany(group => group).Select(pair => pair.Index).ToArray();
        foreach (int index in duplicates) items[index] = items[index] with { Ready = false, Summary = reason, Approval = null };
    }

    private static bool SameApproval(BulkApproval a, BulkApproval b) =>
        a with { Stale = b.Stale } == b && a.Stale.SequenceEqual(b.Stale);
    private static bool HasOperationInProgress(string directory) =>
        Markers.Any(marker => File.Exists(Path.Combine(directory, marker)) || Directory.Exists(Path.Combine(directory, marker)));
    private static bool HasSubmoduleChanges(string line) =>
        line.StartsWith("1 ", StringComparison.Ordinal) && line.Length > 8 && line[5] == 'S' && line.Substring(5, 4) != "S...";
    private static bool IsPush(BulkAction action) => action is BulkAction.Push or BulkAction.CommitPush;
    private static bool Oid(string value) => value.Length is 40 or 64 && value.All(char.IsAsciiHexDigit);
    private static bool Ref(string value, string prefix) => value.Length > prefix.Length && value.StartsWith(prefix, StringComparison.Ordinal) &&
        !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) && !value.Contains('\ufffd');
    private static bool Expected(Exception error) => error is IOException or InvalidOperationException or ArgumentException or
        UnauthorizedAccessException or System.ComponentModel.Win32Exception;
    private static string Short(string id) => id[..Math.Min(id.Length, 12)];
    private static string CanonicalDestination(string url) => Path.IsPathFullyQualified(url)
        ? OperatingSystem.IsWindows() ? Safety.Full(url).ToUpperInvariant() : Safety.Full(url)
        : Safety.CanonicalUrl(url);
    private static string Safe(string text)
    {
        string cleaned = string.Join(" ", Lines(Safety.Redact(text)).Where(line => line.Length > 0));
        return cleaned.Length == 0 ? "Git refused the action." : cleaned[..Math.Min(cleaned.Length, 400)];
    }
    private static string Display(string text) => new(text.Select(c => char.IsControl(c) ? '�' : c).ToArray());
    private static string[] Lines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToArray();
    private static string[] StatusLines(string output) => output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    private static IEnumerable<string> GitStagedPaths(string output) =>
        StatusLines(output).Where((_, index) => index % 2 == 1);
}

internal static class BulkGitResult
{
    internal static string RequireOutput(this GitResult result)
    {
        result.Require();
        return result.Output;
    }
}
