using System.Security.Cryptography;
using System.Text;

namespace WorkspaceRepo;

public sealed partial class WorkspaceIndex
{
    static readonly uint[] CrcTable = CreateCrcTable();
    static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint c = i;
            for (int bit = 0; bit < 8; bit++) c = (c >> 1) ^ ((c & 1) != 0 ? 0xedb88320u : 0);
            table[i] = c;
        }
        return table;
    }
    static uint CrcUpdate(uint sum, byte[] bytes, int count)
    {
        for (int i = 0; i < count; i++) sum = CrcTable[(sum ^ bytes[i]) & 255] ^ (sum >> 8);
        return sum;
    }

    static async Task<(List<MetadataDigest> Files, string Sha256, string Crc32)> Fingerprint(
        string path, string branch, string upstream, CancellationToken token)
    {
        // Resolve each name through Git so linked worktrees and submodules use their actual metadata locations.
        var names = new List<string> { "HEAD", "index", "config", "packed-refs" };
        if (branch.Length > 0) names.Add("refs/heads/" + branch);
        if (upstream.Length > 0) names.Add("refs/remotes/" + upstream);
        var args = new List<string> { "rev-parse", "--path-format=absolute" };
        foreach (var name in names) { args.Add("--git-path"); args.Add(name); }
        var resolved = await RunGit(path, args, token);
        if (resolved.Code != 0) throw new IOException("Could not locate Git metadata.");
        var paths = resolved.Output.Split('\n').Select(p => p.TrimEnd('\r')).ToArray();
        if (names.Count != paths.Length) throw new IOException("Unexpected Git metadata paths.");
        var files = new List<MetadataDigest>();
        var block = new byte[65536];
        for (int i = 0; i < names.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(paths[i]) || (File.GetAttributes(paths[i]) & FileAttributes.ReparsePoint) != 0) continue;
            using var stream = new FileStream(paths[i], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                block.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            uint crc = uint.MaxValue;
            long length = 0;
            int n;
            while ((n = await stream.ReadAsync(block, token).ConfigureAwait(false)) > 0)
            {
                crc = CrcUpdate(crc, block, n);
                hash.AppendData(block, 0, n);
                length += n;
            }
            files.Add(new(names[i], length, (~crc).ToString("x8"), Convert.ToHexStringLower(hash.GetHashAndReset())));
        }
        var manifest = Encoding.UTF8.GetBytes(string.Join('\n', files.Select(f => f.File + "\0" + f.Sha256)));
        return (files, Convert.ToHexStringLower(SHA256.HashData(manifest)), (~CrcUpdate(uint.MaxValue, manifest, manifest.Length)).ToString("x8"));
    }
}
