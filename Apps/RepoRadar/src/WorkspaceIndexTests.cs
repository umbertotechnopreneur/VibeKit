using System.Diagnostics;
using System.Security.Cryptography;

namespace WorkspaceRepo;

public static class WorkspaceIndexTests
{
    sealed class Reporter(Action<ScanActivity> report) : IProgress<ScanActivity>
    {
        public void Report(ScanActivity value) => report(value);
    }

    /// <summary>Exercises real Git and system SQLite, exclusively with a local bare remote.</summary>
    public static async Task<int> Run(string fixtureRoot)
    {
        var scratch = Path.Combine(fixtureRoot, "index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        int checks = 0;
        void Check(bool expected, string description)
        {
            if (!expected) throw new InvalidOperationException("Index test failed: " + description);
            Console.WriteLine("PASS: " + description);
            checks++;
        }
        async Task Git(string path, params string[] args)
        {
            var start = new ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var s in new[] { "-c", "user.name=Index Test", "-c", "user.email=test@example.invalid",
                "-c", "core.hooksPath=" + Path.Combine(scratch, "no-hooks"), "-c", "commit.gpgsign=false", "-C", path })
                start.ArgumentList.Add(s);
            foreach (var s in args) start.ArgumentList.Add(s);
            using var git = Process.Start(start)!;
            var error = git.StandardError.ReadToEndAsync();
            await Task.WhenAll(git.StandardOutput.ReadToEndAsync(), error, git.WaitForExitAsync());
            if (git.ExitCode != 0) throw new InvalidOperationException(await error);
        }
        var origin = Path.Combine(scratch, "origin.git");
        var root = Path.Combine(scratch, "workspace");
        var repo = Path.Combine(root, "project");
        var publisher = Path.Combine(scratch, "publisher");
        var nested = Path.Combine(repo, "nested");
        Directory.CreateDirectory(root);
        await Git(scratch, "init", "--bare", "-b", "main", origin);
        await Git(scratch, "clone", origin, publisher);
        await Git(publisher, "commit", "--allow-empty", "-m", "base");
        await Git(publisher, "push", "-u", "origin", "main");
        await Git(root, "clone", origin, repo);
        await Git(repo, "init", "-b", "main", nested);
        File.AppendAllText(Path.Combine(repo, ".git", "info", "exclude"), "\nnested/\n");
        var linked = Path.Combine(root, "linked");
        await Git(repo, "worktree", "add", "--detach", linked, "HEAD");
        var dbpath = Path.Combine(scratch, "index.sqlite");
        var index = new WorkspaceIndex(dbpath);
        var activities = new List<ScanActivity>();
        var progress = new Reporter(activities.Add);
        var rows = await index.ScanAsync(root, false, progress, default);
        var project = rows.Single(r => r.Path == repo);
        Check(rows.Count == 3 && rows.Any(r => r.Path == nested) && rows.Any(r => r.Path == linked), "nested Git roots and worktree gitfiles discovered");
        Check(project.Health == "Unknown" && project.RemoteVerifiedUtc.Length == 0, "initial local scan never claims remote freshness");
        Check(rows.Single(r => r.Path == nested) is { Ahead: -1, Behind: -1 }, "missing upstream is distinct from zero ahead/behind");
        Check(activities.Any(a => a.Total == 0 && a.Directories > 0) && activities.Any(a => a.Total == 3), "discovery and per-repo progress carry counts");
        var scanModel = new WorkspaceScanSession(root, refresh: false);
        var repositoryView = new WorkspaceViewModel();
        foreach (var report in activities.Where(a => a.Phase == ScanPhase.Discovery))
            scanModel.Report(report);
        var discovery = scanModel.Capture();
        var discoveryRows = repositoryView.Rows(WorkspaceTab.Repositories, discovery);
        Check(discovery.Phase == ScanPhase.Discovery && discoveryRows.Count == 3 && discoveryRows.All(r => r.Status == "Discovered"),
            "the first scan publishes real provisional rows with no inferred Git status");
        foreach (var report in activities.Where(a => a.Phase != ScanPhase.Discovery))
            scanModel.Report(report);
        var inspection = scanModel.Capture();
        Check(inspection.Phase == ScanPhase.Saving && inspection.Inspected.Count == 3 &&
            repositoryView.Rows(WorkspaceTab.Repositories, inspection).All(r => r.Status != "Discovered"),
            "inspected rows replace provisional rows before the snapshot is saved");
        Check(project.HeadSha.Length == 40 && project.MetadataSha256.Length == 64 && project.MetadataCrc32.Length == 8, "Git IDs and metadata fingerprints have distinct formats");
        Check(project.Files.Single(f => f.File == "HEAD").Sha256 == Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(repo, ".git", "HEAD")))), "per-file SHA-256 verified independently");
        Check(new WorkspaceIndex(dbpath).Load(root).Count == 3 && index.HasSnapshot(root), "SQLite snapshot survives reopen");
        project = (await index.RefreshAsync(root, progress, default)).Single(r => r.Path == repo);
        Check(project.Health == "Green" && project.Behind == 0 && project.RemoteVerifiedUtc.Length > 0, "verified main-only repository is green");
        var mainProof = index.Load(root).Single(r => r.Path == repo).VerifiedBranches.Single();
        Check(mainProof == new BranchComparison("main", "refs/remotes/origin/main", project.HeadSha, project.HeadSha, 0, 0),
            "saved alignment identifies the exact fetched ref and both object IDs");
        Check(RepositoryAlignment.Of(project with { VerifiedBranches = [] }) is { Aligned: false, NeedsVerification: true },
            "older snapshot without exact branch evidence does not show green");

        await Git(repo, "config", "remote.origin.fetch", "+refs/heads/*:refs/remotes/custom/*");
        await Git(repo, "update-ref", "refs/remotes/custom/main", "HEAD");
        await Git(publisher, "commit", "--allow-empty", "-m", "advance while configured tracking ref stays stale");
        await Git(publisher, "push");
        project = (await index.RefreshAsync(root, null, default)).Single(r => r.Path == repo);
        Check(project.Ahead == -1 && project.Behind == -1 && project.VerifiedBranches.Count == 0 &&
            RepositoryAlignment.Of(project) is { Aligned: false, Pull: false, NeedsVerification: true },
            "canonical fetch cannot verify a stale custom upstream that still looks aligned");
        await Git(repo, "config", "remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*");
        project = (await index.RefreshAsync(root, null, default)).Single(r => r.Path == repo);
        Check(project.Behind == 1 && RepositoryAlignment.Of(project) is { Pull: true, NeedsVerification: false },
            "rechecking the same checkout against the exact fetched ref finds the incoming commit");
        await Git(repo, "merge", "--ff-only", "origin/main");
        await Git(repo, "update-ref", "-d", "refs/remotes/custom/main");
        await Git(repo, "remote", "set-url", "origin", Path.Combine(scratch, "missing-remote.git"));
        project = (await index.RefreshAsync(root, null, default)).Single(r => r.Path == repo);
        Check(project.Reason.Contains("Fetch failed", StringComparison.Ordinal) && project.VerifiedBranches.Count == 0 && project.RemoteBranchPresence == null &&
            RepositoryAlignment.Of(project) is { Aligned: false, NeedsVerification: true }, "failed fetch never reuses earlier verification");
        await Git(repo, "remote", "set-url", "origin", origin);
        await Git(repo, "branch", "--track", "changed-during-check", "origin/main");
        bool switchedDuringCheck = false;
        project = (await index.RefreshAsync(root, new Reporter(activity =>
        {
            if (switchedDuringCheck || activity.Path != repo || activity.Action != "Git fetch · origin") return;
            Git(repo, "switch", "changed-during-check").GetAwaiter().GetResult();
            switchedDuringCheck = true;
        }), default)).Single(r => r.Path == repo);
        Check(switchedDuringCheck && project.Branch == "changed-during-check" && project.VerifiedBranches.Count == 0 &&
            RepositoryAlignment.Of(project) is { Aligned: false, NeedsVerification: true }, "a checkout change during fetch invalidates the observation");
        await Git(repo, "switch", "main");
        await Git(repo, "branch", "-d", "changed-during-check");

        await Git(repo, "branch", "extra");
        project = (await index.ScanAsync(root, false, null, default)).Single(r => r.Path == repo);
        Check(project.Health == "Amber" && project.BranchIssue.Contains("Local only: extra") && !project.BranchIssue.Contains("gone"), "local-only branch is named without implying a deleted upstream");
        Check(project.RemoteBranchPresence == null, "a local-only scan does not certify branch membership on a server");
        await Git(publisher, "push", "origin", "main:refs/heads/remote-extra");
        project = (await index.RefreshAsync(root, null, default)).Single(r => r.Path == repo);
        Check(project.Health == "Amber" && project.RemoteBranches == 2 && project.BranchIssue.Contains("Remote only: origin/remote-extra"), "extra remote branch is named and symbolic HEAD is excluded");
        Check(RepositoryAlignment.Of(project) is { Aligned: true, Push: false, Pull: false }, "an extra remote branch alone does not make a clean checkout out of sync");
        var presence = index.Load(root).Single(row => row.Path == repo).RemoteBranchPresence;
        Check(presence is { Remote: "origin", RemoteOnly: { } onlyOnRemote } &&
            presence.LocalOnly.SequenceEqual(["extra"]) && onlyOnRemote.SequenceEqual(["remote-extra"]),
            "the persisted branch inventory distinguishes exact local-only and remote-only branches");
        await Git(repo, "branch", "-d", "extra");
        await Git(repo, "switch", "-c", "my-feature", "--track", "origin/remote-extra");
        await Git(repo, "switch", "main");
        await Git(repo, "config", "--replace-all", "remote.origin.fetch", "+refs/heads/main:refs/remotes/origin/main");
        project = (await index.RefreshAsync(root, null, default)).Single(r => r.Path == repo);
        Check(project.BranchIssues.Contains("Tracking not verified: my-feature (no matching fetch mapping)") &&
            RepositoryAlignment.Of(project) is { Compared: true, Aligned: false, NeedsVerification: true },
            "a configured side branch outside the fetch mapping cannot silently become a local-only branch");
        Check(project.RemoteBranchPresence is { } unresolvedPresence && !unresolvedPresence.LocalOnly.Contains("my-feature") &&
            unresolvedPresence.RemoteOnly?.Contains("remote-extra") != true, "an unresolved upstream is never asserted to be local only or remote only");
        await Git(repo, "config", "--add", "remote.origin.fetch", "+refs/heads/remote-extra:refs/remotes/custom/remote-extra");
        await Git(repo, "update-ref", "refs/remotes/custom/remote-extra", "my-feature");
        project = (await index.RefreshAsync(root, null, default)).Single(r => r.Path == repo);
        Check(project.BranchIssues.Contains("Tracking not verified: my-feature → custom/remote-extra") &&
            RepositoryAlignment.Of(project) is { Compared: true, Aligned: false, NeedsVerification: true },
            "a custom secondary upstream remains unverified even if it agrees with a cached ref");
        await Git(repo, "config", "--replace-all", "remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*");
        await Git(repo, "update-ref", "-d", "refs/remotes/custom/remote-extra");
        await Git(repo, "switch", "my-feature");
        await Git(repo, "commit", "--allow-empty", "-m", "side branch ahead");
        await Git(repo, "switch", "main");
        project = (await index.ScanAsync(root, false, null, default)).Single(r => r.Path == repo);
        Check(project.BranchIssues.Contains("Branch drift: my-feature (ahead 1 vs origin/remote-extra)") && project.Ahead == 0,
            "inactive tracked branch drift is named without changing current-branch counters");
        Check(RepositoryAlignment.Of(project) is { Push: false, NeedsVerification: true }, "a cached secondary branch comparison does not claim verified outgoing commits");
        project = (await index.RefreshAsync(root, null, default)).Single(r => r.Path == repo);
        Check(RepositoryAlignment.Of(project) is { Push: true, Aligned: false } &&
            RepositoryAlignment.Of(project).Deltas.Single() == new BranchDelta("my-feature", 1, 0), "verified secondary drift identifies the actual branch to push");
        await Git(publisher, "push", "origin", "--delete", "remote-extra");
        project = (await index.RefreshAsync(root, null, default)).Single(r => r.Path == repo);
        Check(project.Health == "Amber" && project.BranchIssue.Contains("Upstream gone: my-feature → origin/remote-extra") && project.Branch == "main",
            "clean main flags a secondary branch whose tracked upstream disappeared");
        Check(RepositoryAlignment.Of(project) is { Gone: true, Aligned: false, Push: false, Pull: false }, "confirmed missing upstream is separate from commit differences");
        await Git(repo, "switch", "my-feature");
        project = (await index.ScanAsync(root, false, null, default)).Single(r => r.Path == repo);
        Check(project.Health == "Amber" && project.Ahead == -1 && project.BranchIssue.Contains("Upstream missing in cache: my-feature"),
            "missing active upstream is actionable and clearly marked cached");
        await Git(repo, "switch", "main");
        await Git(publisher, "commit", "--allow-empty", "-m", "remote change"); await Git(publisher, "push");
        project = (await index.RefreshAsync(root, null, default)).Single(r => r.Path == repo);
        Check(project.Health == "Purple" && project.Behind == 1 && project.RemoteBranches == 1 && project.BranchIssue.Contains("Upstream gone: my-feature"),
            "purple remote update retains secondary orphan detail after fetch");
        File.WriteAllText(Path.Combine(repo, "new.txt"), "local");
        project = (await index.ScanAsync(root, false, null, default)).Single(r => r.Path == repo);
        Check(project.Health == "Red" && project.Dirty && project.Behind == 1 && project.BranchIssue.Contains("Upstream missing in cache: my-feature"),
            "red worktree keeps remote delta and cached orphan detail");
        Check(RepositoryAlignment.Of(project) is { LocalEdits: true, Pull: false, Gone: false, NeedsVerification: true },
            "a local file edit and a cached remote observation are independent signals");
        await Git(repo, "add", "new.txt"); await Git(repo, "commit", "-m", "local commit");
        project = (await index.ScanAsync(root, false, null, default)).Single(r => r.Path == repo);
        Check(project.Health == "Red" && project.Ahead == 1 && project.Behind == 1, "diverged history keeps both counts");
        var beforeCancellation = project.LastCheckedUtc;
        using var cts = new CancellationTokenSource();
        try { await index.ScanAsync(root, false, new Reporter(_ => cts.Cancel()), cts.Token); } catch (OperationCanceledException) { }
        Check(index.Load(root).Single(r => r.Path == repo).LastCheckedUtc == beforeCancellation, "cancelled rescan preserves last completed snapshot");
        var empty = Path.Combine(scratch, "empty"); Directory.CreateDirectory(empty);
        Check((await index.ScanAsync(empty, false, null, default)).Count == 0 && index.HasSnapshot(empty) && index.Load(root).Count == 3, "empty roots and independent root snapshots persist");
        Directory.Move(nested, Path.Combine(scratch, "removed-from-scope"));
        Check((await index.ScanAsync(root, false, null, default)).Count == 2, "rescan removes entries that disappeared");
        await Git(repo, "remote", "set-url", "origin", "https://username:password@example.invalid/test.git?token=secret");
        project = (await index.ScanAsync(root, false, null, default)).Single(r => r.Path == repo);
        Check(project.RemoteUrl == "https://example.invalid/test.git" && index.Load(root).Single(r => r.Path == repo).RemoteUrl == project.RemoteUrl,
            "URL credentials and query parameters excluded from SQLite records");
        await Git(repo, "remote", "set-url", "origin", "custom::opaque-private-remote");
        project = (await index.RefreshAsync(root, null, default)).Single(r => r.Path == repo);
        Check(project.RemoteUrl == "" && project.Reason.Contains("Unsupported remote transport"), "unknown remote helpers skipped and their opaque arguments hidden");
        await Git(repo, "remote", "set-url", "origin", origin);
        await Git(repo, "config", "credential.helper", "manager");
        project = (await index.RefreshAsync(root, null, default)).Single(r => r.Path == repo);
        Check(project.Reason.Contains("Refresh manually"), "repository-local credential helpers are not invoked");
        await Git(repo, "config", "filter.test.clean", "cat");
        rows = await index.ScanAsync(root, false, null, default);
        Check(rows.Count == 2 && rows.Single(r => r.Path == repo).Reason.Contains("executable filter"), "external filters produce an unknown card while indexing continues");
        Console.WriteLine($"{checks} workspace index checks passed");
        return checks;
    }
}
