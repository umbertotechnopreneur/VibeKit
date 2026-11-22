using System.Globalization;
using System.Text;

namespace WorkspaceRepo;

/// <summary>Measures and fits plain text using the same grapheme and cell rules as the terminal grid.</summary>
internal static class TerminalText
{
    public static int Measure(string text)
    {
        int width = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext()) width += CellWidth(Printable(elements.GetTextElement()));
        return width;
    }

    public static string Fit(string text, int width)
    {
        if (width <= 0) return "";
        if (PrefixLength(text, width) == text.Length) return text;
        return text[..PrefixLength(text, width - 1)] + "…";
    }

    public static string Abbreviate(string text, int width)
    {
        if (width <= 2) return Fit(text, width);
        if (PrefixLength(text, width) == text.Length) return text;
        int prefixLength = PrefixLength(text, Math.Clamp(width / 4, 1, 14));
        int remaining = width - 1 - Measure(text[..prefixLength]);
        var starts = StringInfo.ParseCombiningCharacters(text);
        int suffixStart = text.Length;
        for (int i = starts.Length - 1; i >= 0; i--)
        {
            int size = CellWidth(Printable(text[starts[i]..suffixStart]));
            if (size > remaining) break;
            remaining -= size;
            suffixStart = starts[i];
        }
        return text[..prefixLength] + "…" + text[suffixStart..];
    }

    public static IEnumerable<string> Wrap(string? value, int width)
    {
        if (width <= 0) yield break;
        foreach (var line in (value ?? "").Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            string remaining = line;
            while (remaining.Length > 0)
            {
                int cut = PrefixLength(remaining, width);
                if (cut == remaining.Length) break;
                if (cut == 0)
                {
                    // A wide glyph cannot fit in a one-cell pane. Consume it so wrapping still advances.
                    yield return "…";
                    remaining = remaining[StringInfo.GetNextTextElementLength(remaining.AsSpan())..];
                    continue;
                }
                int space = remaining.LastIndexOf(' ', cut - 1, cut);
                if (space > 0 && Measure(remaining[..space]) >= width / 2) cut = space;
                yield return remaining[..cut];
                remaining = remaining[cut..].TrimStart();
            }
            if (remaining.Length > 0 || line.Length == 0) yield return remaining;
        }
    }

    public static string RemoveLast(string text)
    {
        if (text.Length == 0) return text;
        return text[..StringInfo.ParseCombiningCharacters(text)[^1]];
    }

    /// <summary>Returns a UTF-16 boundary after the last whole grapheme that fits.</summary>
    public static int PrefixLength(string text, int width)
    {
        int columns = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            columns += CellWidth(Printable(elements.GetTextElement()));
            if (columns > width) return elements.ElementIndex;
        }
        return text.Length;
    }

    /// <summary>Neutralizes controls and directional formatting before text reaches a terminal.</summary>
    public static string Printable(string element)
    {
        if (!Rune.TryGetRuneAt(element, 0, out var first)) return "�";
        if (first.Value >= 0x20 && first.Value <= 0x7e && element.Length == 1) return element;
        if (Rune.GetUnicodeCategory(first) is UnicodeCategory.Format) return " ";
        foreach (var rune in element.EnumerateRunes())
        {
            if (Rune.IsControl(rune) ||
                (Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format && rune.Value != 0x200d)) return " ";
        }
        return element;
    }

    public static int CellWidth(string element)
    {
        if (element.Length == 1 && element[0] < 0x1100) return 1;
        if (!Rune.TryGetRuneAt(element, 0, out var rune)) return 1;
        var value = rune.Value;
        return element.Contains('\ufe0f') || element.Contains('\u20e3') || value is >= 0x1100 and <= 0x115f
            or 0x231a or 0x231b or 0x23f0 or 0x23f3 or >= 0x23e9 and <= 0x23ec
            or 0x25fd or 0x25fe or 0x2614 or 0x2615 or >= 0x2648 and <= 0x2653
            or 0x267f or 0x2693 or 0x26a1 or 0x26aa or 0x26ab or 0x26bd or 0x26be
            or 0x26c4 or 0x26c5 or 0x26ce or 0x26d4 or 0x26ea or 0x26f2 or 0x26f3
            or 0x26f5 or 0x26fa or 0x26fd or 0x2705 or 0x270a or 0x270b or 0x2728
            or 0x274c or 0x274e or >= 0x2753 and <= 0x2755 or 0x2757
            or >= 0x2795 and <= 0x2797 or 0x27b0 or 0x27bf or 0x2b1b or 0x2b1c or 0x2b50 or 0x2b55
            or >= 0x2e80 and <= 0xa4cf or >= 0xac00 and <= 0xd7a3
            or >= 0xf900 and <= 0xfaff or >= 0xff01 and <= 0xff60
            or >= 0x1f1e6 and <= 0x1f1ff or >= 0x1f300 and <= 0x1faff
            or >= 0x20000 and <= 0x3fffd ? 2 : 1;
    }
}
