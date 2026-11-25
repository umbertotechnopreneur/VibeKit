/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; material choices for the Markdown reader
 * Human guidance: offer ten paper textures with native samples
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
namespace MarkVibe.Core;

/// <summary>Identifies a bundled pair of light and dark paper tiles and its localizable name.</summary>
public sealed record ReaderPaper(string Id, string Label)
{
    public const string Default = "ivory";
    public const string ThemeBackground = "none";
    public static IReadOnlyList<ReaderPaper> Options { get; } = Array.AsReadOnly<ReaderPaper>(
    [
        new(Default, "Ivory"), new("antique", "Antique paper"), new("cotton", "Cotton rag"),
        new("vellum", "Vellum"), new("recycled", "Recycled pulp"), new("kraft", "Kraft"),
        new("washi", "Washi"), new("watercolor", "Watercolor paper"), new("newsprint", "Newsprint"), new("blue", "Blue paper")
    ]);

    /// <summary>Retains an explicit untextured background and falls back to ivory for missing or unsupported choices.</summary>
    public static string Normalize(string? id) => id == ThemeBackground
        ? ThemeBackground : Options.FirstOrDefault(option => option.Id == id)?.Id ?? Default;
}
