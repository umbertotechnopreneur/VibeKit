/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; graph selection and managed link updates; 2026-10-16
 * Human guidance: Umberto Giacobbi; single-click selection and double-click document navigation
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using MarkVibe.Core;
using System.Text.Json;

namespace MarkVibe.Controls;

/// <summary>Pairs the immutable workspace layout with the small controls used by the renderer and XAML inspector.</summary>
public sealed record MapPresentationState(MapLayout Layout, JsonElement Controls)
{
    /// <summary>Identifies the current opening of a workspace, including when the same folder is reopened.</summary>
    internal string StateId => Controls.GetProperty("stateId").GetString()!;
}

/// <summary>Routes document selection to an existing graph while recognizing a replacement workspace session.</summary>
public partial class DocumentMapView
{
    /// <summary>Describes the exact structure last accepted by the browser for this workspace opening.</summary>
    private sealed record ProjectionBaseline(long Token, string WorkspaceId, string StateId, MapLayout Layout,
        long MetadataRevision, IReadOnlyList<string> Paths, IReadOnlyList<MapEdge> Edges);

    private ProjectionBaseline? _projection;
    private long _presentationSequence;

    /// <summary>Refreshes selection and links while preserving the current map camera.</summary>
    public Task DisplaySelectionAsync(MapSnapshot map, string selected, string workspaceId, MapPresentationState state,
        bool focused, bool center = false)
        => PublishMapAsync(map, selected, workspaceId, state, focused, selection: true, center: center);

    /// <summary>Publishes the layout and topology once, then sends distances, highlighting and controls until the structure changes.</summary>
    private async Task PublishMapAsync(MapSnapshot map, string selected, string workspaceId, MapPresentationState state,
        bool focused, bool selection, bool center)
    {
        var sequence = ++_presentationSequence;
        var sameSession = IsCurrentSession(workspaceId, state.StateId);
        var operation = selection && sameSession ? "map.selection" : "map.render";
        PreparePresentation(map, selected, workspaceId, state, focused);
        var baseline = _projection;
        var selectionData = new { map.Depths, map.Route, map.Attention, map.Truncated, map.TotalDocuments, map.IndexComplete };
        if (selection && sameSession && baseline != null && baseline.WorkspaceId == workspaceId &&
            baseline.StateId == state.StateId && ReferenceEquals(baseline.Layout, state.Layout) &&
            baseline.MetadataRevision == map.MetadataRevision &&
            baseline.Paths.SequenceEqual(map.Nodes.Select(node => node.Path)) && baseline.Edges.SequenceEqual(map.Edges))
        {
            var applied = await BrowserServices.CallAsync<bool>(operation, new
            {
                hostId = HostId, workspaceId, selected, state.Controls, selectionData, focused,
                advanced = _advanced, depth = _depth, center, structureId = baseline.Token, presentationId = sequence
            });
            if (applied || sequence != _presentationSequence)
            {
                return;
            }
        }

        // A lost browser snapshot can decline a delta. Replace it only if this presentation still owns the host.
        _projection = null;
        var structure = new { map.Nodes, map.Edges, map.Folders };
        await BrowserServices.CallAsync(operation, new
        {
            hostId = HostId, workspaceId, selected, state.Controls, selectionData, state.Layout, structure,
            focused, advanced = _advanced, depth = _depth, center, structureId = sequence, presentationId = sequence
        });
        if (sequence == _presentationSequence && ReferenceEquals(_layout, state.Layout) && IsCurrentSession(workspaceId, state.StateId))
        {
            _projection = new(sequence, workspaceId, state.StateId, state.Layout,
                map.MetadataRevision, map.Nodes.Select(node => node.Path).ToArray(), map.Edges);
        }
    }

    /// <summary>Updates managed controls and delivers edit acknowledgments even when their view has been detached.</summary>
    public async Task DisplayStateAsync(string workspaceId, MapPresentationState state)
    {
        var isCurrent = IsCurrentSession(workspaceId, state.StateId);
        if (isCurrent)
        {
            _state = state.Controls;
            _layout = state.Layout;
            UpdateControls();
        }

        var baseline = _projection;
        if (isCurrent && baseline != null && baseline.WorkspaceId == workspaceId &&
            baseline.StateId == state.StateId && ReferenceEquals(baseline.Layout, state.Layout))
        {
            var applied = await BrowserServices.CallAsync<bool>("map.state", new
            {
                hostId = HostId, workspaceId, state.Controls, structureId = baseline.Token
            });
            if (applied)
            {
                return;
            }
        }

        await BrowserServices.CallAsync("map.state", new { hostId = HostId, workspaceId, state.Controls, state.Layout });
    }

    /// <summary>Matches the active XAML presentation to a particular workspace opening.</summary>
    private bool IsCurrentSession(string workspaceId, string stateId)
    {
        return _workspaceId == workspaceId && _state.ValueKind == JsonValueKind.Object &&
            _state.GetProperty("stateId").GetString() == stateId;
    }
}
