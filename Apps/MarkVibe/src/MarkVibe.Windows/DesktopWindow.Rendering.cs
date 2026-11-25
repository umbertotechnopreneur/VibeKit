/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; native shell integration with document renderers and Win2D
 * Human guidance: WebView2 is limited to the Markdown reader and Monaco editor
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using System.Text.Json;
using MarkVibe.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Windows.Foundation;

namespace MarkVibe.Windows;

public sealed partial class DesktopWindow
{
    private Task<CoreWebView2Environment>? _documentEnvironment;

    /// <summary>Shares initialization across document surfaces. A later open can retry a failed startup.</summary>
    private Task<CoreWebView2Environment> GetDocumentEnvironmentAsync()
    {
        if (_documentEnvironment == null || _documentEnvironment.IsFaulted || _documentEnvironment.IsCanceled)
        {
            _documentEnvironment = DocumentWebView.CreateEnvironmentAsync(_options);
        }
        return _documentEnvironment;
    }

    /// <summary>Overlaps WebView startup with loading an explicitly opened document. The reader handles startup errors.</summary>
    private async Task PrepareDocumentEnvironmentAsync()
    {
        try { await GetDocumentEnvironmentAsync(); }
        catch (Exception error) { DesktopLog.Error("document.environment-startup", error); }
    }

    private (DesktopWorkspace Workspace, string Path, string Fingerprint, int Start, int End, bool Unsupported)? _pendingAiSelection;

    /// <summary>Changes the selected document view through the presentation model.</summary>
    private async void SelectView(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (!_syncingViewSelector && sender.SelectedItem is { Tag: string view, IsEnabled: true } &&
            (view != _model.View || _navigation != null))
        {
            await RunAsync(token => SetViewAsync(view, token));
        }
    }

    /// <summary>Preserves reading position when moving between native and embedded views.</summary>
    private async Task SetViewAsync(string view, CancellationToken token)
    {
        if (_changingWorkspace || (view == "map" && _model.Workspace is not { InventoryReady: true }) || (view != "map" && _model.Document == null))
        {
            SyncControls();
            return;
        }
        _navigation?.Cancel();
        using var navigation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _navigation = navigation;
        try
        {
            await CapturePositionAsync(navigation.Token);
            navigation.Token.ThrowIfCancellationRequested();
            await CloseFindAsync();
            navigation.Token.ThrowIfCancellationRequested();
            DismissPreview();
            _model.SetView(view);
            SyncControls();
            await RenderCurrentAsync(true, navigation.Token);
        }
        finally
        {
            if (ReferenceEquals(_navigation, navigation))
            {
                _navigation = null;
            }
        }
    }

    /// <summary>Projects the current managed snapshot into the active native or document surface.</summary>
    private async Task RenderCurrentAsync(bool restorePosition, CancellationToken token)
    {
        var workspace = _model.Workspace;
        var view = _model.View;
        if (workspace == null)
        {
            return;
        }
        if (view == "map")
        {
            await ShowMapAsync(workspace, token);
            return;
        }
        if (workspace.Document is not { } document)
        {
            if (_reader is { IsReady: true } reader)
            {
                await reader.CallAsync("view.clear", token: token);
            }
            return;
        }

        var history = _model.History.Current;
        var renderer = Reader();
        SyncControls();
        try
        {
            await renderer.RenderAsync(workspace.Info, document, view, _model.EffectivePreferences,
                history?.Anchor ?? "", restorePosition ? history?.Position : null, _model.Layout, IsDark, token);
        }
        catch when (!renderer.IsReady && !token.IsCancellationRequested)
        {
            ReaderSlot.Children.Remove(renderer.View);
            renderer.Dispose();
            _reader = null;
            throw;
        }
    }

    /// <summary>Starts the graph only when requested and preserves it across view switches.</summary>
    private async Task ShowMapAsync(DesktopWorkspace workspace, CancellationToken token)
    {
        _model.EnsureCatalogIndex();
        if (_map == null)
        {
            var map = new WindowsMapView();
            map.Loaded += (_, _) => WindowsUiFont.ApplyTo(map);
            var preferences = _model.Preferences;
            map.SetAppearance(IsDark, preferences.Acrylic, preferences.GraphPalette, preferences.GraphFont);
            map.OpenDocument += (path, anchor) => _ = RunAsync(cancellation => NavigateAsync(path, anchor, null, true, cancellation));
            map.PreviewDocument += (path, anchor) => _ = RunAsync(cancellation => PreviewMapDocumentAsync(path, anchor, cancellation));
            map.CreateLinkMenu = target => CreateFileMenu(target, includeReaderActions: true);
            map.FileActions = target =>
            {
                if (_model.Workspace is not { } active || active.Info.Id != target.WorkspaceId)
                {
                    return [];
                }
                var localFiles = !IsSample(active) && !_options.SmokeTest;
                return DocumentCommands.CreateFileActions(localFiles, WorkspacePath.IsMarkdown(target.Path), target.IsFolder, false)
                    .Where(command => command.Enabled).ToArray();
            };
            map.FileActionRequested += (target, action) =>
            {
                if (_model.Workspace is { } active && active.Info.Id == target.WorkspaceId &&
                    map.FileActions?.Invoke(target).Any(command => command.Action == action) == true)
                {
                    _ = RunAsync(cancellation => ExecuteFileCommandAsync(active, target, action, cancellation));
                }
            };
            map.DocumentSelected += path =>
            {
                if (_previewCancellation != null || (_model.Preview is { } preview && preview.Path != path))
                {
                    DismissPreview(restoreMap: false);
                }
            };
            map.AddToAiContext += paths => _ = RunAsync(cancellation => AddAiReferencesAsync(paths, cancellation));
            map.ToggleAiContext += path => _ = RunAsync(cancellation => ToggleMapReferenceAsync(path, cancellation));
            map.PreviewPlacementChanged += QueuePreviewPlacement;
            map.InlinePreviewHostRecycled += host =>
            {
                if (ReferenceEquals(_inlinePreviewHost, host))
                {
                    ReturnPreviewToViewport();
                    PositionPreviewOverlay();
                }
            };
            map.StatusChanged += SetStatus;
            map.IsPreviewOpen = () => PreviewOverlay.Visibility == Visibility.Visible;
            map.RestoreInspectorWidth(_model.Layout.MapInspectorWidth);
            map.InspectorWidthChanged += width => _ = RunAsync(cancellation =>
                _model.SetLayoutAsync(_model.Layout with { MapInspectorWidth = width }, cancellation));
            _map = map;
            MapSlot.Children.Add(map);
        }
        if (_mapWorkspace != workspace)
        {
            await _map.LoadAsync(_model.Files, workspace.Info, workspace.Catalog, workspace.Document?.Path,
                workspace.Visits.ToArray(), _lifetime.Token).WaitAsync(token);
            if (_model.Workspace == workspace && !token.IsCancellationRequested)
            {
                _mapWorkspace = workspace;
            }
        }
        else
        {
            _map.Refresh(workspace.Document?.Path);
        }
        SyncMapReferencePaths();
    }

    /// <summary>Projects the active workspace's AI membership into the loaded map.</summary>
    private void SyncMapReferencePaths()
    {
        if (!_disposed && _mapWorkspace != null && _model.Workspace == _mapWorkspace)
        {
            _map?.SetReferencePaths(_mapWorkspace.References.Paths);
        }
    }

    /// <summary>Creates the main WebView2 on first use, keeping native startup off this path.</summary>
    private DocumentWebView Reader()
    {
        if (_reader != null)
        {
            return _reader;
        }
        var reader = CreateRenderer(preview: false);
        reader.Control.IsTabStop = _model.View != "map";
        ReaderSlot.Children.Add(reader.View);
        _reader = reader;
        return reader;
    }

    /// <summary>Creates a renderer with callbacks scoped to its native workspace model.</summary>
    private DocumentWebView CreateRenderer(bool preview)
    {
        var renderer = new DocumentWebView(_options, GetDocumentEnvironmentAsync, _lifetime.Token);
        renderer.HandleNativeRequest = (operation, args, token) => HandleRendererRequestAsync(renderer, operation, args, preview, token);
        renderer.EventReceived += data => OnRendererEvent(renderer, data, preview);
        return renderer;
    }

    /// <summary>Allows rendering-only operations over authenticated requests from the local page.</summary>
    private async Task<object?> HandleRendererRequestAsync(DocumentWebView sender, string operation, JsonElement args, bool preview, CancellationToken token)
    {
        return operation switch
        {
            "workspace.read" => await _model.ReadImageAsync(Text(args, "workspaceId"), Text(args, "path"), token),
            "export.save" when !preview => await _documentFiles.SaveExportAsync(Text(args, "format"), Text(args, "filename"),
                Text(args, "html"), sender.PrintPdfAsync, token),
            "file.browser" when !preview && !_options.SmokeTest => await _documentFiles.OpenPreviewAsync(Text(args, "html"), token),
            _ => throw new WorkspaceException("unknown-action", "This renderer operation is unavailable.")
        };
    }

    /// <summary>Translates renderer gestures into native commands only for the active document generation.</summary>
    private void OnRendererEvent(DocumentWebView renderer, JsonElement data, bool preview)
    {
        if (_disposed || _model.Workspace is not { } workspace || Text(data, "workspaceId") != workspace.Info.Id)
        {
            return;
        }
        var type = Text(data, "type");
        var path = Text(data, "path");
        var anchor = Text(data, "anchor");
        if (preview)
        {
            if (ReferenceEquals(renderer, _preview) && _model.Preview is { } page &&
                Text(data, "documentPath") == page.Path && PreviewOverlay.Visibility == Visibility.Visible)
            {
                if (type == "navigate")
                {
                    _ = RunAsync(token => PreviewMapDocumentAsync(path, anchor, token));
                }
                else if (type == "preview-close")
                {
                    DismissPreview();
                }
                else if (type == "preview-back")
                {
                    _ = RunAsync(BackMapPreviewAsync);
                }
                else if (type == "file-context" && path.Length > 0)
                {
                    ShowFileMenu(renderer.Control, new FileMenuTarget(workspace.Info.Id, path, Text(data, "title"),
                        SelectedText: Text(data, "selectedText")), new Point(Number(data, "x"), Number(data, "y")));
                }
            }
            return;
        }
        if (workspace.Document is not { } document || _model.View == "map" ||
            Text(data, "documentPath") != document.Path || Text(data, "fingerprint") != document.Fingerprint)
        {
            return;
        }

        switch (type)
        {
            case "navigate":
                _ = RunAsync(token => NavigateAsync(path, anchor, null, false, token));
                break;
            case "status":
                SetStatus(Text(data, "message"));
                break;
            case "position" when data.TryGetProperty("position", out var position):
                _model.CapturePosition(workspace.Info.Id, path, position.Deserialize<ReadingPosition>(Wire.Options) ?? new());
                break;
            case "document-selection":
                TrackSelection(data, workspace, document);
                break;
            case "document-find":
                ShowFind(Target(data, document));
                break;
            case "file-context":
                ShowFileMenu(renderer.Control, Target(data, document), new Point(Number(data, "x"), Number(data, "y")));
                break;
        }
    }

    /// <summary>Captures a validated-snapshot context for native Find and file actions.</summary>
    private static FileMenuTarget Target(JsonElement data, MarkdownModel document)
    {
        var path = Text(data, "path");
        var title = Text(data, "title");
        var targetPath = path.Length > 0 ? path : document.Path;
        var workspaceId = Text(data, "workspaceId");
        if (targetPath != document.Path)
        {
            return new(workspaceId, targetPath, title.Length > 0 ? title : targetPath);
        }
        return new(workspaceId, document.Path, document.Title, SelectedText: Text(data, "selectedText"),
            Fingerprint: document.Fingerprint, SourceLine: Integer(data, "sourceLine"), ElementId: Text(data, "elementId"),
            HeadingId: Text(data, "headingId"), View: Text(data, "view"));
    }

    /// <summary>Retains one source range until the assistant is first created and can own it.</summary>
    private void TrackSelection(JsonElement data, DesktopWorkspace workspace, MarkdownModel document)
    {
        if (data.TryGetProperty("selection", out var selection) && selection.ValueKind == JsonValueKind.Object)
        {
            var unsupported = selection.TryGetProperty("unsupported", out var value) && value.ValueKind == JsonValueKind.True;
            var start = Integer(selection, "start");
            var end = Integer(selection, "end");
            if (_ai is { } assistant)
            {
                assistant.SetSelection(document.Fingerprint, start, end, unsupported);
            }
            else
            {
                _pendingAiSelection = (workspace, document.Path, document.Fingerprint, start, end, unsupported);
            }
        }
        else
        {
            _pendingAiSelection = null;
            _ai?.ClearSelection();
        }
    }

    /// <summary>Captures a current reader or source position without retaining renderer state in the shell.</summary>
    private async Task CapturePositionAsync(CancellationToken token)
    {
        if (_reader is not { IsReady: true } reader || _model.View == "map" || _model.Workspace is not { Document: { } document } workspace)
        {
            return;
        }
        try
        {
            var position = await reader.CallAsync<ReadingPosition>("position.capture", new { view = _model.View }, token);
            _model.CapturePosition(workspace.Info.Id, document.Path, position);
        }
        catch (WorkspaceException error)
        {
            DesktopLog.Error("document.capture-failed", error);
        }
    }

    /// <summary>Reads and displays a map passage separately from the committed reading history.</summary>
    private Task PreviewMapDocumentAsync(string path, string anchor, CancellationToken token) => RunPreviewAsync(async operation =>
    {
        if (_model.View != "map" || _model.Workspace is not { } workspace || _map is not { } map)
        {
            return;
        }
        if (_model.Preview is { } current && current.Path == path && current.Anchor == anchor && _preview is { IsReady: true } renderer)
        {
            renderer.Control.Focus(FocusState.Programmatic);
            return;
        }
        var document = await _model.ReadAsync(path, operation);
        operation.ThrowIfCancellationRequested();
        if (_model.Workspace != workspace || _model.View != "map")
        {
            return;
        }
        var origin = map.CapturePreviewViewport();
        if (_model.Preview != null && await CapturePreviewPositionAsync(operation) is { } position)
        {
            _model.CapturePreview(position, origin);
        }
        operation.ThrowIfCancellationRequested();
        map.HideCompactInspector();
        map.SelectPreviewPath(document.Path);
        _model.PushPreview(document.Path, anchor, map.CapturePreviewViewport(), origin);
        await RenderPreviewAsync(workspace, document, anchor, null, true, operation);
    }, token);

    /// <summary>Shows a standalone assistant preview in the shared native preview chrome.</summary>
    private Task DisplayPreviewAsync(MarkdownModel document, string anchor, bool openDocument, CancellationToken token) => RunPreviewAsync(async operation =>
    {
        if (_model.Workspace is { } workspace)
        {
            await RenderPreviewAsync(workspace, document, anchor, null, openDocument, operation);
        }
    }, token);

    /// <summary>Cancels both the native document read and renderer call when another preview takes ownership.</summary>
    private async Task RunPreviewAsync(Func<CancellationToken, Task> action, CancellationToken token)
    {
        _previewCancellation?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(token);
        _previewCancellation = request;
        try
        {
            await action(request.Token);
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, request))
            {
                _previewCancellation = null;
            }
        }
    }

    /// <summary>Renders the captured passage with lazy media and its source-backed reading position.</summary>
    private async Task RenderPreviewAsync(DesktopWorkspace workspace, MarkdownModel document, string anchor,
        ReadingPosition? position, bool mapDocument, CancellationToken token)
    {
        if (_preview == null)
        {
            _preview = CreateRenderer(preview: true);
            PreviewSlot.Children.Add(_preview.View);
        }
        PreviewTitle.Text = document.Title;
        PreviewOverlay.Visibility = Visibility.Visible;
        PreviewOpenButton.IsEnabled = mapDocument;
        PreviewBackButton.IsEnabled = _model.CanPreviewBack;
        UpdatePreviewPlacement();
        await _preview.RenderAsync(workspace.Info, document, "read", _model.EffectivePreferences, anchor, position,
            _model.Layout, IsDark, token, deferMedia: mapDocument);
        if (mapDocument && !token.IsCancellationRequested && _model.Preview?.Path == document.Path)
        {
            _preview?.Control.Focus(FocusState.Programmatic);
        }
    }

    private ContentControl? _inlinePreviewHost;
    private bool _previewPlacementQueued;

    /// <summary>Coalesces camera, toolbar and resize notifications onto the UI queue while a preview is visible.</summary>
    private void QueuePreviewPlacement()
    {
        if (_disposed || _previewPlacementQueued || PreviewOverlay.Visibility != Visibility.Visible)
        {
            return;
        }
        _previewPlacementQueued = DispatcherQueue.TryEnqueue(() =>
        {
            _previewPlacementQueued = false;
            if (!_disposed)
            {
                UpdatePreviewPlacement();
            }
        });
    }

    /// <summary>Detects preview focus so its Back action stays separate from document history.</summary>
    private bool HasMapPreviewFocus()
    {
        if (_model.Preview == null || PreviewOverlay.Visibility != Visibility.Visible || PreviewOverlay.XamlRoot == null)
        {
            return false;
        }
        for (var element = FocusManager.GetFocusedElement(PreviewOverlay.XamlRoot) as DependencyObject;
             element != null; element = VisualTreeHelper.GetParent(element))
        {
            if (ReferenceEquals(element, PreviewOverlay)) { return true; }
        }
        return false;
    }

    /// <summary>Positions the preview beside the card on the canvas or immediately below it in the compact list.</summary>
    private void UpdatePreviewPlacement()
    {
        if (PreviewOverlay.Visibility != Visibility.Visible)
        {
            return;
        }
        if (_model.Preview is { } preview && _map?.FindInlinePreviewHost(preview.Path) is { } host)
        {
            if (!ReferenceEquals(_inlinePreviewHost, host))
            {
                ReturnPreviewToViewport();
                Viewport.Children.Remove(PreviewOverlay);
                host.Content = PreviewOverlay;
                _inlinePreviewHost = host;
            }
            PreviewOverlay.Width = double.NaN;
            PreviewOverlay.Height = Math.Clamp(Viewport.ActualHeight * .65, 220, 520);
            PreviewOverlay.HorizontalAlignment = HorizontalAlignment.Stretch;
            PreviewOverlay.VerticalAlignment = VerticalAlignment.Top;
            PreviewOverlay.Margin = new Thickness(0, 8, 0, 6);
            return;
        }
        ReturnPreviewToViewport();
        PositionPreviewOverlay();
    }

    /// <summary>Detaches an inline preview before its list container is reused.</summary>
    private void ReturnPreviewToViewport()
    {
        if (_inlinePreviewHost == null)
        {
            return;
        }
        var host = _inlinePreviewHost;
        _inlinePreviewHost = null;
        host.Content = null;
        Viewport.Children.Add(PreviewOverlay);
    }

    /// <summary>Keeps the selected card visible beside the floating reading panel when room permits.</summary>
    private void PositionPreviewOverlay()
    {
        var width = Viewport.ActualWidth;
        var height = Viewport.ActualHeight;
        if (_model.Preview is { } preview && width >= 640 && height >= 300)
        {
            var panelWidth = Math.Min(500, width * .52);
            var topMinimum = Math.Clamp(_map?.PreviewTop ?? 12, 12, height - 172);
            var panelHeight = Math.Min(500, height - topMinimum - 12);
            var left = width - panelWidth - 12;
            var top = topMinimum;
            if (_map?.TryGetPreviewCardBounds(preview.Path, Viewport, out var card) == true)
            {
                var rightOfCard = card.Right + 12;
                var leftOfCard = card.Left - panelWidth - 12;
                left = rightOfCard + panelWidth <= width - 12 ? rightOfCard : leftOfCard >= 12 ? leftOfCard
                    : card.X + card.Width / 2 > width / 2 ? 12 : left;
                top = Math.Clamp(card.Top, topMinimum, Math.Max(topMinimum, height - panelHeight - 12));
            }
            PreviewOverlay.Width = panelWidth;
            PreviewOverlay.Height = panelHeight;
            PreviewOverlay.Margin = new Thickness(left, top, 0, 0);
            PreviewOverlay.HorizontalAlignment = HorizontalAlignment.Left;
            PreviewOverlay.VerticalAlignment = VerticalAlignment.Top;
        }
        else
        {
            PreviewOverlay.Width = double.NaN;
            PreviewOverlay.Height = double.NaN;
            PreviewOverlay.Margin = new Thickness(0);
            PreviewOverlay.HorizontalAlignment = HorizontalAlignment.Stretch;
            PreviewOverlay.VerticalAlignment = VerticalAlignment.Stretch;
        }
    }

    /// <summary>Captures the renderer's stable reading block before a preview is replaced or promoted.</summary>
    private async Task<ReadingPosition?> CapturePreviewPositionAsync(CancellationToken token) => _preview is { IsReady: true } renderer
        ? await renderer.CallAsync<ReadingPosition>("position.capture", new { view = "read" }, token) : null;

    /// <summary>Calls the previous passage in the preview trail or returns to its map.</summary>
    private async void PreviewBack(object sender, RoutedEventArgs args) => await RunAsync(BackMapPreviewAsync);

    /// <summary>Restores the previous passage and camera, or dismisses the first preview.</summary>
    private Task BackMapPreviewAsync(CancellationToken token) => RunPreviewAsync(async operation =>
    {
        if (_model.PreviousPreview is { } previous && _model.Workspace is { } workspace)
        {
            var document = await _model.ReadAsync(previous.Path, operation);
            operation.ThrowIfCancellationRequested();
            _model.PopPreview();
            _map?.RestorePreviewViewport(previous.Viewport, origin: false);
            await RenderPreviewAsync(workspace, document, previous.Anchor, previous.Position, true, operation);
        }
        else { DismissPreview(); }
    }, token);

    /// <summary>Promotes a map preview to a committed full-document visit.</summary>
    private async void OpenPreviewDocument(object sender, RoutedEventArgs args) => await RunAsync(async token =>
    {
        if (_model.Preview is { } preview)
        {
            var position = await CapturePreviewPositionAsync(token);
            if (_model.Preview == preview)
            {
                await NavigateAsync(preview.Path, preview.Anchor, null, true, token, previewPosition: position);
            }
        }
    });

    /// <summary>Dismisses the inline preview overlay from its native toolbar.</summary>
    private void ClosePreview(object sender, RoutedEventArgs args) => DismissPreview();

    /// <summary>Clears preview-only state and releases the isolated web surface and its media.</summary>
    private void DismissPreview(bool restoreMap = true)
    {
        ReturnPreviewToViewport();
        var origin = _model.PreviewOrigin;
        _model.ClearPreview();
        if (origin != null)
        {
            if (restoreMap) { _map?.RestorePreviewViewport(origin, origin: true); }
            else { _map?.SelectPreviewPath(null); }
        }
        _previewCancellation?.Cancel();
        PreviewOverlay.Visibility = Visibility.Collapsed;
        if (_preview is { } preview)
        {
            PreviewSlot.Children.Remove(preview.View);
            preview.Dispose();
            _preview = null;
        }
        if (restoreMap && origin != null)
        {
            _map?.FocusAfterPreview();
        }
    }

    private bool IsDark => Shell.ActualTheme == ElementTheme.Dark;

    /// <summary>Extracts text from local renderer JSON without accepting coercion.</summary>
    private static string Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";

    /// <summary>Extracts a finite renderer coordinate, defaulting to zero when absent.</summary>
    private static double Number(JsonElement value, string name) => value.TryGetProperty(name, out var item) &&
        item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var number) && double.IsFinite(number) ? number : 0;

    /// <summary>Extracts a native-width integer from a renderer event.</summary>
    private static int Integer(JsonElement value, string name) => value.TryGetProperty(name, out var item) &&
        item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var number) ? number : 0;
}
