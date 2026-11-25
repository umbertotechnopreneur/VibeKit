/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; cached Win2D background paths for dense camera gestures
 * Human guidance: keep zoom and pan fluent while retaining the exact directed connections
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using System.Numerics;
using MarkVibe.Core;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.UI;

namespace MarkVibe.Windows;

internal sealed partial class WindowsGraphCanvas
{
    private const float BackgroundEdgeWidth = 1.05f;
    private const float DenseEdgeOpacity = .24f;
    private const float NonFocusedEdgeOpacity = .72f;
    private const float DistantEdgeOpacity = .48f;
    private const float WheelTitleWidth = 96;
    private readonly List<(Card Card, Bounds Endpoint)> _gestureEdgeEndpoints = [];
    private readonly List<(MapEdge Edge, ConnectionCurve Curve, Bounds Hull, bool Distant)> _gestureBackgroundCurves = [];
    private readonly List<MapEdge> _gestureForegroundEdges = [];
    private CanvasGeometry? _gestureBackgroundPath;
    private CanvasGeometry? _gestureDistantPath;
    private float _gestureBackgroundDepthOpacity;
    private float _gestureDistantDepthOpacity;
    private (string? Focus, MapEdge? Hovered, MapEdge? Keyboard) _gestureEdgeFocus;
    private bool _gestureEdgesReady;

    /// <summary>Draws the projected card and a stable-size title without transforming each primitive at every wheel detent.</summary>
    private void DrawWheelCard(CanvasDrawingSession drawing, Card card, Bounds screen, bool relationLens)
    {
        var path = card.Document.Path;
        var selected = path == _selected || path == FocusedCard;
        var picked = _picked.Contains(path);
        var route = _routeCards.Contains(path);
        var opacity = selected || picked || route || !relationLens || _relatedCards.Contains(path) ? 1f : .74f;
        var border = selected || picked ? _palette.Accent : route ? _palette.RouteColor : _palette.Border;
        drawing.FillRectangle(screen.Rect, Alpha(_palette.CardFill(card.Color, selected), opacity));
        drawing.DrawRectangle(screen.Rect, Alpha(border, selected || picked || route ? 1 : .48f), selected ? 1.5f : .8f);

        if (screen.Size.X < WheelTitleWidth + 12 || screen.Size.Y < 25)
        {
            return;
        }
        var origin = screen.Origin + new Vector2(6, 5);
        card.DrawWheelTitle(drawing, _surface, _labelFormat, origin, Alpha(_palette.CardText, opacity));
    }

    /// <summary>Classifies the connections that retain their individual geometry and appearance during camera motion.</summary>
    private EdgeEmphasis GetEdgeEmphasis(MapEdge edge, string? focus)
    {
        var outgoing = focus != null && edge.Source == focus;
        var incoming = focus != null && !outgoing && edge.Target == focus;
        return new(outgoing, incoming, _route.Contains((edge.Source, edge.Target)), _hoveredEdge == edge || FocusedEdge == edge,
            edge.Source == _selected || edge.Target == _selected);
    }

    /// <summary>Retains the current endpoint depth treatment independently of the camera.</summary>
    private float EdgeDepthOpacity(MapEdge edge) =>
        1 - Math.Max(DepthLevel(edge.Source), DepthLevel(edge.Target)) / MaximumDepth * .30f;

    /// <summary>Paints mutually exclusive background batches and keeps the original visible Béziers for pointer hit testing.</summary>
    private void DrawGestureEdges(CanvasDrawingSession drawing, Bounds visible, string? focus, Color quietColor, MapEdge? highlightedEdge)
    {
        var edgeFocus = (focus, _hoveredEdge, FocusedEdge);
        if (!GestureEdgesMatch(edgeFocus))
        {
            BuildGestureEdges(edgeFocus);
        }

        var nearVisible = false;
        var distantVisible = false;
        var hitBounds = visible.Expand(12 / _scale);
        foreach (var (edge, curve, hull, distant) in _gestureBackgroundCurves)
        {
            if (!hull.Overlaps(hitBounds))
            {
                continue;
            }
            _drawnEdges.Add((edge, curve));
            nearVisible |= !distant;
            distantVisible |= distant;
        }

        var opacity = DenseEdgeOpacity * (focus == null ? 1 : NonFocusedEdgeOpacity);
        var width = BackgroundEdgeWidth / _scale;
        // At reading zoom compound strokes can stall while the camera pans as well as when it scales.
        // The overview can still reuse its small native paths throughout a translation.
        if (_wheelGestureAt != 0 || _scale >= .4f)
        {
            if (nearVisible || distantVisible)
            {
                DrawGestureStrokes(drawing, hitBounds, quietColor, opacity);
            }
        }
        else
        {
            if (nearVisible && _gestureBackgroundPath != null)
            {
                drawing.DrawGeometry(_gestureBackgroundPath, Alpha(quietColor, opacity * _gestureBackgroundDepthOpacity), width, _roundStroke);
            }
            if (distantVisible && _gestureDistantPath != null)
            {
                drawing.DrawGeometry(_gestureDistantPath, Alpha(quietColor, opacity * DistantEdgeOpacity * _gestureDistantDepthOpacity), width, _roundStroke);
            }
        }
        foreach (var edge in _gestureForegroundEdges)
        {
            if (edge != highlightedEdge)
            {
                DrawEdge(drawing, edge, visible, focus, quietColor);
            }
        }
    }

    /// <summary>Draws every visible background link during camera movement without tessellating intersecting compound strokes.</summary>
    private void DrawGestureStrokes(CanvasDrawingSession drawing, Bounds visible, Color color, float opacity)
    {
        var near = Alpha(color, opacity * _gestureBackgroundDepthOpacity);
        var far = Alpha(color, opacity * DistantEdgeOpacity * _gestureDistantDepthOpacity);
        var width = BackgroundEdgeWidth / _scale;
        var tolerance = .75f / _scale;
        foreach (var (_, curve, hull, distant) in _gestureBackgroundCurves)
        {
            if (hull.Overlaps(visible))
            {
                DrawGestureCurve(drawing, curve, visible, distant ? far : near, width, tolerance * tolerance, 0);
            }
        }
    }

    /// <summary>Flattens only visible curve sections, stopping when a screen-space chord is within three quarters of a DIP.</summary>
    private static void DrawGestureCurve(CanvasDrawingSession drawing, ConnectionCurve curve, Bounds visible,
        Color color, float width, float toleranceSquared, int depth)
    {
        if (depth >= 10 || curve.FitsChord(toleranceSquared))
        {
            drawing.DrawLine(curve.Start, curve.End, color, width);
            return;
        }
        curve.Split(out var first, out var second);
        if (first.Hull.Overlaps(visible))
        {
            DrawGestureCurve(drawing, first, visible, color, width, toleranceSquared, depth + 1);
        }
        if (second.Hull.Overlaps(visible))
        {
            DrawGestureCurve(drawing, second, visible, color, width, toleranceSquared, depth + 1);
        }
    }

    /// <summary>Checks exact painted endpoints so zoom level changes only rebuild paths when they change a meaningful pose.</summary>
    private bool GestureEdgesMatch((string? Focus, MapEdge? Hovered, MapEdge? Keyboard) focus)
    {
        if (!_gestureEdgesReady || _gestureEdgeFocus != focus || _gestureEdgeEndpoints.Count != _cards.Count)
        {
            return false;
        }
        var index = 0;
        foreach (var card in _cards.Values)
        {
            var previous = _gestureEdgeEndpoints[index++];
            if (previous.Card != card || previous.Endpoint != card.DrawEndpoint)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Builds device-bound open figures once for the current world geometry and distance filter.</summary>
    private void BuildGestureEdges((string? Focus, MapEdge? Hovered, MapEdge? Keyboard) focus)
    {
        InvalidateGestureEdges();
        _gestureEdgeEndpoints.EnsureCapacity(_cards.Count);
        _gestureBackgroundCurves.EnsureCapacity(_edges.Count);
        foreach (var card in _cards.Values)
        {
            _gestureEdgeEndpoints.Add((card, card.DrawEndpoint));
        }

        using var nearPath = new CanvasPathBuilder(_surface);
        using var distantPath = new CanvasPathBuilder(_surface);
        var nearCount = 0;
        var distantCount = 0;
        float nearDepth = 0, distantDepth = 0;
        foreach (var edge in _edges)
        {
            if (GetEdgeEmphasis(edge, focus.Focus).Primary)
            {
                _gestureForegroundEdges.Add(edge);
                continue;
            }
            if (EdgeGeometry(edge, useDrawEndpoints: true) is not { } curve)
            {
                continue;
            }

            var distant = !IsWithinDistance(edge.Source) || !IsWithinDistance(edge.Target);
            var depth = EdgeDepthOpacity(edge);
            AddConnectionFigure(distant ? distantPath : nearPath, curve);
            _gestureBackgroundCurves.Add((edge, curve, curve.Hull, distant));
            if (distant)
            {
                distantCount++;
                distantDepth += depth;
            }
            else
            {
                nearCount++;
                nearDepth += depth;
            }
        }

        if (nearCount > 0)
        {
            _gestureBackgroundPath = CanvasGeometry.CreatePath(nearPath);
            _gestureBackgroundDepthOpacity = nearDepth / nearCount;
        }
        if (distantCount > 0)
        {
            _gestureDistantPath = CanvasGeometry.CreatePath(distantPath);
            _gestureDistantDepthOpacity = distantDepth / distantCount;
        }
        _gestureEdgeFocus = focus;
        _gestureEdgesReady = true;
    }

    /// <summary>Adds the same open cubic used by independent paths and hit testing.</summary>
    private static void AddConnectionFigure(CanvasPathBuilder builder, ConnectionCurve curve)
    {
        builder.BeginFigure(curve.Start);
        builder.AddCubicBezier(curve.StartControl, curve.EndControl, curve.End);
        builder.EndFigure(CanvasFigureLoop.Open);
    }

    /// <summary>Releases native gesture paths while retaining the reusable managed buffers.</summary>
    private void InvalidateGestureEdges()
    {
        _gestureBackgroundPath?.Dispose();
        _gestureDistantPath?.Dispose();
        _gestureBackgroundPath = _gestureDistantPath = null;
        _gestureEdgeEndpoints.Clear();
        _gestureBackgroundCurves.Clear();
        _gestureForegroundEdges.Clear();
        _gestureEdgeFocus = default;
        _gestureEdgesReady = false;
    }

    /// <summary>Describes the per-edge visual roles shared by independent and grouped rendering.</summary>
    private readonly record struct EdgeEmphasis(bool Outgoing, bool Incoming, bool OnRoute, bool Emphasized, bool Selected)
    {
        public bool Primary => Outgoing || Incoming || OnRoute || Emphasized || Selected;
    }
}
