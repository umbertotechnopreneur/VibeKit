/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; native sidebar navigation projections
 * Modified with AI: OpenAI Codex; restore semantic outline rows and file-based page labels
 * Human guidance: views consume the workspace model rather than owning file data
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using MarkVibe.Core;
using static MarkVibe.Core.UiStrings;

namespace MarkVibe.Windows;

/// <summary>Connects one outline row to the reader's render anchor and the corresponding source position.</summary>
internal sealed record DesktopOutlineTarget(string ElementId, string HeadingId, int HeadingLevel, ReadingPosition Position, string Fingerprint);

/// <summary>Describes a folder, document or parsed element in the native navigation tree.</summary>
internal sealed record DesktopNavigationItem(string Label, string Path, string Title, string Icon, string Tooltip,
    IReadOnlyList<DesktopNavigationItem> Children, DesktopOutlineTarget? Target = null)
{
    public bool IsFolder => Icon == "folder";
    public string Key => Target?.ElementId ?? Path;
}

/// <summary>Projects the current catalog and heading model into platform-independent tree rows.</summary>
internal static class DesktopNavigation
{
    /// <summary>Groups catalog paths into a hierarchy with folders preceding documents.</summary>
    public static IReadOnlyList<DesktopNavigationItem> Pages(IReadOnlyList<DocumentCard> cards) => Group(cards, "");

    /// <summary>Projects one file row independently of neighboring metadata and folder membership.</summary>
    public static DesktopNavigationItem Page(DocumentCard card)
    {
        var label = card.Path[(card.Path.LastIndexOf('/') + 1)..];
        var title = string.IsNullOrWhiteSpace(card.Title) ? label : card.Title;
        return new(label, card.Path, title, "file", title + "\n" + card.Path, []);
    }

    /// <summary>Groups parsed content under its surrounding headings without interpreting the rendered HTML.</summary>
    public static IReadOnlyList<DesktopNavigationItem> Outline(MarkdownModel? document)
    {
        if (document == null)
        {
            return [];
        }
        var roots = new List<DesktopNavigationItem>();
        var stack = new Stack<(int Level, List<DesktopNavigationItem> Children)>();
        var headings = document.Headings.ToLookup(heading => heading.Line);
        var sourceOffset = 0;
        var sourceLine = 1;
        foreach (var element in document.Elements.OrderBy(element => element.SourceStart))
        {
            var icon = OutlineIcon(element.Kind);
            if (icon == null)
            {
                continue;
            }
            while (sourceOffset < element.SourceStart && sourceOffset < document.Source.Length)
            {
                var character = document.Source[sourceOffset++];
                if (character == '\n' || (character == '\r' &&
                    (sourceOffset == document.Source.Length || document.Source[sourceOffset] != '\n')))
                {
                    sourceLine++;
                }
            }
            var headingLevel = icon == "heading" && int.TryParse(element.Kind.AsSpan(8), out var level) ? level : 0;
            var heading = headingLevel > 0 ? headings[sourceLine].FirstOrDefault(value => value.Level == headingLevel) : null;
            while (headingLevel > 0 && stack.TryPeek(out var parent) && parent.Level >= headingLevel)
            {
                stack.Pop();
            }

            var kind = headingLevel > 0 ? $"H{headingLevel}" : T(OutlineKind(element.Kind));
            var caption = OutlineCaption(element, heading, kind);
            var children = new List<DesktopNavigationItem>();
            var line = heading?.Line ?? sourceLine;
            var target = new DesktopOutlineTarget(element.Id, heading?.Id ?? "", headingLevel,
                new ReadingPosition(element.BlockId, 0, line), document.Fingerprint);
            var siblings = stack.TryPeek(out var current) ? current.Children : roots;
            var tooltip = kind + " · " + T("Source line {0}", line) + "\n" + caption;
            siblings.Add(new(caption, document.Path, document.Title, icon, tooltip, children, target));
            if (headingLevel > 0)
            {
                stack.Push((headingLevel, children));
            }
        }
        return roots;
    }

    /// <summary>Limits the outline to headings and the source-backed content kinds the sidebar navigates.</summary>
    private static string? OutlineIcon(string kind) => kind switch
    {
        "paragraph" or "image" or "code" or "table" => kind,
        "diagram" => "code",
        _ when kind.StartsWith("heading-", StringComparison.Ordinal) => "heading",
        _ => null
    };

    /// <summary>Names an outline content kind for localized tooltips and unlabeled content.</summary>
    private static string OutlineKind(string kind) => kind switch
    {
        "image" => "Image",
        "code" => "Code",
        "table" => "Table",
        "diagram" => "Diagram",
        _ => "Paragraph"
    };

    /// <summary>Uses a heading's plain text or the bounded parsed source excerpt as the row caption.</summary>
    private static string OutlineCaption(MarkdownElement element, Heading? heading, string fallback)
    {
        var source = element.Kind == "image" && element.AssetPath.Length > 0
            ? element.AssetPath[(element.AssetPath.LastIndexOf('/') + 1)..]
            : heading?.Text ?? element.Quote;
        var normalized = string.Join(" ", source.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length == 0 ? fallback : normalized;
    }

    /// <summary>Projects one canonical folder prefix without touching the local filesystem.</summary>
    private static IReadOnlyList<DesktopNavigationItem> Group(IReadOnlyList<DocumentCard> cards, string prefix)
    {
        var folders = new SortedDictionary<string, List<DocumentCard>>(StringComparer.Ordinal);
        var documents = new List<DesktopNavigationItem>();
        foreach (var card in cards)
        {
            var relative = card.Path[prefix.Length..];
            var slash = relative.IndexOf('/');
            if (slash < 0)
            {
                documents.Add(Page(card));
                continue;
            }
            var directory = relative[..slash];
            if (!folders.TryGetValue(directory, out var children))
            {
                children = [];
                folders.Add(directory, children);
            }
            children.Add(card);
        }
        return folders.Select(pair => new DesktopNavigationItem(pair.Key, prefix + pair.Key, pair.Key, "folder",
                prefix + pair.Key, Group(pair.Value, prefix + pair.Key + "/")))
            .Concat(documents.OrderBy(item => item.Path, StringComparer.Ordinal)).ToArray();
    }
}
