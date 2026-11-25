/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; paired native graph color presets
 * Human guidance: Umberto Giacobbi; offer ten built-in color themes with independent font preferences
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
namespace MarkVibe.Core;

/// <summary>Defines packed sRGB colors for the graph and its matching shell accent.</summary>
public sealed record GraphThemeColors(int Canvas, int Accent, int Incoming, int Outgoing);

/// <summary>Defines one persistable color preset with light and dark variants.</summary>
public sealed record GraphThemeOption(string Id, string Label, GraphThemeColors Light, GraphThemeColors Dark);

/// <summary>Lists the supported graph presets and resolves a missing or unknown preference to MarkVibe.</summary>
public static class GraphThemeCatalog
{
    public static IReadOnlyList<GraphThemeOption> Options { get; } = Array.AsReadOnly<GraphThemeOption>(
    [
        new("system", "MarkVibe",
            new(0xF8F5ED, 0x52633C, 0x945232, 0x496644),
            new(0x1D201C, 0xCBDBA9, 0xECB497, 0xB1CB90)),
        new("arctic", "Arctic",
            new(0xEDF5F9, 0x12637E, 0x0A6C66, 0x4C50A7),
            new(0x111E28, 0x80D9F5, 0x66E1D5, 0xBDBBFF)),
        new("aurora", "Aurora",
            new(0xEBF5F0, 0x116C59, 0x236E38, 0x633CA2),
            new(0x10231F, 0x72E8BD, 0x9BE58B, 0xBDA7FF)),
        new("amethyst", "Amethyst",
            new(0xF2EDFA, 0x663CAA, 0x265C78, 0x87355F),
            new(0x1B1529, 0xC5A8FF, 0x82D5EA, 0xF19DCE)),
        new("ember", "Ember",
            new(0xFAEFEA, 0x9D3D20, 0x176864, 0x93334F),
            new(0x241710, 0xFFB18A, 0x77DDD2, 0xF2A0BE)),
        new("forest", "Forest",
            new(0xEEF4E8, 0x32652F, 0x20616B, 0x705317),
            new(0x172215, 0xA6D98D, 0x76D8DC, 0xE5CA86)),
        new("rosewood", "Rosewood",
            new(0xFAEDF2, 0x963763, 0x275F82, 0x733C8D),
            new(0x281722, 0xF7A5CB, 0x93CCF6, 0xD8A7F0)),
        new("sand", "Sand",
            new(0xF7F0E2, 0x805309, 0x1E6865, 0x8F422F),
            new(0x252017, 0xEAC781, 0x7BD7D0, 0xF0AA91)),
        new("solar", "Solar",
            new(0xF9F5DF, 0x716009, 0x236760, 0x604999),
            new(0x272413, 0xF7DB72, 0x84D7CA, 0xCCB1F6)),
        new("slate", "Slate",
            new(0xEEF0F2, 0x4A5870, 0x226767, 0x6C4C81),
            new(0x1D2229, 0xC1CFE3, 0x87D4D4, 0xD5B4E5))
    ]);

    /// <summary>Resolves a persisted identifier, falling back to the default color family.</summary>
    public static GraphThemeOption Resolve(string? id) =>
        Options.FirstOrDefault(theme => theme.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? Options[0];

    /// <summary>Returns the canonical identifier supported by the catalog.</summary>
    public static string Normalize(string? id) => Resolve(id).Id;
}
