/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; service-owned document menu commands; 2026-10-25
 * Human guidance: Umberto Giacobbi; model, services and presentation separation
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
namespace MarkVibe.Core;

/// <summary>Describes platform-independent file and source-backed document commands.</summary>
public static class DocumentCommands
{
    /// <summary>Builds file commands shared by graph cards, folders and document surfaces.</summary>
    public static IReadOnlyList<DocumentMenuItem> CreateFileActions(
        bool localFiles, bool markdown, bool folder, bool hasSelection)
    {
        var items = new List<DocumentMenuItem>();
        if (!folder)
        {
            items.Add(new("copySelection", "Copy", "copy", hasSelection));
        }
        if (markdown && !folder)
        {
            items.Add(new("copyFile", "Copy entire file content", "copy"));
        }
        if (!folder || localFiles)
        {
            items.Add(new("copyPath", "Copy file path", "link"));
        }
        if (localFiles)
        {
            if (markdown && !folder)
            {
                items.Add(new("browser", "Open in browser", "map-globe"));
            }
            items.Add(new("reveal", "Show in File Explorer", "folder"));
            items.Add(new("terminal", "Open in terminal", "code"));
            if (!folder)
            {
                items.Add(new("delete", "Delete file…", "delete"));
            }
        }
        return items;
    }

    /// <summary>Adds only source-backed commands that apply to the clicked current document.</summary>
    public static IReadOnlyList<DocumentMenuItem> Create(
        FileMenuTarget target, bool localFiles, MarkdownModel? document, bool readerExpanded)
    {
        var items = CreateFileActions(localFiles, WorkspacePath.IsMarkdown(target.Path),
            target.IsFolder, target.SelectedText.Length > 0).ToList();
        if (target.IsFolder || document?.Path != target.Path)
        {
            return items;
        }

        var context = DocumentContentService.Describe(document, target);
        var contextual = new List<DocumentMenuItem>
        {
            new("find", "Find in document", "search"),
            new("column", "Column", "reader-column", Checked: !readerExpanded),
            new("expandedColumn", "Expanded column", "reader-expanded", Checked: readerExpanded)
        };
        if (context.CanGoToSource)
        {
            contextual.Add(new("sourceHere", "Go to source here", "code"));
        }
        if (context.CanCopySection)
        {
            contextual.Add(new("copySection", "Copy link to section", "link"));
        }
        if (context.CanCopyTable)
        {
            contextual.Add(new("copyTableTsv", "For Excel", "copy", Group: "Copy table"));
            contextual.Add(new("copyTableMarkdown", "Markdown", "copy", Group: "Copy table"));
        }
        if (context.CanCopyCode)
        {
            contextual.Add(new("copyCode", "Copy code block", "code"));
        }
        contextual.Add(new("exportPdf", "PDF", "pdf", Group: "Export"));
        contextual.Add(new("exportHtml", "HTML", "code", Group: "Export"));
        items.InsertRange(Math.Min(2, items.Count), contextual);
        return items;
    }
}
