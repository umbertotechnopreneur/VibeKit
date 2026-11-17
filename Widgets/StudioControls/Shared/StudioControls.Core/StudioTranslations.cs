using System.Text.Json;

namespace VibeKit.StudioControls.Core;

public sealed record UiLanguage(string Code, string Label);

/// <summary>Presentation-only strings. Project text, filenames and OBS identifiers remain data.</summary>
public sealed class StudioTranslations
{
    public static IReadOnlyList<UiLanguage> Languages { get; } = Array.AsReadOnly(new[]
    {
        new UiLanguage("it", "Italiano"), new("en", "English"), new("vi", "Tiếng Việt"),
        new("fr", "Français"), new("de", "Deutsch"), new("ko", "한국어"),
        new("zh", "中文"), new("ja", "日本語")
    });
    private readonly Dictionary<string, string[]> strings;
    private readonly string[] prefixes;
    private static readonly IReadOnlyDictionary<string, int> LanguageIndices = Languages
        .Select((language, index) => (language.Code, index)).ToDictionary(item => item.Code, item => item.index);

    public static StudioTranslations LoadDefault()
    {
        using Stream stream = typeof(StudioTranslations).Assembly.GetManifestResourceStream("StudioControls.UI.json")
            ?? throw new InvalidDataException("UI catalog is missing.");
        return new StudioTranslations(stream);
    }

    public StudioTranslations(Stream stream)
    {
        using JsonDocument document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("languages", out JsonElement codes) || codes.ValueKind != JsonValueKind.Array ||
            !codes.EnumerateArray().Select(item => item.GetString()).SequenceEqual(Languages.Select(item => item.Code)))
            throw new InvalidDataException("UI catalog language order is invalid.");
        strings = document.RootElement.GetProperty("strings").Deserialize<Dictionary<string, string[]>>()
            ?? throw new InvalidDataException("UI catalog is empty.");
        if (strings.Any(pair => pair.Value is null || pair.Value.Length != Languages.Count || pair.Value.Any(string.IsNullOrEmpty)))
            throw new InvalidDataException("Every UI string needs all eight languages.");
        prefixes = strings.Keys.Where(key => key.EndsWith(':') || key.EndsWith('…') || key.EndsWith('.'))
            .OrderByDescending(key => key.Length).ToArray();
    }

    public string Text(string source, string language)
    {
        if (!LanguageIndices.TryGetValue(language, out int index) || language == "en" || source.Length == 0) return source;
        if (strings.TryGetValue(source, out string[]? exact)) return exact[index];
        string trimmed = source.Trim();
        if (strings.TryGetValue(trimmed, out exact))
        {
            int start = source.Length - source.TrimStart().Length;
            return source[..start] + exact[index] + source[source.TrimEnd().Length..];
        }
        // Protect the complete variable remainder, including newlines and paths.
        foreach (string prefix in prefixes)
            if (source.StartsWith(prefix, StringComparison.Ordinal))
                return strings[prefix][index] + source[prefix.Length..];
        if (source.Contains('\n')) return string.Join("\n", source.Split('\n').Select(line => Text(line, language)));
        if (source.Contains(" · ", StringComparison.Ordinal))
            return string.Join(" · ", source.Split(" · ").Select(part => Text(part, language)));
        int separator = source.LastIndexOf(' ');
        if (separator >= 0)
        {
            string number = source[(separator + 1)..];
            if (number.Any(char.IsDigit) && number.All(character => "0123456789:.,+-×%".Contains(character)))
                return Text(source[..separator], language) + " " + number;
        }
        return source;
    }
}
