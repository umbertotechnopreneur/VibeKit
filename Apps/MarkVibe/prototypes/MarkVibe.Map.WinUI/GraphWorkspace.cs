/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; reuse the native workspace and Core graph projection in the Win2D prototype.
 * Human guidance: Umberto Giacobbi; preserve saved layouts and measure graph interaction independently of the reader.
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MarkVibe.Core;
using MarkVibe.Native;

namespace MarkVibe.Map.WinUI;

/// <summary>Owns a Win2D map's selection, bounded Core projection and in-memory editing session.</summary>
public sealed class GraphWorkspace
{
    private readonly DocumentCatalog _catalog;
    private readonly MapLayoutSession _layout;
    private readonly MapExploration _exploration = new();

    public MapSnapshot Map { get; private set; }
    public MapLayout Layout => _layout.Value;
    public string SelectedPath { get; private set; }
    public string? Warning { get; }

    /// <summary>Uses the reader's initial document choice and the saved atlas focus settings.</summary>
    internal GraphWorkspace(DocumentCatalog catalog, MapLayoutSession layout, string? warning)
    {
        _catalog = catalog;
        _layout = layout;
        Warning = warning;
        SelectedPath = catalog.Cards.FirstOrDefault(card => card.Path.Equals("README.md", StringComparison.OrdinalIgnoreCase))?.Path
            ?? catalog.Cards.FirstOrDefault()?.Path ?? "";
        _exploration.Refresh(catalog);
        Map = Project(SelectedPath);
    }

    /// <summary>Opens an interactive map from a directory, including full catalog preparation.</summary>
    public static async Task<GraphWorkspace> OpenAsync(string directory, CancellationToken cancellationToken = default)
    {
        var prepared = await PrepareAsync(directory, cancellationToken).ConfigureAwait(false);
        return await prepared.OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Prepares the same bounded metadata index used by the reader before timing a map opening.</summary>
    public static Task<PreparedGraphWorkspace> PrepareAsync(string directory, CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            // NativeFiles requires an existing session directory. The prototype never remembers a session
            // or writes settings, and its executable directory already exists independently of the selected folder.
            var files = new NativeFiles(AppContext.BaseDirectory);
            var info = files.Open(directory);
            await using var provider = new MapFileProvider(files, info);
            var catalog = new DocumentCatalog(new MarkdownParser());
            catalog.Seed(await provider.EnumerateAsync(cancellationToken).ConfigureAwait(false));
            await catalog.IndexAsync(provider, static (_, _) => { }, cancellationToken).ConfigureAwait(false);
            return new PreparedGraphWorkspace(files, info.Id, catalog);
        }, cancellationToken);
    }

    /// <summary>Selects a catalog member and applies Core's node, edge and focus limits to the updated projection.</summary>
    public MapSnapshot Select(string path)
    {
        if (path != SelectedPath && Layout.Nodes.ContainsKey(path))
        {
            Map = Project(path);
            SelectedPath = path;
        }
        return Map;
    }

    /// <summary>Validates and commits the final drag position in memory, leaving the workspace sidecar untouched.</summary>
    public void Move(string path, double x, double y)
    {
        if (!Layout.Nodes.TryGetValue(path, out var node) || (node.X == x && node.Y == y))
        {
            return;
        }
        _layout.Move(path, x, y);
    }

    /// <summary>Includes per-document attention facts for the visible catalog projection, as in the document map.</summary>
    private MapSnapshot Project(string path)
    {
        var map = _catalog.Map(path, neighborhood: false, focusDepth: Layout.Atlas?.FocusDepth ?? 0,
            priorityPaths: _exploration.PriorityPaths());
        return map with
        {
            Attention = map.Nodes.ToDictionary(node => node.Path, node => _exploration.Attention[node.Path], StringComparer.Ordinal)
        };
    }

    /// <summary>Reads local Markdown using the application's existing path, size, UTF-8 and cancellation boundaries.</summary>
    private sealed class MapFileProvider(NativeFiles files, WorkspaceInfo workspace) : IWorkspaceProvider
    {
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);
        public WorkspaceInfo Info { get; } = workspace with { Capabilities = new WorkspaceCapabilities(CanExport: false) };

        /// <summary>Uses the native traversal's depth, file-count and reparse-point checks.</summary>
        public Task<IReadOnlyList<WorkspaceEntry>> EnumerateAsync(CancellationToken cancellationToken)
            => Task.FromResult(files.Entries(Info.Id, cancellationToken));

        /// <summary>Decodes Markdown identically to the browser's fatal UTF-8 decoder, including the optional leading BOM.</summary>
        public async Task<string> ReadDocumentAsync(string path, CancellationToken cancellationToken)
        {
            var bytes = await files.ReadAsync(Info.Id, path, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var contentStart = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? Encoding.UTF8.Preamble.Length : 0;
                var source = StrictUtf8.GetString(bytes, contentStart, bytes.Length - contentStart);
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

        /// <summary>The graph renders catalog metadata and never requests reader assets.</summary>
        public Task<AssetLease> ReadAssetAsync(string path, string owner, CancellationToken cancellationToken)
            => throw new NotSupportedException("The graph does not load document assets.");

        /// <summary>This provider owns no asset leases.</summary>
        public Task ReleaseAssetsAsync(string owner) => Task.CompletedTask;

        /// <summary>Opening another directory prepares a fresh inventory.</summary>
        public Task RefreshAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException("Reopen the directory to refresh the graph.");

        /// <summary>All native file handles close as soon as their reads complete.</summary>
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>A fully enumerated source catalog, reusable for timing the transition from reader-ready data to a drawn map.</summary>
public sealed class PreparedGraphWorkspace
{
    private readonly NativeFiles _files;
    private readonly string _workspaceId;
    private readonly DocumentCatalog _catalog;

    /// <summary>Retains the completed index separately from the map session and its saved sidecar.</summary>
    internal PreparedGraphWorkspace(NativeFiles files, string workspaceId, DocumentCatalog catalog)
    {
        _files = files;
        _workspaceId = workspaceId;
        _catalog = catalog;
    }

    /// <summary>Reads the current sidecar and reconciles it against every enumerated document, then creates the initial projection.</summary>
    public Task<GraphWorkspace> OpenAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            MapLayoutSession layout;
            var warnings = new List<string>();
            try
            {
                var saved = await _files.ReadMapAsync(_workspaceId, cancellationToken).ConfigureAwait(false);
                layout = new MapLayoutSession(saved.Json == null ? null : MapLayoutCodec.Read(saved.Json));
                layout.EnsureDocuments(_catalog.Cards);
            }
            catch (Exception error) when (error is WorkspaceException or IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                // Keep an unreadable or unsupported file intact; the prototype uses a temporary tree for this opening.
                warnings.Add("Unable to use the saved map layout. Showing the default tree.");
                layout = new MapLayoutSession();
                layout.EnsureDocuments(_catalog.Cards);
            }
            if (_catalog.Warnings.FirstOrDefault() is { } indexWarning)
            {
                warnings.Add(indexWarning);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var warning = warnings.Count == 0 ? null : string.Join(Environment.NewLine, warnings);
            return new GraphWorkspace(_catalog, layout, warning);
        }, cancellationToken);
    }
}
