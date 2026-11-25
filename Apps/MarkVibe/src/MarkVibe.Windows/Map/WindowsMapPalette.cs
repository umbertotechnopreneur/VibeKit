/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; shared light and dark graph colors
 * Human guidance: Umberto Giacobbi; give the native graph ten color families that follow the application theme
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using MarkVibe.Core;
using Windows.UI;

namespace MarkVibe.Windows;

/// <summary>Defines the semantic colors shared by Win2D drawing and the native map controls.</summary>
internal sealed record WindowsMapPalette(Color Canvas, Color Panel, Color Border, Color CardBackground, Color HoverBackground,
    Color SelectedBackground, Color CardText, Color DetailText, Color Accent, Color IncomingColor, Color OutgoingColor,
    Color RouteColor, Color AttentionColor, Color ReferenceColor, Color VisitedColor)
{
    private static readonly Dictionary<string, (WindowsMapPalette Light, WindowsMapPalette Dark)> Palettes =
        GraphThemeCatalog.Options.ToDictionary(theme => theme.Id,
            theme => (Create(theme.Light, dark: false), Create(theme.Dark, dark: true)), StringComparer.Ordinal);

    public static WindowsMapPalette Dark { get; } = Palettes["system"].Dark;

    /// <summary>Returns the cached light or dark palette for a supported or defaulted preset.</summary>
    public static WindowsMapPalette Resolve(string? preset, bool dark)
    {
        var palette = Palettes[GraphThemeCatalog.Normalize(preset)];
        return dark ? palette.Dark : palette.Light;
    }

    /// <summary>Returns the visible card fill for the saved color and current interaction state.</summary>
    public Color CardFill(Color saved, bool selected, float hover = 0) =>
        Blend(selected ? SelectedBackground : Blend(CardBackground, HoverBackground, hover), saved, .014f);

    /// <summary>Uses one hue with 3:1 contrast on normal, hovered and selected opaque card fills.</summary>
    public Color IconColor(Color saved)
    {
        var normal = Luminance(CardFill(saved, selected: false));
        var hover = Luminance(CardFill(saved, selected: false, hover: 1));
        var selected = Luminance(CardFill(saved, selected: true));
        for (var step = 0; step <= 10; step++)
        {
            var tint = Blend(saved, CardText, step / 10f);
            var light = Luminance(tint);
            if (IconContrast(light, normal) && IconContrast(light, hover) && IconContrast(light, selected))
            {
                return tint;
            }
        }
        return CardText;
    }

    /// <summary>Checks the minimum contrast for a graphic against an opaque background.</summary>
    private static bool IconContrast(double foreground, double background) =>
        (Math.Max(foreground, background) + .05) / (Math.Min(foreground, background) + .05) >= 3;

    /// <summary>Returns relative luminance after linearizing the sRGB channels.</summary>
    private static double Luminance(Color color)
    {
        static double Linear(byte channel)
        {
            var s = channel / 255.0;
            return s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4);
        }
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }

    /// <summary>Uses each preset's distinct link colors and derives consistent, readable graph surfaces.</summary>
    private static WindowsMapPalette Create(GraphThemeColors preset, bool dark)
    {
        var canvas = Rgb(preset.Canvas);
        var accent = Rgb(preset.Accent);
        var white = Rgb(0xFFFFFF);
        var black = Rgb(0x000000);
        var panel = Blend(canvas, white, dark ? .07f : .62f);
        var card = Blend(canvas, Rgb(dark ? 0x393833 : 0xFFFDF7), dark ? .47f : .85f);
        var hover = Blend(card, accent, dark ? .055f : .025f);
        var selected = Blend(card, accent, dark ? .075f : .035f);
        var text = dark ? Blend(white, canvas, .035f) : Blend(black, canvas, .13f);
        var detail = dark ? Blend(white, canvas, .21f) : Blend(black, canvas, .30f);
        var border = Blend(panel, detail, dark ? .26f : .31f);
        return new WindowsMapPalette(canvas, panel, border, card, hover, selected, text, detail, accent, Rgb(preset.Incoming),
            Rgb(preset.Outgoing), Rgb(dark ? 0xF3CE80 : 0x805307), Rgb(dark ? 0xFFAD78 : 0xA6360F),
            Rgb(dark ? 0xF995D5 : 0x9B266F), Blend(detail, accent, .27f));
    }

    /// <summary>Mixes opaque surfaces in sRGB to preserve the preset hue.</summary>
    private static Color Blend(Color start, Color end, float factor) => Color.FromArgb(255,
        (byte)float.Lerp(start.R, end.R, factor), (byte)float.Lerp(start.G, end.G, factor), (byte)float.Lerp(start.B, end.B, factor));

    /// <summary>Creates an opaque Windows color from a packed sRGB value.</summary>
    private static Color Rgb(int value) => Color.FromArgb(255, (byte)(value >> 16), (byte)(value >> 8), (byte)value);
}
