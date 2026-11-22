namespace WorkspaceRepo;

public static class SelfTests
{
    public static async Task<int> Run(string outputRoot)
    {
        var root = Path.Combine(outputRoot, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(root);
        Console.WriteLine("Offline integration tests. Fixtures are preserved at " + root);
        var git = new Git("core.autocrlf=false", "core.hooksPath=" + Path.Combine(root, "no-hooks"), "commit.gpgsign=false"); var service = new RepositoryService(git); int count = 0;
        var origin = Path.Combine(root, "origin.git"); var seed = Path.Combine(root, "seed");
        void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); count++; Console.WriteLine("PASS: " + name); }
        async Task Run(string? path, params string[] args) => (await git.Run(path, args)).Require();
        async Task Commit(string path, string message) { await Run(path, "add", "."); await Run(path, "-c", "user.name=Repo Tests", "-c", "user.email=tests@example.invalid", "-c", "commit.gpgsign=false", "commit", "-m", message); }
        async Task Advance(string filename, string content)
        {
            File.WriteAllText(Path.Combine(seed, filename), content); await Commit(seed, "Update " + filename); await Run(seed, "push", "origin", "main");
        }
        await Run(null, "init", "--bare", "--initial-branch=main", origin);
        await Run(null, "init", "--initial-branch=main", seed);
        File.WriteAllText(Path.Combine(seed, "README.txt"), "base\n");
        await Commit(seed, "Initial"); await Run(seed, "remote", "add", "origin", origin); await Run(seed, "push", "-u", "origin", "main");
        var workspace = Path.Combine(root, "workspace");
        var repo = new Repository("Sample", origin, "Group/Sample");
        var path = Safety.Destination(workspace, repo.RelativePath);
        Check((await service.Inspect(repo, workspace)).Status.StartsWith("Missing"), "missing repository detected");
        Check((await service.Sync(repo, workspace, "pull", false)).Success, "clone into preserved relative structure");
        Check((await service.Sync(repo with { RelativePath = "Group/Recursive" }, workspace, "pull", true)).Success,
            "a chosen local catalog remote clones when submodule initialization is enabled");
        var submoduleStart = GitExecution.CreateStartInfo(path, ["submodule", "update"], network: true, allowLocalTransport: false);
        Check(!submoduleStart.Environment["GIT_ALLOW_PROTOCOL"]!.Split(':').Contains("file"),
            "recursive submodule initialization excludes the local file transport");
        var configuredDirectory = Environment.GetEnvironmentVariable("GIT_DIR");
        try
        {
            Environment.SetEnvironmentVariable("GIT_DIR", Path.Combine(seed, ".git"));
            var start = GitExecution.CreateStartInfo(path, ["status"], network: false);
            Check(Path.IsPathFullyQualified(start.FileName) && Path.IsPathFullyQualified(start.WorkingDirectory),
                "Git starts from an absolute executable in an absolute working directory");
            Check(!start.Environment.ContainsKey("GIT_DIR") && start.Environment["GIT_ALLOW_PROTOCOL"] == "" &&
                start.Environment["GIT_NO_LAZY_FETCH"] == "1" && start.Environment["GIT_NO_REPLACE_OBJECTS"] == "1",
                "local inspection clears alternate repositories and disables implicit network and replacement refs");
            Check(Safety.SamePath(await git.Value(path, "rev-parse", "--show-toplevel"), path),
                "catalog inspection identifies the requested repository when the shell selects another");
        }
        finally { Environment.SetEnvironmentVariable("GIT_DIR", configuredDirectory); }
        // These fixture settings have no active attribute rules or network service.
        await Run(path, "config", "filter.review.clean", "manual-only");
        Check((await service.Inspect(repo, workspace)).Blocked.Contains("repository-local executable filter", StringComparison.Ordinal),
            "catalog status blocks repository-local executable filters");
        await Run(path, "config", "--unset", "filter.review.clean");
        await Run(path, "config", "credential.helper", "manual-only");
        Check((await git.Run(path, "rev-parse", "HEAD")).Ok &&
            (await service.Fetch(path)).Error.Contains("repository-local transport", StringComparison.Ordinal),
            "read-only metadata remains available when repository-local transport blocks fetch");
        await Run(path, "config", "--unset", "credential.helper");
        await Run(path, "config", "remote.origin.fetch", "+refs/heads/*:refs/remotes/catalog-test/*");
        Check((await service.Fetch(path)).Ok &&
            !(await git.Run(path, "rev-parse", "--verify", "refs/remotes/catalog-test/main")).Ok,
            "catalog fetch updates canonical refs without applying repository-configured mappings");
        await Run(path, "config", "remote.origin.fetch", "+refs/heads/*:refs/remotes/origin/*");
        Check((await service.Inspect(repo, workspace)).Status == "Clean — pull", "cloned worktree clean");
        Check((await service.Sync(repo, workspace, "pull", false)).Message == "Up to date", "unchanged repository stays up to date");
        await Advance("remote.txt", "remote one\n");
        Check((await service.Sync(repo, workspace, "pull", false)).Success && File.Exists(Path.Combine(path, "remote.txt")), "clean fast-forward update");
        File.WriteAllText(Path.Combine(path, "untracked.txt"), "private work\n");
        File.WriteAllText(Path.Combine(path, "staged.txt"), "staged work\n"); await Run(path, "add", "staged.txt");
        await Advance("next.txt", "next\n");
        var oldTracking = await git.Value(path, "rev-parse", "origin/main");
        Check(!(await service.Sync(repo, workspace, "skip", false)).Success && await git.Value(path, "rev-parse", "origin/main") == oldTracking, "skip leaves dirty repository and tracking refs untouched");
        var stashed = await service.Sync(repo, workspace, "stash", false);
        Check(stashed.Success && stashed.Stash.Length > 0, "stash, fast-forward and restore succeed");
        Check(File.ReadAllText(Path.Combine(path, "untracked.txt")) == "private work\n", "untracked work restored");
        Check((await git.Value(path, "diff", "--cached", "--name-only")).Contains("staged.txt"), "staged index restored");
        Check((await git.Value(path, "stash", "list", "--format=%H")).Contains(stashed.Stash), "recovery stash retained");
        await Commit(path, "Save local work"); await Run(path, "push", "origin", "main"); await Run(seed, "pull", "--ff-only");
        File.WriteAllText(Path.Combine(path, "README.txt"), "local conflicting edit\n");
        await Advance("README.txt", "remote conflicting edit\n");
        var refused = await service.Sync(repo, workspace, "pull", false);
        Check(!refused.Success && File.ReadAllText(Path.Combine(path, "README.txt")) == "local conflicting edit\n", "dirty pull refuses overlapping changes without losing edits");
        var conflict = await service.Sync(repo, workspace, "stash", false);
        Check(!conflict.Success && conflict.Stash.Length > 0, "stash restore conflict reports retained recovery object");
        Check((await service.Inspect(repo, workspace)).Blocked.Contains("Unresolved"), "conflicted worktree blocked");
        // Destructive resets below operate only on this freshly created disposable test fixture.
        await Run(path, "reset", "--hard", "origin/main");
        File.AppendAllText(Path.Combine(path, ".git", "info", "exclude"), "\nignored.txt\n");
        File.WriteAllText(Path.Combine(path, "ignored.txt"), "local ignored data\n");
        await Advance("ignored.txt", "tracked on server\n");
        Check(!(await service.Sync(repo, workspace, "pull", false)).Success && File.ReadAllText(Path.Combine(path, "ignored.txt")) == "local ignored data\n", "ignored local files protected from overwrite");
        File.Move(Path.Combine(path, "ignored.txt"), Path.Combine(root, "saved-ignored.txt"));
        Check((await service.Sync(repo, workspace, "pull", false)).Success, "update succeeds after ignored collision resolved");
        await Run(path, "checkout", "--detach");
        Check((await service.Inspect(repo, workspace)).Blocked.Contains("Detached"), "detached HEAD blocked");
        await Run(path, "checkout", "main");
        File.WriteAllText(Path.Combine(path, ".git", "MERGE_HEAD"), await git.Value(path, "rev-parse", "HEAD"));
        Check((await service.Inspect(repo, workspace)).Blocked.Contains("MERGE_HEAD"), "in-progress merge blocked");
        // Preserve the synthetic marker instead of deleting generated material.
        File.Move(Path.Combine(path, ".git", "MERGE_HEAD"), Path.Combine(root, "synthetic-MERGE_HEAD"));
        File.WriteAllText(Path.Combine(path, "local.txt"), "local branch commit\n"); await Commit(path, "Local ahead");
        Check((await service.Sync(repo, workspace, "pull", false)).Message.Contains("ahead"), "ahead branch never pushed automatically");
        await Advance("diverged.txt", "divergence\n");
        Check((await service.Sync(repo, workspace, "pull", false)).Message.Contains("Diverged"), "diverged branches not auto-merged or rebased");
        await Run(path, "reset", "--hard", "origin/main");
        await Run(path, "branch", "merged-local"); await Run(path, "branch", "merged-remote");
        await Run(path, "push", "origin", "merged-remote"); (await service.Fetch(path)).Require();
        var defaultBranch = await service.DefaultBranch(path);
        Check(defaultBranch == "main", "remote default branch verified");
        var local = await service.Candidates(path, false, defaultBranch);
        Check(local.Any(b => b.Name == "merged-local") && local.All(b => b.Name != "main"), "local cleanup excludes default and checked-out branches");
        Check((await service.DeleteBranch(path, local.Single(b => b.Name == "merged-local"), false)).Ok, "merged local branch deleted with Git safety guard");
        var remoteCandidates = await service.Candidates(path, true, defaultBranch);
        Check((await service.DeleteBranch(path, remoteCandidates.Single(b => b.Name == "merged-remote"), true)).Ok, "merged remote branch deleted using an explicit SHA lease");
        Check(!(await git.Value(path, "ls-remote", "--heads", "origin")).Contains("refs/heads/merged-remote"), "remote deletion reaches only selected branch");
        await Run(path, "branch", "lease-check"); await Run(path, "push", "origin", "lease-check"); (await service.Fetch(path)).Require();
        var leased = (await service.Candidates(path, true, defaultBranch)).Single(b => b.Name == "lease-check");
        await Run(seed, "checkout", "-b", "lease-check"); File.WriteAllText(Path.Combine(seed, "lease.txt"), "new server commit\n"); await Commit(seed, "New server commit"); await Run(seed, "push", "origin", "lease-check"); await Run(seed, "checkout", "main");
        Check(!(await service.DeleteBranch(path, leased, true)).Ok, "remote branch changed since selection is protected by lease");
        await Run(path, "checkout", "-b", "unmerged"); File.WriteAllText(Path.Combine(path, "unmerged.txt"), "unmerged\n"); await Commit(path, "Unmerged feature"); await Run(path, "checkout", "main");
        Check((await service.Candidates(path, false, defaultBranch)).All(b => b.Name != "unmerged"), "unmerged branches excluded");
        var linked = Path.Combine(root, "linked-worktree"); await Run(path, "worktree", "add", "-b", "busy-branch", linked);
        Check((await service.Candidates(path, false, defaultBranch)).All(b => b.Name != "busy-branch"), "branch used in another worktree excluded");
        await Run(path, "checkout", "-b", "gone"); await Run(path, "push", "-u", "origin", "gone"); await Run(seed, "push", "origin", "--delete", "gone");
        Check((await service.Sync(repo, workspace, "pull", false)).Message.Contains("deleted"), "deleted upstream detected after prune");
        await Run(path, "checkout", "main");
        var mismatch = new Repository("Mismatch", seed, repo.RelativePath);
        Check((await service.Inspect(mismatch, workspace)).Blocked.Contains("Origin differs"), "remote mismatch blocked");
        var occupied = new Repository("Occupied", origin, "occupied"); Directory.CreateDirectory(Path.Combine(workspace, "occupied"));
        Check((await service.Inspect(occupied, workspace)).Blocked.Contains("not a Git"), "non-Git folder never overwritten");
        foreach (var bad in new[] { "../escape", "a/../../escape", "a/CON", "a:stream", "a/..", "a/", "a. " })
        {
            bool rejected = false; try { Safety.Destination(workspace, bad); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "unsafe path rejected: " + bad);
        }
        bool collision = false; try { Safety.ValidateCatalog(new Catalog { Repositories = [repo, repo] }, workspace); } catch (InvalidDataException) { collision = true; }
        Check(collision, "duplicate destination rejected");
        bool credentials = false; try { Safety.ValidateUrl("https://secret@github.com/example/test.git"); } catch (InvalidDataException) { credentials = true; }
        Check(credentials, "credentials in catalog URL rejected");
        Check(Safety.SameRemote("git@github.com:owner/repo.git", "https://github.com/owner/repo"), "SSH and HTTPS origin forms normalized");
        Check(Safety.Redact("https://secret@github.com/example/repo.git").Contains("***"), "embedded URL credentials redacted");
        Console.WriteLine($"{count} assertions passed. Generated fixtures remain available for manual cleanup.");
        return 0;
    }
}
