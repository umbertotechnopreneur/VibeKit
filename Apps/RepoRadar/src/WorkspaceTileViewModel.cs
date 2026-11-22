namespace WorkspaceRepo;

/// <summary>Projects Git observations and display preferences into data shared by a tile and its preview.</summary>
internal sealed record WorkspaceTileViewModel(
    string Title, string Path, string Badge, string Category, string Branch,
    int Ahead, int Behind, string Branches, string Condition, string Tracking, string Files, string LastChecked)
{
    public static WorkspaceTileViewModel Create(IndexedRepo repository, string name, bool pinned,
        bool showCommitCounts, bool showFiles, string checkedAt)
    {
        var alignment = RepositoryAlignment.Of(repository);
        string category = DisplayStatus(repository);
        string badge = category switch
        {
            "Green" => "SYNCHRONIZED",
            "Amber" => "EXTRA BRANCHES",
            "Gone" => "UPSTREAM GONE",
            "Red" or "Push" => "LOCAL CHANGES",
            "Purple" => "REMOTE CHANGES",
            "Both" => "LOCAL + REMOTE",
            _ => "UNVERIFIED"
        };
        var delta = alignment.Deltas.FirstOrDefault();
        string branch = delta?.Branch ?? repository.Branch;
        int ahead = delta?.Ahead ?? alignment.CurrentComparison?.Ahead ?? -1;
        int behind = delta?.Behind ?? alignment.CurrentComparison?.Behind ?? -1;
        string branches = $"{repository.LocalBranches} local · {repository.RemoteBranches} remote";
        string tracking = alignment.Gone ? CompactBranchIssue(repository) : "";
        string condition = Describe(repository, showCommitCounts, showFiles);
        return new((pinned ? "★ " : "") + name, repository.Path, badge, category,
            branch.Length == 0 ? "—" : branch, ahead, behind, branches, condition,
            tracking != condition ? tracking : "", showFiles ? LocalFiles(repository) : "", checkedAt);
    }

    public static string Describe(IndexedRepo repo, bool showCommitCounts = true, bool showFiles = true)
    {
        var alignment = RepositoryAlignment.Of(repo);
        if (alignment.Deltas.Count > 0)
        {
            string delta = FormatDelta(alignment.Deltas[0], showCommitCounts);
            return alignment.Deltas.Count > 1 ? delta + $" · +{alignment.Deltas.Count - 1} branches" : delta;
        }
        if (alignment.Gone) return CompactBranchIssue(repo);
        if (alignment.Aligned) return "Clean and aligned";
        if (alignment.LocalEdits) return showFiles ? LocalFiles(repo) : "Uncommitted work";
        if (repo.RemoteUrl.Length == 0) return "No remote configured";
        if (repo.Reason.Contains("Fetch failed", StringComparison.OrdinalIgnoreCase)) return "Fetch failed";
        if (repo.Ahead < 0 || repo.Behind < 0) return "No tracked comparison";
        return "Remote needs verification";
    }

    public static string FormatDelta(BranchDelta change, bool counts)
    {
        var summary = new List<string>(2);
        if (change.Ahead > 0) summary.Add(counts ? $"↑ {change.Ahead} to push" : "To push");
        if (change.Behind > 0) summary.Add(counts ? $"↓ {change.Behind} to pull" : "To pull");
        return $"{change.Branch} · {string.Join(" · ", summary)}";
    }

    private static string CompactBranchIssue(IndexedRepo repo)
    {
        var issue = repo.BranchIssues.FirstOrDefault(line => line.StartsWith("Upstream gone:", StringComparison.Ordinal))
            ?? repo.BranchIssue;
        int arrow = issue.IndexOf(" → ", StringComparison.Ordinal);
        // The card has room for the remote ref. Details shows the local branch that tracked it.
        return arrow < 0 ? issue : "Upstream gone: " + issue[(arrow + 3)..];
    }

    private static string LocalFiles(IndexedRepo repo)
    {
        if (!repo.Dirty) return repo.Branch.Length > 0 ? "No local edits" : "Not checked";
        string Kind(WorktreeChange change)
        {
            var code = change.Status;
            if (code.Contains('?')) return "untracked";
            if (code.Contains('U') || code is "AA" or "DD") return "conflicted";
            if (code.Contains('R')) return "renamed";
            if (code.Contains('D')) return "deleted";
            if (code.Contains('A')) return "added";
            if (code.Contains('M') || code.Contains('T')) return "modified";
            return "changed";
        }
        var counts = repo.WorktreeChanges.GroupBy(Kind).OrderByDescending(group => group.Count())
            .Select(group => $"{group.Count()} {group.Key}").ToList();
        if (repo.WorktreeChangesOmitted > 0) counts.Add($"+{repo.WorktreeChangesOmitted} more");
        return counts.Count == 0 ? "Uncommitted work" : string.Join(", ", counts);
    }
}
