using System.Text.Json.Nodes;

namespace VibeKit.StudioControls.Core;

/// <summary>Prepares an import file only. It never opens or changes the user's OBS configuration.</summary>
public static class ScenePackage
{
    private const string AssetToken = "${ASSET_ROOT}/";

    public static string Prepare(string templatePath, string assetsRoot, string outputPath)
    {
        string output = Path.GetFullPath(outputPath);
        if (File.Exists(output)) throw new IOException("Choose a new scene import filename; existing files are preserved.");
        string root = Path.GetFullPath(assetsRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The studio assets directory does not exist.");
        var info = new FileInfo(templatePath);
        if (info.Length > 8 * 1024 * 1024) throw new InvalidDataException("The scene template exceeds the size limit.");
        JsonNode document = JsonNode.Parse(File.ReadAllText(templatePath)) ?? throw new InvalidDataException("The scene template is empty.");
        if (document is not JsonObject) throw new InvalidDataException("The scene template must contain an object.");
        Replace(document, root);
        string directory = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".studio-scenes-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var writer = new System.Text.Json.Utf8JsonWriter(stream, new() { Indented = true });
                document.WriteTo(writer);
                writer.Flush();
                stream.Flush(true);
            }
            File.Move(temporary, output, overwrite: false);
        }
        finally { File.Delete(temporary); }
        return output;
    }

    private static void Replace(JsonNode node, string root)
    {
        if (node is JsonObject dictionary)
        {
            foreach (string key in dictionary.Select(pair => pair.Key).ToArray())
                ReplaceChild(dictionary[key], value => dictionary[key] = value, root);
        }
        else if (node is JsonArray array)
        {
            for (int index = 0; index < array.Count; index++)
            {
                int current = index;
                ReplaceChild(array[index], value => array[current] = value, root);
            }
        }
    }

    private static void ReplaceChild(JsonNode? node, Action<string> assign, string root)
    {
        if (node is JsonValue scalar && scalar.TryGetValue(out string? value) && value is not null && value.Contains("${ASSET_ROOT}", StringComparison.Ordinal))
        {
            if (!value.StartsWith(AssetToken, StringComparison.Ordinal)) throw new InvalidDataException("Invalid scene asset placeholder.");
            string relative = value[AssetToken.Length..];
            if (relative.Contains('\\') || Path.IsPathRooted(relative)) throw new InvalidDataException("Scene assets must use relative paths.");
            string resolved = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            string prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
            StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!resolved.StartsWith(prefix, comparison) || !File.Exists(resolved))
                throw new InvalidDataException("A scene asset is missing or lies outside the asset directory.");
            assign(resolved);
        }
        else if (node is not null) Replace(node, root);
    }
}
