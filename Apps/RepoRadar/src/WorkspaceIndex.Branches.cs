namespace WorkspaceRepo;

public sealed partial class WorkspaceIndex
{
    sealed record BranchRef(string Name, string Upstream, string Remote, string RemoteRef, string Tracking, string ObjectId, string UpstreamShort);

    sealed class BranchInventory
    {
        readonly List<BranchRef> locals = [];
        readonly Dictionary<string, string> remotes = new(StringComparer.Ordinal);
        public IReadOnlyList<BranchRef> Locals => locals;
        public string? RemoteObject(string fullRef) => remotes.GetValueOrDefault(fullRef);
        public int LocalCount => locals.Count;
        public int RemoteCount => remotes.Count;
        public string[] LocalRefs => locals.OrderBy(b => b.Name, StringComparer.Ordinal)
            .Select(b => b.Tracking.Length > 0 ? $"{b.Name} · {b.Tracking}" : b.Name).ToArray();
        public string[] RemoteRefs => remotes.Keys.Order(StringComparer.Ordinal).Select(r => r[13..]).ToArray();
        public bool HasExtras(string remote) => locals.Any(b => b.Name != "main") ||
            remotes.Keys.Any(b => b != $"refs/remotes/{remote}/main");

        public RemoteBranchPresence? ObservePresence(string remote, string checkedUtc,
            IReadOnlyDictionary<string, string> fetched, IReadOnlySet<string> configuredTracking)
        {
            string prefix = $"refs/remotes/{remote}/";
            // Require the full receipt to agree with the freshly read canonical tracking refs.
            if (remotes.Keys.Count(key => key.StartsWith(prefix, StringComparison.Ordinal)) != fetched.Count ||
                fetched.Any(tip => RemoteObject(tip.Key) != tip.Value))
                return null;

            var remoteNames = fetched.Keys.Select(key => key[prefix.Length..]).ToHashSet(StringComparer.Ordinal);
            var localNames = locals.Select(branch => branch.Name).ToHashSet(StringComparer.Ordinal);
            var trackedNames = locals.Where(branch => branch.Remote == remote &&
                branch.RemoteRef.StartsWith("refs/heads/", StringComparison.Ordinal))
                .Select(branch => branch.RemoteRef[11..]).ToHashSet(StringComparer.Ordinal);
            var localOnly = locals.Where(branch => branch.Upstream.Length == 0 &&
                !configuredTracking.Contains(branch.Name) && !remoteNames.Contains(branch.Name))
                .Select(branch => branch.Name).Order(StringComparer.Ordinal).ToArray();

            // An unmapped configured upstream might point at one of the remaining remote names.
            bool unresolved = locals.Any(branch => branch.Upstream.Length == 0 && configuredTracking.Contains(branch.Name) &&
                (branch.Remote.Length == 0 || branch.Remote == remote && !branch.RemoteRef.StartsWith("refs/heads/", StringComparison.Ordinal)));
            var remoteOnly = unresolved ? null :
                remoteNames.Where(name => !localNames.Contains(name) && !trackedNames.Contains(name)).Order(StringComparer.Ordinal).ToArray();
            return new(remote, checkedUtc, localOnly, remoteOnly);
        }

        public void Read(string line)
        {
            // Tabs and line breaks are forbidden in Git ref names. Symbolic refs such as origin/HEAD are not branches.
            var f = line.Split('\t');
            if (f.Length != 8 || f[1].Length > 0) return;
            if (f[0].StartsWith("refs/heads/", StringComparison.Ordinal))
                locals.Add(new(f[0][11..], f[2], f[3], f[4], f[5], f[6], f[7]));
            else if (f[0].StartsWith("refs/remotes/", StringComparison.Ordinal)) remotes.Add(f[0], f[6]);
        }

        public (string Summary, List<string> Details, bool MissingUpstream) Describe(
            string current, IReadOnlyList<BranchComparison> comparisons, bool freshlyFetched, string[] remoteNames,
            IReadOnlySet<string> configuredTracking)
        {
            var missing = new List<string>();
            var cachedMissing = new List<string>();
            var missingTracking = new List<string>();
            var drift = new List<string>();
            var unverified = new List<string>();
            var localOnly = new List<string>();
            var untracked = new List<string>();
            var remoteOnly = new List<string>();
            var extras = new List<string>();
            var linked = locals.Select(b => b.Upstream).ToHashSet(StringComparer.Ordinal);
            var localNames = locals.Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
            var byRemote = remoteNames.OrderByDescending(r => r.Length).ToArray();
            var verified = comparisons.ToDictionary(b => b.Branch, StringComparer.Ordinal);
            var counterparts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var full in remotes.Keys.Order(StringComparer.Ordinal))
            {
                var shortRef = full[13..];
                var owner = byRemote.FirstOrDefault(r => shortRef.StartsWith(r + "/", StringComparison.Ordinal));
                var remoteBranch = owner == null ? shortRef : shortRef[(owner.Length + 1)..];
                counterparts.Add(remoteBranch);
                if (!localNames.Contains(remoteBranch) && !linked.Contains(full)) remoteOnly.Add(shortRef);
            }
            foreach (var b in locals.OrderBy(b => b.Name != current).ThenBy(b => b.Name, StringComparer.Ordinal))
            {
                if (b.Upstream.Length == 0)
                {
                    if (configuredTracking.Contains(b.Name)) unverified.Add(b.Name + " (no matching fetch mapping)");
                    else if (counterparts.Contains(b.Name)) untracked.Add(b.Name);
                    else localOnly.Add(b.Name);
                    continue;
                }
                var target = b.Upstream.StartsWith("refs/remotes/", StringComparison.Ordinal) ? b.Upstream[13..] :
                    b.Upstream.StartsWith("refs/heads/", StringComparison.Ordinal) ? b.Upstream[11..] : b.Upstream;
                var comparison = verified.GetValueOrDefault(b.Name);
                if (comparison is { Gone: true })
                {
                    missing.Add(b.Name + " → " + target);
                    continue;
                }
                if (b.Tracking == "gone" && comparison == null)
                {
                    var description = b.Name + " → " + target;
                    if (freshlyFetched || b.Remote == ".") missingTracking.Add(description);
                    else cachedMissing.Add(description);
                    continue;
                }
                if (freshlyFetched && comparison == null) unverified.Add(b.Name + " → " + target);
                else if (b.Name != current && (comparison != null ? comparison.Ahead > 0 || comparison.Behind > 0 : b.Tracking.Length > 0))
                {
                    var tracking = comparison == null ? b.Tracking : string.Join(", ", new[]
                    {
                        comparison.Ahead > 0 ? $"ahead {comparison.Ahead}" : "",
                        comparison.Behind > 0 ? $"behind {comparison.Behind}" : ""
                    }.Where(value => value.Length > 0));
                    drift.Add($"{b.Name} ({tracking} vs {target})");
                }
                else if (b.Name != "main") extras.Add(b.Name);
            }
            var compact = new List<string>();
            var details = new List<string>();
            void Add(string label, List<string> values)
            {
                if (values.Count == 0) return;
                details.AddRange(values.Select(v => label + ": " + v));
                string text = string.Join(", ", values.Take(2));
                if (values.Count > 2) text += $" +{values.Count - 2}";
                compact.Add(label + ": " + text);
            }
            Add("Upstream gone", missing);
            Add("Upstream missing in cache", cachedMissing);
            Add("Tracking ref missing", missingTracking);
            Add("Tracking not verified", unverified);
            Add("Branch drift", drift);
            Add("Local only", localOnly);
            Add("No upstream", untracked);
            Add("Remote only", remoteOnly);
            Add("Extra branch", extras);
            return (string.Join(" · ", compact), details, missing.Count + cachedMissing.Count + missingTracking.Count > 0);
        }
    }
}
