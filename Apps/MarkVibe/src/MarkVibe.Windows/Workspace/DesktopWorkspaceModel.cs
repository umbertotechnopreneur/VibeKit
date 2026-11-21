/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; authoritative desktop workspace and document model
 * Human guidance: keep file data, history and revisions out of the WinUI views
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using System.IO;
using System.Text.Json;
using MarkVibe.Core;
using MarkVibe.Native;

namespace MarkVibe.Windows;

/// <summary>Owns the inventory, effective document and local revisions for one workspace.</summary>
internal sealed class DesktopWorkspace(LocalWorkspaceProvider provider)
{
    private readonly List<string> _visits = [];
    public LocalWorkspaceProvider Provider { get; } = provider;
    public WorkspaceInfo Info => Provider.Info;
    public DocumentCatalog Catalog { get; } = new(new MarkdownParser());
    public AiDocumentCollection References { get; } = new();
    public MarkdownModel? Document { get; set; }
    public string DiskFingerprint { get; set; } = "";
    public bool InventoryReady { get; set; }
    public IReadOnlyList<string> Visits => _visits;

    /// <summary>Retains the bounded sequence of real document visits for the graph trail.</summary>
    public void RecordVisit(string path)
    {
        if (_visits.LastOrDefault() == path)
        {
            return;
        }
        _visits.Add(path);
        if (_visits.Count > 10)
        {
            _visits.RemoveAt(0);
        }
    }
}

/// <summary>Provides the data and operations shared by the native shell, graph, reader and AI panel.</summary>
internal sealed class DesktopWorkspaceModel : IDisposable
{
    private static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg" };
    private readonly MarkdownParser _parser = new();
    private readonly string _settingsPath;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly Dictionary<string, ReaderAppearance> _folderAppearance = new(StringComparer.Ordinal);
    private CancellationTokenSource? _indexCancellation;
    private Task? _indexing;
    private long _settingsRevision;
    private readonly List<DesktopPreview> _previews = [];

    public NativeFiles Files { get; }
    public RecentLocations Recent { get; }
    public NavigationHistory History { get; } = new();
    public DesktopWorkspace? Workspace { get; private set; }
    public Preferences Preferences { get; private set; } = new();
    public ShellLayout Layout { get; private set; } = new();
    public string View { get; private set; } = "read";
    public MarkdownModel? Document => Workspace?.Document;
    public bool HasDrafts => Workspace?.Catalog.Sessions.Any(session => session.NeedsSave) == true;
    public DesktopPreview? Preview => _previews.LastOrDefault();
    public DesktopPreview? PreviousPreview => _previews.Count > 1 ? _previews[^2] : null;
    public MapPreviewViewport? PreviewOrigin { get; private set; }
    public bool CanPreviewBack => _previews.Count > 0;
    public bool HasFolderAppearance => Workspace != null && _folderAppearance.ContainsKey(Workspace.Info.Id);
    public DocumentFindState Find { get; } = new();
    public Preferences EffectivePreferences => Workspace != null && _folderAppearance.TryGetValue(Workspace.Info.Id, out var appearance)
        ? appearance.ApplyTo(Preferences) : Preferences;
    public event Action? CatalogChanged;

    /// <summary>Reads native-only settings and initializes local file access.</summary>
    public DesktopWorkspaceModel(string dataDirectory)
    {
        Files = new NativeFiles(dataDirectory);
        Recent = new RecentLocations(dataDirectory);
        _settingsPath = Path.Combine(dataDirectory, "desktop-settings.json");
        try
        {
            if (File.Exists(_settingsPath) && new FileInfo(_settingsPath).Length <= 262144)
            {
                var saved = Wire.Decode<Settings>(File.ReadAllText(_settingsPath));
                Preferences = saved.Preferences?.Validated() ?? new();
                Layout = saved.Layout?.Validated() ?? new();
                foreach (var pair in (saved.FolderAppearance ?? []).Take(64))
                {
                    if (pair.Value != null)
                    {
                        _folderAppearance[pair.Key] = pair.Value;
                    }
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            DesktopLog.Write("warning", "desktop-settings.read-failed");
        }
    }

    /// <summary>Opens a verified startup page first and exposes the graph only after full enumeration.</summary>
    public async Task OpenAsync(string directory, string? preferredPath, HistoryEntry? restore, bool readOnly,
        Func<Task> initialReady, CancellationToken token)
    {
        var info = Files.Open(directory);
        if (readOnly)
        {
            info = info with { Capabilities = info.Capabilities with { CanWriteSidecars = false, CanOpenParent = false } };
        }
        var workspace = new DesktopWorkspace(new LocalWorkspaceProvider(Files, info));
        var priority = Files.FindInitialDocument(info.Id, preferredPath) ?? Files.FindInitialDocument(info.Id, "README.md");
        if (priority != null)
        {
            workspace.Catalog.Seed([priority]);
            await LoadDocumentAsync(workspace, priority.Path, token);
            await ActivateWorkspaceAsync(workspace, priority.Path, restore, token);
            var fullInventory = workspace.Provider.EnumerateAsync(token);
            await Task.WhenAll(fullInventory, initialReady());
            token.ThrowIfCancellationRequested();
            workspace.Catalog.CompleteInitialEnumeration(fullInventory.Result);
            workspace.InventoryReady = true;
            StartIndex();
            return;
        }
        var inventory = await workspace.Provider.EnumerateAsync(token);
        token.ThrowIfCancellationRequested();
        workspace.Catalog.Seed(inventory);
        var path = FindDocument(workspace, preferredPath)
            ?? workspace.Catalog.Cards.FirstOrDefault(item => item.Path.Equals("README.md", StringComparison.OrdinalIgnoreCase))?.Path
            ?? workspace.Catalog.Cards.FirstOrDefault()?.Path;
        if (path != null)
        {
            await LoadDocumentAsync(workspace, path, token);
        }
        workspace.InventoryReady = true;
        await ActivateWorkspaceAsync(workspace, path, restore, token);
        StartIndex();
    }

    /// <summary>Commits the new workspace only after a bounded, successful first document read.</summary>
    private async Task ActivateWorkspaceAsync(DesktopWorkspace workspace, string? path, HistoryEntry? restore, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await StopIndexAsync();
        token.ThrowIfCancellationRequested();
        Workspace = workspace;
        ClearPreview();
        if (path != null)
        {
            CommitVisit(workspace, path, restore?.Anchor ?? "", restore);
        }
    }

    /// <summary>Loads an available page before committing its history entry.</summary>
    public async Task NavigateAsync(string path, string anchor, HistoryEntry? restore, CancellationToken token)
    {
        var workspace = Workspace ?? throw new WorkspaceException("workspace-unavailable", "Open a folder first.");
        var actual = FindDocument(workspace, path) ?? throw new WorkspaceException("missing-document", "This page is unavailable in the opened folder.");
        await LoadDocumentAsync(workspace, actual, token);
        token.ThrowIfCancellationRequested();
        CommitVisit(workspace, actual, anchor, restore);
    }

    /// <summary>Reads a catalog page for export, AI context or map preview without changing the visible document.</summary>
    public Task<MarkdownModel> ReadAsync(string path, CancellationToken token)
    {
        var workspace = Workspace ?? throw new WorkspaceException("workspace-unavailable", "Open a folder first.");
        var actual = FindDocument(workspace, path) ?? throw new WorkspaceException("missing-document", "This page is unavailable in the opened folder.");
        return workspace.Catalog.OpenAsync(workspace.Provider, actual, token);
    }

    /// <summary>Reseeds catalog metadata without dropping retained drafts.</summary>
    public async Task RefreshAsync(CancellationToken token)
    {
        var workspace = Workspace;
        if (workspace == null)
        {
            return;
        }

        var inventory = await workspace.Provider.EnumerateAsync(token);
        token.ThrowIfCancellationRequested();
        await StopIndexAsync();
        token.ThrowIfCancellationRequested();
        workspace.Catalog.Seed(inventory);
        workspace.InventoryReady = true;
        var available = workspace.Catalog.Cards.Select(card => card.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var reference in workspace.References.Paths.Where(path => !available.Contains(path)))
        {
            workspace.References.Remove(reference);
        }
        if (workspace.Document is { } document && !available.Contains(document.Path))
        {
            workspace.Document = null;
            workspace.DiskFingerprint = "";
        }
        StartIndex();
    }

    /// <summary>Accepts a fresh on-disk revision only after the document was successfully read and parsed.</summary>
    public async Task ReloadAsync(CancellationToken token)
    {
        var workspace = Workspace;
        var document = workspace?.Document;
        if (workspace == null || document == null)
        {
            return;
        }

        var source = await workspace.Provider.ReadDocumentAsync(document.Path, token);
        token.ThrowIfCancellationRequested();
        if (Workspace != workspace || workspace.Document != document)
        {
            return;
        }
        var reloaded = _parser.Parse(document.Path, source);
        workspace.Catalog.ReloadFromDisk(reloaded);
        workspace.Document = reloaded;
        workspace.DiskFingerprint = reloaded.Fingerprint;
        CatalogChanged?.Invoke();
    }

    /// <summary>Commits a revision to the catalog only if its captured workspace and source remain current.</summary>
    public void ApplyRevision(DesktopWorkspace workspace, MarkdownModel expected, string source, DocumentRevisionAction action)
    {
        if (Workspace != workspace || workspace.Document?.Path != expected.Path || workspace.Document.Fingerprint != expected.Fingerprint)
        {
            throw new WorkspaceException("stale-document", "The selected document changed. Open it again before editing.");
        }
        var revised = _parser.Parse(expected.Path, source);
        workspace.Catalog.ApplyRevision(expected, revised, action);
        workspace.Document = revised;
        CatalogChanged?.Invoke();
    }

    /// <summary>Applies undo or redo through the catalog's bounded revision history.</summary>
    public void StepRevision(DocumentRevisionAction action)
    {
        if (Workspace is { Document: { } document } workspace && workspace.Catalog.TryGetSession(document.Path) is { } session)
        {
            ApplyRevision(workspace, document, session.PreviewRevision(action), action);
        }
    }

    /// <summary>Records a Markdown-copy export only for the original active workspace.</summary>
    public void MarkExported(DesktopWorkspace workspace, string path, string source)
    {
        if (Workspace == workspace)
        {
            workspace.Catalog.MarkExported(path, source);
        }
    }

    /// <summary>Exposes a validated recent location after the user asks to reopen it.</summary>
    public RecentLocation ResolveRecent(string path, string kind) => Recent.Resolve(path, kind);

    /// <summary>Stores a bounded preview trail separately from committed reading visits.</summary>
    public void PushPreview(string path, string anchor, MapPreviewViewport viewport, MapPreviewViewport origin)
    {
        PreviewOrigin ??= origin;
        _previews.Add(new DesktopPreview(path, anchor, null, viewport));
        if (_previews.Count > 40)
        {
            _previews.RemoveAt(0);
        }
    }

    /// <summary>Records a preview reading position without changing document navigation history.</summary>
    public void CapturePreview(ReadingPosition position, MapPreviewViewport viewport)
    {
        if (Preview is { } preview)
        {
            _previews[^1] = preview with { Position = position, Viewport = viewport };
        }
    }

    /// <summary>Returns the preceding inline preview or ends the trail at the map.</summary>
    public DesktopPreview? PopPreview()
    {
        if (_previews.Count > 0)
        {
            _previews.RemoveAt(_previews.Count - 1);
        }
        return Preview;
    }

    /// <summary>Clears all transient preview visits without moving document history.</summary>
    public void ClearPreview()
    {
        _previews.Clear();
        PreviewOrigin = null;
    }

    /// <summary>Finds the visit preceding or following today's history cursor without moving it.</summary>
    public HistoryEntry? AdjacentVisit(int direction)
    {
        var current = History.Current;
        var entries = History.Entries;
        for (var index = 0; index < entries.Count; index++)
        {
            if (entries[index].Id == current?.Id)
            {
                var next = index + direction;
                return next >= 0 && next < entries.Count ? entries[next] : null;
            }
        }
        return null;
    }

    /// <summary>Retains current scrolling only for the selected document and workspace.</summary>
    public void CapturePosition(string workspaceId, string path, ReadingPosition position)
    {
        if (History.Current is { } visit && visit.WorkspaceId == workspaceId && visit.Path == path)
        {
            History.Capture(position);
        }
    }

    /// <summary>Updates the current native view token used by the shell and the document renderer.</summary>
    public void SetView(string view) => View = view is "read" or "source" or "split" or "map" ? view : "read";

    /// <summary>Writes current global appearance and layout to native settings.</summary>
    public async Task SetPreferencesAsync(Preferences preferences, CancellationToken token)
    {
        Preferences = preferences.Validated();
        await SaveSettingsAsync(token);
        await RememberAsync(token);
    }

    /// <summary>Updates validated shell geometry and persists it with global settings.</summary>
    public Task SetLayoutAsync(ShellLayout layout, CancellationToken token)
    {
        Layout = layout.Validated();
        return SaveSettingsAsync(token);
    }

    /// <summary>Retains or clears the current folder's independent reader appearance.</summary>
    public Task SetFolderAppearanceAsync(ReaderAppearance? appearance, CancellationToken token)
    {
        if (Workspace == null)
        {
            return Task.CompletedTask;
        }
        if (appearance == null)
        {
            _folderAppearance.Remove(Workspace.Info.Id);
        }
        else
        {
            _folderAppearance.Remove(Workspace.Info.Id);
            _folderAppearance[Workspace.Info.Id] = appearance;
            if (_folderAppearance.Count > 64)
            {
                _folderAppearance.Remove(_folderAppearance.Keys.First());
            }
        }
        return SaveSettingsAsync(token);
    }

    /// <summary>Stores the current visit through the native session store.</summary>
    public Task RememberAsync(CancellationToken token) => Workspace is { Info.Capabilities.CanWriteSidecars: false } && Preferences.RememberLastDocument
        ? Task.CompletedTask
        : Files.RememberAsync(Workspace?.Document != null ? History.Current : null, Preferences.RememberLastDocument, token);

    /// <summary>Reads bounded images in the active granted workspace for local WebView rendering.</summary>
    public async Task<string> ReadImageAsync(string workspaceId, string path, CancellationToken token)
    {
        var workspace = Workspace;
        if (workspace == null || workspace.Info.Id != workspaceId || !ImageTypes.Contains(Path.GetExtension(path)))
        {
            throw new WorkspaceException("unsupported-image", "This image is unavailable in the current workspace.");
        }
        var bytes = await Files.ReadAsync(workspaceId, path, token);
        token.ThrowIfCancellationRequested();
        if (Workspace != workspace)
        {
            throw new WorkspaceException("workspace-unavailable", "The opened folder changed.");
        }
        return Convert.ToBase64String(bytes);
    }

    /// <summary>Resolves a canonical document path, with a Windows case-insensitive fallback for links.</summary>
    private static string? FindDocument(DesktopWorkspace workspace, string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }
        var canonical = WorkspacePath.Normalize(path);
        return workspace.Catalog.Cards.FirstOrDefault(item => item.Path.Equals(canonical, StringComparison.Ordinal))?.Path
            ?? workspace.Catalog.Cards.FirstOrDefault(item => item.Path.Equals(canonical, StringComparison.OrdinalIgnoreCase))?.Path;
    }

    /// <summary>Loads a document while retaining its original fingerprint for file-change observation.</summary>
    private static async Task LoadDocumentAsync(DesktopWorkspace workspace, string path, CancellationToken token)
    {
        if (workspace.Document?.Path == path)
        {
            return;
        }
        var document = await workspace.Catalog.OpenAsync(workspace.Provider, path, token);
        token.ThrowIfCancellationRequested();
        workspace.Document = document;
        var session = workspace.Catalog.TryGetSession(path);
        workspace.DiskFingerprint = session == null ? document.Fingerprint : MarkdownParser.Fingerprint(session.OriginalSource);
    }

    /// <summary>Accepts a matching restored position or records a new successful visit.</summary>
    private void CommitVisit(DesktopWorkspace workspace, string path, string anchor, HistoryEntry? restore)
    {
        workspace.RecordVisit(path);
        if (restore != null && restore.WorkspaceId == workspace.Info.Id && restore.Path == path)
        {
            History.Restore(restore);
        }
        else if (History.Current is not { } current || current.WorkspaceId != workspace.Info.Id || current.Path != path || current.Anchor != anchor)
        {
            History.Push(workspace.Info.Id, path, anchor);
        }
    }

    /// <summary>Starts the catalog's bounded incremental metadata pass on its owning dispatcher.</summary>
    private void StartIndex()
    {
        if (Workspace is not { } workspace)
        {
            return;
        }
        _indexCancellation = new CancellationTokenSource();
        _indexing = IndexAsync(workspace, _indexCancellation.Token);
    }

    /// <summary>Publishes bounded progress notifications while rejecting obsolete workspace results.</summary>
    private async Task IndexAsync(DesktopWorkspace workspace, CancellationToken token)
    {
        try
        {
            await workspace.Catalog.IndexAsync(workspace.Provider, (done, count) =>
            {
                if (!token.IsCancellationRequested && Workspace == workspace && (done == count || done % 20 == 0))
                {
                    CatalogChanged?.Invoke();
                }
            }, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The next workspace or refresh owns its metadata now.
        }
        catch (Exception error)
        {
            DesktopLog.Error("workspace.index-failed", error);
        }
    }

    /// <summary>Drains old index mutations before reseeding the same catalog or replacing its workspace.</summary>
    private async Task StopIndexAsync()
    {
        var source = _indexCancellation;
        if (source == null)
        {
            return;
        }
        source.Cancel();
        try
        {
            if (_indexing != null)
            {
                await _indexing;
            }
        }
        finally
        {
            source.Dispose();
            _indexCancellation = null;
            _indexing = null;
        }
    }

    /// <summary>Serializes writes and skips snapshots superseded while waiting for the local store.</summary>
    private async Task SaveSettingsAsync(CancellationToken token)
    {
        var revision = ++_settingsRevision;
        await _saveGate.WaitAsync(token);
        try
        {
            if (revision == _settingsRevision)
            {
                await AtomicFile.WriteTextAsync(_settingsPath, Wire.Encode(new Settings(Preferences, Layout, _folderAppearance)), token);
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    /// <summary>Cancels incremental reads when the native window exits.</summary>
    public void Dispose() => _indexCancellation?.Cancel();

    /// <summary>Describes the current native-only preferences file.</summary>
    private sealed record Settings(Preferences? Preferences, ShellLayout? Layout, Dictionary<string, ReaderAppearance>? FolderAppearance);
}

/// <summary>Identifies one passage opened in the map preview.</summary>
internal sealed record DesktopPreview(string Path, string Anchor, ReadingPosition? Position, MapPreviewViewport Viewport);
