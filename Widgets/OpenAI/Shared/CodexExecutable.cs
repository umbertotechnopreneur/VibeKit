using System.Runtime.InteropServices;

namespace OpenAIUsage;

public static class CodexExecutable
{
    public static string Resolve(string configured)
    {
        configured = Environment.ExpandEnvironmentVariables(configured.Trim());
        if (configured is not ("codex" or "codex.exe")) return configured;
        var candidates = new List<string>();
        foreach (var path in new[] { Environment.GetEnvironmentVariable("PATH"),
            OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) : null,
            OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) : null })
            foreach (var directory in (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                candidates.Add(Path.Combine(Environment.ExpandEnvironmentVariables(directory.Trim().Trim('"')), OperatingSystem.IsWindows() ? "codex.exe" : "codex"));
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            candidates.Add(Path.Combine(local, "Microsoft", "WinGet", "Links", "codex.exe"));
            candidates.Add(Path.Combine(local, "OpenAI", "Codex", "bin", "codex.exe"));
        }
        else
        {
            var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            candidates.Add(Path.Combine(user, ".local", "bin", "codex"));
            candidates.Add(Path.Combine(user, ".npm-global", "bin", "codex"));
            candidates.Add("/usr/local/bin/codex");
            candidates.Add("/usr/bin/codex");
        }
        return candidates.FirstOrDefault(File.Exists)
            ?? (OperatingSystem.IsWindows() ? FindStoreExecutable() : null)
            ?? configured;
    }

    private static string? FindStoreExecutable()
    {
        // The Store app exposes its CLI under a versioned installation path.
        const string packageFamily = "OpenAI.Codex_2p2nqsd0c76g0";
        const int insufficientBuffer = 122;
        uint count = 0, bufferLength = 0;
        if (GetPackagesByPackageFamily(packageFamily, ref count, null, ref bufferLength, 0) != insufficientBuffer)
            return null;

        var names = new nint[count];
        nint buffer = Marshal.AllocHGlobal(checked((int)bufferLength * sizeof(char)));
        try
        {
            if (GetPackagesByPackageFamily(packageFamily, ref count, names, ref bufferLength, buffer) != 0)
                return null;

            for (var i = 0; i < count; i++)
            {
                string? name = Marshal.PtrToStringUni(names[i]);
                if (name is null) continue;
                uint pathLength = 0;
                if (GetPackagePathByFullName(name, ref pathLength, null) != insufficientBuffer) continue;

                var path = new char[pathLength];
                if (GetPackagePathByFullName(name, ref pathLength, path) != 0) continue;
                string executable = Path.Combine(new string(path, 0, (int)pathLength - 1), "app", "resources", "codex.exe");
                if (File.Exists(executable)) return executable;
            }
            return null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GetPackagesByPackageFamily(string packageFamilyName, ref uint count,
        [Out] nint[]? packageFullNames, ref uint bufferLength, nint buffer);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GetPackagePathByFullName(string packageFullName, ref uint pathLength, [Out] char[]? path);
}
