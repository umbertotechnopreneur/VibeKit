/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; render a standalone graph prototype with Win2D.
 * Human guidance: reuse the C# map and saved layout while retaining the embedded document reader.
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using System.Diagnostics;
using System.Numerics;
using MarkVibe.Core;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace MarkVibe.Map.WinUI;

/// <summary>Draws a managed map projection on one native surface and owns its transient camera and gestures.</summary>
public sealed class GraphCanvas : Grid, IDisposable
{
    private static readonly Color BackgroundColor = Color.FromArgb(255, 24, 29, 37);
    private static readonly Color CardColor = Color.FromArgb(255, 41, 48, 59);
    private static readonly Color EdgeColor = Color.FromArgb(255, 97, 111, 128);
    private static readonly Color SelectedColor = Color.FromArgb(255, 110, 203, 255);
    private static readonly Color TitleColor = Color.FromArgb(255, 246, 248, 252);
    private static readonly Color DetailColor = Color.FromArgb(255, 190, 199, 210);
    private readonly CanvasControl _surface = new() { ClearColor = BackgroundColor };
    private readonly CanvasTextFormat _titleFormat = new() { FontSize = 22, Options = CanvasDrawTextOptions.Clip };
    private readonly CanvasTextFormat _detailFormat = new() { FontSize = 15, Options = CanvasDrawTextOptions.Clip };
    private readonly Dictionary<string, Card> _byPath = new(StringComparer.Ordinal);
    private readonly List<Card> _cards = [];
    private readonly List<(Card Source, Card Target)> _edges = [];
    private TaskCompletionSource<(long Draw, long Composition)> _initialFrame =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private GraphWorkspace? _workspace;
    private Vector2 _translation;
    private float _scale = 1;
    private bool _needsFit;
    private bool _disposed;
    private long _firstDraw;
    private uint? _pointerId;
    private Vector2 _pointerStart;
    private Vector2 _gestureOrigin;
    private Card? _dragged;

    /// <summary>Notifies the shell when a pointer selects a different managed document.</summary>
    public event Action<string>? DocumentSelected;

    /// <summary>Reports graph draw completion on the UI thread, after all visible drawing commands have been issued.</summary>
    internal event Action<long>? FrameDrawn;
    internal Task<(long Draw, long Composition)> InitialFrame => _initialFrame.Task;
    internal bool IsDisposed => _disposed;
    internal int NodeCount => _cards.Count;
    internal int EdgeCount => _edges.Count;
    internal string SelectedPath => _workspace?.SelectedPath ?? string.Empty;
    internal float CameraScale => _scale;
    internal Vector2 ViewportCenter => new((float)ActualWidth / 2, (float)ActualHeight / 2);
    internal Vector2 SelectedPosition => _byPath[SelectedPath].Position;

    /// <summary>Selects the card closest to the viewport center for a repeatable drag measurement.</summary>
    internal void SelectCentralCard()
    {
        var worldCenter = (ViewportCenter - _translation) / _scale;
        var card = _cards.MinBy(candidate => Vector2.DistanceSquared(
            candidate.Position + candidate.Size / 2, worldCenter));
        if (card != null && card.Document.Path != SelectedPath)
        {
            SetProjection(_workspace!.Select(card.Document.Path));
        }
    }

    /// <summary>Connects native drawing and pointer input without subscribing to a continuous idle render loop.</summary>
    public GraphCanvas()
    {
        Children.Add(_surface);
        _surface.Draw += Draw;
        _surface.CreateResources += (_, _) => ClearText();
        _surface.PointerPressed += PointerDown;
        _surface.PointerMoved += PointerMove;
        _surface.PointerReleased += PointerUp;
        _surface.PointerCaptureLost += CaptureLost;
        _surface.PointerWheelChanged += Wheel;
    }

    /// <summary>Replaces the projection, retaining persisted coordinates, dimensions and color for every visible node.</summary>
    public void SetWorkspace(GraphWorkspace workspace)
    {
        _surface.ReleasePointerCaptures();
        if (_workspace != null)
        {
            CompositionTarget.Rendering -= FirstComposition;
            _initialFrame.TrySetCanceled();
            _initialFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _firstDraw = 0;
        }
        _workspace = workspace;
        SetProjection(workspace.Map);
        Fit();
    }

    /// <summary>Resolves edge endpoints once per projection; moved endpoint references remain valid throughout a drag.</summary>
    private void SetProjection(MapSnapshot snapshot)
    {
        ClearText();
        _byPath.Clear();
        _cards.Clear();
        _edges.Clear();
        foreach (var document in snapshot.Nodes)
        {
            var card = new Card(document, _workspace!.Layout.Nodes[document.Path]);
            _byPath.Add(document.Path, card);
            _cards.Add(card);
        }
        foreach (var edge in snapshot.Edges)
        {
            if (_byPath.TryGetValue(edge.Source, out var source) && _byPath.TryGetValue(edge.Target, out var target))
            {
                _edges.Add((source, target));
            }
        }
    }

    /// <summary>Fits the saved geometry into the measured viewport, including cards with custom sizes.</summary>
    public void Fit()
    {
        _needsFit = true;
        _surface.Invalidate();
    }

    /// <summary>Defers the first fit until WinUI has arranged the canvas at its actual size.</summary>
    private void FitMeasuredViewport()
    {
        if (_cards.Count == 0 || ActualWidth <= 60 || ActualHeight <= 60)
        {
            return;
        }
        var minimum = _cards[0].Position;
        var maximum = minimum + _cards[0].Size;
        foreach (var card in _cards)
        {
            minimum = Vector2.Min(minimum, card.Position);
            maximum = Vector2.Max(maximum, card.Position + card.Size);
        }
        var span = maximum - minimum;
        _scale = Math.Clamp(Math.Min((2 * ViewportCenter.X - 60) / span.X,
            (2 * ViewportCenter.Y - 60) / span.Y), .001f, 1);
        _translation = ViewportCenter - (minimum + maximum) * (.5f * _scale);
        _needsFit = false;
    }

    /// <summary>Draws the visible edges and cards, then timestamps the submission of the actual graph content.</summary>
    private void Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_disposed || _workspace == null || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }
        if (_needsFit)
        {
            FitMeasuredViewport();
            if (_needsFit && _cards.Count > 0)
            {
                // Wait for the next size-driven draw before recording an unarranged graph.
                return;
            }
        }
        var session = args.DrawingSession;
        session.Transform = Matrix3x2.CreateScale(_scale) * Matrix3x2.CreateTranslation(_translation);
        var minimum = -_translation / _scale;
        var maximum = (2 * ViewportCenter - _translation) / _scale;
        foreach (var (source, target) in _edges)
        {
            DrawEdge(session, source, target, minimum, maximum);
        }
        foreach (var card in _cards)
        {
            if (Intersects(card.Position, card.Position + card.Size, minimum, maximum))
            {
                DrawCard(session, card);
            }
        }
        var completed = Stopwatch.GetTimestamp();
        if (_firstDraw == 0)
        {
            _firstDraw = completed;
            CompositionTarget.Rendering += FirstComposition;
        }
        FrameDrawn?.Invoke(completed);
    }

    /// <summary>Records the first subsequent XAML composition callback as a presentation proxy.</summary>
    private void FirstComposition(object? sender, object args)
    {
        CompositionTarget.Rendering -= FirstComposition;
        _initialFrame.TrySetResult((_firstDraw, Stopwatch.GetTimestamp()));
    }

    /// <summary>Tests an axis-aligned bounding box against the visible world rectangle.</summary>
    private static bool Intersects(Vector2 a, Vector2 b, Vector2 minimum, Vector2 maximum) =>
        a.X <= maximum.X && b.X >= minimum.X && a.Y <= maximum.Y && b.Y >= minimum.Y;

    /// <summary>Draws a directed connection up to each card's boundary and keeps arrows legible when zoomed out.</summary>
    private void DrawEdge(CanvasDrawingSession session, Card source, Card target, Vector2 minimum, Vector2 maximum)
    {
        var boundary = 12 / _scale;
        if (!Intersects(Vector2.Min(source.Position, target.Position) - new Vector2(boundary),
            Vector2.Max(source.Position + source.Size, target.Position + target.Size) + new Vector2(boundary),
            minimum, maximum))
        {
            return;
        }
        var selected = source.Document.Path == SelectedPath || target.Document.Path == SelectedPath;
        var color = selected ? SelectedColor : EdgeColor;
        var width = (selected ? 1.65f : .85f) / _scale;
        if (source == target)
        {
            session.DrawCircle(source.Position + new Vector2(source.Size.X * .8f, 0), boundary, color, width);
            return;
        }
        var delta = target.Center - source.Center;
        if (delta.LengthSquared() < 1)
        {
            return;
        }
        var direction = Vector2.Normalize(delta);
        var start = source.Center + BoundaryOffset(source.Size, direction);
        var end = target.Center - BoundaryOffset(target.Size, direction);
        if (Vector2.Dot(end - start, direction) <= 0)
        {
            return;
        }
        session.DrawLine(start, end, color, width);
        var tipLength = Math.Min(6 / _scale, Vector2.Distance(start, end) * .4f);
        var wing = new Vector2(-direction.Y, direction.X) * (tipLength * .5f);
        var basePoint = end - direction * tipLength;
        session.DrawLine(end, basePoint + wing, color, width);
        session.DrawLine(end, basePoint - wing, color, width);
    }

    /// <summary>Intersects a ray from a rectangular card's center with the near edge of its outline.</summary>
    private static Vector2 BoundaryOffset(Vector2 size, Vector2 direction) =>
        direction * (.5f / Math.Max(Math.Abs(direction.X) / size.X, Math.Abs(direction.Y) / size.Y));

    /// <summary>Draws cached titles at overview scale and progressively reveals the path and introduction.</summary>
    private void DrawCard(CanvasDrawingSession session, Card card)
    {
        var rect = new Rect(card.Position.X, card.Position.Y, card.Size.X, card.Size.Y);
        var selected = card.Document.Path == SelectedPath;
        session.FillRoundedRectangle(rect, 9, 9, CardColor);
        session.DrawRoundedRectangle(rect, 9, 9, selected ? SelectedColor : card.Color,
            (selected ? 2.3f : 1.2f) / _scale);
        const float inset = 13;
        var width = card.Size.X - 2 * inset;
        card.Title ??= new CanvasTextLayout(_surface, card.Document.Title, _titleFormat, width, 54);
        session.DrawTextLayout(card.Title, card.Position + new Vector2(inset, inset), TitleColor);
        if (_scale >= .4f)
        {
            card.Path ??= new CanvasTextLayout(_surface, card.Document.Path, _detailFormat, width, 21);
            session.DrawTextLayout(card.Path, card.Position + new Vector2(inset, card.Size.Y - 29), DetailColor);
        }
        if (_scale >= .72f && card.Size.Y > 108)
        {
            card.Introduction ??= new CanvasTextLayout(_surface, card.Document.Introduction, _detailFormat,
                width, card.Size.Y - 103);
            session.DrawTextLayout(card.Introduction, card.Position + new Vector2(inset, 66), DetailColor);
        }
    }

    /// <summary>Starts either a card gesture or a background pan, capturing only the initiating pointer.</summary>
    private void PointerDown(object sender, PointerRoutedEventArgs args)
    {
        var input = args.GetCurrentPoint(_surface);
        if (_workspace == null || _pointerId.HasValue || !input.IsInContact ||
            (!input.Properties.IsLeftButtonPressed && !input.Properties.IsMiddleButtonPressed))
        {
            return;
        }
        if (!_surface.CapturePointer(args.Pointer))
        {
            return;
        }
        _pointerId = args.Pointer.PointerId;
        _pointerStart = new((float)input.Position.X, (float)input.Position.Y);
        var world = (_pointerStart - _translation) / _scale;
        _dragged = null;
        if (input.Properties.IsLeftButtonPressed)
        {
            for (var index = _cards.Count - 1; index >= 0; index--)
            {
                var card = _cards[index];
                if (Intersects(card.Position, card.Position + card.Size, world, world))
                {
                    if (SelectedPath != card.Document.Path)
                    {
                        var path = card.Document.Path;
                        SetProjection(_workspace.Select(path));
                        DocumentSelected?.Invoke(path);
                    }
                    _dragged = _byPath[card.Document.Path];
                    break;
                }
            }
        }
        _gestureOrigin = _dragged?.Position ?? _translation;
        _surface.Invalidate();
        args.Handled = true;
    }

    /// <summary>Uses world-space displacement for a node and screen-space displacement for the camera.</summary>
    private void PointerMove(object sender, PointerRoutedEventArgs args)
    {
        if (args.Pointer.PointerId != _pointerId)
        {
            return;
        }
        var point = args.GetCurrentPoint(_surface).Position;
        var delta = new Vector2((float)point.X, (float)point.Y) - _pointerStart;
        if (_dragged != null)
        {
            MoveCard(_dragged, _gestureOrigin, delta);
        }
        else
        {
            _translation = _gestureOrigin + delta;
            _surface.Invalidate();
        }
        args.Handled = true;
    }

    /// <summary>Commits the last node position to the in-memory model only when the gesture actually moved it.</summary>
    private void PointerUp(object sender, PointerRoutedEventArgs args)
    {
        if (args.Pointer.PointerId != _pointerId)
        {
            return;
        }
        PointerMove(sender, args);
        if (_dragged != null && _dragged.Position != _gestureOrigin)
        {
            _workspace!.Move(_dragged.Document.Path, _dragged.Position.X, _dragged.Position.Y);
        }
        _dragged = null;
        _pointerId = null;
        _surface.ReleasePointerCapture(args.Pointer);
        args.Handled = true;
    }

    /// <summary>Rolls back an interrupted pointer drag so the transient view still matches the model.</summary>
    private void CaptureLost(object sender, PointerRoutedEventArgs args)
    {
        if (args.Pointer.PointerId == _pointerId)
        {
            if (_dragged != null)
            {
                _dragged.Position = _gestureOrigin;
            }
            _dragged = null;
            _pointerId = null;
            _surface.Invalidate();
        }
    }

    /// <summary>Zooms at the mouse position while keeping the world point under the pointer fixed.</summary>
    private void Wheel(object sender, PointerRoutedEventArgs args)
    {
        if (_pointerId.HasValue)
        {
            return;
        }
        var input = args.GetCurrentPoint(_surface);
        var factor = MathF.Pow(1.15f, input.Properties.MouseWheelDelta / 120f);
        ZoomAt(new Vector2((float)input.Position.X, (float)input.Position.Y), _scale * factor);
        args.Handled = true;
    }

    /// <summary>Applies an absolute scale around a screen-space anchor for both wheel and benchmark input.</summary>
    internal void ZoomAt(Vector2 anchor, float scale)
    {
        var nextScale = Math.Clamp(scale, .001f, 3);
        _translation = anchor - (anchor - _translation) * (nextScale / _scale);
        _scale = nextScale;
        _surface.Invalidate();
    }

    /// <summary>Applies benchmark input to the same transient node geometry used by pointer movement.</summary>
    internal void DragSelected(Vector2 origin, Vector2 screenDelta) =>
        MoveCard(_byPath[SelectedPath], origin, screenDelta);

    /// <summary>Moves a node and its attached edges without rebuilding the layout or touching a workspace file.</summary>
    private void MoveCard(Card card, Vector2 origin, Vector2 screenDelta)
    {
        var limit = new Vector2((float)MapLayoutCodec.MaximumCoordinate);
        card.Position = Vector2.Clamp(origin + screenDelta / _scale, -limit, limit);
        _surface.Invalidate();
    }

    /// <summary>Releases device-dependent DirectWrite layouts before changing documents or recreating the device.</summary>
    private void ClearText()
    {
        foreach (var card in _cards)
        {
            card.Title?.Dispose();
            card.Path?.Dispose();
            card.Introduction?.Dispose();
            card.Title = card.Path = card.Introduction = null;
        }
    }

    /// <summary>Detaches the native surface and releases device resources owned by this window.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        CompositionTarget.Rendering -= FirstComposition;
        _initialFrame.TrySetCanceled();
        _surface.Draw -= Draw;
        _surface.ReleasePointerCaptures();
        ClearText();
        _titleFormat.Dispose();
        _detailFormat.Dispose();
        _surface.RemoveFromVisualTree();
    }

    /// <summary>Caches draw geometry and lazily shaped text for one managed document in the current projection.</summary>
    private sealed class Card
    {
        public DocumentCard Document { get; }
        public Vector2 Position { get; set; }
        public Vector2 Size { get; }
        public Vector2 Center => Position + Size / 2;
        public Color Color { get; }
        public CanvasTextLayout? Title, Path, Introduction;

        /// <summary>Reads the dimensions and RGB value already validated by the shared layout codec.</summary>
        public Card(DocumentCard document, MapCardLayout layout)
        {
            Document = document;
            Position = new((float)layout.X, (float)layout.Y);
            Size = new((float)(layout.Width ?? MapLayoutCodec.CardWidth), (float)(layout.Height ?? MapLayoutCodec.CardHeight));
            var rgb = Convert.ToInt32(layout.Color[1..], 16);
            Color = Windows.UI.Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }
    }
}
