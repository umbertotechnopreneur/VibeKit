/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; fine tiled paper grain and feathered native surface edges
 * Human guidance: use the same quiet paper finish across toolbars, options and dialogs
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace MarkVibe.Windows;

/// <summary>Decorates a native material with static composition visuals that have no input, focus or automation peers.</summary>
internal sealed class WindowsPaperSurface
{
    private const int TilePixels = 256;
    // Even the strongest texel then changes a flat surface by less than two 8-bit levels.
    private const float GrainOpacity = .08f;
    private static readonly ConditionalWeakTable<FrameworkElement, WindowsPaperSurface> Surfaces = new();
    private static readonly Lazy<LoadedImageSurface> Grain = new(() =>
        LoadedImageSurface.StartLoadFromUri(new Uri("ms-appx:///app/images/chrome-paper-grain.png")));
    private readonly FrameworkElement _host;
    private readonly FrameworkElement _material;
    private readonly DependencyProperty? _backgroundProperty;
    private long _backgroundSubscription;
    private readonly AccessibilitySettings _contrast = new();
    private readonly UISettings _effects = new();
    private XamlRoot? _root;
    private Drawing? _drawing;
    private bool _contrastSubscribed;
    private bool _effectsSubscribed;

    /// <summary>Observes system preferences only while this element belongs to a live visual tree.</summary>
    private WindowsPaperSurface(FrameworkElement host, FrameworkElement material)
    {
        _host = host;
        _material = material;
        _backgroundProperty = material switch
        {
            Control => Control.BackgroundProperty,
            Border => Border.BackgroundProperty,
            Panel => Panel.BackgroundProperty,
            _ => null
        };
        host.Loaded += Loaded;
        host.Unloaded += Unloaded;
        host.SizeChanged += SizeChanged;
        if (host.IsLoaded) { Start(); }
    }

    /// <summary>Attaches once to the actual surface while preserving its native brush and children.</summary>
    public static void Attach(FrameworkElement host) => Attach(host, host);

    /// <summary>Allows a popup's dedicated decoration to observe the material painted by its owning dialog.</summary>
    private static void Attach(FrameworkElement host, FrameworkElement material) =>
        Surfaces.GetValue(host, element => new WindowsPaperSurface(element, material));

    /// <summary>Owns a decorative layer inside the actual popup template for this presentation of a dialog.</summary>
    internal sealed class DialogLayer : IDisposable
    {
        private readonly FrameworkElement? _content;
        private readonly Border _overlay = new() { IsHitTestVisible = false };

        /// <summary>Uses the presented content as an anchor even when WinUI moves the dialog into a separate popup tree.</summary>
        public DialogLayer(ContentDialog dialog)
        {
            if (dialog.Content is string text)
            {
                dialog.Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            }
            _content = dialog.Content as FrameworkElement;
            if (_content == null) { return; }
            AutomationProperties.SetAccessibilityView(_overlay, AccessibilityView.Raw);
            Attach(_overlay, dialog);
            _content.Loaded += ContentLoaded;
            if (_content.IsLoaded) { ResolveLayout(); }
        }

        /// <summary>Reacquires the visible dialog grid when a popup or a new theme realizes the content.</summary>
        private void ContentLoaded(object sender, RoutedEventArgs args) => ResolveLayout();

        /// <summary>Waits for the first completed layout when the loaded event precedes popup reparenting.</summary>
        private void LayoutUpdated(object? sender, object args) => ResolveLayout();

        /// <summary>Places a noninteractive sibling above both native rows and leaves the popup's own composition visual alone.</summary>
        private void ResolveLayout()
        {
            if (_content == null) { return; }
            _content.LayoutUpdated -= LayoutUpdated;
            for (DependencyObject? ancestor = _content; ancestor != null; ancestor = VisualTreeHelper.GetParent(ancestor))
            {
                if (ancestor is not Grid { Name: "DialogSpace" } space) { continue; }
                if (ReferenceEquals(_overlay.Parent, space)) { return; }
                if (_overlay.Parent is Panel previous) { previous.Children.Remove(_overlay); }
                _overlay.CornerRadius = space.CornerRadius;
                Grid.SetRowSpan(_overlay, Math.Max(1, space.RowDefinitions.Count));
                Grid.SetColumnSpan(_overlay, Math.Max(1, space.ColumnDefinitions.Count));
                space.Children.Add(_overlay);
                return;
            }
            _content.LayoutUpdated += LayoutUpdated;
        }

        /// <summary>Removes the owned overlay and its realization handlers when the modal closes.</summary>
        public void Dispose()
        {
            if (_content == null) { return; }
            _content.Loaded -= ContentLoaded;
            _content.LayoutUpdated -= LayoutUpdated;
            if (_overlay.Parent is Panel panel) { panel.Children.Remove(_overlay); }
        }
    }

    /// <summary>Installs the finish on each popup realization, including later openings of the same flyout.</summary>
    public static void AttachFlyout(FlyoutBase flyout)
    {
        flyout.Opened -= FlyoutOpened;
        flyout.Opened += FlyoutOpened;
    }

    /// <summary>Finds the presenter belonging to this flyout from its own realized content or menu item.</summary>
    private static void FlyoutOpened(object? sender, object args)
    {
        var content = sender switch
        {
            Flyout { Content: FrameworkElement element } => element,
            MenuFlyout menu => menu.Items.FirstOrDefault(),
            _ => null
        };
        if (content == null) { return; }
        content.DispatcherQueue.TryEnqueue(() =>
        {
            for (DependencyObject? parent = content; parent != null; parent = VisualTreeHelper.GetParent(parent))
            {
                if (parent is Control presenter && presenter is FlyoutPresenter or MenuFlyoutPresenter)
                {
                    Attach(presenter);
                    break;
                }
            }
        });
    }

    /// <summary>Connects composition after the owner obtains its current XAML root.</summary>
    private void Loaded(object sender, RoutedEventArgs args) => Start();

    /// <summary>Tracks preferences and scale for this realization, with size and root events as a desktop fallback.</summary>
    private void Start()
    {
        if (_root != null || _host.XamlRoot is not { } root) { return; }
        _root = root;
        root.Changed += RootChanged;
        if (_backgroundProperty != null)
        {
            _backgroundSubscription = _material.RegisterPropertyChangedCallback(_backgroundProperty, BackgroundChanged);
        }
        try { _contrast.HighContrastChanged += ContrastChanged; _contrastSubscribed = true; }
        catch (COMException error) when (error.HResult == unchecked((int)0x80070490)) { }
        try { _effects.AdvancedEffectsEnabledChanged += EffectsChanged; _effectsSubscribed = true; }
        catch (COMException error) when (error.HResult == unchecked((int)0x80070490)) { }
        Update();
    }

    /// <summary>Releases composition visuals and system subscriptions when a popup closes or a toolbar unloads.</summary>
    private void Unloaded(object sender, RoutedEventArgs args)
    {
        if (_root != null)
        {
            _root.Changed -= RootChanged;
            if (_backgroundProperty != null)
            {
                _material.UnregisterPropertyChangedCallback(_backgroundProperty, _backgroundSubscription);
            }
        }
        _root = null;
        if (_contrastSubscribed) { _contrast.HighContrastChanged -= ContrastChanged; _contrastSubscribed = false; }
        if (_effectsSubscribed) { _effects.AdvancedEffectsEnabledChanged -= EffectsChanged; _effectsSubscribed = false; }
        if (_drawing != null) { ElementCompositionPreview.SetElementChildVisual(_host, null); _drawing = null; }
    }

    /// <summary>Retiles when the host changes size.</summary>
    private void SizeChanged(object sender, SizeChangedEventArgs args) => Update();

    /// <summary>Preserves one source pixel per screen pixel when the owner changes monitors.</summary>
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Update();

    /// <summary>Switches grain ownership when the map or a dialog replaces a solid brush with native Acrylic.</summary>
    private void BackgroundChanged(DependencyObject sender, DependencyProperty property) => Update();

    /// <summary>Marshals a system contrast change onto the owning UI thread.</summary>
    private void ContrastChanged(AccessibilitySettings sender, object args) => _host.DispatcherQueue.TryEnqueue(Update);

    /// <summary>Removes decorative transparency when Windows disables advanced visual effects.</summary>
    private void EffectsChanged(UISettings sender, object args) => _host.DispatcherQueue.TryEnqueue(Update);

    /// <summary>Keeps the native solid or acrylic fallback intact and hides all decoration in accessibility modes.</summary>
    private void Update()
    {
        if (_root == null) { return; }
        if (_contrast.HighContrast || !_effects.AdvancedEffectsEnabled)
        {
            if (_drawing != null) { _drawing.Layer.IsVisible = false; }
            return;
        }
        var size = new Vector2((float)_host.ActualWidth, (float)_host.ActualHeight);
        if (size.X <= 0 || size.Y <= 0) { return; }
        if (_drawing == null)
        {
            if (ElementCompositionPreview.GetElementChildVisual(_host) != null) { return; }
            _drawing = new Drawing(ElementCompositionPreview.GetElementVisual(_host).Compositor);
            ElementCompositionPreview.SetElementChildVisual(_host, _drawing.Layer);
        }
        var radius = _host switch { Border border => border.CornerRadius.TopLeft, Control control => control.CornerRadius.TopLeft, _ => 0 };
        _drawing.Layer.IsVisible = true;
        var nativeGrain = (_backgroundProperty != null && _material.GetValue(_backgroundProperty) is AcrylicBrush) ||
            _material is MenuFlyoutPresenter { SystemBackdrop: DesktopAcrylicBackdrop };
        _drawing.Arrange(size, (float)_root.RasterizationScale, (float)radius, nativeGrain);
    }

    /// <summary>Shares one decoded transparent texture across every tile and places the feather along the surface edges.</summary>
    private sealed class Drawing
    {
        private readonly Compositor _compositor;
        private readonly ContainerVisual _tiles;
        private readonly List<SpriteVisual> _sprites = [];
        private readonly CompositionSurfaceBrush _brush;
        private readonly CompositionRoundedRectangleGeometry _clip;
        private readonly SpriteVisual _highlight;
        private readonly SpriteVisual _left;
        private readonly SpriteVisual _right;
        private readonly SpriteVisual _shadow;
        private Vector2 _size;
        private float _scale;
        public ContainerVisual Layer { get; }

        /// <summary>Creates the tile layer, rounded clip and stationary light and shade at the edges.</summary>
        public Drawing(Compositor compositor)
        {
            _compositor = compositor;
            Layer = compositor.CreateContainerVisual();
            _clip = compositor.CreateRoundedRectangleGeometry();
            Layer.Clip = compositor.CreateGeometricClip(_clip);
            _tiles = compositor.CreateContainerVisual();
            _tiles.Opacity = GrainOpacity;
            Layer.Children.InsertAtBottom(_tiles);
            _brush = compositor.CreateSurfaceBrush(Grain.Value);
            _brush.Stretch = CompositionStretch.Fill;
            _highlight = CreateEdge(Vector2.UnitY, Color.FromArgb(52, 255, 255, 255), Color.FromArgb(0, 255, 255, 255));
            _left = CreateEdge(Vector2.UnitX, Color.FromArgb(32, 255, 255, 255), Color.FromArgb(0, 255, 255, 255));
            _right = CreateEdge(Vector2.UnitX, Color.FromArgb(0, 0, 0, 0), Color.FromArgb(19, 0, 0, 0));
            _shadow = CreateEdge(Vector2.UnitY, Color.FromArgb(0, 0, 0, 0), Color.FromArgb(37, 0, 0, 0));
        }

        /// <summary>Creates a short alpha gradient over the existing material without recoloring its palette.</summary>
        private SpriteVisual CreateEdge(Vector2 direction, Color start, Color end)
        {
            var brush = _compositor.CreateLinearGradientBrush();
            brush.StartPoint = Vector2.Zero;
            brush.EndPoint = direction;
            brush.ColorStops.Add(_compositor.CreateColorGradientStop(0, start));
            brush.ColorStops.Add(_compositor.CreateColorGradientStop(1, end));
            var edge = _compositor.CreateSpriteVisual();
            edge.Brush = brush;
            Layer.Children.InsertAtTop(edge);
            return edge;
        }

        /// <summary>Places square tiles in physical pixels and clips the final row and column to the native bounds.</summary>
        public void Arrange(Vector2 size, float scale, float radius, bool nativeGrain)
        {
            // WinUI Acrylic already includes its own noise; the edge visuals are independent of the grain.
            _tiles.IsVisible = !nativeGrain;
            _clip.CornerRadius = new Vector2(radius);
            if (_size == size && _scale == scale) { return; }
            _size = size;
            _scale = scale;
            Layer.Size = _tiles.Size = _clip.Size = size;
            var side = TilePixels / scale;
            var columns = (int)Math.Ceiling(size.X / side);
            var count = columns * (int)Math.Ceiling(size.Y / side);
            while (_sprites.Count < count)
            {
                var tile = _compositor.CreateSpriteVisual();
                tile.Brush = _brush;
                _tiles.Children.InsertAtTop(tile);
                _sprites.Add(tile);
            }
            while (_sprites.Count > count)
            {
                _tiles.Children.Remove(_sprites[^1]);
                _sprites.RemoveAt(_sprites.Count - 1);
            }
            for (var index = 0; index < count; index++)
            {
                _sprites[index].Size = new Vector2(side);
                _sprites[index].Offset = new Vector3(index % columns * side, index / columns * side, 0);
            }
            _highlight.Size = new Vector2(size.X, 1.6f);
            _left.Size = _right.Size = new Vector2(1.3f, size.Y);
            _right.Offset = new Vector3(size.X - _right.Size.X, 0, 0);
            _shadow.Size = new Vector2(size.X, 3);
            _shadow.Offset = new Vector3(0, size.Y - _shadow.Size.Y, 0);
        }
    }
}
