namespace WorkspaceRepo;

internal static class RepositoryInfoTests
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("Repository Info test failed: " + description);
            checks++;
        }

        const string when = "2026-11-22T04:00:00.0000000Z";
        var original = new IndexedRepo
        {
            Path = Path.Combine(Path.GetTempPath(), "repo-info"), Branch = "main", HeadSha = new string('a', 40),
            RemoteUrl = "https://example.invalid/repo.git", VerifiedRemote = "origin",
            RemoteVerifiedUtc = when, VerifiedThisInspection = true, Dirty = true,
            WorktreeChanges = [new("M.", "local.txt"), new("??", "new/")], WorktreeChangesOmitted = 1,
            RemoteBranchPresence = new("origin", when, ["draft", "notes"], ["server-release"]),
            VerifiedBranches =
            [
                new("main", "refs/remotes/origin/main", new string('a', 40), new string('b', 40), 1, 5),
                new("local-name", "refs/remotes/origin/server-name", new string('c', 40), new string('d', 40), 2, 0),
                new("paused", "refs/remotes/origin/old", new string('e', 40), "", -1, -1, Gone: true)
            ]
        };
        string[] Lines(IndexedRepo repo, string section) =>
            RepositoryInfo.Create(repo).Single(item => item.Title == section).Lines.ToArray();

        var commits = Lines(original, "Commit differences");
        Check(commits.SequenceEqual(["'main' and 'origin/main' diverged. You have 1 commit to push and 5 commits to pull.",
            "'local-name' has 2 commits to push to 'origin/server-name'."]), "uses exact per-branch counts and remote targets with singular and plural grammar");
        Check(Lines(original, "Branch inventory").SequenceEqual(["2 local branches have no counterpart on 'origin'.",
            "'draft', 'notes'", "'origin' has 1 branch with no local counterpart.", "'server-release'"]),
            "names local and server-only branches separately");
        Check(Lines(original, "Upstream tracking").SequenceEqual(["'paused' still tracks 'origin/old', which was missing on the server at the last check."]),
            "keeps a confirmed missing upstream distinct from unlinked local-only branches");
        Check(Lines(original, "Working tree").Single() ==
            "The saved check found 1 modified path, 1 untracked path, and 1 additional status entry in the working tree.",
            "keeps separately counted local file changes and unlisted entries in saved tense");
        Check(RepositoryInfo.Create(original)[0].Lines.Single() ==
            "The saved check shows 2 branches with commit differences, 2 local branches without a counterpart on 'origin', 1 missing upstream, and uncommitted working-tree changes.",
            "surfaces independent facts before long detail sections without summing commit counts");

        var old = original with
        {
            RemoteBranchPresence = null, BranchIssues = ["Local only: old-draft", "Remote only: origin/old-release"]
        };
        Check(Lines(old, "Branch inventory")[0] == "The saved refs show no remote match for 1 local branch." &&
            Lines(old, "Verification").Any(line => line.Contains("no verified branch inventory", StringComparison.Ordinal)) &&
            Lines(old, "Commit differences").SequenceEqual(commits), "old snapshots keep exact commit proof and label inventory as saved refs");

        var stale = old with
        {
            VerifiedThisInspection = false, Ahead = 1, Behind = 5,
            BranchIssues = ["Upstream gone: paused → origin/old"],
            Reason = "Folder inaccessible during this scan. Showing cached metadata."
        };
        Check(Lines(stale, "Commit differences").Single().Contains("unverified", StringComparison.Ordinal) &&
            Lines(stale, "Upstream tracking").Single().Contains("server status is unverified", StringComparison.Ordinal) &&
            Lines(stale, "Working tree").Single().StartsWith("The saved check found", StringComparison.Ordinal),
            "a failed observation never upgrades cached counts, tracking hints or working-tree evidence");

        var incomplete = original with { RemoteBranchPresence = original.RemoteBranchPresence! with { RemoteOnly = null } };
        Check(Lines(incomplete, "Branch inventory").Any(line => line.Contains("remote-only count for 'origin' incomplete", StringComparison.Ordinal)),
            "unresolved tracking does not become a remote-only count");
        return checks;
    }
}
