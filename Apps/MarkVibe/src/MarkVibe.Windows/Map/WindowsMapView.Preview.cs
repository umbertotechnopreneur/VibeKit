/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; restore graph camera and selection around preview navigation
 * Human guidance: keep map reading independent of the saved layout and reading history
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using MarkVibe.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace MarkVibe.Windows;

/// <summary>Captures the map's transient selection, graph camera and compact scroll position.</summary>
internal sealed record MapPreviewViewport(string SelectedPath, MapCamera Camera, double? CompactOffset);

internal sealed partial class WindowsMapView
{
    public event Action<ContentControl>? InlinePreviewHostRecycled;
    public event Action? PreviewPlacementChanged;

    /// <summary>Returns focus to the graph or compact row that owned the preview.</summary>
    public void FocusAfterPreview()
    {
        if (_compact.Visibility != Visibility.Visible)
        {
            _canvas.FocusAfterPreview();
        }
        else if (_model?.Document(_model.SelectedPath) is { } card && _compact.ContainerFromItem(card) is ListViewItem row)
        {
            row.Focus(FocusState.Programmatic);
        }
        else
        {
            _compact.Focus(FocusState.Programmatic);
        }
    }

    /// <summary>Captures the viewport without mutating the map or saving a viewpoint.</summary>
    public MapPreviewViewport CapturePreviewViewport() => new(_model?.SelectedPath ?? "", _canvas.CaptureCamera(),
        _compact.Visibility == Visibility.Visible ? FindScrollViewer(_compact)?.VerticalOffset : null);

    /// <summary>Reveals a preview target temporarily without recording a visit or changing persisted folder state.</summary>
    public void SelectPreviewPath(string? path)
    {
        if (_model is { } model)
        {
            model.SetPreviewPath(path);
            RenderModel();
            if (path != null && _canvas.Visibility == Visibility.Visible && !_canvas.TryGetCardViewportBounds(path, out _))
            {
                _canvas.Center(path);
            }
        }
    }

    /// <summary>Restores a previous preview's camera and compact scroll after its selection is applied.</summary>
    public void RestorePreviewViewport(MapPreviewViewport viewport, bool origin)
    {
        if (_model is not { } model)
        {
            return;
        }
        model.SetPreviewPath(origin ? null : viewport.SelectedPath);
        if (origin)
        {
            model.Select(viewport.SelectedPath);
        }
        RenderModel();
        _canvas.SetCamera(viewport.Camera);
        if (viewport.CompactOffset is { } offset)
        {
            FindScrollViewer(_compact)?.ChangeView(null, offset, null, disableAnimation: true);
        }
    }

    /// <summary>Gets a visible card rectangle in the preview container's coordinate space.</summary>
    public bool TryGetPreviewCardBounds(string path, UIElement relativeTo, out Rect bounds)
    {
        if (_canvas.Visibility == Visibility.Visible && _canvas.TryGetCardViewportBounds(path, out var canvasBounds))
        {
            bounds = _canvas.TransformToVisual(relativeTo).TransformBounds(canvasBounds);
            return true;
        }
        bounds = default;
        return false;
    }

    /// <summary>Finds the document's native inline row host, realizing a scrolled-out target if necessary.</summary>
    public ContentControl? FindInlinePreviewHost(string path)
    {
        if (_compact.Visibility != Visibility.Visible || _model?.Document(path) is not { } card)
        {
            return null;
        }
        if (_compact.ContainerFromItem(card) is ListViewItem row && InlineHost(row) is { } realized)
        {
            return realized;
        }
        _compact.ScrollIntoView(card);
        _compact.UpdateLayout();
        return _compact.ContainerFromItem(card) is ListViewItem scrolled ? InlineHost(scrolled) : null;
    }

    /// <summary>Locates the inline reader slot in the compact row's shallow template.</summary>
    private static ContentControl? InlineHost(ListViewItem row) =>
        row.ContentTemplateRoot is Panel template ? template.Children.OfType<ContentControl>().FirstOrDefault(IsInlineHost) : null;

    /// <summary>Identifies the dedicated reader host within a compact row.</summary>
    private static bool IsInlineHost(ContentControl element) => element.Name == "MapInlinePreviewHost";

    /// <summary>Finds the native list's scroll presenter once the compact view has a template.</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject element)
    {
        if (element is ScrollViewer viewer)
        {
            return viewer;
        }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(element, index)) is { } child)
            {
                return child;
            }
        }
        return null;
    }
}
