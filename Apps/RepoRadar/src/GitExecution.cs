using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace WorkspaceRepo;

internal sealed record GitRepositoryPolicy(string LocalBlock, string NetworkBlock);

/// <summary>Runs Git for both the index and catalog under one noninteractive repository trust policy.</summary>
internal static class GitExecution
{
    private const int OutputLimit = 2_000_000;
    private const int ProgressLineLimit = 8192;
    private static readonly string[] FixedConfiguration = [
        "color.ui=false", "core.quotepath=false", "core.longpaths=true", "core.fsmonitor=false",
        "core.hooksPath=/dev/null", "submodule.recurse=false", "push.recurseSubmodules=no", "push.followTags=false", "fetch.pruneTags=false",
        "fetch.writeCommitGraph=false", "maintenance.auto=false", "gc.auto=0"];
    private static readonly Regex WorktreeHelper = new(
        @"^(filter\..+\.(clean|smudge|process)|diff\..+\.(command|textconv)|diff\.external|merge\..+\.driver|gpg(\..+)?\.program)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly Regex NetworkHelper = new(
        @"^(credential(\..+)?\.helper|core\.(gitproxy|askpass|sshcommand)|ssh\.variant|http\..+|remote\..+\.(vcs|uploadpack|receivepack|proxy)|url\..+\.(insteadof|pushinsteadof))$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    internal static async Task<GitResult> Run(string? directory, IEnumerable<string> arguments, CancellationToken token,
        TimeSpan timeout, Action<string>? stdoutLine = null, Action<string>? stderrLine = null,
        IEnumerable<string>? trustedConfiguration = null, string? standardInput = null)
    {
        var args = arguments.ToArray();
        // The application can prefix a verb with explicit -c options. The options never come from a repository.
        int commandIndex = 0;
        while (commandIndex + 1 < args.Length && args[commandIndex] == "-c") commandIndex += 2;
        string command = commandIndex < args.Length ? args[commandIndex] : "";
        bool network = command is "clone" or "fetch" or "ls-remote" or "pull" or "push" or "submodule";
        bool worktree = command is "add" or "checkout" or "commit" or "diff" or "merge" or "pull" or "reset" or "stash" or "status" or "submodule" or "switch";

        if (directory is not null && (worktree || network))
        {
            var policy = await ReadRepositoryPolicy(directory, token).ConfigureAwait(false);
            if (worktree && policy.LocalBlock.Length > 0) return new(1, "", policy.LocalBlock);
            if (network && policy.NetworkBlock.Length > 0) return new(1, "", policy.NetworkBlock);
            if (command == "status" && args.Contains("--ignore-submodules=none", StringComparer.Ordinal))
            {
                var nestedBlock = await CheckSubmodules(directory, token).ConfigureAwait(false);
                if (nestedBlock.Length > 0) return new(1, "", nestedBlock);
            }
        }

        // An explicitly chosen local remote is valid. Repository-provided submodule URLs cannot access local transports.
        bool allowLocalTransport = command != "submodule" && !(command == "clone" && args.Contains("--recurse-submodules"));
        return await Execute(directory, args, network, token, timeout, stdoutLine, stderrLine, trustedConfiguration,
            allowLocalTransport: allowLocalTransport, standardInput: standardInput).ConfigureAwait(false);
    }

    /// <summary>Finds executable settings inherited from repository and worktree files before inspecting or contacting a remote.</summary>
    internal static async Task<GitRepositoryPolicy> ReadRepositoryPolicy(string directory, CancellationToken token)
    {
        const string unavailable = "Git configuration could not be inspected safely. Inspect this repository manually.";
        var result = await Execute(directory, ["config", "--includes", "--show-scope", "--name-only", "--null", "--list"],
            network: false, token, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        if (!result.Ok) return new(unavailable, unavailable);
        string[] fields = result.Output.Split('\0');
        if (fields.Length % 2 != 1 || fields[^1].Length != 0) return new(unavailable, unavailable);
        string local = "", network = "";
        for (int index = 0; index + 1 < fields.Length; index += 2)
        {
            if (fields[index] is not ("local" or "worktree")) continue;
            string key = fields[index + 1];
            if (WorktreeHelper.IsMatch(key)) local = "A repository-local executable filter or worktree helper is configured. Inspect this repository manually.";
            if (NetworkHelper.IsMatch(key)) network = "A repository-local transport or credential setting is configured. Refresh this repository manually.";
        }
        return new(local, network);
    }

    private static async Task<string> CheckSubmodules(string directory, CancellationToken token)
    {
        const string unavailable = "An initialized submodule could not be inspected safely. Inspect it manually before syncing.";
        var pending = new Queue<string>();
        pending.Enqueue(directory);
        int checkedModules = 0;
        while (pending.TryDequeue(out string? parent))
        {
            var modules = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            bool invalid = false;
            var index = await Execute(parent, ["ls-files", "--stage", "-z"], network: false,
                token, TimeSpan.FromSeconds(15), stdoutLine: entry =>
                {
                    if (invalid || !entry.StartsWith("160000 ", StringComparison.Ordinal)) return;
                    int separator = entry.IndexOf('\t');
                    if (separator < 0 || entry.Length == ProgressLineLimit) { invalid = true; return; }
                    modules.Add(entry[(separator + 1)..]);
                    invalid = modules.Count > 256;
                }, outputSeparator: '\0', captureOutput: false).ConfigureAwait(false);
            if (!index.Ok || invalid) return unavailable;
            foreach (string relative in modules)
            {
                if (relative.Contains('\ufffd') || Path.IsPathRooted(relative) || relative.Split('/').Any(part => part is "" or "." or "..")) return unavailable;
                string child = Path.Combine(parent, relative);
                string gitEntry = Path.Combine(child, ".git");
                if (!File.Exists(gitEntry) && !Directory.Exists(gitEntry)) continue;
                if (++checkedModules > 256) return unavailable;
                try { Safety.NoLinks(child); }
                catch (InvalidDataException) { return unavailable; }
                var policy = await ReadRepositoryPolicy(child, token).ConfigureAwait(false);
                if (policy.LocalBlock.Length > 0) return "Submodule status blocked. " + policy.LocalBlock;
                pending.Enqueue(child);
            }
        }
        return "";
    }

    private static async Task<GitResult> Execute(string? directory, IEnumerable<string> args, bool network,
        CancellationToken token, TimeSpan timeout, Action<string>? stdoutLine = null, Action<string>? stderrLine = null,
        IEnumerable<string>? trustedConfiguration = null, char? outputSeparator = null, bool captureOutput = true,
        bool allowLocalTransport = true, string? standardInput = null)
    {
        token.ThrowIfCancellationRequested();
        var start = CreateStartInfo(directory, args, network, trustedConfiguration, allowLocalTransport);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        using var process = Process.Start(start) ?? throw new IOException("Could not start Git.");

        try
        {
            var stdout = Drain(process.StandardOutput, stdoutLine, deadline.Token, outputSeparator, captureOutput);
            var stderr = Drain(process.StandardError, stderrLine, deadline.Token);
            var input = Feed(process.StandardInput, standardInput, deadline.Token);
            await Task.WhenAll(stdout, stderr, input, process.WaitForExitAsync(deadline.Token)).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            return output.Overflow
                ? new(1, "", "Git output exceeded the safety limit. Inspect this repository manually.")
                : new(process.ExitCode, output.Text, error.Text);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            token.ThrowIfCancellationRequested();
            throw new IOException("Git timed out. Inspect the repository before retrying an interrupted operation.");
        }
    }

    private static async Task Feed(StreamWriter writer, string? input, CancellationToken token)
    {
        try
        {
            if (input != null)
            {
                await writer.WriteAsync(input.AsMemory(), token).ConfigureAwait(false);
                await writer.FlushAsync(token).ConfigureAwait(false);
            }
        }
        finally { writer.Close(); }
    }

    private static async Task<(string Text, bool Overflow)> Drain(StreamReader reader, Action<string>? onLine,
        CancellationToken token, char? separator = null, bool capture = true)
    {
        var output = new StringBuilder();
        var fragment = new StringBuilder();
        var buffer = new char[4096];
        bool overflow = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
        {
            if (capture)
            {
                int retained = Math.Min(OutputLimit - output.Length, read);
                output.Append(buffer, 0, retained);
                overflow |= retained < read;
            }
            if (onLine is null) continue;
            foreach (char c in buffer.AsSpan(0, read))
            {
                if (separator is { } delimiter ? c == delimiter : c is '\n' or '\r')
                {
                    if (fragment.Length > 0) onLine(fragment.ToString());
                    fragment.Clear();
                }
                else if (fragment.Length < ProgressLineLimit) fragment.Append(c);
            }
        }
        if (fragment.Length > 0) onLine?.Invoke(fragment.ToString());
        return (output.ToString(), overflow);
    }

    internal static ProcessStartInfo CreateStartInfo(string? directory, IEnumerable<string> args, bool network,
        IEnumerable<string>? trustedConfiguration = null, bool allowLocalTransport = true)
    {
        string[] searchPath = SearchPath(directory);
        string gitName = OperatingSystem.IsWindows() ? "git.exe" : "git";
        string sshName = OperatingSystem.IsWindows() ? "ssh.exe" : "ssh";
        string git = FindTool(gitName, GitLocations().Concat(searchPath))
            ?? throw new IOException("Git was not found in a standard installation or an absolute PATH entry. Install Git and add it to PATH.");
        string? ssh = network ? FindTool(sshName, SshLocations(git).Concat(searchPath)) : null;
        var start = new ProcessStartInfo(git)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(git)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardInputEncoding = new UTF8Encoding(false),
            CreateNoWindow = true
        };

        // Shell selectors can redirect object lookup, repository discovery, config, and helper resolution.
        foreach (string key in start.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)
            && !key.Equals("GIT_CONFIG_NOSYSTEM", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["PATH"] = string.Join(Path.PathSeparator, searchPath);
        start.Environment["LC_ALL"] = "C";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_NO_LAZY_FETCH"] = "1";
        start.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
        start.Environment["GCM_INTERACTIVE"] = "never";
        start.Environment["GIT_ALLOW_PROTOCOL"] = network ? "https" + (ssh is null ? "" : ":ssh") + (allowLocalTransport ? ":file" : "") : "";
        if (ssh is not null)
        {
            string portable = OperatingSystem.IsWindows() ? ssh.Replace('\\', '/') : ssh;
            start.Environment["GIT_SSH_COMMAND"] = "'" + portable.Replace("'", "'\\''") + "' -o BatchMode=yes -o StrictHostKeyChecking=yes -o ConnectTimeout=15";
            start.Environment["GIT_SSH_VARIANT"] = "ssh";
        }

        start.ArgumentList.Add("--no-pager");
        foreach (string pair in (trustedConfiguration ?? []).Concat(FixedConfiguration))
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(pair);
        }
        if (directory is not null)
        {
            start.ArgumentList.Add("-C");
            start.ArgumentList.Add(Path.GetFullPath(directory));
        }
        foreach (string arg in args) start.ArgumentList.Add(arg);
        return start;
    }

    private static string[] SearchPath(string? repository)
    {
        string caller = Environment.CurrentDirectory;
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.Trim('"'))
            .Where(Path.IsPathFullyQualified)
            .Select(Path.GetFullPath)
            .Where(entry => !IsWithin(entry, caller) && (repository is null || !IsWithin(entry, repository)))
            .ToArray();
    }

    private static bool IsWithin(string candidate, string directory)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string path = Path.TrimEndingDirectorySeparator(candidate);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.Equals(root, comparison) || (!Path.EndsInDirectorySeparator(root) && path.StartsWith(root + Path.DirectorySeparatorChar, comparison));
    }

    private static string? FindTool(string name, IEnumerable<string> directories) => directories
        .Where(Path.IsPathFullyQualified).Select(directory => Path.Combine(directory, name)).FirstOrDefault(File.Exists);

    private static IEnumerable<string> GitLocations()
    {
        if (!OperatingSystem.IsWindows()) return ["/usr/bin", "/bin", "/usr/local/bin"];
        return [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "cmd")];
    }

    private static IEnumerable<string> SshLocations(string git)
    {
        if (!OperatingSystem.IsWindows()) return ["/usr/bin", "/bin", "/usr/local/bin"];
        return [Path.Combine(Environment.SystemDirectory, "OpenSSH"),
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(git)!, "..", "usr", "bin"))];
    }
}
