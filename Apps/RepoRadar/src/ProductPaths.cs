using System.Security.Cryptography;
using System.Text;

namespace WorkspaceRepo;

internal static class ProductPaths
{
    private static readonly string[] StateFiles = ["repos.json", "settings.json", "dashboard.json", "workspace.sqlite3"];

    public static string? HomeOverride => Configured("REPO_RADAR_HOME", "REPO_HOME");

    public static string DataHome(string home, bool import)
    {
        if (Configured("REPO_RADAR_DATA", "VIBEREPO_DATA") is { } custom)
        {
            if (!Path.IsPathFullyQualified(custom))
                throw new ArgumentException("REPO_RADAR_DATA requires an absolute folder.");
            return Path.GetFullPath(custom);
        }

        bool portable = HomeOverride != null || File.Exists(Path.Combine(home, "portable.flag"));
        if (!OperatingSystem.IsWindows())
        {
            if (portable) return home;
            string? configured = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            string data = configured != null && Path.IsPathFullyQualified(configured)
                ? configured
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            return UseState(Path.Combine(data, "repo-radar"), Path.Combine(data, "VibeWare", "VibeRepo"), import);
        }

        if (portable)
        {
            const string extendedUnc = @"\\?\UNC\";
            string share = home.StartsWith(extendedUnc, StringComparison.OrdinalIgnoreCase)
                ? @"\\" + home[extendedUnc.Length..] : home;
            if (!share.StartsWith(@"\\", StringComparison.Ordinal) ||
                share.StartsWith(@"\\?\", StringComparison.Ordinal) ||
                share.StartsWith(@"\\.\", StringComparison.Ordinal)) return home;
            string shareId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(share.ToUpperInvariant())));
            string local = WindowsLocalData();
            return UseState(Path.Combine(local, "RepoRadarShares", shareId),
                Path.Combine(local, "Repo", "Shares", shareId), import);
        }

        string localData = WindowsLocalData();
        return UseState(Path.Combine(localData, "RepoRadar"), Path.Combine(localData, "VibeWare", "VibeRepo"), import);
    }

    private static string WindowsLocalData()
    {
        string path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        if (!Path.IsPathFullyQualified(path)) throw new IOException("Cannot locate your per-user application data folder.");
        return path;
    }

    private static string? Configured(string current, string previous) =>
        Environment.GetEnvironmentVariable(current) is { Length: > 0 } value ? value :
        Environment.GetEnvironmentVariable(previous) is { Length: > 0 } legacy ? legacy : null;

    private static string UseState(string target, string previous, bool import)
    {
        if (Directory.Exists(target) || !StateFiles.Any(name => File.Exists(Path.Combine(previous, name)))) return target;
        if (!import) return previous;

        // Stage the complete copy next to its destination so simultaneous launches cannot overwrite state.
        string parent = Path.GetDirectoryName(target)!;
        string staging = Path.Combine(parent, ".repo-radar-import-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(staging);
        else Directory.CreateDirectory(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            foreach (string name in StateFiles)
            {
                string source = Path.Combine(previous, name);
                if (!File.Exists(source)) continue;
                string copy = Path.Combine(staging, name);
                if (name == "workspace.sqlite3") IndexDatabase.Backup(source, copy);
                else File.Copy(source, copy);
            }
            try { Directory.Move(staging, target); }
            catch (IOException) when (Directory.Exists(target)) { }
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                foreach (string name in StateFiles) File.Delete(Path.Combine(staging, name));
                Directory.Delete(staging);
            }
        }
        return target;
    }
}
