namespace WorkspaceRepo;

internal static class BulkOperationsTests
{
    public static async Task<int> Run(string outputRoot)
    {
        string directory = Path.Combine(outputRoot, "bulk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string remote = Path.Combine(directory, "remote.git");
        string seed = Path.Combine(directory, "seed");
        string first = Path.Combine(directory, "first");
        string second = Path.Combine(directory, "second");
        string[] configuration = ["user.name=Repo Radar Tests", "user.email=tests@example.invalid", "commit.gpgsign=false"];
        var git = new Git(configuration);
        var operations = new BulkOperations(configuration);
        int checks = 0;

        async Task Invoke(string path, params string[] args) => (await git.Run(path, args)).Require();
        async Task Commit(string path, string file, string content)
        {
            File.WriteAllText(Path.Combine(path, file), content);
            await Invoke(path, "add", "--", file);
            await Invoke(path, "commit", "--message", file);
        }
        void Check(bool value, string reason)
        {
            if (!value) throw new InvalidOperationException("Bulk operations: " + reason);
            checks++;
        }
        Task<BulkPlan> Plan(BulkAction action, string message = "", bool untracked = false) =>
            operations.PlanAsync([new(first, action, message, IncludeUntracked: untracked)], CancellationToken.None);
        async Task<BulkResult> Apply(BulkPlan plan) => (await operations.ApplyAsync(plan, CancellationToken.None)).Single();

        await Invoke(directory, "init", "--bare", "--initial-branch=main", remote);
        await Invoke(directory, "init", "--initial-branch=main", seed);
        await Commit(seed, "base.txt", "first commit");
        await Invoke(seed, "remote", "add", "origin", remote);
        await Invoke(seed, "push", "origin", "main");
        await Invoke(directory, "clone", "--", remote, first);
        await Invoke(directory, "clone", "--", remote, second);

        string original = await git.Value(first, "rev-parse", "HEAD");
        await Commit(seed, "incoming.txt", "incoming commit");
        await Invoke(seed, "push", "origin", "main");
        string incoming = await git.Value(seed, "rev-parse", "HEAD");
        Check(!(await git.Run(first, "cat-file", "-e", incoming)).Ok, "fresh pull fixture must start without the incoming object.");
        var pull = await Plan(BulkAction.Pull);
        Check(pull.Items.Single().Ready && pull.Items.Single().Details.Any(line => line.Contains(incoming)) &&
            !(await git.Run(first, "cat-file", "-e", incoming)).Ok &&
            await git.Value(first, "rev-parse", "HEAD") == original,
            "pull preview pins the incoming ID without obtaining objects or changing HEAD.");
        var pulled = await Apply(pull);
        Check(pulled.Success && await git.Value(first, "rev-parse", "HEAD") == incoming &&
            await git.Value(second, "rev-parse", "HEAD") == original &&
            await git.Value(first, "rev-parse", "refs/remotes/origin/main") == original &&
            !(await git.Run(first, "rev-parse", "--verify", "FETCH_HEAD")).Ok,
            "pull gets a new object then fast-forwards only the chosen checkout without writing tracking refs.");

        File.WriteAllText(Path.Combine(first, "loose.txt"), "untracked");
        Check(!(await Plan(BulkAction.Stash)).Items.Single().Ready, "untracked files require explicit inclusion.");
        var stashed = await Apply(await Plan(BulkAction.Stash, untracked: true));
        Check(stashed.Success && !string.IsNullOrEmpty(stashed.Stash) && !File.Exists(Path.Combine(first, "loose.txt")) &&
            await git.Value(first, "rev-parse", "refs/stash") == stashed.Stash &&
            await git.Value(first, "rev-parse", "HEAD") == incoming,
            "stash-only saves work for manual application and leaves HEAD untouched.");

        File.WriteAllText(Path.Combine(first, "base.txt"), "edited before pull");
        await Commit(seed, "next.txt", "second remote update");
        await Invoke(seed, "push", "origin", "main");
        string next = await git.Value(seed, "rev-parse", "HEAD");
        Check(!(await Plan(BulkAction.Pull)).Items.Single().Ready, "normal pull refuses a dirty worktree.");
        var stashPulled = await Apply(await Plan(BulkAction.StashPull));
        Check(stashPulled.Success && stashPulled.Stash.Length > 0 &&
            await git.Value(first, "rev-parse", "refs/stash") == stashPulled.Stash &&
            await git.Value(first, "rev-parse", "HEAD") == next &&
            File.ReadAllText(Path.Combine(first, "base.txt")) == "first commit",
            "stash and pull uses the approved fast-forward and retains the stash.");

        File.WriteAllText(Path.Combine(first, "staged.txt"), "first reviewed version");
        await Invoke(first, "add", "--", "staged.txt");
        var beforeEdit = await Plan(BulkAction.Commit, "Stage only");
        File.WriteAllText(Path.Combine(first, "staged.txt"), "new staging after approval");
        await Invoke(first, "add", "--", "staged.txt");
        var rejected = await Apply(beforeEdit);
        Check(rejected.Skipped && await git.Value(first, "rev-parse", "HEAD") == next,
            "a changed index invalidates the approved commit.");
        File.WriteAllText(Path.Combine(first, "unstaged.txt"), "leave alone");
        var commitPlan = await Plan(BulkAction.Commit, "Stage only");
        Check(commitPlan.Items.Single().Details.Contains("Staged: staged.txt") &&
            !commitPlan.Items.Single().Details.Contains("Staged: unstaged.txt"), "preview names exact staged paths.");
        var committed = await Apply(commitPlan);
        Check(committed.Success && await git.Value(first, "log", "-1", "--format=%s") == "Stage only" &&
            await git.Value(remote, "rev-parse", "refs/heads/main") == next &&
            !(await git.Run(first, "cat-file", "-e", "HEAD:unstaged.txt")).Ok && File.Exists(Path.Combine(first, "unstaged.txt")),
            "commit-only preserves unstaged files and leaves the remote alone.");

        File.Delete(Path.Combine(first, "unstaged.txt"));
        var justPush = await Plan(BulkAction.Push);
        var canceled = new CancellationToken(canceled: true);
        var stopped = (await operations.ApplyAsync(justPush, canceled)).Single();
        Check(stopped.Skipped && await git.Value(remote, "rev-parse", "refs/heads/main") == next,
            "a stopped batch skips pending pushes.");
        var pushed = await Apply(justPush);
        Check(pushed.Success && await git.Value(remote, "rev-parse", "refs/heads/main") == await git.Value(first, "rev-parse", "HEAD"),
            "push-only updates the approved upstream.");

        await Invoke(second, "pull", "--ff-only", "origin", "main");
        await Commit(first, "first-only.txt", "outgoing first");
        await Commit(second, "second-only.txt", "another outgoing checkout");
        var collision = await operations.PlanAsync([new(first, BulkAction.Push), new(second, BulkAction.Push)], CancellationToken.None);
        Check(collision.Items.All(item => !item.Ready && item.Summary.Contains("More than one checkout")),
            "two checkouts cannot push to the same branch in one batch.");
        File.WriteAllText(Path.Combine(first, "combined.txt"), "one more staged commit");
        await Invoke(first, "add", "--", "combined.txt");
        var commitPushed = await Apply(await Plan(BulkAction.CommitPush, "Stage then push"));
        Check(commitPushed.Success && await git.Value(first, "log", "-1", "--format=%s") == "Stage then push" &&
            await git.Value(remote, "rev-parse", "refs/heads/main") == await git.Value(first, "rev-parse", "HEAD"),
            "commit and push sends the successful staged commit.");

        await Invoke(seed, "branch", "obsolete");
        await Invoke(seed, "push", "origin", "refs/heads/obsolete");
        await Invoke(first, "fetch", "--no-prune", "origin");
        await Invoke(first, "branch", "obsolete", "refs/remotes/origin/obsolete");
        await Invoke(remote, "update-ref", "-d", "refs/heads/obsolete");
        var prune = await Plan(BulkAction.Prune);
        Check(prune.Items.Single().Ready && prune.Items.Single().Details.Count(line => line.Contains("Prune local tracking ref")) == 1 &&
            (await git.Run(first, "show-ref", "--verify", "refs/remotes/origin/obsolete")).Ok,
            "prune previews the exact stale tracking ref without changing it.");
        var pruned = await Apply(prune);
        Check(pruned.Success && !(await git.Run(first, "show-ref", "--verify", "refs/remotes/origin/obsolete")).Ok &&
            (await git.Run(first, "show-ref", "--verify", "refs/heads/obsolete")).Ok &&
            (await git.Run(remote, "show-ref", "--verify", "refs/heads/main")).Ok,
            "prune affects only local stale tracking refs.");

        return checks;
    }
}
