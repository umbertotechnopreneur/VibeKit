/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; read-only local workspace for the native reader
 * Human guidance: reuse the existing Markdown parser and native file boundaries
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using System.Text;
using MarkVibe.Core;
using MarkVibe.Native;

namespace MarkVibe.Reader.WinUI;

/// <summary>Holds one granted folder and provides bounded reads for its documents and images.</summary>
internal sealed class ReaderWorkspace(NativeFiles files, WorkspaceInfo info, IReadOnlyList<WorkspaceEntry> entries)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public WorkspaceInfo Info { get; } = info;
    public IReadOnlyList<WorkspaceEntry> Documents { get; } = entries;

    /// <summary>Enumerates Markdown paths without parsing or reading their contents.</summary>
    public static ReaderWorkspace Open(string dataDirectory, string folder, CancellationToken token)
    {
        var files = new NativeFiles(dataDirectory);
        var info = files.Open(folder) with { Capabilities = new WorkspaceCapabilities(CanExport: false) };
        var entries = files.Entries(info.Id, token).Where(entry => WorkspacePath.IsMarkdown(entry.Path)).ToArray();
        return new ReaderWorkspace(files, info, entries);
    }

    /// <summary>Parses only the document being opened, keeping its original source on the managed side.</summary>
    public async Task<MarkdownModel> ReadDocumentAsync(string path, CancellationToken token)
    {
        var bytes = await files.ReadAsync(Info.Id, path, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var start = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? Encoding.UTF8.Preamble.Length : 0;
        try
        {
            var source = StrictUtf8.GetString(bytes, start, bytes.Length - start);
            if (source.Contains('\0'))
            {
                throw new WorkspaceException("invalid-text", "Choose a UTF-8 Markdown file.");
            }
            return new MarkdownParser().Parse(path, source) with { Source = "" };
        }
        catch (DecoderFallbackException)
        {
            throw new WorkspaceException("invalid-text", "Choose a UTF-8 Markdown file.");
        }
    }

    /// <summary>Reads only supported images within the selected root for web-side validation and decoding.</summary>
    public Task<byte[]> ReadImageAsync(string path, CancellationToken token)
    {
        if (Path.GetExtension(path).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".svg"))
        {
            throw new WorkspaceException("unsupported-image", "Choose a supported image.");
        }
        return files.ReadAsync(Info.Id, path, token);
    }
}
