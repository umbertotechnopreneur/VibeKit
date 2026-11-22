using System.Diagnostics;
using System.Text.RegularExpressions;

namespace WorkspaceRepo;

public sealed partial class WorkspaceIndex
{
    static bool IsObjectId(string value) => value.Length is 40 or 64 && value.All(char.IsAsciiHexDigit);

    static async Task<HashSet<string>> ReadTrackingConfiguration(string path, CancellationToken token)
    {
        var result = await RunGit(path, ["config", "--null", "--name-only", "--get-regexp", @"^branch\..*\.merge$"], token);
        if (result.Code != 0 && (result.Code != 1 || result.Error.Length > 0))
            throw new InvalidDataException("Git branch tracking configuration could not be read.");
        // for-each-ref leaves upstream fields empty when a configured merge has no matching fetch mapping.
        return result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(key => key.StartsWith("branch.", StringComparison.Ordinal) && key.EndsWith(".merge", StringComparison.Ordinal))
            .Select(key => key[7..^6]).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Captures the full remote object IDs independently of the repository's configured fetch mappings.</summary>
    static async Task<Dictionary<string, string>?> FetchBranchTips(string path, string remote,
        Action<string, int?> activity, CancellationToken token)
    {
        var prefix = $"refs/remotes/{remote}/";
        var tips = new Dictionary<string, string>(StringComparer.Ordinal);
        var args = new[] { "--progress", "--atomic", "--no-tags", "--prune", "--no-prune-tags", "--no-write-fetch-head",
            "--no-recurse-submodules", "--no-auto-maintenance", "--refmap=", "--", remote, $"+refs/heads/*:{prefix}*" };
        var options = new[] { "-c", "http.lowSpeedLimit=1", "-c", "http.lowSpeedTime=20" };
        var watch = Stopwatch.StartNew();
        long next = 0;
        bool complete = true;
        void Progress(string line)
        {
            if (watch.ElapsedMilliseconds < next) return;
            var match = Regex.Match(line, @"(Receiving objects|Resolving deltas|Enumerating objects|Compressing objects|Counting objects):\s*(?:(\d+)%)?");
            if (!match.Success) return;
            next = watch.ElapsedMilliseconds + 70;
            activity("Git · " + match.Groups[1].Value, int.TryParse(match.Groups[2].Value, out int percent) ? percent : null);
        }
        void ReadReceipt(string line)
        {
            // A leading space is the fast-forward flag. Verbose mode also reports unchanged refs.
            var fields = line.Length > 2 ? line[2..].Split(' ', 3) : [];
            if (fields.Length != 3 || line[1] != ' ' || !IsObjectId(fields[0]) || !IsObjectId(fields[1]) ||
                !fields[2].StartsWith(prefix, StringComparison.Ordinal) || line[0] is not (' ' or '+' or '*' or '=' or '-'))
            {
                complete = false;
                return;
            }
            if (line[0] != '-') complete &= tips.TryAdd(fields[2], fields[1]);
        }
        var result = await RunGit(path, [.. options, "fetch", "--porcelain", "--verbose", .. args], token,
            outputLine: ReadReceipt, errorLine: Progress, timeoutSeconds: 45);
        if (result.Code == 0) return complete ? tips : null;
        if (result.Code != 129 || !result.Error.Contains("unknown option", StringComparison.Ordinal) ||
            !result.Error.Contains("porcelain", StringComparison.Ordinal)) return null;

        // Debian installations can have Git older than fetch --porcelain. Use advertised branch IDs as evidence
        // and require the fetched tracking refs to match them. A remote update between the calls cannot verify a stale ref.
        result = await RunGit(path, [.. options, "fetch", .. args], token, errorLine: Progress, timeoutSeconds: 45);
        if (result.Code != 0) return null;
        activity("Git · verifying advertised branch tips", null);
        tips.Clear();
        complete = true;
        result = await RunGit(path, [.. options, "ls-remote", "--heads", "--refs", "--", remote], token, line =>
        {
            var fields = line.Split('\t', 2);
            if (fields.Length != 2 || !IsObjectId(fields[0]) || !fields[1].StartsWith("refs/heads/", StringComparison.Ordinal))
                complete = false;
            else complete &= tips.TryAdd(prefix + fields[1][11..], fields[0]);
        }, timeoutSeconds: 45);
        return result.Code == 0 && complete ? tips : null;
    }

    static async Task<List<BranchComparison>> CompareBranches(string path, string current, string head, string remote,
        IReadOnlyDictionary<string, string> fetched, BranchInventory inventory, CancellationToken token)
    {
        var comparisons = new List<BranchComparison>();
        bool? shallow = null;
        foreach (var branch in inventory.Locals.OrderBy(branch => branch.Name != current))
        {
            if (branch.Remote != remote || !branch.RemoteRef.StartsWith("refs/heads/", StringComparison.Ordinal) ||
                !IsObjectId(branch.ObjectId) || branch.Name == current && branch.ObjectId != head) continue;

            var targetRef = $"refs/remotes/{remote}/{branch.RemoteRef[11..]}";
            // Git status may use a custom mapping. A canonical fetch does not certify that other tracking ref.
            if (branch.Upstream != targetRef) continue;
            var observed = inventory.RemoteObject(targetRef);
            if (!fetched.TryGetValue(targetRef, out var remoteObject))
            {
                if (observed == null) comparisons.Add(new(branch.Name, targetRef, branch.ObjectId, "", -1, -1, Gone: true));
                continue;
            }
            if (observed != remoteObject) continue;
            if (branch.ObjectId == remoteObject)
            {
                comparisons.Add(new(branch.Name, targetRef, branch.ObjectId, remoteObject, 0, 0));
                continue;
            }

            // Count immutable object IDs. Shallow history can hide ancestors and yield incorrect counts.
            if (shallow == null)
            {
                var result = await RunGit(path, ["rev-parse", "--is-shallow-repository"], token);
                shallow = result.Code != 0 || result.Output != "false";
            }
            if (shallow == true) continue;
            var counts = await RunGit(path, ["rev-list", "--left-right", "--count", $"{branch.ObjectId}...{remoteObject}"], token);
            var parts = counts.Output.Split('\t', 2);
            if (counts.Code == 0 && parts.Length == 2 && int.TryParse(parts[0], out int ahead) && int.TryParse(parts[1], out int behind))
                comparisons.Add(new(branch.Name, targetRef, branch.ObjectId, remoteObject, ahead, behind));
        }
        return comparisons;
    }
}
