namespace WorkspaceRepo;

internal static class WorkspaceTileViewModelTests
{
    public static void Run()
    {
        var repo = new IndexedRepo
        {
            Path = Path.Combine(Path.GetTempPath(), "repo-radar-ui-fixture"),
            Branch = "main",
            HeadSha = new string('a', 40),
            RemoteUrl = "https://example.invalid/repo.git",
            VerifiedThisInspection = true,
            RemoteVerifiedUtc = "2026-01-01T00:00:00Z",
            Dirty = true,
            WorktreeChanges = [new("M", "README.md")],
            VerifiedBranches = [new("main", "refs/remotes/origin/main", new string('a', 40), new string('b', 40), 0, 3)]
        };
        var withoutCounts = WorkspaceTileViewModel.Create(repo, "repo", false, false, true, "now");
        if (withoutCounts.Condition != "main · To pull" || withoutCounts.Files != "1 modified")
            throw new InvalidOperationException("Tile preferences must hide commit counts independently of file details.");
        var withoutFiles = WorkspaceTileViewModel.Create(repo, "repo", false, true, false, "now");
        if (withoutFiles.Condition != "main · ↓ 3 to pull" || withoutFiles.Files.Length != 0)
            throw new InvalidOperationException("Tile preferences must hide file details independently of commit counts.");

        var green = repo with { Path = repo.Path + "-green", Dirty = false,
            VerifiedBranches = [new("main", "refs/remotes/origin/main", repo.HeadSha, repo.HeadSha, 0, 0)] };
        var extra = green with { Path = repo.Path + "-extra", RemoteBranches = 2 };
        var model = new WorkspaceViewModel();
        model.Update([extra, repo, green], false, false, [], [], "", "Overview", 0);
        if (!model.Rows(WorkspaceTab.Repositories).Select(row => row.Status).SequenceEqual(["Green", "Purple", "Amber"]))
            throw new InvalidOperationException("Overview must place green rows before drift and aligned extra branches.");
        Console.WriteLine("Tile and overview self-test passed (3 checks).");
    }
}
