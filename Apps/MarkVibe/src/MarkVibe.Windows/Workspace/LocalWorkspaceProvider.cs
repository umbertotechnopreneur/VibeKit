/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; direct Windows workspace provider
 * Human guidance: own document and workspace data outside the WinUI shell
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using System.Text;
using MarkVibe.Core;
using MarkVibe.Native;

namespace MarkVibe.Windows;

/// <summary>Connects the shared catalog to bounded local Markdown reads.</summary>
internal sealed class LocalWorkspaceProvider(NativeFiles files, WorkspaceInfo info) : IWorkspaceProvider
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public WorkspaceInfo Info { get; private set; } = info;

    /// <summary>Runs bounded folder enumeration away from the dispatcher and publishes its diagnostics on completion.</summary>
    public async Task<IReadOnlyList<WorkspaceEntry>> EnumerateAsync(CancellationToken token)
    {
        var id = Info.Id;
        var (entries, skippedNames) = await Task.Run(() =>
        {
            var result = files.Entries(id, out var namesOmitted, token);
            return (result, namesOmitted);
        }, token);
        token.ThrowIfCancellationRequested();
        Info = Info with { SkippedNames = skippedNames };
        return entries;
    }

    /// <summary>Decodes a Markdown page strictly and strips an optional UTF-8 BOM.</summary>
    public async Task<string> ReadDocumentAsync(string path, CancellationToken token)
    {
        var bytes = await files.ReadAsync(Info.Id, path, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        try
        {
            var offset = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? Encoding.UTF8.Preamble.Length : 0;
            var source = Utf8.GetString(bytes, offset, bytes.Length - offset);
            if (source.Contains('\0'))
            {
                throw new WorkspaceException("invalid-text", "This file does not appear to be a text document.");
            }
            return source;
        }
        catch (DecoderFallbackException)
        {
            throw new WorkspaceException("invalid-text", "Choose a UTF-8 Markdown file to read this page.");
        }
    }

    /// <summary>Image leases belong to the renderer; this provider exposes only catalog data.</summary>
    public Task<AssetLease> ReadAssetAsync(string path, string owner, CancellationToken token) =>
        throw new NotSupportedException("The document renderer owns image leases.");

    /// <summary>Completes a release because the provider owns no renderer assets.</summary>
    public Task ReleaseAssetsAsync(string owner) => Task.CompletedTask;

    /// <summary>Completes a refresh because every enumeration reads the current filesystem.</summary>
    public Task RefreshAsync(CancellationToken token) => Task.CompletedTask;

    /// <summary>Completes disposal because native reads hold handles only until their operation finishes.</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
