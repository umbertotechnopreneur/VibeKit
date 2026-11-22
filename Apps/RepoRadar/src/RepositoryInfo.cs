using System.Globalization;

namespace WorkspaceRepo;

internal sealed record RepositoryInfoSection(string Title, IReadOnlyList<string> Lines);

/// <summary>Composes an English explanation from the saved observation, with separate confidence for commit and branch evidence.</summary>
internal static class RepositoryInfo
{
    public static IReadOnlyList<RepositoryInfoSection> Create(IndexedRepo repo)
    {
        var alignment = RepositoryAlignment.Of(repo);
        var presence = repo.VerifiedThisInspection == true && repo.RemoteUrl.Length > 0 &&
            repo.RemoteBranchPresence is { } inventoryProof && inventoryProof.Remote == repo.VerifiedRemote
            ? inventoryProof : null;
        var comparisons = alignment.VerifiedComparisons;
        var overview = DescribeOverview(repo, alignment, presence);
        var commits = DescribeCommits(repo, alignment);
        var inventory = DescribeInventory(repo, presence);
        var tracking = DescribeTracking(repo, comparisons);
        var workingTree = DescribeWorktree(repo);
        var verification = DescribeVerification(repo, alignment, presence);
        return new RepositoryInfoSection[]
        {
            new("At a glance", overview), new("Commit differences", commits), new("Branch inventory", inventory),
            new("Upstream tracking", tracking), new("Working tree", workingTree), new("Verification", verification)
        }.Where(section => section.Lines.Count != 0).ToArray();
    }

    private static string[] DescribeOverview(IndexedRepo repo, RepositoryAlignment alignment, RemoteBranchPresence? presence)
    {
        var facts = new List<string>();
        int different = alignment.Deltas.Count;
        int missing = alignment.VerifiedComparisons.Count(branch => branch.Gone);
        if (different > 0)
            facts.Add(Count(different, "branch with commit differences", "branches with commit differences"));
        if (presence is { LocalOnly.Count: > 0 })
            facts.Add($"{Count(presence.LocalOnly.Count, "local branch", "local branches")} without a counterpart on '{presence.Remote}'");
        if (missing > 0)
            facts.Add(Count(missing, "missing upstream", "missing upstreams"));
        if (repo.Dirty)
            facts.Add("uncommitted working-tree changes");
        return facts.Count >= 3 || different > 5 ? [$"The saved check shows {JoinEnglish(facts)}."] : [];
    }

    private static List<string> DescribeCommits(IndexedRepo repo, RepositoryAlignment alignment)
    {
        var lines = new List<string>();
        if (repo.Branch == "(detached)")
            lines.Add("HEAD is detached. There is no checked-out branch.");
        else if (repo.Branch.Length == 0)
            lines.Add("The saved check has no current branch.");
        else if (alignment.CurrentComparison == null && !alignment.VerifiedComparisons.Any(b => b.Branch == repo.Branch && b.Gone))
            lines.Add($"The commit difference for '{repo.Branch}' is unverified.");

        foreach (var comparison in alignment.VerifiedComparisons.Where(b => !b.Gone &&
                     (b.Ahead > 0 || b.Behind > 0 || b == alignment.CurrentComparison))
                     .OrderBy(b => b.Branch != repo.Branch).ThenBy(b => b.Branch, StringComparer.Ordinal))
        {
            string branch = $"'{comparison.Branch}'", upstream = $"'{ShortTrackingRef(comparison.Upstream)}'";
            string outgoing = Count(comparison.Ahead, "commit", "commits");
            string incoming = Count(comparison.Behind, "commit", "commits");
            lines.Add((comparison.Ahead > 0, comparison.Behind > 0) switch
            {
                (true, true) => $"{branch} and {upstream} diverged. You have {outgoing} to push and {incoming} to pull.",
                (true, false) => $"{branch} has {outgoing} to push to {upstream}.",
                (false, true) => $"{upstream} has {incoming} for {branch} to pull.",
                _ => $"{branch} matches {upstream} in the saved check."
            });
        }
        return lines;
    }

    private static List<string> DescribeInventory(IndexedRepo repo, RemoteBranchPresence? presence)
    {
        var lines = new List<string>();
        if (presence != null)
        {
            if (presence.LocalOnly.Count > 0)
            {
                string verb = presence.LocalOnly.Count == 1 ? "has" : "have";
                lines.Add($"{Count(presence.LocalOnly.Count, "local branch", "local branches")} {verb} no counterpart on '{presence.Remote}'.");
                lines.AddRange(Names(presence.LocalOnly));
            }
            if (presence.RemoteOnly is { Count: > 0 } remoteOnly)
            {
                lines.Add($"'{presence.Remote}' has {Count(remoteOnly.Count, "branch", "branches")} with no local counterpart.");
                lines.AddRange(Names(remoteOnly));
            }
            if (presence.RemoteOnly == null)
                lines.Add($"Unresolved tracking settings make the remote-only count for '{presence.Remote}' incomplete.");
            return lines;
        }

        // Old snapshots only contain labels derived from cached refs, which cannot prove branch presence on a server.
        var knownLocalOnly = Values(repo, "Local only: ");
        var knownRemoteOnly = Values(repo, "Remote only: ");
        if (knownLocalOnly.Count > 0)
        {
            lines.Add($"The saved refs show no remote match for {Count(knownLocalOnly.Count, "local branch", "local branches")}.");
            lines.AddRange(Names(knownLocalOnly));
        }
        if (knownRemoteOnly.Count > 0)
        {
            lines.Add($"The saved refs list {Count(knownRemoteOnly.Count, "remote-tracking branch", "remote-tracking branches")} with no local match.");
            lines.AddRange(Names(knownRemoteOnly));
        }
        return lines;
    }

    private static List<string> DescribeTracking(IndexedRepo repo, IReadOnlyList<BranchComparison> verified)
    {
        var lines = new List<string>();
        var gone = verified.Where(branch => branch.Gone).OrderBy(branch => branch.Branch, StringComparer.Ordinal).ToArray();
        foreach (var branch in gone)
            lines.Add($"'{branch.Branch}' still tracks '{ShortTrackingRef(branch.Upstream)}', which was missing on the server at the last check.");

        foreach (string value in Values(repo, "Upstream gone: ", "Upstream missing in cache: ", "Tracking ref missing: "))
        {
            var pair = value.Split(" → ", 2, StringSplitOptions.None);
            if (gone.Any(branch => branch.Branch == pair[0])) continue;
            lines.Add(pair.Length == 2
                ? $"The saved refs lack '{pair[1]}', the tracking ref for '{pair[0]}'. Its server status is unverified."
                : $"The saved refs show a missing tracking ref for '{value}'. Its server status is unverified.");
        }
        foreach (string value in Values(repo, "Tracking not verified: "))
            lines.Add($"Tracking could not be verified for {TrackingLabel(value)}.");
        foreach (string branch in Values(repo, "No upstream: "))
            lines.Add($"'{branch}' has no resolved upstream in the saved check.");
        return lines;
    }

    private static string[] DescribeWorktree(IndexedRepo repo)
    {
        if (repo.Branch.Length == 0 && !repo.Dirty) return ["Working-tree status is unavailable."];
        if (!repo.Dirty) return ["The saved check shows a clean working tree."];

        var entries = repo.WorktreeChanges.GroupBy(ChangeKind)
            .Select(group => Count(group.Count(), $"{group.Key} path", $"{group.Key} paths")).ToList();
        if (repo.WorktreeChangesOmitted > 0)
            entries.Add(Count(repo.WorktreeChangesOmitted, "additional status entry", "additional status entries"));
        return entries.Count > 0 ? [$"The saved check found {JoinEnglish(entries)} in the working tree."]
            : ["The saved check shows uncommitted changes in the working tree."];
    }

    private static List<string> DescribeVerification(IndexedRepo repo, RepositoryAlignment alignment, RemoteBranchPresence? presence)
    {
        var lines = new List<string>();
        if (alignment.VerifiedComparisons.Count > 0 && FormatDate(repo.RemoteVerifiedUtc) is { } recorded)
            lines.Add($"Commit and upstream information is from the remote check on {recorded}.");
        if (presence != null && FormatDate(presence.CheckedUtc) is { } recordedInventory && presence.CheckedUtc != repo.RemoteVerifiedUtc)
            lines.Add($"Branch inventory on '{presence.Remote}' was checked on {recordedInventory}.");
        if (presence == null && repo.RemoteUrl.Length > 0)
            lines.Add("The saved index has no verified branch inventory for this remote.");
        if (repo.HasUnverifiedTracking)
            lines.Add("Some tracked branches have no verified commit comparison.");
        if (alignment.NeedsVerification && repo.RemoteUrl.Length == 0)
            lines.Add("The saved check has no usable remote URL.");
        else if (alignment.NeedsVerification || presence == null)
            lines.Add("Close Info and press F5 to check the saved repositories against their remotes.");
        return lines;
    }

    private static string ChangeKind(WorktreeChange change)
    {
        var status = change.Status;
        if (status.Contains('?')) return "untracked";
        if (status.Contains('U') || status is "AA" or "DD") return "conflicted";
        if (status.Contains('R')) return "renamed";
        if (status.Contains('D')) return "deleted";
        if (status.Contains('A')) return "added";
        if (status.Contains('M') || status.Contains('T')) return "modified";
        return "changed";
    }

    private static IReadOnlyList<string> Values(IndexedRepo repo, params string[] prefixes) =>
        repo.BranchIssues.SelectMany(line => prefixes.Where(prefix => line.StartsWith(prefix, StringComparison.Ordinal))
            .Select(prefix => line[prefix.Length..])).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static string ShortTrackingRef(string name) =>
        name.StartsWith("refs/remotes/", StringComparison.Ordinal) ? name[13..] : name;

    private static string TrackingLabel(string value)
    {
        var pair = value.Split(" → ", 2, StringSplitOptions.None);
        if (pair.Length == 2) return $"'{pair[0]}' against '{pair[1]}'";
        const string unmapped = " (no matching fetch mapping)";
        return value.EndsWith(unmapped, StringComparison.Ordinal) ? $"'{value[..^unmapped.Length]}' (no matching fetch mapping)" : $"'{value}'";
    }

    private static string? FormatDate(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUniversalTime().ToString("yyyy-MM-dd 'at' HH:mm 'UTC'", CultureInfo.InvariantCulture) : null;

    private static string Count(int count, string singular, string plural) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? singular : plural)}";

    private static IEnumerable<string> Names(IEnumerable<string> values) =>
        values.Chunk(5).Select(group => string.Join(", ", group.Select(value => $"'{value}'")));

    private static string JoinEnglish(IReadOnlyList<string> values) => values.Count switch
    {
        0 => "", 1 => values[0], 2 => $"{values[0]} and {values[1]}",
        _ => $"{string.Join(", ", values.Take(values.Count - 1))}, and {values[^1]}"
    };
}
