using System.Runtime.CompilerServices;

namespace WorkspaceRepo;

internal sealed record BranchDelta(string Branch, int Ahead, int Behind);

/// <summary>Projects exact Git observations independently from local edits and branch inventory.</summary>
internal sealed class RepositoryAlignment
{
    static readonly ConditionalWeakTable<IndexedRepo, RepositoryAlignment> Cache = new();

    public static RepositoryAlignment Of(IndexedRepo repo) => Cache.GetValue(repo, r => new RepositoryAlignment(r));

    public bool LocalEdits { get; }
    public bool Gone { get; }
    public BranchComparison? CurrentComparison { get; }
    public bool Compared => CurrentComparison != null;
    public bool NeedsVerification { get; }
    public IReadOnlyList<BranchComparison> VerifiedComparisons { get; }
    public IReadOnlyList<BranchDelta> Deltas { get; }
    public bool Push => Deltas.Any(b => b.Ahead > 0);
    public bool Pull => Deltas.Any(b => b.Behind > 0);
    public bool Aligned => Compared && !NeedsVerification && !LocalEdits && !Gone && Deltas.Count == 0;
    public string Status => Aligned ? "Green" : (Push, Pull) switch
    {
        (true, true) => "Both",
        (true, false) => "Push",
        (false, true) => "Purple",
        _ when Gone => "Gone",
        _ when LocalEdits => "Red",
        _ => "Unknown"
    };

    RepositoryAlignment(IndexedRepo repo)
    {
        LocalEdits = repo.Dirty;
        // Older SQLite snapshots record fetch success without the exact refs. They remain useful as inventory.
        VerifiedComparisons = repo.VerifiedThisInspection == true && repo.RemoteVerifiedUtc.Length > 0 && repo.RemoteUrl.Length > 0
            ? repo.VerifiedBranches : [];
        Gone = VerifiedComparisons.Any(branch => branch.Gone);
        CurrentComparison = VerifiedComparisons.FirstOrDefault(branch => !branch.Gone && branch.Branch == repo.Branch && branch.LocalObject == repo.HeadSha);
        Deltas = VerifiedComparisons.Where(branch => !branch.Gone && (branch.Ahead > 0 || branch.Behind > 0))
            .Select(branch => new BranchDelta(branch.Branch, branch.Ahead, branch.Behind)).ToArray();
        NeedsVerification = !Compared || repo.HasUnverifiedTracking;
    }
}
