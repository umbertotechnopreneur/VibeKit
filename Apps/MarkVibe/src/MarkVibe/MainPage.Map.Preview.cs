/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; cancellable in-place graph reading
 * Human guidance: Umberto Giacobbi; follow links to sections and return to the map position
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using System.Text.Json;
using MarkVibe.Core;

namespace MarkVibe;

/// <summary>Resolves the single graph preview against the current workspace and its edited revisions.</summary>
public partial class MainPage
{
    private CancellationTokenSource? _mapPreviewRead;
    private string? _mapPreviewRequestId;
    private string? _pendingPreviewPath;

    /// <summary>Invalidates the outstanding preview. The requesting operation disposes its own source.</summary>
    private void CancelMapPreview()
    {
        _mapPreviewRequestId = null;
        _pendingPreviewPath = null;
        _mapPreviewRead?.Cancel();
        _mapPreviewRead = null;
    }

    /// <summary>Stops a pending read before an explicit navigation selects a different document on the graph.</summary>
    private Task CloseSupersededMapPreviewAsync(string nextPath) =>
        _pendingPreviewPath != null && _pendingPreviewPath != nextPath ? CloseMapPreviewAsync() : Task.CompletedTask;

    /// <summary>Loads one page for the map, keeping the reader, navigation history and saved layout independent.</summary>
    private async Task HandleMapPreviewAsync(JsonElement request)
    {
        var context = _active;
        if (context == null || !IsCurrentExplorationRequest(context, request))
        {
            return;
        }

        var requestId = request.GetProperty("requestId").GetString();
        if (string.IsNullOrEmpty(requestId))
        {
            return;
        }
        if (request.TryGetProperty("cancel", out var cancel) && cancel.GetBoolean())
        {
            if (_mapPreviewRequestId == requestId)
            {
                CancelMapPreview();
            }
            return;
        }

        CancelMapPreview();
        using var read = new CancellationTokenSource();
        _mapPreviewRead = read;
        _mapPreviewRequestId = requestId;
        var path = request.GetProperty("path").GetString() ?? "";
        _pendingPreviewPath = path;
        try
        {
            var model = await context.Catalog.OpenAsync(context.Provider, path, read.Token);
            read.Token.ThrowIfCancellationRequested();
            await _mapGate.WaitAsync(read.Token);
            try
            {
                read.Token.ThrowIfCancellationRequested();
                if (!IsCurrentExplorationRequest(context, request))
                {
                    return;
                }

                // A link may lead outside the current projection. Select it before positioning the preview.
                context.Map.SelectedPath = model.Path;
                await MapHost.DisplaySelectionAsync(BuildAtlasMap(context, model.Path), model.Path,
                    context.Provider.Info.Id, MapPresentation(context.Map), context.Map.Session.Value.Atlas?.FocusDepth > 0);
            }
            finally
            {
                _mapGate.Release();
            }

            if (IsCurrentExplorationRequest(context, request))
            {
                await BrowserServices.CallAsync("map.preview", new
                {
                    hostId = MapHost.HostId,
                    workspaceId = context.Provider.Info.Id,
                    stateId = context.Map.StateId,
                    requestId,
                    path = model.Path,
                    document = new { model.Title, model.Html, model.Headings }
                }, read.Token);
            }
        }
        catch (OperationCanceledException) when (read.IsCancellationRequested)
        {
            // The replacement request or the closed preview now owns the UI.
        }
        catch (Exception error)
        {
            if (read.IsCancellationRequested || !IsCurrentExplorationRequest(context, request))
            {
                return;
            }
            await BrowserServices.CallAsync("map.preview", new
            {
                hostId = MapHost.HostId, workspaceId = context.Provider.Info.Id, stateId = context.Map.StateId,
                requestId, path,
                error = error is WorkspaceException ? error.Message : "This page could not be opened"
            });
        }
        finally
        {
            if (ReferenceEquals(_mapPreviewRead, read))
            {
                _mapPreviewRead = null;
                _pendingPreviewPath = null;
            }
        }
    }

    /// <summary>Promotes the visible preview to a reader visit, optionally restoring its current source block.</summary>
    private async Task OpenMapPreviewInReaderAsync(JsonElement request)
    {
        if (_active == null || !IsCurrentExplorationRequest(_active, request))
        {
            return;
        }
        var path = request.GetProperty("path").GetString() ?? "";
        var anchor = request.GetProperty("anchor").GetString() ?? "";
        var position = request.TryGetProperty("position", out var value) && value.ValueKind == JsonValueKind.Object
            ? value.Deserialize<ReadingPosition>(Wire.Options)
            : null;
        if (_active.Map.SelectedPath == path && _active.Catalog.Cards.Any(card => card.Path == path))
        {
            await OpenMapDocumentInReaderAsync(path, anchor, position);
        }
    }
}
