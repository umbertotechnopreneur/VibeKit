/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; native graph commands and revision-checked workspace persistence
 * Modified with AI: OpenAI Codex; align search, map preview, trail and save semantics with the browser map
 * Human guidance: reuse the current map schema from the intermediate model during the WinUI migration
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using System.ComponentModel;
using System.Text;
using MarkVibe.Core;
using MarkVibe.Native;

namespace MarkVibe.Windows;

/// <summary>Owns a local workspace map, its transient exploration state and the revision of its current layout.</summary>
internal sealed class WindowsMapModel(NativeFiles files, WorkspaceInfo workspace, DocumentCatalog catalog)
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly MapExploration _exploration = new();
    private MapLayoutSession _session = new();
    private string _revision = "";
    private bool _canWrite = true;
    private long _metadataRevision = -1;
    private string _routeStart = "";
    private string _routeEnd = "";
    private string? _lastVisited;
    private string? _previewPath;

    public string WorkspaceId => workspace.Id;
    public string SelectedPath { get; private set; } = "";
    public string RouteStart => _routeStart;
    public string Message { get; private set; } = "";
    public bool Dirty => _session.Dirty;
    public bool Depth { get; private set; } = true;
    public bool Advanced { get; private set; }
    public bool CanWrite => _canWrite;
    public MapLayout Layout => _session.Value;
    public MapAtlasSettings Atlas => Layout.Atlas ?? new();
    public MapExplorationSnapshot Exploration => _exploration.Snapshot();
    public IReadOnlyList<DocumentCard> Cards => catalog.Cards;
    public MapSnapshot Snapshot { get; private set; } = new([], [], [], [], 0, false, new Dictionary<string, int>());

    /// <summary>Checks whether a live controller already owns the given catalog.</summary>
    public bool IsCurrent(WorkspaceInfo info, DocumentCatalog current) => workspace.Id == info.Id && ReferenceEquals(catalog, current);

    /// <summary>Loads only the current layout schema and preserves unreadable or externally changed files.</summary>
    public async Task ReloadAsync(string? selectedPath, CancellationToken token)
    {
        await _saveGate.WaitAsync(token);
        try
        {
            var stored = await files.ReadMapAsync(workspace.Id, token);
            token.ThrowIfCancellationRequested();
            var layout = stored.Json == null ? null : MapLayoutCodec.Read(stored.Json);
            _session = new MapLayoutSession(layout);
            _revision = stored.Revision;
            _canWrite = workspace.Capabilities.CanWriteSidecars && stored.CanWrite;
            Message = "";
        }
        catch (Exception error) when (error is WorkspaceException or IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            _session = new MapLayoutSession();
            _revision = "";
            _canWrite = false;
            Message = error is DecoderFallbackException
                ? "The map layout could not be read. The original file is preserved; download a separate copy of your changes."
                : error.Message;
        }
        finally
        {
            _saveGate.Release();
        }

        _previewPath = null;
        _routeStart = "";
        _routeEnd = "";
        _metadataRevision = -1;
        Refresh(selectedPath);
    }

    /// <summary>Imports recent successful reader visits before the first map projection, without changing selection.</summary>
    public void SeedTrail(IEnumerable<string> visitedPaths)
    {
        var known = catalog.Cards.Select(card => card.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var path in visitedPaths.Where(known.Contains))
        {
            _exploration.RecordVisit(path);
        }
    }

    /// <summary>Reconciles only complete catalog membership and refreshes the current bounded graph projection.</summary>
    public void Refresh(string? visitedPath = null)
    {
        if (_metadataRevision != catalog.MetadataRevision)
        {
            _session.EnsureDocuments(catalog.Cards);
            _metadataRevision = catalog.MetadataRevision;
        }
        _exploration.Refresh(catalog);
        if (visitedPath != null && visitedPath != _lastVisited && Layout.Nodes.ContainsKey(visitedPath))
        {
            SelectedPath = visitedPath;
            _exploration.RecordVisit(visitedPath);
            _lastVisited = visitedPath;
        }
        if (!Layout.Nodes.ContainsKey(SelectedPath))
        {
            SelectedPath = catalog.Cards.FirstOrDefault()?.Path ?? "";
        }
        if (_previewPath != null && !Layout.Nodes.ContainsKey(_previewPath))
        {
            _previewPath = null;
        }
        if ((_routeStart.Length > 0 && !Layout.Nodes.ContainsKey(_routeStart)) ||
            (_routeEnd.Length > 0 && !Layout.Nodes.ContainsKey(_routeEnd)))
        {
            _routeStart = "";
            _routeEnd = "";
        }

        Project();
    }

    /// <summary>Searches every title and path by all terms, prioritizing exact titles and title prefixes.</summary>
    public IReadOnlyList<DocumentCard> Search(string query)
    {
        query = query.Trim();
        if (query.Length == 0)
        {
            return [];
        }

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return catalog.Cards
            .Where(card => terms.All(term => card.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                card.Path.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(card => card.Title.Equals(query, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(card => card.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .ThenBy(card => card.Path, StringComparer.Ordinal)
            .Take(20)
            .ToArray();
    }

    /// <summary>Gets a document that is still part of the workspace.</summary>
    public DocumentCard? Document(string path) => catalog.FindCard(path);

    /// <summary>Supplies the inspector with every known incoming page and outgoing destination.</summary>
    public (IReadOnlyList<DocumentCard> Incoming, IReadOnlyList<DocumentLink> Outgoing) DocumentRelations(string path) => catalog.DocumentRelations(path);

    /// <summary>Selects an inspected document without recording a successful full reader visit.</summary>
    public void Select(string path, bool revealFolder = false)
    {
        if (!Layout.Nodes.ContainsKey(path))
        {
            return;
        }
        SelectedPath = path;
        if (revealFolder && Atlas.CollapsedFolders?.Contains(FolderOf(path), StringComparer.Ordinal) == true)
        {
            _session.SetAtlas(Atlas with { CollapsedFolders = Atlas.CollapsedFolders.Where(folder => folder != FolderOf(path)).ToArray() });
        }
        Project();
    }

    /// <summary>Reveals a previewed document without recording a reader visit or saving an island expansion.</summary>
    public void SetPreviewPath(string? path)
    {
        _previewPath = path != null && Layout.Nodes.ContainsKey(path) ? path : null;
        if (_previewPath != null)
        {
            Select(_previewPath);
        }
    }

    /// <summary>Reports the displayed collapse state, including the temporary exception for the open preview.</summary>
    public bool IsFolderCollapsed(string path) => Atlas.CollapsedFolders?.Contains(path, StringComparer.Ordinal) == true &&
        (_previewPath == null || FolderOf(_previewPath) != path);

    /// <summary>Moves one card after a native gesture commits its last valid logical location.</summary>
    public void Move(string path, double x, double y) => _session.Move(path, x, y);

    /// <summary>Resizes one card after its native size preview is committed.</summary>
    public void Resize(string path, double width, double height) => _session.Resize(path, width, height);

    /// <summary>Updates the independent color and icon of one document card.</summary>
    public void Style(string path, string color, string icon) => _session.Style(path, color, icon);

    /// <summary>Positions the full catalog with a managed arrangement and turns on island grouping for Folders.</summary>
    public void Arrange(string preset)
    {
        _session.Arrange(catalog.Cards, preset);
        var grouped = preset == "folders";
        if (Atlas.FolderGroups != grouped)
        {
            _session.SetAtlas(Atlas with { FolderGroups = grouped });
        }
        Project();
    }

    /// <summary>Restores initial card geometry and appearance while preserving named views.</summary>
    public void Reset()
    {
        _session.Reset(catalog.Cards);
        ClearRoute();
    }

    /// <summary>Applies a validated distance without changing coordinates.</summary>
    public void SetDistance(int depth)
    {
        if (depth != Atlas.FocusDepth)
        {
            _session.SetAtlas(Atlas with { FocusDepth = depth });
            Project();
        }
    }

    /// <summary>Toggles a known island without changing its documents' saved geometry.</summary>
    public void ToggleFolder(string path)
    {
        if (Snapshot.Folders?.Any(folder => folder.Path == path) != true)
        {
            return;
        }
        var collapsed = (Atlas.CollapsedFolders ?? []).ToHashSet(StringComparer.Ordinal);
        if (!collapsed.Add(path))
        {
            collapsed.Remove(path);
        }
        _session.SetAtlas(Atlas with { CollapsedFolders = collapsed.Order(StringComparer.Ordinal).ToArray() });
    }

    /// <summary>Stores a named camera and current atlas presentation using the shared schema limits.</summary>
    public void SaveView(string name, MapCamera camera)
    {
        name = name.Trim();
        var atlas = Atlas;
        var views = (atlas.Views ?? []).Where(view => !view.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (views.Count >= MapAtlas.MaximumViews)
        {
            throw new WorkspaceException("map-view-limit", "Eight views are already saved. Delete a view or reuse its name.");
        }
        views.Add(new MapViewpoint(name, camera, SelectedPath, atlas.FocusDepth, atlas.FolderGroups, atlas.CollapsedFolders ?? []));
        _session.SetAtlas(atlas with { Views = views });
    }

    /// <summary>Restores saved atlas settings and an existing selection before returning the saved camera.</summary>
    public MapCamera RestoreView(MapViewpoint view)
    {
        view = CurrentView(view.Name);
        _session.SetAtlas(Atlas with { FocusDepth = view.FocusDepth, FolderGroups = view.FolderGroups, CollapsedFolders = view.CollapsedFolders });
        if (Layout.Nodes.ContainsKey(view.SelectedPath))
        {
            SelectedPath = view.SelectedPath;
        }
        ClearRoute();
        return view.Camera;
    }

    /// <summary>Removes only the specified named viewpoint.</summary>
    public void DeleteView(MapViewpoint view)
    {
        var current = CurrentView(view.Name);
        _session.SetAtlas(Atlas with { Views = (Atlas.Views ?? []).Where(item => item != current).ToArray() });
    }

    /// <summary>Uses current model data even if a control still holds an earlier view snapshot.</summary>
    private MapViewpoint CurrentView(string name) => Atlas.Views?.FirstOrDefault(view => view.Name == name)
        ?? throw new WorkspaceException("missing-map-view", "This saved view is unavailable.");

    /// <summary>Sets the current selection as the starting point of a future shortest path.</summary>
    public void StartRoute()
    {
        _routeStart = SelectedPath;
        _routeEnd = "";
        Project();
    }

    /// <summary>Finds the shortest route and reveals any collapsed folders on it.</summary>
    public void FinishRoute()
    {
        if (_routeStart.Length == 0)
        {
            return;
        }
        _routeEnd = SelectedPath;
        var route = catalog.FindRoute(_routeStart, _routeEnd).Select(FolderOf).ToHashSet(StringComparer.Ordinal);
        var collapsed = Atlas.CollapsedFolders ?? [];
        var remaining = collapsed.Where(folder => !route.Contains(folder)).ToArray();
        if (remaining.Length != collapsed.Count)
        {
            _session.SetAtlas(Atlas with { CollapsedFolders = remaining });
        }
        Project();
    }

    /// <summary>Clears both route endpoints and restores an ordinary graph projection.</summary>
    public void ClearRoute()
    {
        _routeStart = "";
        _routeEnd = "";
        Project();
    }

    /// <summary>Controls temporary depth emphasis independently of the persisted atlas.</summary>
    public void SetDepth(bool value) => Depth = value;

    /// <summary>Toggles statistics labels independently of workspace source and layout.</summary>
    public void SetAdvanced(bool value) => Advanced = value;

    /// <summary>Enables the transient incoming and outgoing link emphasis.</summary>
    public void SetLens(bool value) => _exploration.LensEnabled = value;

    /// <summary>Enables toggling a card into the comparison selection without a modifier.</summary>
    public void SetPicking(bool value) => _exploration.SelectionEnabled = value;

    /// <summary>Controls whether comparison for the bounded selection is open.</summary>
    public void SetCompare(bool value) => _exploration.CompareOpen = value && _exploration.PickedPaths.Count >= 2;

    /// <summary>Changes the attention lens and refreshes which important documents enter the projection.</summary>
    public void SetAttention(string mode)
    {
        _exploration.SetAttentionMode(mode);
        Project();
    }

    /// <summary>Sets the current comparison selection, applying the shared retention limit.</summary>
    public void Pick(IEnumerable<string> paths, bool append)
    {
        _exploration.SetPicked(append ? _exploration.PickedPaths.Concat(paths) : paths);
        Project();
    }

    /// <summary>Removes or adds a document to the bounded comparison selection.</summary>
    public void TogglePick(string path)
    {
        _exploration.Toggle(path);
        Project();
    }

    /// <summary>Writes a separate portable copy and completes the copied revision only for inherently read-only workspaces.</summary>
    public async Task SaveCopyAsync(string path, CancellationToken token)
    {
        await _saveGate.WaitAsync(token);
        try
        {
            var original = Path.Combine(files.Root(workspace.Id), MapLayoutCodec.FileName);
            using var guard = DesktopDocumentFiles.ProtectOriginal(original, path, "map-copy-destination",
                "Choose a separate destination for the map copy.");
            var pending = _session.Value;
            await AtomicFile.WriteTextAsync(path, MapLayoutCodec.Write(pending), token);
            // A copy is the primary save action only when the workspace never supported native sidecar writes.
            if (!workspace.Capabilities.CanWriteSidecars && ReferenceEquals(pending, _session.Value))
            {
                _session.Saved();
                Message = "Layout copy saved";
            }
        }
        catch (Win32Exception error)
        {
            throw new IOException("The map copy could not be saved. The original layout is preserved.", error);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    /// <summary>Drains autosave and optionally writes a clean layout when the user explicitly selects Save layout.</summary>
    public async Task SaveAsync(CancellationToken token, bool force = false)
    {
        await _saveGate.WaitAsync(token);
        try
        {
            if (!_canWrite)
            {
                if (force && Message.Length == 0)
                {
                    Message = "This workspace cannot save the map directly. Download a separate copy of the layout.";
                }
                return;
            }

            while (_session.Dirty || force)
            {
                var pending = _session.Value;
                _revision = await files.WriteMapAsync(workspace.Id, _revision, MapLayoutCodec.Write(pending), token);
                if (ReferenceEquals(pending, _session.Value))
                {
                    _session.Saved();
                    force = false;
                }
                Message = "Layout saved";
            }
        }
        catch (Exception error) when (error is WorkspaceException or IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            if (error is WorkspaceException { Code: "map-conflict" } or DecoderFallbackException)
            {
                _canWrite = false;
            }
            Message = error is DecoderFallbackException
                ? "The map layout changed on disk. Download your layout before reopening the folder; the existing file was preserved."
                : error.Message;
        }
        finally
        {
            _saveGate.Release();
        }
    }

    /// <summary>Publishes projection facts selected by model state, before view-only zoom and screen filtering.</summary>
    private void Project()
    {
        var map = catalog.Map(SelectedPath, false, _routeStart, _routeEnd, Atlas.FocusDepth, _exploration.PriorityPaths());
        Snapshot = map with { Attention = _exploration.Attention };
    }

    /// <summary>Gets the exact workspace-relative directory, including the unnamed root island.</summary>
    internal static string FolderOf(string path) => path.LastIndexOf('/') is var end && end >= 0 ? path[..end] : "";
}
