/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; port the Win2D graph renderer and its native input
 * Human guidance: keep the map native and use the same document and layout model as the desktop shell
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using MarkVibe.Core;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Svg;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using static MarkVibe.Core.UiStrings;

namespace MarkVibe.Windows;

/// <summary>Draws the visible map on a single Win2D surface and owns pointer previews and camera geometry.</summary>
internal sealed class WindowsGraphCanvas : Grid, IDisposable
{
    private readonly MapPerformanceProbe? _performance = MapPerformanceProbe.Create();
    private static readonly Color Black = Color.FromArgb(255, 0, 0, 0);
    private static readonly Color Accent = Color.FromArgb(255, 110, 203, 255);
    private static readonly Color CardText = Color.FromArgb(255, 246, 248, 252);
    private static readonly Color DetailText = Color.FromArgb(255, 188, 199, 213);
    private static readonly Color IncomingColor = Color.FromArgb(255, 85, 215, 207);
    private static readonly Color OutgoingColor = Color.FromArgb(255, 183, 163, 255);
    private static readonly Color RouteColor = Color.FromArgb(255, 232, 200, 122);
    private static readonly Color AttentionColor = Color.FromArgb(255, 252, 155, 96);
    private static readonly Color ReferenceColor = Color.FromArgb(255, 255, 119, 210);
    private static readonly Color VisitedColor = Color.FromArgb(255, 114, 149, 176);
    private static readonly Color CardBackground = Color.FromArgb(255, 8, 11, 16);
    private static readonly Color HoverBackground = Color.FromArgb(255, 13, 17, 25);
    private static readonly Color SelectedBackground = Color.FromArgb(255, 14, 16, 27);
    private static readonly string[] IslandPalette = ["#009fbd", "#914bff", "#e8ac45", "#008f83", "#5865d9", "#d64578"];
    private static readonly HashSet<Color> PersistentLights = IslandPalette.Select(ParseColor).Append(Accent).ToHashSet();
    private static readonly Lazy<IReadOnlyDictionary<string, XElement>> IconSymbols = new(ReadIconSymbols);
    private const float MaximumDepth = 12;
    private const int DenseGraphEdgeCount = 250;
    private const int CardTransitionMilliseconds = 420;
    private const int ImpulseMilliseconds = 850;
    private const int ImpulseMaximumDelay = 180;
    private const int DoubleClickWidthMetric = 36;
    private const int DoubleClickHeightMetric = 37;
    private const string StatisticSeparator = "  ·  ";
    private static readonly Vector2 CoordinateLimit = new((float)MapLayoutCodec.MaximumCoordinate);
    private static readonly Vector2 MinimumCard = new(MapLayoutCodec.MinimumCardWidth, MapLayoutCodec.MinimumCardHeight);
    private static readonly Vector2 MaximumCard = new(MapLayoutCodec.MaximumCardWidth, MapLayoutCodec.MaximumCardHeight);
    private readonly CanvasControl _surface = new() { ClearColor = Black };
    private readonly Button _readHere = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };
    private readonly TranslateTransform _readHerePosition = new();
    private readonly TextBlock _linkDetails = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TranslateTransform _linkTipPosition = new();
    private readonly Border _linkTip = new()
    {
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed,
        IsHitTestVisible = false, Padding = new Thickness(10, 7, 10, 7), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
        Background = new SolidColorBrush(Color.FromArgb(246, 24, 30, 42)), BorderBrush = new SolidColorBrush(Accent)
    };
    private readonly CanvasTextFormat _titleFormat = new() { FontSize = 19, Options = CanvasDrawTextOptions.Clip };
    private readonly CanvasTextFormat _overviewTitleFormat = new() { FontSize = 28, Options = CanvasDrawTextOptions.Clip };
    private readonly CanvasTextFormat _detailFormat = new()
    {
        FontSize = 14, Options = CanvasDrawTextOptions.Clip,
        TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis
    };
    private readonly CanvasTextFormat _statsFormat = new()
    {
        FontFamily = "Cascadia Mono", FontSize = 11, Options = CanvasDrawTextOptions.Clip,
        WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
        TrimmingSign = CanvasTrimmingSign.Ellipsis
    };
    private readonly CanvasTextFormat _labelFormat = new()
    {
        FontSize = 11, Options = CanvasDrawTextOptions.Clip, WordWrapping = CanvasWordWrapping.NoWrap,
        TrimmingGranularity = CanvasTextTrimmingGranularity.Character, TrimmingSign = CanvasTrimmingSign.Ellipsis
    };
    private readonly CanvasTextFormat _folderCountFormat = new() { FontSize = 12, HorizontalAlignment = CanvasHorizontalAlignment.Right, Options = CanvasDrawTextOptions.Clip };
    private readonly CanvasTextFormat _iconFormat = new() { FontSize = 18, FontFamily = "Segoe Fluent Icons" };
    private readonly CanvasStrokeStyle _dashed = new() { DashStyle = CanvasDashStyle.Dash };
    private readonly CanvasStrokeStyle _dotted = new() { DashStyle = CanvasDashStyle.Dot };
    private readonly CanvasStrokeStyle _roundStroke = new() { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round };
    private readonly Dictionary<string, Card> _cards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Island> _islands = new(StringComparer.Ordinal);
    private readonly List<MapEdge> _edges = [];
    private readonly HashSet<(string, string)> _route = [];
    private readonly HashSet<string> _routeCards = new(StringComparer.Ordinal);
    private readonly HashSet<string> _picked = new(StringComparer.Ordinal);
    private readonly HashSet<string> _referencePaths = new(StringComparer.Ordinal);
    private readonly HashSet<string> _visited = new(StringComparer.Ordinal);
    private readonly HashSet<string> _relatedCards = new(StringComparer.Ordinal);
    private readonly Dictionary<MapEdge, int> _impulse = [];
    private readonly Dictionary<string, float> _depthLevels = new(StringComparer.Ordinal);
    private Dictionary<string, (float Scale, float Depth)> _previousDepth = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (float Scale, float Emphasis)> _previousHover = new(StringComparer.Ordinal);
    private Dictionary<string, Bounds> _previousCardPositions = new(StringComparer.Ordinal);
    private Dictionary<string, Bounds> _previousIslandPositions = new(StringComparer.Ordinal);
    private readonly Dictionary<MapEdge, (ConnectionCurve Curve, CanvasGeometry Path)> _curvePaths = [];
    private readonly Dictionary<(string Icon, Color Color), RenderedIcon> _icons = [];
    private readonly Dictionary<Color, CanvasRadialGradientBrush> _lights = [];
    private readonly DispatcherQueueTimer _effectTimer;
    private readonly DispatcherQueueTimer _pointerTipTimer;
    private MapSnapshot? _map;
    private MapExplorationSnapshot? _exploration;
    private string _selected = "";
    private string? _hovered;
    private MapEdge? _hoveredEdge;
    private IReadOnlyList<(string Path, Rect Box)> _labels = [];
    private IReadOnlyList<(string Path, Rect Box)> _folderLabels = [];
    private Vector2 _translation;
    private float _scale = 1;
    private float _toolbarInset;
    private int _focusDepth;
    private bool _depth;
    private bool _advanced;
    private bool _fitPending;
    private bool _disposed;
    private MapCamera? _pendingCamera;
    private CameraTransition? _cameraTransition;
    private string? _pendingCenter;
    private long _wheelGestureAt;
    private uint? _pointer;
    private Gesture _gesture;
    private Card? _dragged;
    private WindowsMapModel? _deferredModel;
    private string? _gesturePath;
    private MapEdge? _gestureEdge;
    private float _gestureCardScale = 1;
    private bool _gestureMoved;
    private Vector2 _pointerStart, _pointerCurrent, _gestureOrigin;
    private Vector2 _wheelPointer;
    private bool _appendSelection;
    private IReadOnlyList<(string Path, Bounds Bounds)> _rectangleCards = [];
    private readonly HashSet<string> _rectangleCandidates = new(StringComparer.Ordinal);
    private FocusTarget _focusTarget;
    private string? _keyboardPath;
    private MapEdge? _keyboardEdge;
    private bool _reverseTabEntry;
    private TooltipHit _pointerTip;
    private TooltipHit _describedTip;
    private bool _pointerTipReady;
    private bool _keyboardTip;
    private DocumentStatistics? _measuredStatistics;
    private float[][] _statisticEdges = [];
    private readonly Dictionary<DocumentStatistics, StatisticPart[][]> _formattedStatistics = [];
    private string? _lastClick;
    private long _lastClickAt;
    private Vector2 _lastClickPoint;
    private long _selectionStarted, _entranceStarted, _hoverStarted;
    private long? _arrangementStarted;
    private float _selectionProgress = 1, _entranceProgress = 1, _hoverProgress = 1, _arrangementProgress = 1;
    private float _impulseElapsed = ImpulseMilliseconds + ImpulseMaximumDelay;

    public event Action<string>? Selected;
    public event Action<string>? Opened;
    public event Action<string>? ReadHere;
    public event Action<string>? Picked;
    public event Action<IReadOnlyList<string>, bool>? RectanglePicked;
    public event Action<MapEdge>? PreviewLink;
    public event Action<string, double, double>? Moved;
    public event Action<string, double, double>? Resized;
    public event Action<string>? FolderToggled;
    public event Action<string, Point>? ContextMenu;
    public event Action? ViewportDrawn;

    /// <summary>Attaches one draw surface and listens only for viewport changes and native input.</summary>
    public WindowsGraphCanvas()
    {
        IsTabStop = true;
        AutomationProperties.SetName(this, T("Document map"));
        AutomationProperties.SetHelpText(this, T("Use Tab and Shift+Tab to focus map items. Page Up and Page Down focus documents. Add Shift for connections or Control for folders. Space selects, and Enter opens. R focuses the resize grip and M focuses the minimap."));
        Children.Add(_surface);
        _readHere.Content = T("Read here");
        ToolTipService.SetToolTip(_readHere, T("Read here"));
        _readHere.RenderTransform = _readHerePosition;
        _readHere.Click += (_, _) => OpenSelectedPreview();
        _readHere.SizeChanged += (_, _) => _surface.Invalidate();
        Children.Add(_readHere);
        _linkDetails.Foreground = new SolidColorBrush(CardText);
        _linkTip.Child = _linkDetails;
        _linkTip.RenderTransform = _linkTipPosition;
        _linkTip.SizeChanged += (_, _) => _surface.Invalidate();
        AutomationProperties.SetAccessibilityView(_linkTip, AccessibilityView.Raw);
        Children.Add(_linkTip);
        _surface.Draw += Draw;
        _surface.CreateResources += (_, _) => ReleaseDrawingResources();
        _surface.PointerPressed += PointerDown;
        _surface.PointerMoved += PointerMove;
        _surface.PointerReleased += PointerUp;
        _surface.PointerCaptureLost += PointerLost;
        _surface.PointerCanceled += PointerLost;
        _surface.PointerWheelChanged += Wheel;
        _surface.PointerExited += (_, _) => ClearPointerHover();
        _surface.RightTapped += RightTappedOnSurface;
        KeyDown += OnKeyDown;
        GettingFocus += OnGettingFocus;
        GotFocus += OnGotFocus;
        LostFocus += (_, _) => SetKeyboardFocus(FocusTarget.Viewport);
        _effectTimer = DispatcherQueue.CreateTimer();
        _effectTimer.Interval = TimeSpan.FromMilliseconds(16);
        _effectTimer.Tick += AdvanceEffects;
        _pointerTipTimer = DispatcherQueue.CreateTimer();
        _pointerTipTimer.Interval = TimeSpan.FromMilliseconds(450);
        _pointerTipTimer.IsRepeating = false;
        _pointerTipTimer.Tick += PointerTipReady;
    }

    /// <summary>Updates projection geometry and keeps compatible DirectWrite layouts across selection changes.</summary>
    public void Show(WindowsMapModel model, bool animateArrangement = false)
    {
        // An index update may arrive during a pointer preview. The drag continues against the geometry it started with.
        if (_dragged != null)
        {
            _deferredModel = model;
            return;
        }
        _deferredModel = null;
        var canAnimateArrangement = animateArrangement && _map != null && model.Snapshot.Edges.Count < DenseGraphEdgeCount && AnimationsEnabled();
        var priorCards = canAnimateArrangement ? _cards.ToDictionary(pair => pair.Key, pair => AnimatedLogical(pair.Value), StringComparer.Ordinal) : null;
        var priorIslands = canAnimateArrangement ? _islands.ToDictionary(pair => pair.Key, pair => AnimatedIsland(pair.Value), StringComparer.Ordinal) : null;
        var changedSelection = _selected != model.SelectedPath;
        var animateDepth = _selected.Length > 0 && (changedSelection || _depth != model.Depth) && AnimationsEnabled();
        if (animateDepth && model.Snapshot.Edges.Count < DenseGraphEdgeCount)
        {
            // Capture the drawn poses so rapid selection resumes from the current frame.
            _previousDepth = _cards.ToDictionary(pair => pair.Key,
                pair => (DisplayScale(pair.Value), DepthLevel(pair.Key)), StringComparer.Ordinal);
        }
        else if (animateDepth || changedSelection)
        {
            _previousDepth.Clear();
        }
        _map = model.Snapshot;
        _exploration = model.Exploration;
        _selected = model.SelectedPath;
        _advanced = model.Advanced;
        _depth = model.Depth;
        _focusDepth = model.Atlas.FocusDepth;
        _picked.Clear();
        _picked.UnionWith(_exploration.Picked.Select(document => document.Path));
        _visited.Clear();
        _visited.UnionWith(_exploration.Trail.Select(document => document.Path));
        _route.Clear();
        var route = _map.Route ?? [];
        _routeCards.Clear();
        _routeCards.UnionWith(route);
        foreach (var step in route.Zip(route.Skip(1)))
        {
            _route.Add(step);
            _route.Add((step.Second, step.First));
        }

        var current = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in _map.Nodes)
        {
            if (!model.Layout.Nodes.TryGetValue(node.Path, out var layout))
            {
                continue;
            }
            current.Add(node.Path);
            if (_cards.TryGetValue(node.Path, out var card))
            {
                card.Update(node, layout);
            }
            else
            {
                _cards.Add(node.Path, new Card(node, layout));
            }
        }
        foreach (var path in _cards.Keys.Where(path => !current.Contains(path)).ToArray())
        {
            _cards[path].ReleaseText();
            _cards.Remove(path);
        }
        _edges.Clear();
        _edges.AddRange(_map.Edges.Where(edge => _cards.ContainsKey(edge.Source) && _cards.ContainsKey(edge.Target)));
        var projectedEdges = _edges.ToHashSet();
        foreach (var edge in _curvePaths.Keys.Where(edge => !projectedEdges.Contains(edge)).ToArray())
        {
            _curvePaths[edge].Path.Dispose();
            _curvePaths.Remove(edge);
        }
        var activeIcons = _cards.Values.Select(card => (card.Layout.Icon, card.Color)).ToHashSet();
        foreach (var icon in _icons.Keys.Where(icon => !activeIcons.Contains(icon)).ToArray())
        {
            _icons[icon].Dispose();
            _icons.Remove(icon);
        }
        var activeColors = activeIcons.Select(icon => icon.Color).ToHashSet();
        foreach (var color in _lights.Keys.Where(color => !PersistentLights.Contains(color) && !activeColors.Contains(color)).ToArray())
        {
            _lights[color].Dispose();
            _lights.Remove(color);
        }
        if (animateDepth)
        {
            _impulse.Clear();
            if (changedSelection)
            {
                foreach (var edge in _edges.Where(edge => edge.Source == _selected || edge.Target == _selected).Take(24))
                {
                    _impulse.TryAdd(edge, Math.Min(_impulse.Count * 18, ImpulseMaximumDelay));
                }
            }
            _selectionStarted = Stopwatch.GetTimestamp();
            _selectionProgress = 0;
            _impulseElapsed = _impulse.Count > 0 ? 0 : ImpulseMilliseconds + ImpulseMaximumDelay;
            _effectTimer.Start();
        }
        BuildIslands(model);
        if (priorCards != null && priorIslands != null)
        {
            BeginArrangement(priorCards, priorIslands);
        }
        UpdateDepthLevels();
        ValidateKeyboardFocus();
        _surface.Invalidate();
    }

    /// <summary>Highlights documents already attached to the native AI reference list.</summary>
    public void SetReferencePaths(IReadOnlyList<string> paths)
    {
        if (_referencePaths.SetEquals(paths))
        {
            return;
        }
        _referencePaths.Clear();
        _referencePaths.UnionWith(paths);
        _surface.Invalidate();
    }

    /// <summary>Reserves the top of the canvas for the measured floating toolbar.</summary>
    public void SetToolbarInset(double top)
    {
        var inset = double.IsFinite(top) ? (float)Math.Clamp(top, 0, float.MaxValue) : 0;
        if (_toolbarInset == inset)
        {
            return;
        }
        _toolbarInset = inset;
        _surface.Invalidate();
    }

    /// <summary>Detaches the displayed map and releases its text and gesture resources.</summary>
    public void Clear()
    {
        _performance?.Flush();
        _deferredModel = null;
        SuspendInteractions();
        CancelCamera();
        ReleaseDrawingResources();
        _map = null;
        _exploration = null;
        _selected = "";
        _cards.Clear();
        _islands.Clear();
        _edges.Clear();
        _route.Clear();
        _routeCards.Clear();
        _referencePaths.Clear();
        _visited.Clear();
        _relatedCards.Clear();
        _depthLevels.Clear();
        _labels = [];
        _folderLabels = [];
        SetHover(null, null);
        _surface.Invalidate();
    }

    /// <summary>Animates a bounded entrance only when system motion settings allow it.</summary>
    public void AnimateEntrance()
    {
        if (_cards.Count == 0 || !AnimationsEnabled())
        {
            return;
        }
        _entranceStarted = Stopwatch.GetTimestamp();
        _entranceProgress = 0;
        _effectTimer.Start();
    }

    /// <summary>Stops finite drawing effects immediately when the native map is suspended or closed.</summary>
    public void StopEffects()
    {
        _effectTimer.Stop();
        _selectionProgress = _entranceProgress = _hoverProgress = _arrangementProgress = 1;
        _selectionStarted = _entranceStarted = _hoverStarted = 0;
        _arrangementStarted = null;
        _impulseElapsed = ImpulseMilliseconds + ImpulseMaximumDelay;
        _impulse.Clear();
        _previousDepth.Clear();
        _previousHover.Clear();
        _previousCardPositions.Clear();
        _previousIslandPositions.Clear();
        if (!_disposed)
        {
            _surface.Invalidate();
        }
    }

    /// <summary>Stops hidden graph effects, clears pending clicks and discards transient geometry.</summary>
    public void SuspendInteractions()
    {
        var fitWhenVisible = _fitPending;
        var savedCamera = _pendingCamera;
        CancelCamera();
        _fitPending = fitWhenVisible;
        _pendingCamera = savedCamera;
        CancelGesture();
        ClearPointerHover();
        SetKeyboardFocus(FocusTarget.Viewport);
        StopEffects();
        _readHere.Visibility = Visibility.Collapsed;
    }

    /// <summary>Determines whether this native desktop prefers reduced motion.</summary>
    private static bool AnimationsEnabled() => new UISettings().AnimationsEnabled;

    /// <summary>Updates transition phases once per frame and drops the timer when all effects end.</summary>
    private void AdvanceEffects(DispatcherQueueTimer timer, object args)
    {
        var now = Stopwatch.GetTimestamp();
        _selectionProgress = AnimationProgress(_selectionStarted, now, CardTransitionMilliseconds);
        _entranceProgress = AnimationProgress(_entranceStarted, now, 1070);
        _hoverProgress = AnimationProgress(_hoverStarted, now, CardTransitionMilliseconds);
        _arrangementProgress = AnimationProgress(_arrangementStarted ?? 0, now, CardTransitionMilliseconds);
        _impulseElapsed = _selectionStarted == 0 || _impulse.Count == 0 ? ImpulseMilliseconds + ImpulseMaximumDelay :
            (float)Stopwatch.GetElapsedTime(_selectionStarted, now).TotalMilliseconds;
        if (_arrangementProgress >= 1 && _arrangementStarted.HasValue)
        {
            _arrangementStarted = null;
            _previousCardPositions.Clear();
            _previousIslandPositions.Clear();
        }
        AdvanceCamera(now);
        _surface.Invalidate();
        if (_selectionProgress >= 1 && _impulseElapsed >= ImpulseMilliseconds + ImpulseMaximumDelay && _entranceProgress >= 1 &&
            _hoverProgress >= 1 && _arrangementProgress >= 1 && _cameraTransition == null && _wheelGestureAt == 0)
        {
            StopEffects();
        }
    }

    /// <summary>Returns the bounded phase of a finite presentation transition.</summary>
    private static float AnimationProgress(long started, long now, int duration) => started == 0 ? 1 :
        (float)Math.Clamp(Stopwatch.GetElapsedTime(started, now).TotalMilliseconds / duration, 0, 1);

    /// <summary>Starts a new layout transition using the positions actually visible before the command.</summary>
    private void BeginArrangement(Dictionary<string, Bounds> cards, Dictionary<string, Bounds> islands)
    {
        if (!_cards.Any(pair => cards.TryGetValue(pair.Key, out var prior) && prior != pair.Value.Logical) &&
            !_islands.Any(pair => islands.TryGetValue(pair.Key, out var prior) && prior != pair.Value.Bounds))
        {
            return;
        }
        _previousCardPositions = cards;
        _previousIslandPositions = islands;
        _arrangementStarted = Stopwatch.GetTimestamp();
        _arrangementProgress = 0;
        _effectTimer.Start();
    }

    /// <summary>Builds folder bounds from the full layout, independently of the bounded draw projection.</summary>
    private void BuildIslands(WindowsMapModel model)
    {
        _islands.Clear();
        if (!model.Atlas.FolderGroups)
        {
            return;
        }

        foreach (var folder in model.Layout.Nodes.GroupBy(pair => WindowsMapModel.FolderOf(pair.Key)))
        {
            var bounds = Bounds.Combine(folder.Select(pair => Bounds.From(pair.Value)));
            var compact = model.IsFolderCollapsed(folder.Key);
            var origin = bounds.Origin - new Vector2(32, 62);
            var frame = new Bounds(origin, compact ? new Vector2(300, 110) : bounds.Size + new Vector2(64, 94));
            _islands.Add(folder.Key, new Island(folder.Key, frame, folder.Count(), compact, FolderColor(folder.Key)));
        }
    }

    /// <summary>Fits the graph after the native canvas has a measured viewport.</summary>
    public void Fit()
    {
        CancelRectangleForCamera();
        CancelCamera();
        ClearPointerHover();
        _fitPending = true;
        _surface.Invalidate();
    }

    /// <summary>Centers a projected card without changing the persisted drag positions.</summary>
    public void Center(string path)
    {
        if (_cards.ContainsKey(path))
        {
            CancelRectangleForCamera();
            CancelCamera();
            ClearPointerHover();
            _pendingCenter = path;
            _surface.Invalidate();
        }
    }

    /// <summary>Captures the logical center and bounded zoom in the current viewport.</summary>
    public MapCamera CaptureCamera()
    {
        if (_fitPending || _pendingCamera != null)
        {
            ApplyDeferredCamera(GraphBounds(), Viewport);
        }
        var center = World(Viewport / 2);
        return new MapCamera(Math.Clamp(center.X, -1_000_000, 1_000_000), Math.Clamp(center.Y, -1_000_000, 1_000_000), _scale);
    }

    /// <summary>Applies a managed viewpoint, deferring until layout if necessary.</summary>
    public void SetCamera(MapCamera camera)
    {
        CancelRectangleForCamera();
        CancelCamera();
        ClearPointerHover();
        _pendingCamera = camera;
        _surface.Invalidate();
    }

    /// <summary>Applies a saved camera or fits the canvas only after it has a usable viewport.</summary>
    private void ApplyDeferredCamera(Bounds bounds, Vector2 viewport)
    {
        var usableHeight = viewport.Y - ToolbarInset;
        if (viewport.X <= 60 || usableHeight <= 60)
        {
            return;
        }
        if (_fitPending)
        {
            _fitPending = false;
            var fitScale = Math.Clamp(Math.Min((viewport.X - 60) / bounds.Size.X, (usableHeight - 60) / bounds.Size.Y), .001f, 1);
            var center = ShiftCenterBelowToolbar(bounds.Center, fitScale);
            if (_arrangementStarted.HasValue)
            {
                _cameraTransition = new(World(viewport / 2), center, _scale, fitScale, Stopwatch.GetTimestamp());
                _effectTimer.Start();
            }
            else
            {
                ApplyCamera(center, fitScale);
            }
        }
        if (_pendingCamera is { } camera)
        {
            _pendingCamera = null;
            ApplyCamera(new Vector2((float)camera.CenterX, (float)camera.CenterY), (float)camera.Scale);
        }
        if (_pendingCenter is { } path)
        {
            _pendingCenter = null;
            if (_cards.TryGetValue(path, out var card) && !IsHidden(card))
            {
                var size = card.Logical.Size * (_depth && path == _selected ? 1.28f : 1);
                var target = Math.Clamp(Math.Min((viewport.X - 80) / size.X, (usableHeight - 80) / size.Y), .05f, 1.15f);
                var center = ShiftCenterBelowToolbar(card.Logical.Center, target);
                if (!AnimationsEnabled())
                {
                    ApplyCamera(center, target);
                }
                else
                {
                    _cameraTransition = new(World(viewport / 2), center, _scale, target, Stopwatch.GetTimestamp());
                    _effectTimer.Start();
                }
            }
        }
    }

    /// <summary>Uses a bounded ease-out transition and restores mouse hover after wheel movement settles.</summary>
    private void AdvanceCamera(long now)
    {
        if (_cameraTransition is { } camera)
        {
            var progress = AnimationProgress(camera.Started, now, CardTransitionMilliseconds);
            var eased = EaseOut(progress);
            ApplyCamera(Vector2.Lerp(camera.StartCenter, camera.EndCenter, eased), camera.StartScale + (camera.EndScale - camera.StartScale) * eased);
            if (progress >= 1)
            {
                _cameraTransition = null;
            }
        }
        if (_wheelGestureAt != 0 && Stopwatch.GetElapsedTime(_wheelGestureAt, now).TotalMilliseconds >= 180)
        {
            _wheelGestureAt = 0;
            if (_pointer == null)
            {
                UpdatePointerHover(_wheelPointer);
            }
        }
    }

    /// <summary>Preserves the requested logical center at the supplied zoom.</summary>
    private void ApplyCamera(Vector2 center, float scale)
    {
        _scale = Math.Clamp(scale, .001f, 1.8f);
        _translation = Viewport / 2 - center * _scale;
    }

    /// <summary>Offsets fitted or centered content below the floating toolbar in screen space.</summary>
    private Vector2 ShiftCenterBelowToolbar(Vector2 center, float zoom) => center - new Vector2(0, ToolbarInset / (2 * zoom));

    /// <summary>Discards a rectangle if a separate action is about to move the coordinate space it captured.</summary>
    private void CancelRectangleForCamera()
    {
        if (_gesture == Gesture.Select)
        {
            CancelGesture();
        }
    }

    /// <summary>Lets new input continue from the current interpolated camera rather than an obsolete endpoint.</summary>
    private void CancelCamera()
    {
        _fitPending = false;
        _pendingCamera = null;
        _pendingCenter = null;
        _cameraTransition = null;
        _wheelGestureAt = 0;
    }

    private Vector2 Viewport => new((float)ActualWidth, (float)ActualHeight);
    private float ToolbarInset => Math.Min(_toolbarInset, Viewport.Y * .5f);
    private bool IsArranging => _arrangementStarted.HasValue;
    private bool IsDense => _edges.Count >= DenseGraphEdgeCount || _cards.Count >= DenseGraphEdgeCount;

    /// <summary>Draws one bounded frame. The control issues no continuous idle redraws.</summary>
    private void Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_disposed || _map == null || ActualWidth < 1 || ActualHeight < 1)
        {
            return;
        }
        var measuredAt = _performance == null ? 0 : Stopwatch.GetTimestamp();
        var allocatedBefore = _performance == null ? 0 : GC.GetAllocatedBytesForCurrentThread();
        var bounds = GraphBounds();
        var viewport = Viewport;
        ApplyDeferredCamera(bounds, viewport);

        var drawing = args.DrawingSession;
        var visible = new Bounds(World(Vector2.Zero), viewport / _scale);
        UpdateRelations();
        var worldTransform = Matrix3x2.CreateScale(_scale) * Matrix3x2.CreateTranslation(_translation);
        drawing.Transform = worldTransform;
        foreach (var island in _islands.Values)
        {
            var shape = AnimatedIsland(island);
            if (shape.Overlaps(visible))
            {
                DrawIsland(drawing, island, shape);
            }
        }
        DrawTrail(drawing);
        foreach (var edge in _edges)
        {
            DrawEdge(drawing, edge, visible);
        }
        foreach (var card in VisibleCards().OrderBy(CardLayer))
        {
            var shape = Display(card);
            if (shape.Overlaps(visible))
            {
                // Animating the whole card retains static text and lines up the links with its visible sides.
                var logical = card.Logical;
                var magnification = shape.Size / logical.Size;
                drawing.Transform = Matrix3x2.CreateTranslation(-logical.Origin) * Matrix3x2.CreateScale(magnification) *
                    Matrix3x2.CreateTranslation(shape.Origin) * worldTransform;
                DrawCard(drawing, card, card.Logical);
            }
        }
        drawing.Transform = Matrix3x2.Identity;
        UpdateReadHere(viewport);
        UpdateLinkDetails(viewport);
        DrawLabels(drawing, viewport);
        var minimapBounds = IsArranging ? Bounds.Combine(VisibleCards().Select(AnimatedLogical).Concat(_islands.Values.Select(AnimatedIsland))).Expand(24) : bounds;
        DrawMinimap(drawing, minimapBounds, visible);
        if (_focusTarget == FocusTarget.Minimap && Minimap is { } focusedMinimap)
        {
            drawing.DrawRectangle(focusedMinimap.Rect, Accent, 2);
        }
        DrawMapLegend(drawing, viewport);
        if (_gesture == Gesture.Select)
        {
            var selection = Bounds.Between(_pointerStart, _pointerCurrent).Rect;
            drawing.FillRectangle(selection, Alpha(Accent, .15f));
            drawing.DrawRectangle(selection, Accent, 1);
        }
        ViewportDrawn?.Invoke();
        _performance?.Record(_gesture == Gesture.Pan ? "pan" : _wheelGestureAt != 0 ? "zoom" : "other",
            measuredAt, allocatedBefore, _cards.Count, _edges.Count);
    }

    /// <summary>Paints folder tint, boundary and the expanded or collapsed island heading.</summary>
    private void DrawIsland(CanvasDrawingSession drawing, Island island, Bounds shape)
    {
        const float radius = 20;
        var activeEdge = _hoveredEdge ?? FocusedEdge;
        var active = FocusedFolder == island.Path || activeEdge != null &&
            (_cards.GetValueOrDefault(activeEdge.Source)?.Folder == island.Path || _cards.GetValueOrDefault(activeEdge.Target)?.Folder == island.Path);
        var background = island.Collapsed ? Color.FromArgb(255, 8, 11, 18) : Color.FromArgb(128, 5, 7, 11);
        if (active && !IsDirectCameraGesture)
        {
            var spread = 3 / _scale;
            drawing.DrawRoundedRectangle(shape.Expand(spread).Rect, radius + spread, radius + spread, Alpha(Accent, .15f), 5 / _scale);
        }
        drawing.FillRoundedRectangle(shape.Rect, radius, radius, background);
        if (!IsDense && !IsDirectCameraGesture)
        {
            DrawCardLight(drawing, shape, radius, island.Color, shape.Origin + new Vector2(shape.Size.X * .12f, 0),
                shape.Size * new Vector2(.9f, .95f), island.Collapsed ? .23f : .15f);
        }
        drawing.DrawRoundedRectangle(shape.Rect, radius, radius, active ? Accent : Alpha(island.Color, .38f), (active ? 2 : 1) / _scale);
        if (_scale >= .4f)
        {
            var prefix = island.Collapsed ? "▸ " : "▾ ";
            var foreground = active ? CardText : Mix(island.Color, CardText, .35f);
            var count = string.Format(System.Globalization.CultureInfo.CurrentCulture, T("{0} documents"), island.Count);
            if (island.Collapsed)
            {
                drawing.DrawText(prefix + island.Label, new Rect(shape.Origin.X + 16, shape.Origin.Y + 32, shape.Size.X - 32, 24), foreground, _titleFormat);
                drawing.DrawText(count, new Rect(shape.Origin.X + 16, shape.Origin.Y + 62, shape.Size.X - 32, 19), DetailText, _labelFormat);
            }
            else
            {
                drawing.DrawText(prefix + island.Label, new Rect(shape.Origin.X + 16, shape.Origin.Y + 11, shape.Size.X - 154, 25), foreground, _detailFormat);
                drawing.DrawText(count, new Rect(shape.End.X - 136, shape.Origin.Y + 11, 120, 19), DetailText, _folderCountFormat);
            }
        }
    }

    /// <summary>Draws a dotted path only between recent visits that remain visible.</summary>
    private void DrawTrail(CanvasDrawingSession drawing)
    {
        var trail = _exploration?.Trail ?? [];
        foreach (var step in trail.Zip(trail.Skip(1)))
        {
            if (_cards.TryGetValue(step.First.Path, out var a) && _cards.TryGetValue(step.Second.Path, out var b) && !IsHidden(a) && !IsHidden(b))
            {
                drawing.DrawLine(Display(a).Center, Display(b).Center, Alpha(VisitedColor, .3f), 1 / _scale, _dotted);
            }
        }
    }

    /// <summary>Draws directed edges with dynamic geometry, endpoint depth and optional relationship emphasis.</summary>
    private void DrawEdge(CanvasDrawingSession drawing, MapEdge edge, Bounds visible)
    {
        if (EdgeGeometry(edge) is not { } curve || !curve.Hull.Expand(12 / _scale).Overlaps(visible))
        {
            return;
        }

        var focus = RelationFocus;
        var outgoing = focus != null && edge.Source == focus;
        var incoming = focus != null && !outgoing && edge.Target == focus;
        var onRoute = _route.Contains((edge.Source, edge.Target));
        var emphasized = _hoveredEdge == edge || FocusedEdge == edge;
        var selected = edge.Source == _selected || edge.Target == _selected;
        var color = onRoute ? RouteColor : emphasized ? Accent : incoming ? IncomingColor : outgoing ? OutgoingColor : Accent;
        if (!emphasized && !onRoute)
        {
            var furthest = Math.Max(DepthLevel(edge.Source), DepthLevel(edge.Target));
            var depthOpacity = 1 - furthest / MaximumDepth * .72f;
            var relationOpacity = focus != null && !incoming && !outgoing ? .2f : 1;
            var focusOpacity = IsWithinDistance(edge.Source) && IsWithinDistance(edge.Target) ? 1 : .24f;
            var baseOpacity = selected ? .90f : _scale < .4f ? .08f : .32f;
            color = Alpha(color, baseOpacity * depthOpacity * focusOpacity * relationOpacity);
        }

        var path = CurvePath(edge, curve);
        var width = (emphasized || onRoute ? 3 : selected || incoming || outgoing ? 1.55f : 1) / _scale;
        if (incoming && !onRoute && !emphasized)
        {
            drawing.DrawGeometry(path, color, width, _dashed);
        }
        else
        {
            drawing.DrawGeometry(path, color, width);
        }
        if (!IsDirectCameraGesture)
        {
            var tangent = Vector2.Normalize(curve.End - curve.EndControl);
            var arrowBase = curve.End - tangent * (8 / _scale);
            var wing = new Vector2(-tangent.Y, tangent.X) * (3.2f / _scale);
            drawing.DrawLine(curve.End, arrowBase + wing, color, width);
            drawing.DrawLine(curve.End, arrowBase - wing, color, width);
        }
        if (!IsDirectCameraGesture && _impulse.TryGetValue(edge, out var delay))
        {
            DrawImpulse(drawing, curve, edge.Source == _selected ? OutgoingColor : IncomingColor,
                (_impulseElapsed - delay) / ImpulseMilliseconds);
        }
    }

    /// <summary>Travels along the directed curve as a short stroke with a finite fade and no extra device geometry.</summary>
    private void DrawImpulse(CanvasDrawingSession drawing, ConnectionCurve curve, Color color, float progress)
    {
        if (progress <= 0 || progress >= 1)
        {
            return;
        }

        // A short cumulative-length table keeps the pulse speed consistent through tight curves.
        const int samples = 16;
        Span<float> lengths = stackalloc float[samples + 1];
        lengths[0] = 0;
        var previous = curve.Start;
        for (var index = 1; index <= samples; index++)
        {
            var point = curve.PointAt((float)index / samples);
            lengths[index] = lengths[index - 1] + Vector2.Distance(previous, point);
            previous = point;
        }

        var opacity = Math.Min(1, Math.Min(progress / .15f, (1 - progress) / .12f));
        var head = progress * 1.065f;
        var start = Math.Max(0, head - .065f);
        var end = Math.Min(1, head);
        var stroke = Alpha(color, opacity);
        var first = curve.PointAt(ImpulseParameter(lengths, start));
        for (var segment = 1; segment <= 5; segment++)
        {
            var point = curve.PointAt(ImpulseParameter(lengths, float.Lerp(start, end, segment / 5f)));
            drawing.DrawLine(first, point, stroke, 3 / _scale, _roundStroke);
            first = point;
        }
    }

    /// <summary>Converts a normalized distance along the edge to the underlying cubic parameter.</summary>
    private static float ImpulseParameter(ReadOnlySpan<float> lengths, float progress)
    {
        var total = lengths[^1];
        if (total <= 0)
        {
            return 0;
        }
        var distance = progress * total;
        for (var index = 1; index < lengths.Length; index++)
        {
            if (lengths[index] >= distance && lengths[index] > lengths[index - 1])
            {
                return (index - 1 + (distance - lengths[index - 1]) / (lengths[index] - lengths[index - 1])) / (lengths.Length - 1);
            }
        }
        return 1;
    }

    /// <summary>Reuses stationary Béziers across camera-only frames and replaces edges whose endpoints moved.</summary>
    private CanvasGeometry CurvePath(MapEdge edge, ConnectionCurve curve)
    {
        if (_curvePaths.TryGetValue(edge, out var cached))
        {
            if (cached.Curve == curve)
            {
                return cached.Path;
            }
            cached.Path.Dispose();
        }

        using var builder = new CanvasPathBuilder(_surface);
        builder.BeginFigure(curve.Start);
        builder.AddCubicBezier(curve.StartControl, curve.EndControl, curve.End);
        builder.EndFigure(CanvasFigureLoop.Open);
        var path = CanvasGeometry.CreatePath(builder);
        _curvePaths[edge] = (curve, path);
        return path;
    }

    /// <summary>Draws one card with retained style, level of detail, document statistics and emphasis.</summary>
    private void DrawCard(CanvasDrawingSession drawing, Card card, Bounds shape)
    {
        var path = card.Document.Path;
        var selected = path == _selected;
        var focused = selected || path == _hovered || path == FocusedCard;
        var hoverEmphasis = HoverEmphasis(path);
        var edge = _hoveredEdge ?? FocusedEdge;
        var endpoint = edge?.Source == path || edge?.Target == path;
        var picked = _picked.Contains(path);
        var candidate = IsRectangleCandidate(path);
        var route = _routeCards.Contains(path);
        var attention = _map?.Attention.GetValueOrDefault(path);
        var matchesAttention = _exploration is { AttentionMode: not "all" } state && MatchesAttention(attention, state.AttentionMode);
        var entry = EntryOpacity(AnimatedLogical(card).Center);
        var opacity = entry;
        if (!selected && !picked && !candidate && !route)
        {
            var mutedOpacity = VisibilityOf(path);
            if (RelationFocus != null && !_relatedCards.Contains(path))
            {
                mutedOpacity *= .38f;
            }
            if (_exploration is { AttentionMode: not "all" } && !matchesAttention)
            {
                mutedOpacity *= .25f;
            }
            opacity *= float.Lerp(mutedOpacity, 1, hoverEmphasis);
        }
        var origin = shape.Origin;
        var size = shape.Size;
        var strongBorder = selected || picked || candidate || endpoint ? 1 : hoverEmphasis;
        var regularBorder = route ? RouteColor : matchesAttention ? AttentionColor : card.Color;
        var border = Mix(regularBorder, Accent, strongBorder);
        DrawCardSurface(drawing, shape, card.Color, border, selected, hoverEmphasis, strongBorder, opacity, endpoint ? entry : opacity);

        if (_scale < .4f)
        {
            var heading = origin + new Vector2(12, Math.Max(8, (size.Y - 65) / 2));
            DrawIcon(drawing, card, heading + new Vector2(0, 4), 24, opacity);
            card.DrawTitle(drawing, _surface, _overviewTitleFormat, heading + new Vector2(31, 0), Alpha(CardText, opacity), size.X - 53, 65);
            DrawCardMarkers(drawing, path, shape, picked, opacity);
            return;
        }

        DrawIcon(drawing, card, origin + new Vector2(12, 11), 18, opacity);
        card.DrawTitle(drawing, _surface, _titleFormat, origin + new Vector2(37, 8), Alpha(CardText, opacity), size.X - 52, 44);
        DrawAttentionBadge(drawing, shape, attention, opacity);

        var detail = focused && _scale >= .72f;
        var outline = detail && selected && !_advanced && _scale >= 1.05f && card.Document.Headings.Count > 0;
        var availableDetail = size.Y - (detail ? outline || _advanced ? 53 : 24 : 8);
        var introductionHeight = detail && _advanced ? 0 : Math.Min(focused ? 48 : 18, availableDetail - 43);
        if (introductionHeight >= 15)
        {
            card.DrawDetail(drawing, _surface, _detailFormat, origin + new Vector2(12, 43), Alpha(DetailText, opacity), size.X - 25, introductionHeight);
        }
        if (outline)
        {
            var y = origin.Y + size.Y - 53;
            foreach (var heading in card.Document.Headings.Take(3))
            {
                drawing.DrawText(heading.Text, new Rect(origin.X + 12, y, size.X - 28, 14), Alpha(DetailText, opacity), _labelFormat);
                y += 14;
            }
        }
        else if (detail && _advanced)
        {
            drawing.DrawText(path, new Rect(origin.X + 12, origin.Y + size.Y - 62, size.X - 29, 16), Alpha(DetailText, opacity), _statsFormat);
            DrawStatistics(drawing, card.Document.Statistics, shape, opacity);
        }
        else if (detail)
        {
            drawing.DrawText(path, new Rect(origin.X + 12, origin.Y + size.Y - 25, size.X - 29, 18), Alpha(DetailText, opacity), _statsFormat);
        }
        if (focused)
        {
            var grip = origin + size - new Vector2(6);
            drawing.DrawLine(grip - new Vector2(8, 0), grip - new Vector2(0, 8), Accent, 1 / _scale);
            drawing.DrawLine(grip - new Vector2(4, 0), grip - new Vector2(0, 4), Accent, 1 / _scale);
        }
        DrawCardMarkers(drawing, path, shape, picked, opacity);
    }

    /// <summary>Paints the dark card, two subtle radial lights and bounded soft elevation on closer views.</summary>
    private void DrawCardSurface(CanvasDrawingSession drawing, Bounds shape, Color tint, Color border, bool selected,
        float hover, float emphasis, float opacity, float borderOpacity)
    {
        const float radius = 10;
        var fill = selected ? SelectedBackground : Mix(CardBackground, HoverBackground, hover);
        var lift = _scale >= .4f && !IsDense && !IsDirectCameraGesture;
        if (lift)
        {
            var offset = (4 + 2 * emphasis) / _scale;
            var shadow = new Bounds(shape.Origin + new Vector2(0, offset), shape.Size);
            for (var layer = 3; layer >= 1; layer--)
            {
                var spread = layer * (2 + emphasis) / _scale;
                drawing.FillRoundedRectangle(shadow.Expand(spread).Rect, radius + spread, radius + spread,
                    Alpha(Black, opacity * (.09f + .035f * emphasis)));
            }
            if (emphasis > 0)
            {
                var spread = 2.5f / _scale;
                drawing.DrawRoundedRectangle(shape.Expand(spread).Rect, radius + spread, radius + spread,
                    Alpha(border, opacity * emphasis * .17f), 4 / _scale);
            }
        }
        drawing.FillRoundedRectangle(shape.Rect, radius, radius, Alpha(fill, opacity));
        if (_scale >= .4f && (!IsDense || emphasis > 0) && !IsDirectCameraGesture)
        {
            DrawCardLight(drawing, shape, radius, selected ? Accent : tint, shape.Origin,
                shape.Size * new Vector2(.78f, 1.12f), opacity * (selected ? .16f : .12f));
            DrawCardLight(drawing, shape, radius, selected ? tint : Accent, shape.End,
                shape.Size * new Vector2(.67f, .84f), opacity * (selected ? .1f : .07f));
        }
        drawing.DrawRoundedRectangle(shape.Rect, radius, radius, Alpha(border, borderOpacity), float.Lerp(1.05f, 2.2f, emphasis) / _scale);
    }

    /// <summary>Fills a card with a device-cached gradient whose origin and extent follow the card.</summary>
    private void DrawCardLight(CanvasDrawingSession drawing, Bounds shape, float radius, Color color, Vector2 center, Vector2 extent, float opacity)
    {
        if (!_lights.TryGetValue(color, out var brush))
        {
            brush = new CanvasRadialGradientBrush(_surface, color, Alpha(color, 0));
            _lights.Add(color, brush);
        }
        brush.Center = center;
        brush.RadiusX = extent.X;
        brush.RadiusY = extent.Y;
        brush.Opacity = opacity;
        drawing.FillRoundedRectangle(shape.Rect, radius, radius, brush);
    }

    /// <summary>Marks a picked, visited or AI-referenced document at the edge of its own card.</summary>
    private void DrawCardMarkers(CanvasDrawingSession drawing, string path, Bounds shape, bool picked, float opacity)
    {
        if (picked)
        {
            var position = new Vector2(shape.End.X - 11, shape.Origin.Y + 9);
            var color = Alpha(Accent, opacity);
            drawing.DrawLine(position + new Vector2(-9, 0), position + new Vector2(-5, 4), color, 1.6f / _scale, _roundStroke);
            drawing.DrawLine(position + new Vector2(-5, 4), position + new Vector2(2, -4), color, 1.6f / _scale, _roundStroke);
        }
        if (_visited.Contains(path))
        {
            var point = new Vector2(shape.Origin.X + 14, shape.End.Y - 8);
            drawing.DrawLine(point, point + new Vector2(14, 0), Alpha(VisitedColor, opacity), 2);
        }
        if (_referencePaths.Contains(path))
        {
            var point = shape.End - new Vector2(13, 13);
            drawing.FillCircle(point, Math.Max(4, 2.5f / _scale), Alpha(ReferenceColor, opacity));
        }
    }

    /// <summary>Draws distinct local attention badges independently of the selected attention lens.</summary>
    private void DrawAttentionBadge(CanvasDrawingSession drawing, Bounds shape, MapAttention? attention, float opacity)
    {
        var text = attention switch
        {
            { MissingLinks: > 0 } missing => $"! {missing.MissingLinks:N0}",
            { Changed: true } => "↻",
            { Isolated: true } when _exploration is { AttentionMode: not "all" } => "◇",
            _ => null
        };
        if (text == null)
        {
            return;
        }
        var badge = new Rect(shape.End.X - 46, shape.Origin.Y + 25, 36, 16);
        drawing.FillRoundedRectangle(badge, 5, 5, Alpha(Black, opacity));
        drawing.DrawText(text, badge, Alpha(AttentionColor, opacity), _labelFormat);
    }

    /// <summary>Separates source size and estimated tokens from the smaller counts on active cards.</summary>
    private void DrawStatistics(CanvasDrawingSession drawing, DocumentStatistics? statistics, Bounds shape, float opacity)
    {
        var color = Alpha(DetailText, opacity);
        if (statistics == null)
        {
            drawing.DrawText(UiStrings.T("Metadata unavailable"), StatisticsRowBounds(shape, 0), color, _statsFormat);
            return;
        }
        var rows = StatisticsRows(statistics);
        for (var index = 0; index < rows.Length; index++)
        {
            var text = string.Join(StatisticSeparator, rows[index].Select(part => part.VisibleText));
            drawing.DrawText(text, StatisticsRowBounds(shape, index), color, _statsFormat);
        }
    }

    /// <summary>Reveals cards progressively according to how far they lie from the arranged viewport center.</summary>
    private float EntryOpacity(Vector2 world)
    {
        if (_entranceProgress >= 1)
        {
            return 1;
        }
        var span = Math.Max(1, (Viewport / 2).Length());
        var stagger = Math.Min(.72f, Vector2.Distance(Screen(world), Viewport / 2) / span * .72f);
        return Math.Clamp((_entranceProgress - stagger) / .28f, 0, 1);
    }

    /// <summary>Places a bounded number of nonoverlapping overview labels in screen coordinates.</summary>
    private void DrawLabels(CanvasDrawingSession drawing, Vector2 viewport)
    {
        if (_scale >= .4f || viewport.X < 160 || viewport.Y < 90)
        {
            _labels = [];
            _folderLabels = [];
            return;
        }
        var labels = new List<(string Path, Rect Box)>();
        var folders = new List<(string Path, Rect Box)>();
        var occupied = new List<Bounds>(12);

        // Inspect every projected document before bounding labels so panning outside the hub reveals names.
        foreach (var card in VisibleCards().OrderByDescending(card => card.Document.Path == _selected))
        {
            var selected = card.Document.Path == _selected;
            if (_islands.Count != 0 && !selected)
            {
                continue;
            }
            if (TryLabel(Display(card).Center, card.Document.Title, selected ? Accent : DetailText) is { } label)
            {
                labels.Add((card.Document.Path, label));
            }
            if (occupied.Count >= 12)
            {
                break;
            }
        }
        foreach (var island in _islands.Values)
        {
            if (occupied.Count >= 12)
            {
                break;
            }
            var shape = AnimatedIsland(island);
            var point = new Vector2(shape.Center.X, shape.Origin.Y);
            if (TryLabel(point, island.Label, island.Color) is { } label)
            {
                folders.Add((island.Path, label));
            }
        }
        _labels = labels;
        _folderLabels = folders;

        /// <summary>Places an on-screen caption while protecting the minimap and previously placed labels.</summary>
        Rect? TryLabel(Vector2 world, string caption, Color color)
        {
            var point = Screen(world);
            if (point.X < 12 || point.X > viewport.X - 12 || point.Y < ToolbarInset + 18 || point.Y > viewport.Y - 40)
            {
                return null;
            }
            var left = Math.Clamp(point.X - 74, 6, viewport.X - 154);
            var label = new Bounds(new Vector2(left, point.Y - 14), new Vector2(148, 28));
            if (occupied.Any(previous => previous.Expand(3).Overlaps(label)) || Minimap is { } mini && mini.Overlaps(label))
            {
                return null;
            }
            drawing.FillRoundedRectangle(label.Rect, 7, 7, Color.FromArgb(237, 8, 13, 22));
            drawing.DrawRoundedRectangle(label.Rect, 7, 7, Alpha(color, .68f), 1);
            drawing.DrawText(caption, new Rect(left + 8, label.Origin.Y + 6, 132, 17), color, _labelFormat);
            occupied.Add(label);
            return label.Rect;
        }
    }

    /// <summary>Shows the map zoom level and the meaning of both relationship strokes.</summary>
    private void DrawMapLegend(CanvasDrawingSession drawing, Vector2 viewport)
    {
        if (viewport.X < 165 || viewport.Y < 72)
        {
            return;
        }
        var mode = UiStrings.T(_scale < .4f ? "Overview" : _scale >= 1.05f ? "Detail" : "Documents");
        var caption = $"{_scale:P0} · {mode}";
        var width = Math.Min(Math.Max(105, caption.Length * 7 + 14), viewport.X - 24);
        var badge = new Rect(12, viewport.Y - 35, width, 23);
        drawing.FillRoundedRectangle(badge, 10, 10, Color.FromArgb(230, 8, 11, 18));
        drawing.DrawRoundedRectangle(badge, 10, 10, Alpha(DetailText, .18f), 1);
        drawing.DrawText(caption, new Rect(badge.X + 8, badge.Y + 4, badge.Width - 13, 17), DetailText, _labelFormat);

        if (RelationFocus != null && viewport.X >= 330)
        {
            var incoming = "⇢ " + UiStrings.T("Incoming");
            var outgoing = "→ " + UiStrings.T("Outgoing");
            var top = ToolbarInset + 12;
            var panel = new Rect(12, top - 4, 232, 24);
            drawing.FillRoundedRectangle(panel, 7, 7, Alpha(CardBackground, .94f));
            drawing.DrawText(incoming, new Rect(20, top, 105, 16), IncomingColor, _labelFormat);
            drawing.DrawText(outgoing, new Rect(132, top, 105, 16), OutgoingColor, _labelFormat);
        }
    }

    /// <summary>Returns the on-screen card rectangle for anchoring the native in-map preview.</summary>
    public bool TryGetCardViewportBounds(string path, out Rect bounds)
    {
        bounds = default;
        if (!_cards.TryGetValue(path, out var card) || IsHidden(card) || Viewport.X < 1 || Viewport.Y < 1)
        {
            return false;
        }
        var shape = Display(card);
        var visible = new Bounds(Screen(shape.Origin), shape.Size * _scale);
        if (!visible.Overlaps(new Bounds(Vector2.Zero, Viewport)))
        {
            return false;
        }
        bounds = visible.Rect;
        return true;
    }

    /// <summary>Draws the minimap from card and island geometry and marks the current camera rectangle.</summary>
    private void DrawMinimap(CanvasDrawingSession drawing, Bounds graph, Bounds visible)
    {
        if (Minimap is not { } minimap)
        {
            return;
        }
        drawing.FillRoundedRectangle(minimap.Rect, 14, 14, Color.FromArgb(255, 8, 11, 18));
        drawing.DrawRoundedRectangle(minimap.Rect, 14, 14, Color.FromArgb(255, 48, 59, 76), 1);
        var (origin, scale) = MinimapProjection(minimap, graph);
        foreach (var card in VisibleCards())
        {
            var rectangle = MinimapRect(AnimatedLogical(card));
            var radius = 10 * scale;
            drawing.FillRoundedRectangle(rectangle, radius, radius, card.Document.Path == _selected ? Accent : Alpha(card.Color, .65f));
        }
        foreach (var island in _islands.Values.Where(island => island.Collapsed))
        {
            var radius = 12 * scale;
            drawing.FillRoundedRectangle(MinimapRect(AnimatedIsland(island)), radius, radius, island.Color);
        }
        var start = Vector2.Clamp(visible.Origin, graph.Origin, graph.End);
        var end = Vector2.Clamp(visible.End, graph.Origin, graph.End);
        var camera = MinimapRect(Bounds.Between(start, end));
        drawing.FillRectangle(camera, Alpha(Accent, .12f));
        drawing.DrawRectangle(camera, Mix(Accent, CardText, .4f), 1.5f);

        // Minimap coordinates remain independent of the main camera transform.
        Rect MinimapRect(Bounds source) => new Bounds(origin + (source.Origin - graph.Origin) * scale, source.Size * scale).Rect;
    }

    /// <summary>Fits the graph uniformly within the minimap's inset and letterboxes its shorter axis.</summary>
    private static (Vector2 Origin, float Scale) MinimapProjection(Bounds minimap, Bounds graph)
    {
        var content = minimap.Size - new Vector2(16);
        var scale = Math.Min(content.X / graph.Size.X, content.Y / graph.Size.Y);
        return (minimap.Center - graph.Size * scale / 2, scale);
    }

    /// <summary>Begins a pan, minimap jump, selection rectangle or card move and resize.</summary>
    private void PointerDown(object sender, PointerRoutedEventArgs args)
    {
        var input = args.GetCurrentPoint(_surface);
        if (_map == null || IsArranging || _pointer.HasValue || input.Position.Y < ToolbarInset ||
            (!input.Properties.IsLeftButtonPressed && !input.Properties.IsMiddleButtonPressed))
        {
            return;
        }
        CancelCamera();
        Focus(FocusState.Pointer);
        _pointerStart = _pointerCurrent = PointOf(input.Position);
        var world = World(_pointerStart);
        var control = args.KeyModifiers.HasFlag(VirtualKeyModifiers.Control);
        var shift = args.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift);
        var left = input.Properties.IsLeftButtonPressed;
        var picking = _exploration?.SelectionEnabled == true;
        if (left && Minimap is { } minimap && minimap.Contains(_pointerStart))
        {
            _lastClick = null;
            if (BeginGesture(args, Gesture.Minimap, _translation))
            {
                SetKeyboardFocus(FocusTarget.Minimap);
                JumpMinimap(minimap);
            }
        }
        else if (left && _labels.FirstOrDefault(item => Bounds.From(item.Box).Contains(_pointerStart)) is { Path.Length: > 0 } label)
        {
            _gesturePath = label.Path;
            BeginGesture(args, control || shift || picking ? Gesture.Pick : Gesture.CenterLabel, Vector2.Zero);
        }
        else if (left && FolderLabelAt(_pointerStart) is { } folder)
        {
            _gesturePath = folder;
            BeginGesture(args, Gesture.Folder, Vector2.Zero);
        }
        else if (left && HitCard(world) is { } card)
        {
            var displayed = Display(card);
            _gesturePath = card.Document.Path;
            var resize = IsResizeGrip(card, displayed, world);
            if (!resize && (control || shift || picking))
            {
                BeginGesture(args, Gesture.Pick, Vector2.Zero);
            }
            else
            {
                _dragged = card;
                _gestureCardScale = displayed.Size.X / card.Logical.Size.X;
                if (BeginGesture(args, resize ? Gesture.Resize : Gesture.Move, resize ? card.Logical.Size : card.Logical.Origin))
                {
                    if (resize)
                    {
                        _lastClick = null;
                    }
                    SetKeyboardFocus(resize ? FocusTarget.Resize : FocusTarget.Card, card.Document.Path);
                }
            }
        }
        else if (left && _islands.Values.FirstOrDefault(island => island.Header.Contains(world)) is { } island)
        {
            _gesturePath = island.Path;
            BeginGesture(args, Gesture.Folder, Vector2.Zero);
        }
        else if (left && !shift && !picking && HitEdge(world) is { } edge)
        {
            _gestureEdge = edge;
            BeginGesture(args, Gesture.Link, Vector2.Zero);
        }
        else
        {
            _lastClick = null;
            _appendSelection = control;
            var gesture = left && (shift || picking) ? Gesture.Select : Gesture.Pan;
            if (BeginGesture(args, gesture, _translation) && gesture == Gesture.Select)
            {
                _rectangleCards = VisibleCards().Select(candidate => (candidate.Document.Path, ScreenBounds(Display(candidate)))).ToArray();
                UpdateRectangleCandidates();
            }
        }
        args.Handled = true;
    }

    /// <summary>Captures only the initiating pointer and retains the start geometry for rollback.</summary>
    private bool BeginGesture(PointerRoutedEventArgs args, Gesture gesture, Vector2 origin)
    {
        if (!_surface.CapturePointer(args.Pointer))
        {
            ResetGesture();
            return false;
        }
        _pointer = args.Pointer.PointerId;
        _gesture = gesture;
        _gestureOrigin = origin;
        _gestureMoved = false;
        SetPointerTip(default);
        SetHover(null, null);
        return true;
    }

    /// <summary>Updates one transient gesture or the relationship hover without persisting any geometry.</summary>
    private void PointerMove(object sender, PointerRoutedEventArgs args)
    {
        if (_pointer == null)
        {
            _pointerCurrent = PointOf(args.GetCurrentPoint(_surface).Position);
            if (args.Pointer.PointerDeviceType != PointerDeviceType.Touch && _wheelGestureAt == 0)
            {
                UpdatePointerHover(_pointerCurrent);
            }
            else if (_wheelGestureAt != 0)
            {
                _wheelPointer = _pointerCurrent;
            }
            return;
        }
        if (_pointer != args.Pointer.PointerId)
        {
            return;
        }
        _pointerCurrent = PointOf(args.GetCurrentPoint(_surface).Position);
        var delta = _pointerCurrent - _pointerStart;
        var threshold = _gesture == Gesture.Move ? 5 : _gesture == Gesture.Select ? 6 : 3;
        _gestureMoved |= delta.LengthSquared() >= threshold * threshold;
        switch (_gesture)
        {
            case Gesture.Move when _dragged != null && _gestureMoved:
                var position = Vector2.Clamp(_gestureOrigin + delta / _scale, -CoordinateLimit, CoordinateLimit);
                _dragged.Logical = _dragged.Logical with { Origin = RoundGeometry(position) };
                break;
            case Gesture.Resize when _dragged != null && _gestureMoved:
                var size = Vector2.Clamp(_gestureOrigin + delta / (_scale * _gestureCardScale), MinimumCard, MaximumCard);
                _dragged.Logical = _dragged.Logical with { Size = RoundGeometry(size) };
                break;
            case Gesture.Pan:
                _translation = _gestureOrigin + delta;
                break;
            case Gesture.Minimap when Minimap is { } minimap:
                JumpMinimap(minimap);
                break;
            case Gesture.Select:
                UpdateRectangleCandidates();
                break;
        }
        _surface.Invalidate();
        args.Handled = true;
    }

    /// <summary>Commits one completed edit and releases the pointer before invoking application commands.</summary>
    private void PointerUp(object sender, PointerRoutedEventArgs args)
    {
        if (_pointer != args.Pointer.PointerId)
        {
            return;
        }
        PointerMove(sender, args);
        var card = _dragged;
        var gesture = _gesture;
        var moved = _gestureMoved;
        var targetPath = _gesturePath;
        var targetEdge = _gestureEdge;
        var rectangle = gesture == Gesture.Select ? _rectangleCards.Where(candidate => IsRectangleCandidate(candidate.Path)).Select(candidate => candidate.Path).ToArray() : [];
        var append = _appendSelection;
        ResetGesture();
        _surface.ReleasePointerCapture(args.Pointer);
        if (gesture == Gesture.Move && card != null)
        {
            if (moved)
            {
                _lastClick = null;
                if (card.Logical.Origin != _gestureOrigin)
                {
                    Moved?.Invoke(card.Document.Path, card.Logical.Origin.X, card.Logical.Origin.Y);
                }
            }
            else
            {
                RegisterClick(card.Document.Path);
            }
        }
        else if (gesture == Gesture.Resize && card != null)
        {
            _lastClick = null;
            if (moved && card.Logical.Size != _gestureOrigin)
            {
                Resized?.Invoke(card.Document.Path, card.Logical.Size.X, card.Logical.Size.Y);
            }
        }
        else if (gesture == Gesture.Select)
        {
            RectanglePicked?.Invoke(rectangle, append);
        }
        else if (!moved)
        {
            CompleteClickGesture(gesture, targetPath, targetEdge);
        }
        if (args.Pointer.PointerDeviceType != PointerDeviceType.Touch && gesture is Gesture.Pan or Gesture.Minimap)
        {
            UpdatePointerHover(_pointerCurrent);
        }
        FlushDeferredModel();
        _surface.Invalidate();
        args.Handled = true;
    }

    /// <summary>Recognizes a nearby second click on the same document even if its visual bounds changed.</summary>
    private void RegisterClick(string path, bool center = false)
    {
        var stamp = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(_lastClickAt, stamp).TotalMilliseconds;
        var distance = Vector2.Abs(_lastClickPoint - _pointerCurrent);
        // Windows reports physical double-click bounds; scale the composed pointer's DIPs accordingly.
        var pixels = distance * (float)(XamlRoot?.RasterizationScale ?? 1);
        var doubleClick = elapsed <= GetDoubleClickTime() &&
            pixels.X <= GetSystemMetrics(DoubleClickWidthMetric) / 2f && pixels.Y <= GetSystemMetrics(DoubleClickHeightMetric) / 2f;
        var stablePathFallback = elapsed <= 500 && distance.LengthSquared() <= 36;
        if (_lastClick == path && (doubleClick || stablePathFallback))
        {
            _lastClick = null;
            Opened?.Invoke(path);
            return;
        }
        _lastClick = path;
        _lastClickAt = stamp;
        _lastClickPoint = _pointerCurrent;
        Selected?.Invoke(path);
        if (center)
        {
            Center(path);
        }
    }

    /// <summary>Applies a completed click target independently of pointer drag or double-click state.</summary>
    private void CompleteClickGesture(Gesture gesture, string? path, MapEdge? edge)
    {
        if (gesture == Gesture.CenterLabel && path != null)
        {
            SetKeyboardFocus(FocusTarget.Card, path);
            RegisterClick(path, center: true);
            return;
        }
        _lastClick = null;
        if (gesture == Gesture.Pick && path != null)
        {
            SetKeyboardFocus(FocusTarget.Card, path);
            Picked?.Invoke(path);
        }
        else if (gesture == Gesture.Folder && path != null)
        {
            SetKeyboardFocus(FocusTarget.Folder, path);
            FolderToggled?.Invoke(path);
        }
        else if (gesture == Gesture.Link && edge != null)
        {
            PreviewLink?.Invoke(edge);
        }
    }

    /// <summary>Rolls back an interrupted gesture to the last accepted managed geometry.</summary>
    private void PointerLost(object sender, PointerRoutedEventArgs args)
    {
        if (_pointer == args.Pointer.PointerId)
        {
            CancelGesture();
        }
    }

    /// <summary>Discards a pointer preview without affecting the current saved layout.</summary>
    private void CancelGesture()
    {
        if (_dragged != null)
        {
            _dragged.Logical = Bounds.From(_dragged.Layout);
        }
        ResetGesture();
        _lastClick = null;
        _surface.ReleasePointerCaptures();
        FlushDeferredModel();
        _surface.Invalidate();
    }

    /// <summary>Clears only transient capture data before invoking application commands.</summary>
    private void ResetGesture()
    {
        _pointer = null;
        _dragged = null;
        _gesture = Gesture.None;
        _gesturePath = null;
        _gestureEdge = null;
        _rectangleCards = [];
        _rectangleCandidates.Clear();
    }

    /// <summary>Recomputes the temporary lasso highlight from the captured screen-space card bounds.</summary>
    private void UpdateRectangleCandidates()
    {
        var rectangle = Bounds.Between(_pointerStart, _pointerCurrent);
        _rectangleCandidates.Clear();
        foreach (var card in _rectangleCards)
        {
            if (card.Bounds.Overlaps(rectangle))
            {
                _rectangleCandidates.Add(card.Path);
            }
        }
    }

    /// <summary>Informs drawing which cards the current rectangle would include.</summary>
    private bool IsRectangleCandidate(string path) => _rectangleCandidates.Contains(path);

    /// <summary>Applies the last deferred model refresh after a preview releases its card geometry.</summary>
    private void FlushDeferredModel()
    {
        if (_deferredModel is { } model)
        {
            Show(model);
        }
    }

    /// <summary>Zooms around the pointer and preserves the logical point directly below it.</summary>
    private void Wheel(object sender, PointerRoutedEventArgs args)
    {
        var input = args.GetCurrentPoint(_surface);
        if (input.Properties.IsHorizontalMouseWheel)
        {
            return;
        }
        if (_pointer.HasValue)
        {
            args.Handled = true;
            return;
        }
        if (_map == null || IsArranging || input.Position.Y < ToolbarInset || input.Properties.MouseWheelDelta == 0)
        {
            return;
        }
        CancelCamera();
        _lastClick = null;
        var anchor = PointOf(input.Position);
        var delta = Math.Clamp(input.Properties.MouseWheelDelta, -240, 240);
        var scale = Math.Clamp(_scale * MathF.Exp(delta * .0018f), .001f, 1.8f);
        _translation = anchor - (anchor - _translation) * (scale / _scale);
        _scale = scale;
        _wheelGestureAt = Stopwatch.GetTimestamp();
        _wheelPointer = anchor;
        SetPointerTip(default);
        SetHover(null, null);
        _effectTimer.Start();
        _surface.Invalidate();
        args.Handled = true;
    }

    /// <summary>Requests a native context menu at the card under a secondary pointer.</summary>
    private void RightTappedOnSurface(object sender, RightTappedRoutedEventArgs args)
    {
        var point = args.GetPosition(this);
        if (IsArranging || point.Y < ToolbarInset)
        {
            return;
        }
        var position = PointOf(point);
        var path = _labels.FirstOrDefault(item => Bounds.From(item.Box).Contains(position)).Path ?? HitCard(World(position))?.Document.Path;
        if (path != null)
        {
            CancelCamera();
            CancelGesture();
            ClearPointerHover();
            SetKeyboardFocus(FocusTarget.Card, path);
            ContextMenu?.Invoke(path, point);
            args.Handled = true;
        }
    }

    /// <summary>Supports keyboard selection, opening, context menus, panning, card movement and size changes.</summary>
    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (_map == null || (XamlRoot is { } root && ReferenceEquals(FocusManager.GetFocusedElement(root), _readHere)))
        {
            return;
        }
        if (args.Key == VirtualKey.Escape)
        {
            if (_pointer == null && _cameraTransition == null && _pendingCenter == null)
            {
                if (FocusedEdge != null)
                {
                    SetKeyboardFocus(FocusTarget.Viewport);
                }
                return;
            }
            CancelCamera();
            CancelGesture();
        }
        else if (args.Key == VirtualKey.Tab)
        {
            if (_pointer == null && !Pressed(VirtualKey.Control) && !Pressed(VirtualKey.Menu) && !Pressed(VirtualKey.LeftWindows) && !Pressed(VirtualKey.RightWindows))
            {
                args.Handled = MoveTabFocus(Pressed(VirtualKey.Shift) ? -1 : 1);
            }
            return;
        }
        else if (_pointer != null || IsArranging)
        {
            return;
        }
        else if (args.Key is VirtualKey.Home or VirtualKey.Number0 or VirtualKey.NumberPad0)
        {
            _lastClick = null;
            Fit();
        }
        else if (args.Key is VirtualKey.PageUp or VirtualKey.PageDown)
        {
            var target = Pressed(VirtualKey.Shift) ? FocusTarget.Link : Pressed(VirtualKey.Control) ? FocusTarget.Folder : FocusTarget.Card;
            AdvanceFocus(args.Key == VirtualKey.PageUp ? -1 : 1, target);
        }
        else if (args.Key is VirtualKey.Enter or VirtualKey.Space)
        {
            _lastClick = null;
            ActivateKeyboardTarget(args.Key == VirtualKey.Enter);
        }
        else if (_focusTarget is FocusTarget.Viewport or FocusTarget.Card or FocusTarget.Resize &&
            (args.Key == VirtualKey.Application || args.Key == VirtualKey.F10 && Pressed(VirtualKey.Shift)) &&
            CurrentKeyboardCard is { } menuPath && _cards.TryGetValue(menuPath, out var menuCard))
        {
            CancelCamera();
            _lastClick = null;
            var center = Vector2.Clamp(Screen(Display(menuCard).Origin) + new Vector2(20, 32), Vector2.Zero, Viewport);
            ContextMenu?.Invoke(menuPath, new Point(center.X, center.Y));
        }
        else if (args.Key == VirtualKey.R && CurrentKeyboardCard is { } resizePath)
        {
            SetKeyboardFocus(FocusTarget.Resize, resizePath, announce: true);
        }
        else if (args.Key == VirtualKey.M && Minimap != null)
        {
            SetKeyboardFocus(FocusTarget.Minimap, announce: true);
        }
        else if (Arrow(args.Key) is { } direction)
        {
            Nudge(direction);
        }
        else
        {
            return;
        }
        args.Handled = true;
    }

    /// <summary>Records when native focus enters the graph from the next control in Tab order.</summary>
    private void OnGettingFocus(UIElement sender, GettingFocusEventArgs args)
    {
        _reverseTabEntry = ReferenceEquals(args.NewFocusedElement, this) &&
            args.Direction == FocusNavigationDirection.Previous;
    }

    /// <summary>Begins reverse traversal at the last item once the graph has received native focus.</summary>
    private void OnGotFocus(object sender, RoutedEventArgs args)
    {
        var reverse = _reverseTabEntry;
        _reverseTabEntry = false;
        if (reverse && XamlRoot is { } root && ReferenceEquals(FocusManager.GetFocusedElement(root), this))
        {
            if (AutomationTargets(sequential: true).LastOrDefault() is { Kind: not FocusTarget.Viewport } last)
            {
                FocusVirtualTarget(last);
            }
        }
    }

    /// <summary>Traverses painted items in Tab order and leaves focus movement at either end to WinUI.</summary>
    private bool MoveTabFocus(int direction)
    {
        var current = CurrentAutomationTarget();
        if (current.Kind == FocusTarget.Viewport && direction < 0)
        {
            return false;
        }
        var previous = new AutomationTarget(FocusTarget.Viewport);
        var found = current.Kind == FocusTarget.Viewport;
        foreach (var target in AutomationTargets(sequential: true))
        {
            if (direction > 0 && found)
            {
                FocusVirtualTarget(target);
                return true;
            }
            if (target == current)
            {
                if (direction < 0)
                {
                    FocusVirtualTarget(previous);
                    return true;
                }
                found = true;
            }
            previous = target;
        }
        return false;
    }

    /// <summary>Returns the automation identity of the current keyboard stop.</summary>
    private AutomationTarget CurrentAutomationTarget() =>
        new(_focusTarget, _focusTarget == FocusTarget.Link ? null : _keyboardPath, _keyboardEdge);

    /// <summary>Focuses a painted target and pans only when needed to reveal it.</summary>
    private void FocusVirtualTarget(AutomationTarget target)
    {
        SetKeyboardFocus(target.Kind, target.Path ?? target.Edge?.Source, target.Edge, announce: true);
        if (!IsArranging && target.Kind != FocusTarget.Minimap && TooltipAnchor(KeyboardTooltip()) is { } point)
        {
            RevealOnKeyboard(point);
        }
    }

    /// <summary>Moves native keyboard focus between visible cards or their outgoing and incoming connections.</summary>
    private void AdvanceFocus(int direction, FocusTarget target)
    {
        if (target == FocusTarget.Link)
        {
            var path = CurrentKeyboardCard;
            var edges = _edges.Where(edge => (edge.Source == path || edge.Target == path) && EdgeGeometry(edge) != null).ToList();
            if (edges.Count == 0)
            {
                return;
            }
            var index = _keyboardEdge == null ? -1 : edges.IndexOf(_keyboardEdge);
            var edge = edges[NextIndex(index, direction, edges.Count)];
            SetKeyboardFocus(FocusTarget.Link, path, edge, announce: true);
            if (EdgeGeometry(edge) is { } curve)
            {
                RevealOnKeyboard(Screen(curve.PointAt(.5f)));
            }
            return;
        }
        if (target == FocusTarget.Folder)
        {
            var folders = _islands.Keys.Order(StringComparer.Ordinal).ToList();
            if (folders.Count > 0)
            {
                var path = folders[NextIndex(folders.IndexOf(FocusedFolder ?? "\0"), direction, folders.Count)];
                SetKeyboardFocus(FocusTarget.Folder, path, announce: true);
                RevealOnKeyboard(Screen(_islands[path].Header.Center));
            }
            return;
        }
        var paths = VisibleCards().Select(card => card.Document.Path).ToList();
        if (paths.Count > 0)
        {
            var path = paths[NextIndex(paths.IndexOf(CurrentKeyboardCard ?? ""), direction, paths.Count)];
            SetKeyboardFocus(FocusTarget.Card, path, announce: true);
            RevealOnKeyboard(Screen(Display(_cards[path]).Center));
        }
    }

    /// <summary>Invokes the keyboard target, with plain Space selecting and modified Space adding to the picked set.</summary>
    private void ActivateKeyboardTarget(bool open)
    {
        if (FocusedEdge is { } edge)
        {
            if (open) { PreviewLink?.Invoke(edge); }
            return;
        }
        if (FocusedFolder is { } folder)
        {
            FolderToggled?.Invoke(folder);
            return;
        }
        if (_focusTarget is FocusTarget.Minimap or FocusTarget.Resize || CurrentKeyboardCard is not { } path)
        {
            return;
        }
        if (open)
        {
            Opened?.Invoke(path);
        }
        else if (_exploration?.SelectionEnabled == true || Pressed(VirtualKey.Control) || Pressed(VirtualKey.Shift))
        {
            Picked?.Invoke(path);
        }
        else
        {
            Selected?.Invoke(path);
        }
    }

    /// <summary>Wraps focus within the current ordered collection and starts predictably without a prior item.</summary>
    private static int NextIndex(int current, int direction, int count) => current < 0
        ? (direction < 0 ? count - 1 : 0)
        : (current + count + direction) % count;

    /// <summary>Moves or resizes a selection only when its modifier explicitly requests a layout edit.</summary>
    private void Nudge(Vector2 direction)
    {
        CancelCamera();
        _lastClick = null;
        var fast = Pressed(VirtualKey.Shift);
        if (_focusTarget != FocusTarget.Minimap && CurrentKeyboardCard is { } path && _cards.TryGetValue(path, out var card))
        {
            if (_focusTarget != FocusTarget.Resize && Pressed(VirtualKey.Menu))
            {
                var point = RoundGeometry(Vector2.Clamp(card.Logical.Origin + direction * (fast ? 100 : 20), -CoordinateLimit, CoordinateLimit));
                Moved?.Invoke(path, point.X, point.Y);
                return;
            }
            if (_focusTarget == FocusTarget.Resize || Pressed(VirtualKey.Control))
            {
                var size = RoundGeometry(Vector2.Clamp(card.Logical.Size + direction * (fast ? 40 : 10), MinimumCard, MaximumCard));
                Resized?.Invoke(path, size.X, size.Y);
                return;
            }
        }
        _translation -= direction * (fast ? 100 : 50);
        _surface.Invalidate();
    }

    /// <summary>Publishes tooltips for stable hovered edges and guides foreground emphasis.</summary>
    private void SetHover(string? path, MapEdge? edge)
    {
        if (_hovered == path && _hoveredEdge == edge)
        {
            return;
        }
        BeginCardHoverTransition(path);
        _hovered = path;
        _hoveredEdge = edge;
        _surface.Invalidate();
    }

    private string? FocusedCard => _focusTarget is FocusTarget.Card or FocusTarget.Resize ? _keyboardPath : null;
    private string? FocusedFolder => _focusTarget == FocusTarget.Folder ? _keyboardPath : null;
    private MapEdge? FocusedEdge => _focusTarget == FocusTarget.Link ? _keyboardEdge : null;
    private bool IsDirectCameraGesture => _gesture is Gesture.Pan or Gesture.Minimap || _wheelGestureAt != 0;

    private string? CurrentKeyboardCard =>
        (_focusTarget == FocusTarget.Link ? _keyboardPath : FocusedCard) ?? (_cards.ContainsKey(_selected) ? _selected : null);

    /// <summary>Tracks keyboard focus independently of a pointer hover and announces the current virtual target.</summary>
    private void SetKeyboardFocus(FocusTarget target, string? path = null, MapEdge? edge = null, bool announce = false)
    {
        if (_focusTarget == target && _keyboardPath == path && _keyboardEdge == edge && _keyboardTip == announce)
        {
            return;
        }
        _focusTarget = target;
        _keyboardPath = path;
        _keyboardEdge = edge;
        _keyboardTip = announce;
        if (announce)
        {
            _lastClick = null;
            SetPointerTip(default);
            SetHover(null, null);
            var details = target switch
            {
                FocusTarget.Link when edge != null => DescribeLink(edge),
                FocusTarget.Minimap => T("Minimap. Arrow keys pan. Home fits the graph."),
                FocusTarget.Folder when path != null => _islands[path].Caption,
                FocusTarget.Resize when path != null => T("Resize {0}", _cards[path].Document.Title),
                _ when path != null && _cards.TryGetValue(path, out var card) => card.Document.Title + " · " + path,
                _ => T("Document map")
            };
            var peer = FrameworkElementAutomationPeer.FromElement(this) ?? FrameworkElementAutomationPeer.CreatePeerForElement(this);
            peer?.RaiseNotificationEvent(AutomationNotificationKind.Other, AutomationNotificationProcessing.MostRecent, details, "DocumentMapFocus");
        }
        _surface.Invalidate();
    }

    /// <summary>Drops keyboard and pointer targets that are no longer present in the accepted projection.</summary>
    private void ValidateKeyboardFocus()
    {
        _describedTip = default;
        if (FocusedCard is { } path && (!_cards.TryGetValue(path, out var card) || IsHidden(card)) ||
            FocusedFolder is { } folder && !_islands.ContainsKey(folder) ||
            FocusedEdge is { } link && (!_edges.Contains(link) || EdgeGeometry(link) == null))
        {
            SetKeyboardFocus(FocusTarget.Viewport);
        }
        if (_hovered is { } hovered && (!_cards.TryGetValue(hovered, out var target) || IsHidden(target)) ||
            _hoveredEdge is { } hoveredEdge && (!_edges.Contains(hoveredEdge) || EdgeGeometry(hoveredEdge) == null))
        {
            SetHover(null, null);
        }
        if (AutomationPeer.ListenerExists(AutomationEvents.StructureChanged))
        {
            FrameworkElementAutomationPeer.FromElement(this)?.RaiseAutomationEvent(AutomationEvents.StructureChanged);
        }
    }

    /// <summary>Pans only when necessary to reveal a keyboard-focused target.</summary>
    private void RevealOnKeyboard(Vector2 point)
    {
        var viewport = Viewport;
        if (viewport.X > 60 && viewport.Y - ToolbarInset > 60 &&
            (point.X < 30 || point.Y < ToolbarInset + 30 || point.X > viewport.X - 30 || point.Y > viewport.Y - 30))
        {
            CancelCamera();
            _translation += viewport / 2 + new Vector2(0, ToolbarInset / 2) - point;
            _surface.Invalidate();
        }
    }

    /// <summary>Updates card and connection hover only for the most recent valid pointer position.</summary>
    private void UpdatePointerHover(Vector2 point)
    {
        if (point.X < 0 || point.Y < ToolbarInset || point.X > Viewport.X || point.Y > Viewport.Y)
        {
            SetPointerTip(default);
            SetHover(null, null);
            return;
        }
        _pointerCurrent = point;
        if (Minimap is { } minimap && minimap.Contains(point))
        {
            SetHover(null, null);
            SetPointerTip(new(TooltipKind.Minimap));
            return;
        }
        var label = _labels.FirstOrDefault(item => Bounds.From(item.Box).Contains(point)).Path;
        if (label != null)
        {
            SetHover(label, null);
            SetPointerTip(new(TooltipKind.DocumentLabel, label));
            return;
        }
        if (FolderLabelAt(point) is { } folder)
        {
            SetHover(null, null);
            SetPointerTip(new(TooltipKind.FolderLabel, folder));
            return;
        }
        var world = World(point);
        var card = HitCard(world);
        if (card != null)
        {
            var path = card.Document.Path;
            SetHover(path, null);
            var shape = Display(card);
            SetPointerTip(IsResizeGrip(card, shape, world) ? new(TooltipKind.Resize, path) : StatisticAt(card, shape, world));
        }
        else if (_islands.Values.FirstOrDefault(island => island.Header.Contains(world)) is { } island)
        {
            SetHover(null, null);
            SetPointerTip(new(TooltipKind.Folder, island.Path));
        }
        else
        {
            var edge = HitEdge(world);
            SetHover(null, edge);
            SetPointerTip(edge == null ? default : new(TooltipKind.Link, Edge: edge));
        }
    }

    /// <summary>Clears pointer hover without discarding a keyboard-focused edge.</summary>
    private void ClearPointerHover()
    {
        _wheelGestureAt = 0;
        SetPointerTip(default);
        SetHover(null, null);
    }

    /// <summary>Changes the hovered description and starts a finite delay for small targets or gesture guidance.</summary>
    private void SetPointerTip(TooltipHit hit)
    {
        if (_pointerTip == hit)
        {
            if (_pointerTipReady)
            {
                _surface.Invalidate();
            }
            return;
        }
        _pointerTipTimer.Stop();
        _pointerTip = hit;
        _pointerTipReady = hit.Kind == TooltipKind.Link;
        if (hit.Kind != TooltipKind.None && !_pointerTipReady)
        {
            _pointerTipTimer.Start();
        }
        _surface.Invalidate();
    }

    /// <summary>Reveals a still-relevant pointer tooltip without requiring further mouse movement.</summary>
    private void PointerTipReady(DispatcherQueueTimer timer, object args)
    {
        _pointerTipReady = _pointerTip.Kind != TooltipKind.None;
        _surface.Invalidate();
    }

    /// <summary>Checks the visible resize grip in screen-scaled map coordinates.</summary>
    private bool IsResizeGrip(Card card, Bounds shape, Vector2 world) =>
        _scale >= .4f && (card.Document.Path == _selected || card.Document.Path == _hovered || card.Document.Path == FocusedCard) &&
        Vector2.Distance(shape.End, world) <= 19 / _scale;

    /// <summary>Finds the clickable folder overview label at a screen coordinate, including the root folder.</summary>
    private string? FolderLabelAt(Vector2 point)
    {
        foreach (var label in _folderLabels)
        {
            if (Bounds.From(label.Box).Contains(point))
            {
                return label.Path;
            }
        }
        return null;
    }

    /// <summary>Keeps card magnification constant while a move or resize is calculated in logical coordinates.</summary>
    private float? CapturedCardScale(Card card) => _dragged == card && _gesture is Gesture.Move or Gesture.Resize ? _gestureCardScale : null;

    /// <summary>Rounds accepted geometry to the legacy map's one-decimal editing steps.</summary>
    private static Vector2 RoundGeometry(Vector2 value) => new(MathF.Round(value.X * 10) / 10, MathF.Round(value.Y * 10) / 10);

    /// <summary>Maps a world-space box to the axis-aligned screen coordinates used by pointer and accessibility targets.</summary>
    private Bounds ScreenBounds(Bounds bounds) => new(Screen(bounds.Origin), bounds.Size * _scale);

    /// <summary>Opens the same selected-card preview exposed by the inspector and the card menu.</summary>
    private void OpenSelectedPreview()
    {
        _lastClick = null;
        CancelCamera();
        if (_cards.TryGetValue(_selected, out var card) && !IsHidden(card))
        {
            ReadHere?.Invoke(_selected);
        }
    }

    /// <summary>Returns keyboard focus to the selected card's reading trigger or to the graph.</summary>
    public void FocusAfterPreview()
    {
        UpdateReadHere(Viewport);
        if (_readHere.Visibility != Visibility.Visible || !_readHere.Focus(FocusState.Programmatic))
        {
            Focus(FocusState.Programmatic);
        }
    }

    /// <summary>Aligns a native, keyboard-accessible Read here button to the selected card after each camera change.</summary>
    private void UpdateReadHere(Vector2 viewport)
    {
        if (_gesture != Gesture.None || IsArranging || !_cards.TryGetValue(_selected, out var card) || IsHidden(card) || viewport.X < 130 || viewport.Y - ToolbarInset < 90)
        {
            _readHere.Visibility = Visibility.Collapsed;
            return;
        }
        var shape = Display(card);
        var start = Screen(shape.Origin);
        var end = Screen(shape.End);
        if (end.X < 0 || end.Y < ToolbarInset || start.X > viewport.X || start.Y > viewport.Y)
        {
            _readHere.Visibility = Visibility.Collapsed;
            return;
        }
        var width = Math.Max(_readHere.ActualWidth, 92);
        var height = Math.Max(_readHere.ActualHeight, 32);
        var right = Math.Clamp(end.X - width - 5, 4, Math.Max(4, viewport.X - width - 4));
        var topEdge = ToolbarInset + 4;
        var bottom = Math.Clamp(end.Y + 4, topEdge, Math.Max(topEdge, viewport.Y - height - 4));
        _readHerePosition.X = right;
        _readHerePosition.Y = bottom;
        var caption = T("Read here");
        if (!Equals(_readHere.Content, caption))
        {
            _readHere.Content = caption;
        }
        _readHere.Visibility = Visibility.Visible;
    }

    /// <summary>Anchors the native description to the same target pointer or keyboard users interact with.</summary>
    private void UpdateLinkDetails(Vector2 viewport)
    {
        var positionedByPointer = _pointerTipReady;
        var hit = positionedByPointer ? _pointerTip : _keyboardTip ? KeyboardTooltip() : default;
        if (hit.Kind == TooltipKind.None || _gesture != Gesture.None || _wheelGestureAt != 0 || IsArranging || viewport.X <= 90 || viewport.Y - ToolbarInset <= 65)
        {
            _linkTip.Visibility = Visibility.Collapsed;
            return;
        }
        var anchor = positionedByPointer ? _pointerCurrent : TooltipAnchor(hit);
        if (anchor is not { } point || point.Y < ToolbarInset || point.Y > viewport.Y)
        {
            _linkTip.Visibility = Visibility.Collapsed;
            return;
        }
        if (_describedTip != hit)
        {
            _describedTip = hit;
            _linkDetails.Text = DescribeTooltip(hit);
        }
        if (_linkDetails.Text.Length == 0)
        {
            _linkTip.Visibility = Visibility.Collapsed;
            return;
        }
        _linkTip.MaxWidth = Math.Min(320, viewport.X - 16);
        var width = Math.Min(_linkTip.MaxWidth, Math.Max(_linkTip.ActualWidth, 240));
        var height = Math.Max(_linkTip.ActualHeight, 34);
        var top = ToolbarInset + 6;
        _linkTipPosition.X = Math.Clamp(point.X + 12, 6, Math.Max(6, viewport.X - width - 6));
        var y = point.Y + height + 18 <= viewport.Y ? point.Y + 12 : point.Y - height - 12;
        _linkTipPosition.Y = Math.Clamp(y, top, Math.Max(top, viewport.Y - height - 6));
        _linkTip.Visibility = Visibility.Visible;
    }

    /// <summary>Returns the applicable native description for the current keyboard target.</summary>
    private TooltipHit KeyboardTooltip() => _focusTarget switch
    {
        FocusTarget.Card => new(TooltipKind.Card, _keyboardPath),
        FocusTarget.Resize => new(TooltipKind.Resize, _keyboardPath),
        FocusTarget.Folder => new(TooltipKind.Folder, _keyboardPath),
        FocusTarget.Link => new(TooltipKind.Link, Edge: _keyboardEdge),
        FocusTarget.Minimap => new(TooltipKind.Minimap),
        _ => default
    };

    /// <summary>Anchors a keyboard description to its painted target instead of using the mouse location.</summary>
    private Vector2? TooltipAnchor(TooltipHit hit)
    {
        if (hit is { Kind: TooltipKind.Link, Edge: { } edge } && EdgeGeometry(edge) is { } curve)
        {
            return Screen(curve.PointAt(.5f));
        }
        if (hit.Kind == TooltipKind.Minimap)
        {
            return Minimap?.Origin;
        }
        if (hit.Path is not { } path)
        {
            return null;
        }
        if (hit.Kind is TooltipKind.Folder or TooltipKind.FolderLabel)
        {
            return _islands.TryGetValue(path, out var island) ? Screen(island.Header.Center) : null;
        }
        if (_cards.TryGetValue(path, out var card) && !IsHidden(card))
        {
            var shape = Display(card);
            return Screen(hit.Kind == TooltipKind.Resize ? shape.End : shape.Origin + new Vector2(12, 24));
        }
        return null;
    }

    /// <summary>Localizes descriptions for graph affordances while preserving literal document and folder names.</summary>
    private string DescribeTooltip(TooltipHit hit)
    {
        if (hit is { Kind: TooltipKind.Link, Edge: { } edge })
        {
            return DescribeLink(edge);
        }
        if (hit.Kind == TooltipKind.Minimap)
        {
            return T("Map overview. Click to navigate; use arrow keys to pan and Home to fit.");
        }
        if (hit.Path is not { } path)
        {
            return "";
        }
        if (hit.Kind is TooltipKind.Folder or TooltipKind.FolderLabel)
        {
            if (!_islands.TryGetValue(path, out var island))
            {
                return "";
            }
            var name = path.Length == 0 ? T("Workspace root") : path;
            var action = T(island.Collapsed ? "Expand folder {0}" : "Collapse folder {0}", name);
            return hit.Kind == TooltipKind.FolderLabel ? name + "\n" + action : action;
        }
        if (!_cards.TryGetValue(path, out var card))
        {
            return "";
        }
        return hit.Kind switch
        {
            TooltipKind.Resize => T("Resize {0}", card.Document.Title) + "\n" + T("Drag to resize · Arrow keys to resize · Shift for larger steps · Escape to cancel"),
            TooltipKind.DocumentLabel => card.Document.Title + "\n" + path,
            TooltipKind.Statistics => card.Document.Statistics is { } stats ? StatisticsRows(stats)[hit.Row][hit.Column].Tooltip : T("Metadata unavailable"),
            _ => card.Document.Title + "\n" + path + "\n" + T("Click to select · Double-click to open · Drag to move · Right-click to customize · Space to select · Enter to open · Alt+arrows to move")
        };
    }

    /// <summary>Hits each statistics item by the text extents used in the Win2D card.</summary>
    private TooltipHit StatisticAt(Card card, Bounds shape, Vector2 point)
    {
        var path = card.Document.Path;
        if (!_advanced || _scale < .72f || (_selected != path && _hovered != path && FocusedCard != path))
        {
            return new(TooltipKind.Card, path);
        }
        var rowIndex = Bounds.From(StatisticsRowBounds(shape, 0)).Contains(point) ? 0 :
            Bounds.From(StatisticsRowBounds(shape, 1)).Contains(point) ? 1 : -1;
        if (rowIndex < 0)
        {
            return new(TooltipKind.Card, path);
        }
        if (card.Document.Statistics is not { } statistics)
        {
            return rowIndex == 0 ? new(TooltipKind.Statistics, path) : new(TooltipKind.Card, path);
        }
        var relative = point.X - (float)StatisticsRowBounds(shape, rowIndex).X;
        var edges = StatisticEdges(statistics)[rowIndex];
        var column = Array.FindIndex(edges, edge => relative <= edge);
        return column < 0 ? new(TooltipKind.Card, path) : new(TooltipKind.Statistics, path, Row: rowIndex, Column: column);
    }

    /// <summary>Measures cumulative text widths once for the currently inspected statistics.</summary>
    private float[][] StatisticEdges(DocumentStatistics statistics)
    {
        if (_measuredStatistics == statistics && _statisticEdges.Length != 0)
        {
            return _statisticEdges;
        }
        _statisticEdges = StatisticsRows(statistics).Select(row => row.Select((_, index) => Measure(index, row)).ToArray()).ToArray();
        _measuredStatistics = statistics;
        return _statisticEdges;

        /// <summary>Uses the renderer's own format to account for symbol and font fallback widths.</summary>
        float Measure(int index, StatisticPart[] row)
        {
            var prefix = string.Join(StatisticSeparator, row.Take(index + 1).Select(part => part.VisibleText));
            try
            {
                using var layout = new CanvasTextLayout(_surface, prefix, _statsFormat, 1600, 40);
                return (float)(layout.LayoutBounds.X + layout.LayoutBounds.Width);
            }
            catch (Exception error) when (error is COMException or InvalidOperationException)
            {
                // Pointer input can race Win2D device recovery. The visible control still has a monospace fallback.
                return prefix.Length * _statsFormat.FontSize * .6f;
            }
        }
    }

    /// <summary>Computes the shared Win2D bounds for a statistics row, in the card's logical space.</summary>
    private static Rect StatisticsRowBounds(Bounds shape, int rowIndex) =>
        new(shape.Origin.X + 12, shape.End.Y - 44 + 16 * rowIndex, shape.Size.X - 30, 15);

    /// <summary>Formats the same visible values and full descriptions for painting, pointer hit-testing and automation.</summary>
    private StatisticPart[][] StatisticsRows(DocumentStatistics statistics)
    {
        if (_formattedStatistics.TryGetValue(statistics, out var formatted))
        {
            return formatted;
        }
        if (_formattedStatistics.Count >= 16)
        {
            _formattedStatistics.Clear();
        }
        var size = statistics.SourceBytes < 1024 ? $"{statistics.SourceBytes:N0} B" : $"{statistics.SourceBytes / 1024d:0.#} KiB";
        return _formattedStatistics[statistics] =
        [
            [new(size, "UTF-8 size", $"{statistics.SourceBytes:N0} B"),
                new($"≈{statistics.EstimatedTokens:N0}", "Est. tokens", $"{statistics.EstimatedTokens:N0}", Estimated: true)],
            [new($"# {statistics.Characters:N0}", "Characters", $"{statistics.Characters:N0}"),
                new($"Aa {statistics.Words:N0}", "Words", $"{statistics.Words:N0}"),
                new($"≡ {statistics.Lines:N0}", "Lines", $"{statistics.Lines:N0}"),
                new($"↗ {statistics.OutgoingLinks:N0}", "Links", $"{statistics.OutgoingLinks:N0}")]
        ];
    }

    /// <summary>Formats endpoint titles and the existing bounded source excerpt for speech and visual focus.</summary>
    private string DescribeLink(MapEdge edge)
    {
        var source = _cards.GetValueOrDefault(edge.Source)?.Document.Title ?? edge.Source;
        var target = _cards.GetValueOrDefault(edge.Target)?.Document.Title ?? edge.Target;
        var line = edge.Line > 0 ? "\n" + T("Source line {0}", edge.Line) : "";
        return source + " → " + target + line + "\n" + (edge.Excerpt.Length > 0 ? edge.Excerpt : T("Source excerpt unavailable"));
    }

    /// <summary>Creates the native automation peer that announces graph keyboard targets.</summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new MapAutomationPeer(this);

    /// <summary>Enumerates the bounded painted graph as accessible items without creating a XAML element per edge.</summary>
    private IEnumerable<AutomationTarget> AutomationTargets(bool sequential = false)
    {
        if (_map == null || _disposed || Visibility != Visibility.Visible)
        {
            yield break;
        }
        foreach (var folder in _islands.Keys)
        {
            yield return new(FocusTarget.Folder, folder);
        }
        foreach (var edge in _edges)
        {
            if ((!sequential || edge.Source == _selected || edge.Target == _selected) && EdgeGeometry(edge) != null)
            {
                yield return new(FocusTarget.Link, Edge: edge);
            }
        }
        foreach (var card in VisibleCards())
        {
            yield return new(FocusTarget.Card, card.Document.Path);
            if (!sequential || _scale >= .4f)
            {
                yield return new(FocusTarget.Resize, card.Document.Path);
            }
        }
        if (Minimap != null)
        {
            yield return new(FocusTarget.Minimap);
        }
    }

    /// <summary>Resolves one virtual automation target against the current accepted model and computes its screen bounds.</summary>
    private Bounds? AutomationTargetBounds(AutomationTarget target)
    {
        if (_disposed || _map == null)
        {
            return null;
        }
        if (target is { Kind: FocusTarget.Link, Edge: { } edge } && _edges.Contains(edge) && EdgeGeometry(edge) is { } curve)
        {
            return ScreenBounds(curve.Hull).Expand(4);
        }
        if (target.Kind == FocusTarget.Minimap)
        {
            return Minimap;
        }
        if (target.Kind == FocusTarget.Folder && target.Path is { } path && _islands.TryGetValue(path, out var island))
        {
            return ScreenBounds(island.Header);
        }
        if (target.Path is not { } cardPath || !_cards.TryGetValue(cardPath, out var card) || IsHidden(card))
        {
            return null;
        }
        var cardBounds = ScreenBounds(Display(card));
        return target.Kind == FocusTarget.Resize ? new Bounds(cardBounds.End - new Vector2(18), new Vector2(18)) : cardBounds;
    }

    /// <summary>Returns an accessible name appropriate to a card, connection, folder or map control.</summary>
    private string AutomationTargetName(AutomationTarget target)
    {
        if (target is { Kind: FocusTarget.Link, Edge: { } edge })
        {
            return T("Connection from {0} to {1}. Source line {2}. {3}", edge.Source, edge.Target, edge.Line, edge.Excerpt);
        }
        if (target.Kind == FocusTarget.Minimap)
        {
            return T("Map overview");
        }
        if (target.Kind == FocusTarget.Folder)
        {
            return DescribeTooltip(new(TooltipKind.Folder, target.Path));
        }
        if (target.Path is { } path && _cards.TryGetValue(path, out var card))
        {
            return target.Kind == FocusTarget.Resize ? T("Resize {0}", card.Document.Title) : T("Select {0}, {1}", card.Document.Title, path);
        }
        return "";
    }

    /// <summary>Exposes the same descriptions to assistive software that are visible in the map tooltip.</summary>
    private string AutomationTargetHelp(AutomationTarget target)
    {
        var kind = target.Kind switch
        {
            FocusTarget.Card => TooltipKind.Card,
            FocusTarget.Resize => TooltipKind.Resize,
            FocusTarget.Link => TooltipKind.Link,
            FocusTarget.Folder => TooltipKind.Folder,
            FocusTarget.Minimap => TooltipKind.Minimap,
            _ => TooltipKind.None
        };
        var description = DescribeTooltip(new(kind, target.Path, target.Edge));
        if (_advanced && target.Kind == FocusTarget.Card && target.Path is { } path && _cards.TryGetValue(path, out var card))
        {
            var statistics = card.Document.Statistics is { } stats
                ? string.Join("\n", StatisticsRows(stats).SelectMany(row => row).Select(part => part.Tooltip))
                : T("Metadata unavailable");
            return description + "\n" + statistics;
        }
        return description;
    }

    /// <summary>Moves a native keyboard focus request to the referenced graph item or invokes its primary action.</summary>
    private void RunAutomationTarget(AutomationTarget target, bool invoke)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_pointer != null || IsArranging || AutomationTargetBounds(target) == null)
            {
                return;
            }
            if (!invoke)
            {
                Focus(FocusState.Programmatic);
                FocusVirtualTarget(target);
                return;
            }
            switch (target)
            {
                case { Kind: FocusTarget.Card, Path: { } card }:
                    if (_exploration?.SelectionEnabled == true) { Picked?.Invoke(card); }
                    else { Selected?.Invoke(card); }
                    break;
                case { Kind: FocusTarget.Link, Edge: { } edge }:
                    PreviewLink?.Invoke(edge);
                    break;
                case { Kind: FocusTarget.Folder, Path: { } folder }:
                    FolderToggled?.Invoke(folder);
                    break;
            }
        });
    }

    /// <summary>Returns the topmost visible card for a logical pointer coordinate.</summary>
    private Card? HitCard(Vector2 world)
    {
        Card? result = null;
        var topLayer = int.MinValue;
        foreach (var card in VisibleCards())
        {
            var layer = CardLayer(card);
            if (layer >= topLayer && Display(card).Contains(world))
            {
                result = card;
                topLayer = layer;
            }
        }
        return result;
    }

    /// <summary>Finds the nearest visible link within a constant screen-space tolerance.</summary>
    private MapEdge? HitEdge(Vector2 world)
    {
        var tolerance = 7 / _scale;
        var bestDistance = tolerance * tolerance;
        MapEdge? result = null;
        foreach (var edge in _edges)
        {
            if (EdgeGeometry(edge) is not { } curve)
            {
                continue;
            }
            var distance = curve.DistanceSquaredTo(world, bestDistance, tolerance / 6);
            if (distance <= bestDistance)
            {
                bestDistance = distance;
                result = edge;
            }
        }
        return result;
    }

    /// <summary>Connects current card or island sides with a directional cubic and hides links within collapsed islands.</summary>
    private ConnectionCurve? EdgeGeometry(MapEdge edge)
    {
        if (!_cards.TryGetValue(edge.Source, out var source) || !_cards.TryGetValue(edge.Target, out var target) ||
            source.Folder == target.Folder && IsHidden(source))
        {
            return null;
        }
        var a = Endpoint(source);
        var b = Endpoint(target);
        if (source == target)
        {
            return new(new Vector2(a.End.X, a.Center.Y), new Vector2(a.End.X + 45, a.Origin.Y - 85),
                new Vector2(a.Origin.X - 45, a.Origin.Y - 85), new Vector2(a.Origin.X, a.Center.Y));
        }
        var delta = b.Center - a.Center;
        var span = (a.Size + b.Size) / 2;
        if (Math.Abs(delta.X) / span.X >= Math.Abs(delta.Y) / span.Y)
        {
            var right = delta.X >= 0;
            var start = new Vector2(right ? a.End.X : a.Origin.X, a.Center.Y);
            var end = new Vector2(right ? b.Origin.X : b.End.X, b.Center.Y);
            var bend = Math.Max(45, Math.Abs(end.X - start.X) * .45f);
            var tangency = new Vector2(right ? bend : -bend, 0);
            return new(start, start + tangency, end - tangency, end);
        }

        var down = delta.Y >= 0;
        var top = new Vector2(a.Center.X, down ? a.End.Y : a.Origin.Y);
        var bottom = new Vector2(b.Center.X, down ? b.Origin.Y : b.End.Y);
        var verticalBend = Math.Max(45, Math.Abs(bottom.Y - top.Y) * .45f);
        var verticalTangency = new Vector2(0, down ? verticalBend : -verticalBend);
        return new(top, top + verticalTangency, bottom - verticalTangency, bottom);
    }

    /// <summary>Uses the collapsed island boundary when a link endpoint's card is hidden.</summary>
    private Bounds Endpoint(Card card) => _islands.TryGetValue(card.Folder, out var island) && island.Collapsed ? AnimatedIsland(island) : Display(card);

    /// <summary>Applies visual magnification without changing logical drag, save or minimap coordinates.</summary>
    private Bounds Display(Card card)
    {
        var logical = AnimatedLogical(card);
        var factor = DisplayScale(card);
        var size = logical.Size * factor;
        return new Bounds(logical.Center - size / 2, size);
    }

    /// <summary>Interprets the saved card bounds through an arrangement's current display-only pose.</summary>
    private Bounds AnimatedLogical(Card card) => IsArranging && _previousCardPositions.TryGetValue(card.Document.Path, out var prior)
        ? Interpolate(prior, card.Logical, EaseOut(_arrangementProgress)) : card.Logical;

    /// <summary>Moves the folder frame with its cards during an arrangement.</summary>
    private Bounds AnimatedIsland(Island island) => IsArranging && _previousIslandPositions.TryGetValue(island.Path, out var prior)
        ? Interpolate(prior, island.Bounds, EaseOut(_arrangementProgress)) : island.Bounds;

    /// <summary>Interpolates position and size independently so reset can restore both.</summary>
    private static Bounds Interpolate(Bounds before, Bounds after, float progress) =>
        new(Vector2.Lerp(before.Origin, after.Origin, progress), Vector2.Lerp(before.Size, after.Size, progress));

    /// <summary>Eases existing geometry toward its final position without overshooting saved bounds.</summary>
    private static float EaseOut(float progress)
    {
        var timeLeft = 1 - progress;
        return 1 - timeLeft * timeLeft * timeLeft;
    }

    /// <summary>Remembers the current hover poses before replacing the pointer target.</summary>
    private void BeginCardHoverTransition(string? next)
    {
        if (next == _hovered)
        {
            return;
        }
        if (_map == null || Visibility != Visibility.Visible || !_surface.IsLoaded || IsDense || _pointer.HasValue ||
            IsDirectCameraGesture || !AnimationsEnabled())
        {
            _previousHover.Clear();
            _hoverStarted = 0;
            _hoverProgress = 1;
            return;
        }

        // Keep at most the outgoing and incoming hover targets alive across rapid pointer movement.
        var outgoing = _hovered != null && _cards.TryGetValue(_hovered, out var previous) ? HoverPose(previous) : null;
        var incoming = next != null && _cards.TryGetValue(next, out var current) ? HoverPose(current) : null;
        _previousHover.Clear();
        if (_hovered != null && outgoing.HasValue)
        {
            _previousHover.Add(_hovered, outgoing.Value);
        }
        if (next != null && incoming.HasValue)
        {
            _previousHover.Add(next, incoming.Value);
        }
        if (_previousHover.Count > 0)
        {
            _hoverStarted = Stopwatch.GetTimestamp();
            _hoverProgress = 0;
            _effectTimer.Start();
        }
    }

    /// <summary>Captures the current scale and light before a pointer transition changes either.</summary>
    private (float Scale, float Emphasis)? HoverPose(Card card) => (DisplayScale(card), HoverEmphasis(card.Document.Path));

    /// <summary>Fades native hover lighting and dimming without changing the document's active state.</summary>
    private float HoverEmphasis(string path)
    {
        if (path == FocusedCard)
        {
            return 1;
        }
        var target = path == _hovered ? 1 : 0;
        return HoverPhase < 1 && _previousHover.TryGetValue(path, out var prior)
            ? float.Lerp(prior.Emphasis, target, HoverPhase) : target;
    }

    /// <summary>Interpolates the native card pose while keeping a captured gesture's pixel scale constant.</summary>
    private float DisplayScale(Card card)
    {
        if (CapturedCardScale(card) is { } captured)
        {
            return captured;
        }
        var path = card.Document.Path;
        var target = path == _selected ? (_depth ? 1.28f : 1.14f) : !IsDense && (path == _hovered || path == FocusedCard) ? 1.035f :
            900 / (900 + 85 * _depthLevels.GetValueOrDefault(path));
        var projected = ProjectionPhase < 1 && _previousDepth.TryGetValue(path, out var prior)
            ? float.Lerp(prior.Scale, target, ProjectionPhase) : target;
        return path != _selected && path != FocusedCard && HoverPhase < 1 && _previousHover.TryGetValue(path, out var hover)
            ? float.Lerp(hover.Scale, projected, HoverPhase) : projected;
    }

    private float ProjectionPhase => _selectionProgress >= 1 || _pointer.HasValue || IsDirectCameraGesture
        ? 1 : EaseOut(_selectionProgress);

    private float HoverPhase => _hoverProgress >= 1 || _pointer.HasValue || IsDirectCameraGesture ? 1 : EaseOut(_hoverProgress);

    /// <summary>Computes the twelve logarithmic depth planes after a selection or committed layout change.</summary>
    private void UpdateDepthLevels()
    {
        _depthLevels.Clear();
        if (!_depth || _map == null || !_cards.TryGetValue(_selected, out var selected))
        {
            return;
        }
        var unit = new Vector2(MapLayoutCodec.CardWidth, MapLayoutCodec.CardHeight).Length();
        foreach (var (path, card) in _cards)
        {
            if (path == _selected)
            {
                continue;
            }
            var hops = _map.Depths.GetValueOrDefault(path, -1);
            var graphLevel = hops < 0 ? 6 : 2 * MathF.Log2(1 + hops);
            var spatialLevel = 1.5f * MathF.Log2(1 + Vector2.Distance(card.Logical.Center, selected.Logical.Center) / unit);
            _depthLevels[path] = Math.Clamp(MathF.Round(graphLevel + spatialLevel, MidpointRounding.AwayFromZero), 1, MaximumDepth);
        }
    }

    /// <summary>Returns a precomputed depth level for the current layout and selected reference card.</summary>
    private float DepthLevel(string path)
    {
        var target = _depthLevels.GetValueOrDefault(path);
        return ProjectionPhase < 1 && _previousDepth.TryGetValue(path, out var prior)
            ? float.Lerp(prior.Depth, target, ProjectionPhase) : target;
    }

    /// <summary>Puts the foreground card and interaction targets above receding geometry.</summary>
    private int CardLayer(Card card) => card == _dragged ? 50 : card.Document.Path == _selected ? 41 :
        card.Document.Path == _hovered || card.Document.Path == FocusedCard ? 40 : 30 - (int)DepthLevel(card.Document.Path);

    /// <summary>Balances focus dimming and graph depth without hiding out-of-range cards.</summary>
    private float VisibilityOf(string path) => (IsWithinDistance(path) ? 1 : .35f) * (1 - DepthLevel(path) * .042f);

    /// <summary>Retains route members and documents within the current relationship distance.</summary>
    private bool IsWithinDistance(string path)
    {
        var depth = _map?.Depths.GetValueOrDefault(path, -1) ?? -1;
        return _focusDepth == 0 || depth >= 0 && depth <= _focusDepth || _routeCards.Contains(path);
    }

    private string? RelationFocus => _exploration?.LensEnabled == true && _cards.ContainsKey(_hovered ?? FocusedCard ?? _selected)
        ? _hovered ?? FocusedCard ?? _selected : null;

    /// <summary>Derives visible participants of the current relation lens without revisiting the catalog.</summary>
    private void UpdateRelations()
    {
        _relatedCards.Clear();
        if (RelationFocus is not { } focus)
        {
            return;
        }
        _relatedCards.Add(focus);
        foreach (var edge in _edges)
        {
            if (edge.Source == focus || edge.Target == focus)
            {
                _relatedCards.Add(edge.Source);
                _relatedCards.Add(edge.Target);
            }
        }
    }

    /// <summary>Classifies a catalog attention fact against the active presentation lens.</summary>
    private static bool MatchesAttention(MapAttention? attention, string mode) => (mode, attention) switch
    {
        ("attention", { } fact) => fact.MissingLinks > 0 || fact.Changed || fact.Isolated,
        ("missing", { MissingLinks: > 0 }) or ("changed", { Changed: true }) or ("isolated", { Isolated: true }) => true,
        _ => false
    };

    /// <summary>Returns cards outside collapsed folder islands.</summary>
    private IEnumerable<Card> VisibleCards() => _cards.Values.Where(card => !IsHidden(card));

    /// <summary>Tests whether a saved card is temporarily represented by a collapsed folder.</summary>
    private bool IsHidden(Card card) => _islands.TryGetValue(card.Folder, out var island) && island.Collapsed;

    /// <summary>Bounds the rendered map independently of its current camera.</summary>
    private Bounds GraphBounds() => Bounds.Combine(VisibleCards().Select(card => card.Logical).Concat(_islands.Values.Select(island => island.Bounds))).Expand(24);

    private Bounds? Minimap => ActualWidth >= 500 && ActualHeight >= 280 && (_cards.Count > 0 || _islands.Count > 0)
        ? new Bounds(Viewport - new Vector2(206, 138), new Vector2(188, 120)) : null;

    /// <summary>Centers the main view on the minimap pointer while retaining the active zoom.</summary>
    private void JumpMinimap(Bounds minimap)
    {
        var graph = GraphBounds();
        var (origin, scale) = MinimapProjection(minimap, graph);
        var target = Vector2.Clamp(graph.Origin + (_pointerCurrent - origin) / scale, graph.Origin, graph.End);
        ApplyCamera(ShiftCenterBelowToolbar(target, _scale), _scale);
    }

    /// <summary>Maps viewport pixels back into logical graph space.</summary>
    private Vector2 World(Vector2 point) => (point - _translation) / _scale;

    /// <summary>Maps logical graph coordinates into the WinUI viewport.</summary>
    private Vector2 Screen(Vector2 point) => point * _scale + _translation;

    /// <summary>Converts a floating-point WinUI input coordinate to a Win2D vector.</summary>
    private static Vector2 PointOf(Point point) => new((float)point.X, (float)point.Y);

    /// <summary>Applies a visual alpha without mutating the stored card palette.</summary>
    private static Color Alpha(Color color, float factor) => Color.FromArgb((byte)(color.A * Math.Clamp(factor, 0, 1)), color.R, color.G, color.B);

    /// <summary>Blends two opaque card colors by their transition progress.</summary>
    private static Color Mix(Color before, Color after, float progress) => Color.FromArgb(255,
        (byte)float.Lerp(before.R, after.R, progress), (byte)float.Lerp(before.G, after.G, progress), (byte)float.Lerp(before.B, after.B, progress));

    /// <summary>Maps a canonical folder to a stable island color without randomized process hashes.</summary>
    private static Color FolderColor(string path)
    {
        var hash = 0u;
        foreach (var character in path.EnumerateRunes())
        {
            hash = unchecked(hash * 31 + (uint)character.Value);
        }
        return ParseColor(IslandPalette[(int)(hash % (uint)IslandPalette.Length)]);
    }

    /// <summary>Converts the shared validated map palette from RGB text to a Windows color.</summary>
    internal static Color ParseColor(string value)
    {
        var rgb = Convert.ToInt32(value[1..], 16);
        return Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    /// <summary>Reads the shared application icon symbols from the embedded asset.</summary>
    private static IReadOnlyDictionary<string, XElement> ReadIconSymbols()
    {
        using var source = typeof(WindowsGraphCanvas).Assembly.GetManifestResourceStream("MarkVibe.Windows.MapIcons.svg");
        if (source == null)
        {
            return new Dictionary<string, XElement>(StringComparer.Ordinal);
        }
        var root = XDocument.Load(source).Root!;
        return root.Elements(root.Name.Namespace + "symbol")
            .Where(symbol => ((string?)symbol.Attribute("id"))?.StartsWith("map-", StringComparison.Ordinal) == true)
            .ToDictionary(symbol => ((string)symbol.Attribute("id")!)[4..], symbol => symbol, StringComparer.Ordinal);
    }

    /// <summary>Creates a standalone local SVG for one saved map icon in its selected tint.</summary>
    internal static string? IconSvg(string icon, Color tint)
    {
        if (!IconSymbols.Value.TryGetValue(icon, out var symbol))
        {
            return null;
        }
        var document = new XElement(symbol.Name.Namespace + "svg",
            new XAttribute("viewBox", (string?)symbol.Attribute("viewBox") ?? "0 0 24 24"), symbol.Elements());
        return document.ToString(SaveOptions.DisableFormatting)
            .Replace("currentColor", $"#{tint.R:X2}{tint.G:X2}{tint.B:X2}", StringComparison.Ordinal);
    }

    /// <summary>Draws the local SVG on the native canvas, retaining one Win2D object per active icon and color.</summary>
    private void DrawIcon(CanvasDrawingSession drawing, Card card, Vector2 position, float size, float opacity)
    {
        var key = (card.Layout.Icon, card.Color);
        if (!_icons.TryGetValue(key, out var rendered))
        {
            var xml = IconSvg(card.Layout.Icon, card.Color);
            CanvasSvgDocument? document = null;
            if (xml != null)
            {
                try
                {
                    document = CanvasSvgDocument.LoadFromXml(_surface, xml);
                }
                catch (COMException)
                {
                    // A bad vector must not make the map unreadable on the current device.
                }
            }
            rendered = new RenderedIcon(document);
            _icons.Add(key, rendered);
        }

        if (rendered.Document is { } icon)
        {
            rendered.SetOpacity(opacity);
            drawing.DrawSvg(icon, new Size(size, size), position);
        }
        else
        {
            drawing.DrawText(Glyph(card.Layout.Icon), position, Alpha(card.Color, opacity), _iconFormat);
        }
    }

    /// <summary>Resolves every saved icon key to a local Windows glyph.</summary>
    internal static string Glyph(string icon) => icon switch
    {
        "folder" => "\uE8B7", "coin" => "\uEAFD", "book" => "\uE82D", "graduation" => "\uE7BE",
        "pencil" => "\uE70F", "pen-tool" => "\uED63", "code" => "\uE943", "terminal" => "\uE756",
        "music" => "\uE8D6", "flower" => "\uE91B", "palette" => "\uE790", "stethoscope" => "\uE95E",
        "sparkles" => "\uF4A5", "briefcase" => "\uE821", "chart" => "\uE9D9", "kettlebell" => "\uE95B",
        "dumbbell" => "\uE95D", "notebook" => "\uE70B", "balance" => "\uE9D2", "moon" => "\uE708",
        "plane" => "\uE709", "globe" => "\uE774", "heart" => "\uEB51", _ => "\uE8A5"
    };

    /// <summary>Formats all indexed local document counts for the native graph and inspector.</summary>
    internal static string Statistics(DocumentStatistics? stats) => stats == null ? "Statistics pending" :
        $"{stats.SourceBytes:N0} B  ·  ~{stats.EstimatedTokens:N0} tokens  ·  {stats.Characters:N0} chars  ·  {stats.Words:N0} words  ·  {stats.Lines:N0} lines  ·  {stats.OutgoingLinks:N0} links";

    /// <summary>Determines whether a native keyboard modifier is currently pressed.</summary>
    private static bool Pressed(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    /// <summary>Converts directional keys to cardinal vectors without handling unrelated keys.</summary>
    private static Vector2? Arrow(VirtualKey key) => key switch
    {
        VirtualKey.Left => new Vector2(-1, 0), VirtualKey.Right => new Vector2(1, 0),
        VirtualKey.Up => new Vector2(0, -1), VirtualKey.Down => new Vector2(0, 1), _ => null
    };

    /// <summary>Releases card text layouts before resource recreation or disposal.</summary>
    private void ReleaseText()
    {
        foreach (var card in _cards.Values)
        {
            card.ReleaseText();
        }
    }

    /// <summary>Discards caches whose native handles are tied to the current map device.</summary>
    private void ReleaseDrawingResources()
    {
        ReleaseText();
        foreach (var entry in _curvePaths.Values)
        {
            entry.Path.Dispose();
        }
        _curvePaths.Clear();
        foreach (var icon in _icons.Values)
        {
            icon.Dispose();
        }
        _icons.Clear();
        foreach (var brush in _lights.Values)
        {
            brush.Dispose();
        }
        _lights.Clear();
    }

    /// <summary>Detaches Win2D resources permanently when the desktop window closes.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _effectTimer.Stop();
        _pointerTipTimer.Stop();
        _pointerTipTimer.Tick -= PointerTipReady;
        _surface.Draw -= Draw;
        _surface.ReleasePointerCaptures();
        ReleaseDrawingResources();
        _titleFormat.Dispose();
        _overviewTitleFormat.Dispose();
        _detailFormat.Dispose();
        _labelFormat.Dispose();
        _folderCountFormat.Dispose();
        _statsFormat.Dispose();
        _iconFormat.Dispose();
        _dashed.Dispose();
        _dotted.Dispose();
        _roundStroke.Dispose();
        _surface.RemoveFromVisualTree();
    }

    /// <summary>Names the mutually exclusive operations performed by the captured pointer.</summary>
    private enum Gesture { None, Pan, Move, Resize, Select, Minimap, Pick, CenterLabel, Folder, Link }

    /// <summary>Names the keyboard targets addressed on the single native drawing surface.</summary>
    private enum FocusTarget { Viewport, Card, Resize, Link, Folder, Minimap }

    /// <summary>Identifies a delayed hover or keyboard description within the current render.</summary>
    private enum TooltipKind { None, Card, Resize, Link, DocumentLabel, FolderLabel, Folder, Minimap, Statistics }

    /// <summary>Retains a description target without allocating a new tooltip on each mouse move.</summary>
    private readonly record struct TooltipHit(TooltipKind Kind, string? Path = null, MapEdge? Edge = null, int Row = 0, int Column = 0);

    /// <summary>Links one painted statistics value to its full localized explanation.</summary>
    private readonly record struct StatisticPart(string VisibleText, string Label, string FullValue, bool Estimated = false)
    {
        public string Tooltip => T(Label) + " · " + FullValue + (Estimated ? "\n" +
            T("Token counts are estimates; the final AI request also includes instructions and reference documents.") : "");
    }

    /// <summary>Names a virtual UI Automation item and resolves it through the current graph on demand.</summary>
    private readonly record struct AutomationTarget(FocusTarget Kind, string? Path = null, MapEdge? Edge = null);

    /// <summary>Retains a finite camera move independently of the saved graph positions.</summary>
    private sealed record CameraTransition(Vector2 StartCenter, Vector2 EndCenter, float StartScale, float EndScale, long Started);

    /// <summary>Exposes the Win2D viewport as an accessible native pane.</summary>
    private sealed class MapAutomationPeer(WindowsGraphCanvas owner) : FrameworkElementAutomationPeer(owner)
    {
        private readonly Dictionary<AutomationTarget, MapItemAutomationPeer> _items = [];

        /// <summary>Identifies the native control to accessibility clients.</summary>
        protected override string GetClassNameCore() => nameof(WindowsGraphCanvas);

        /// <summary>Reports a navigable pane whose focused items are announced by the viewport.</summary>
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;

        /// <summary>Exposes stable virtual children for the bounded projection alongside its native Read here control.</summary>
        protected override IList<AutomationPeer> GetChildrenCore()
        {
            var result = base.GetChildrenCore() is { } native ? new List<AutomationPeer>(native) : [];
            var targets = owner.AutomationTargets().ToHashSet();
            foreach (var retired in _items.Keys.Where(key => !targets.Contains(key)).ToArray())
            {
                _items.Remove(retired);
            }
            foreach (var target in targets)
            {
                if (!_items.TryGetValue(target, out var item))
                {
                    _items[target] = item = new MapItemAutomationPeer(owner, this, target);
                }
                result.Add(item);
            }
            return result;
        }
    }

    /// <summary>Provides an independently discoverable native UI Automation item for a painted graph affordance.</summary>
    private sealed class MapItemAutomationPeer(WindowsGraphCanvas owner, MapAutomationPeer parent, AutomationTarget target) : AutomationPeer, IInvokeProvider
    {
        private bool CanInvoke => target.Kind is FocusTarget.Card or FocusTarget.Link or FocusTarget.Folder;

        /// <summary>Reports the item's current model-derived name.</summary>
        protected override string GetNameCore() => owner.AutomationTargetName(target);

        /// <summary>Reports the tooltip and available interaction for the current item.</summary>
        protected override string GetHelpTextCore() => owner.AutomationTargetHelp(target);

        /// <summary>Names the virtual item's class without exposing a XAML container that does not exist.</summary>
        protected override string GetClassNameCore() => "DocumentMapItem";

        /// <summary>Uses a stable path and link identity for native automation clients.</summary>
        protected override string GetAutomationIdCore() => target.Edge is { } edge
            ? $"Map:{target.Kind}:{edge.Source}:{edge.Target}:{edge.Anchor}:{edge.Line}"
            : $"Map:{target.Kind}:{target.Path}";

        /// <summary>Reports the role corresponding to the painted item's interaction.</summary>
        protected override AutomationControlType GetAutomationControlTypeCore() => target.Kind switch
        {
            FocusTarget.Link => AutomationControlType.Hyperlink, FocusTarget.Resize => AutomationControlType.Thumb,
            FocusTarget.Minimap => AutomationControlType.Pane, _ => AutomationControlType.Button
        };

        /// <summary>Includes the item in the native content and control views.</summary>
        protected override bool IsControlElementCore() => true;

        /// <summary>Includes the item when an accessibility client enumerates content.</summary>
        protected override bool IsContentElementCore() => true;

        /// <summary>Marks targets disabled once their workspace or paint representation disappears.</summary>
        protected override bool IsEnabledCore() => owner.AutomationTargetBounds(target) != null;

        /// <summary>Offers keyboard focus while the target remains in the current graph.</summary>
        protected override bool IsKeyboardFocusableCore() => owner.AutomationTargetBounds(target) != null;

        /// <summary>Reports focus only when this target owns the actual native keyboard focus.</summary>
        protected override bool HasKeyboardFocusCore() => target.Kind == owner._focusTarget &&
            (target.Edge != null ? target.Edge == owner._keyboardEdge : target.Path == owner._keyboardPath) &&
            owner.XamlRoot is { } root && ReferenceEquals(FocusManager.GetFocusedElement(root), owner);

        /// <summary>Computes the visible part of a painted item in native physical screen coordinates.</summary>
        protected override Rect GetBoundingRectangleCore()
        {
            if (owner.AutomationTargetBounds(target) is not { } item)
            {
                return default;
            }
            var near = Vector2.Max(item.Origin, new Vector2(0, owner.ToolbarInset));
            var far = Vector2.Min(item.End, owner.Viewport);
            var rootBounds = parent.GetBoundingRectangle();
            var scale = owner.XamlRoot?.RasterizationScale ?? 1;
            return far.X <= near.X || far.Y <= near.Y || rootBounds.Width <= 0 || rootBounds.Height <= 0 ? default :
                new Rect(rootBounds.X + near.X * scale, rootBounds.Y + near.Y * scale, (far.X - near.X) * scale, (far.Y - near.Y) * scale);
        }

        /// <summary>Marks items outside the visible canvas, including those behind the toolbar, as offscreen.</summary>
        protected override bool IsOffscreenCore() => GetBoundingRectangleCore().Width <= 0;

        /// <summary>Routes native focus to the same item state as the canvas keyboard shortcuts.</summary>
        protected override void SetFocusCore() => owner.RunAutomationTarget(target, invoke: false);

        /// <summary>Advertises Invoke for the graph items with a primary pointer action.</summary>
        protected override object? GetPatternCore(PatternInterface patternInterface) => patternInterface == PatternInterface.Invoke && CanInvoke ? this : null;

        /// <summary>Invokes the target through the UI dispatcher and the current workspace model.</summary>
        public void Invoke()
        {
            if (CanInvoke)
            {
                owner.RunAutomationTarget(target, invoke: true);
            }
        }
    }

    /// <summary>Reads the user's Windows double-click interval in milliseconds.</summary>
    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    /// <summary>Reads a Windows system metric, including physical double-click rectangle dimensions.</summary>
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    /// <summary>Represents an island plus the whole-world bounds of its expandable header.</summary>
    private sealed record Island(string Path, Bounds Bounds, int Count, bool Collapsed, Color Color)
    {
        public string Label => Path.Length == 0 ? UiStrings.T("Workspace root") : Path;
        public string Caption => $"{Label} · {Count:N0}";
        public Bounds Header => Collapsed ? Bounds : new(Bounds.Origin, new Vector2(Bounds.Size.X, Math.Min(42, Bounds.Size.Y)));
    }

    /// <summary>Represents one rendered connection and the same geometry used for pointer and keyboard interaction.</summary>
    private readonly record struct ConnectionCurve(Vector2 Start, Vector2 StartControl, Vector2 EndControl, Vector2 End)
    {
        /// <summary>Returns the conservative bounds of the endpoints and control points.</summary>
        public Bounds Hull
        {
            get
            {
                var a = Bounds.Between(Start, End);
                var b = Bounds.Between(StartControl, EndControl);
                return Bounds.Between(Vector2.Min(a.Origin, b.Origin), Vector2.Max(a.End, b.End));
            }
        }

        /// <summary>Samples the directed cubic at the given unit-interval parameter.</summary>
        public Vector2 PointAt(float progress)
        {
            var inverse = 1 - progress;
            return inverse * inverse * inverse * Start + 3 * inverse * inverse * progress * StartControl +
                3 * inverse * progress * progress * EndControl + progress * progress * progress * End;
        }

        /// <summary>Tests the curve inside a pointer radius, subdividing curvature until the screen tolerance is met.</summary>
        public float DistanceSquaredTo(Vector2 point, float maximumSquared, float precision, int depth = 0)
        {
            if (!Hull.Expand(MathF.Sqrt(maximumSquared) + precision).Contains(point))
            {
                return float.PositiveInfinity;
            }
            var precisionSquared = precision * precision;
            if (depth >= 10 || DistanceToSegmentSquared(StartControl, Start, End) <= precisionSquared &&
                DistanceToSegmentSquared(EndControl, Start, End) <= precisionSquared)
            {
                return DistanceToSegmentSquared(point, Start, End);
            }
            var a = Vector2.Lerp(Start, StartControl, .5f);
            var b = Vector2.Lerp(StartControl, EndControl, .5f);
            var c = Vector2.Lerp(EndControl, End, .5f);
            var left = Vector2.Lerp(a, b, .5f);
            var right = Vector2.Lerp(b, c, .5f);
            var middle = Vector2.Lerp(left, right, .5f);
            var first = new ConnectionCurve(Start, a, left, middle).DistanceSquaredTo(point, maximumSquared, precision, depth + 1);
            var second = new ConnectionCurve(middle, right, c, End).DistanceSquaredTo(point, Math.Min(first, maximumSquared), precision, depth + 1);
            return Math.Min(first, second);
        }

        /// <summary>Measures a point against one flattened piece, including degenerate endpoints.</summary>
        private static float DistanceToSegmentSquared(Vector2 point, Vector2 start, Vector2 end)
        {
            var difference = end - start;
            var lengthSquared = difference.LengthSquared();
            var position = lengthSquared > 0 ? Math.Clamp(Vector2.Dot(point - start, difference) / lengthSquared, 0, 1) : 0;
            return Vector2.DistanceSquared(point, start + difference * position);
        }
    }

    /// <summary>Owns the Win2D document and mutable device opacity for one icon tint.</summary>
    private sealed class RenderedIcon(CanvasSvgDocument? document) : IDisposable
    {
        private readonly CanvasSvgNamedElement? _root = document?.Root;
        private float _opacity = 1;
        public CanvasSvgDocument? Document { get; } = document;

        /// <summary>Updates the native opacity only when depth, emphasis or entry animation changed.</summary>
        public void SetOpacity(float value)
        {
            if (_root != null && _opacity != value)
            {
                _root.SetFloatAttribute("opacity", value);
                _opacity = value;
            }
        }

        /// <summary>Releases the device-bound result once its icon or palette leaves the projection.</summary>
        public void Dispose()
        {
            _root?.Dispose();
            Document?.Dispose();
        }
    }

    /// <summary>Represents axis-aligned bounds in logical or screen coordinates.</summary>
    private readonly record struct Bounds(Vector2 Origin, Vector2 Size)
    {
        public Vector2 End => Origin + Size;
        public Vector2 Center => Origin + Size / 2;
        public Rect Rect => new(Origin.X, Origin.Y, Size.X, Size.Y);

        /// <summary>Returns saved logical card bounds with the schema's default dimensions.</summary>
        public static Bounds From(MapCardLayout layout) => new(new Vector2((float)layout.X, (float)layout.Y),
            new Vector2((float)(layout.Width ?? MapLayoutCodec.CardWidth), (float)(layout.Height ?? MapLayoutCodec.CardHeight)));

        /// <summary>Converts a WinUI rectangle to vector-backed bounds.</summary>
        public static Bounds From(Rect rect) => new(new Vector2((float)rect.X, (float)rect.Y), new Vector2((float)rect.Width, (float)rect.Height));

        /// <summary>Returns the smallest rectangle spanning both coordinates.</summary>
        public static Bounds Between(Vector2 a, Vector2 b) => new(Vector2.Min(a, b), Vector2.Abs(a - b));

        /// <summary>Adds a uniform margin without changing the center.</summary>
        public Bounds Expand(float amount) => new(Origin - new Vector2(amount), Size + new Vector2(2 * amount));

        /// <summary>Checks whether a logical or screen point is inside this rectangle.</summary>
        public bool Contains(Vector2 point) => point.X >= Origin.X && point.Y >= Origin.Y && point.X <= End.X && point.Y <= End.Y;

        /// <summary>Checks whether a second rectangle intersects this one.</summary>
        public bool Overlaps(Bounds other) => Origin.X <= other.End.X && End.X >= other.Origin.X && Origin.Y <= other.End.Y && End.Y >= other.Origin.Y;

        /// <summary>Combines all rectangles and provides safe nonzero bounds for an empty map.</summary>
        public static Bounds Combine(IEnumerable<Bounds> boxes)
        {
            using var current = boxes.GetEnumerator();
            if (!current.MoveNext())
            {
                return new Bounds(Vector2.Zero, Vector2.One);
            }
            var start = current.Current.Origin;
            var end = current.Current.End;
            while (current.MoveNext())
            {
                start = Vector2.Min(start, current.Current.Origin);
                end = Vector2.Max(end, current.Current.End);
            }
            return new Bounds(start, Vector2.Max(end - start, Vector2.One));
        }
    }

    /// <summary>Retains transient card bounds and measured text without owning the saved map layout.</summary>
    private sealed class Card(DocumentCard document, MapCardLayout layout)
    {
        private CanvasTextLayout? _title, _detail;
        private CanvasTextFormat? _lastTitleFormat;
        private float _titleWidth, _titleHeight, _detailWidth, _detailHeight;
        public DocumentCard Document { get; private set; } = document;
        public MapCardLayout Layout { get; private set; } = layout;
        public string Folder { get; } = WindowsMapModel.FolderOf(document.Path);
        public Bounds Logical { get; set; } = Bounds.From(layout);
        public Color Color { get; private set; } = ParseColor(layout.Color);

        /// <summary>Accepts document and layout data while retaining compatible cached text.</summary>
        public void Update(DocumentCard next, MapCardLayout nextLayout)
        {
            if (next.Title != Document.Title || next.Introduction != Document.Introduction)
            {
                ReleaseText();
            }
            Document = next;
            if (Layout != nextLayout)
            {
                Layout = nextLayout;
                Color = ParseColor(nextLayout.Color);
            }
            Logical = Bounds.From(nextLayout);
        }

        /// <summary>Retains one DirectWrite title shape while its width matches the rendered card.</summary>
        public void DrawTitle(CanvasDrawingSession drawing, CanvasControl device, CanvasTextFormat format, Vector2 position, Color color, float width, float height)
        {
            if (_title == null || _titleWidth != width || _titleHeight != height || _lastTitleFormat != format)
            {
                _title?.Dispose();
                _title = new CanvasTextLayout(device, Document.Title, format, width, height);
                _titleWidth = width;
                _titleHeight = height;
                _lastTitleFormat = format;
            }
            drawing.DrawTextLayout(_title, position, color);
        }

        /// <summary>Uses a single line at rest and a bounded multiline excerpt while focused.</summary>
        public void DrawDetail(CanvasDrawingSession drawing, CanvasControl device, CanvasTextFormat format, Vector2 position, Color color, float width, float height)
        {
            if (_detail == null || _detailWidth != width || _detailHeight != height)
            {
                _detail?.Dispose();
                _detail = new CanvasTextLayout(device, Document.Introduction, format, width, height);
                _detailWidth = width;
                _detailHeight = height;
            }
            drawing.DrawTextLayout(_detail, position, color);
        }

        /// <summary>Releases cached text when the card data or drawing device changes.</summary>
        public void ReleaseText()
        {
            _title?.Dispose();
            _detail?.Dispose();
            _title = _detail = null;
        }
    }
}
