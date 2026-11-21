/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; WinUI menus, find, layout and assistant bindings
 * Human guidance: keep actions and data outside the desktop's visual controls
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using System.Globalization;
using System.Text.Json;
using MarkVibe.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using static MarkVibe.Core.UiStrings;

namespace MarkVibe.Windows;

public sealed partial class DesktopWindow
{
    /// <summary>Opens a native menu from the data layer's applicable file and document commands.</summary>
    private void ShowFileMenu(FrameworkElement anchor, FileMenuTarget target, Point? position = null)
    {
        if (CreateFileMenu(target) is not { } menu)
        {
            return;
        }
        if (position is { } point)
        {
            menu.ShowAt(anchor, new FlyoutShowOptions { Position = point });
        }
        else
        {
            menu.ShowAt(anchor);
        }
    }

    /// <summary>Composes native file and reader actions for the current workspace at the moment the menu opens.</summary>
    private MenuFlyout? CreateFileMenu(FileMenuTarget target, bool includeReaderActions = false)
    {
        if (_model.Workspace is not { } workspace || workspace.Info.Id != target.WorkspaceId)
        {
            return null;
        }
        var local = !IsSample(workspace) && !_options.SmokeTest;
        var contextual = includeReaderActions || target.Fingerprint.Length > 0 || target.HeadingId.Length > 0;
        var current = contextual && workspace.Document?.Path == target.Path ? workspace.Document : null;
        var descriptors = DocumentCommands.Create(target, local, current, _model.Layout.ReaderExpanded);
        var menu = new MenuFlyout();
        var groups = new Dictionary<string, MenuFlyoutSubItem>(StringComparer.Ordinal);
        foreach (var command in descriptors)
        {
            MenuFlyoutItem item = command.Action is "column" or "expandedColumn"
                ? new ToggleMenuFlyoutItem { IsChecked = command.Checked }
                : new MenuFlyoutItem();
            item.Text = T(command.Label);
            item.IsEnabled = command.Enabled;
            item.Click += async (_, _) => await RunAsync(token => ExecuteFileCommandAsync(workspace, target, command.Action, token));
            if (command.Group.Length == 0)
            {
                menu.Items.Add(item);
            }
            else
            {
                if (!groups.TryGetValue(command.Group, out var group))
                {
                    group = new MenuFlyoutSubItem { Text = T(command.Group) };
                    groups.Add(command.Group, group);
                    menu.Items.Add(group);
                }
                group.Items.Add(item);
            }
        }
        if (includeReaderActions || current != null)
        {
            var appearance = new MenuFlyoutItem { Text = T("Reader appearance") };
            appearance.Click += async (_, _) => await RunAsync(ReaderAppearanceAsync);
            menu.Items.Add(appearance);
        }
        if (!target.IsFolder && WorkspacePath.IsMarkdown(target.Path) && target.Path != workspace.Document?.Path)
        {
            var reference = new MenuFlyoutItem { Text = T("Add to AI context") };
            reference.Click += async (_, _) => await RunAsync(token => AddAiReferencesAsync([target.Path], token));
            menu.Items.Add(reference);
        }
        return menu;
    }

    /// <summary>Executes the captured native command while validating its workspace and optional document revision.</summary>
    private async Task ExecuteFileCommandAsync(DesktopWorkspace workspace, FileMenuTarget target, string action, CancellationToken token)
    {
        if (_model.Workspace != workspace)
        {
            return;
        }
        var document = workspace.Document;
        var current = document?.Path == target.Path && (target.Fingerprint.Length == 0 || document.Fingerprint == target.Fingerprint)
            ? document : null;
        if (target.Fingerprint.Length > 0 && current == null)
        {
            return;
        }

        switch (action)
        {
            case "copySelection": Copy(target.SelectedText); break;
            case "copyPath": Copy(IsSample(workspace) ? target.Path : _model.Files.ResolveLocation(workspace.Info.Id, target.Path)); break;
            case "copyFile":
                var content = current ?? await _model.ReadAsync(target.Path, token);
                if (_model.Workspace == workspace) { Copy(content.Source); }
                break;
            case "copySection" when current != null: Copy(DocumentContentService.CopySectionLink(current, target)); break;
            case "copyTableTsv" when current != null: Copy(DocumentContentService.CopyTableTsv(current, target)); break;
            case "copyTableMarkdown" when current != null: Copy(DocumentContentService.CopyTableMarkdown(current, target)); break;
            case "copyCode" when current != null: Copy(DocumentContentService.CopyCode(current, target)); break;
            case "browser":
                var selected = current ?? await _model.ReadAsync(target.Path, token);
                if (_model.Workspace == workspace) { await ExportAsync(workspace, selected, "browser", token); }
                break;
            case "reveal":
            case "terminal": DesktopFileActions.OpenLocation(_model.Files, workspace.Info.Id, target.Path, action == "terminal"); break;
            case "delete" when !target.IsFolder && !IsSample(workspace): await DeleteDocumentAsync(workspace, target.Path, token); break;
            case "find":
                if (_model.View == "map") { await SetViewAsync("read", token); }
                if (_model.Workspace == workspace) { ShowFind(target); }
                break;
            case "column":
            case "expandedColumn":
                await _model.SetLayoutAsync(_model.Layout with { ReaderExpanded = action == "expandedColumn" }, token);
                await RefreshRendererAppearanceAsync(token);
                break;
            case "sourceHere" when current != null:
                var sourceLine = DocumentContentService.Describe(current, target).SourceLine;
                if (sourceLine > 0)
                {
                    await SetViewAsync("source", token);
                    var position = new ReadingPosition(SourceLine: sourceLine);
                    await Reader().CallAsync("position.restore", new { view = "source", position }, token);
                    _model.CapturePosition(target.WorkspaceId, target.Path, position);
                }
                break;
            case "exportPdf" when current != null: await ExportAsync(workspace, current, "pdf", token); break;
            case "exportHtml" when current != null: await ExportAsync(workspace, current, "html", token); break;
        }
    }

    /// <summary>Requests an explicit, revision-checked move of one local file to the Recycle Bin.</summary>
    private async Task DeleteDocumentAsync(DesktopWorkspace workspace, string path, CancellationToken token)
    {
        var revision = DesktopRecycleBin.Revision(_model.Files, workspace.Info.Id, path);
        var draft = workspace.Catalog.TryGetSession(path)?.NeedsSave == true;
        var message = T("Move this file to the Recycle Bin?") + "\n\n" + path;
        if (draft) { message += "\n\n" + T("Its unsaved Markdown draft will be discarded."); }
        if (!await ConfirmAsync("Delete file", message, "Delete") || _model.Workspace != workspace)
        {
            return;
        }
        token.ThrowIfCancellationRequested();
        DesktopRecycleBin.Delete(_model.Files, workspace.Info.Id, path, revision);
        workspace.Catalog.ForgetDeletedDocument(path);
        workspace.References.Remove(path);
        await RefreshAsync(token);
        SetStatus("File moved to the Recycle Bin.");
    }

    /// <summary>Copies an explicit text payload through the Windows clipboard.</summary>
    private static void Copy(string text)
    {
        if (text.Length == 0) { return; }
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    /// <summary>Opens the native export choices for the current document snapshot.</summary>
    private void ShowExport(object sender, RoutedEventArgs args)
    {
        if (_model.Workspace is not { Document: { } document } workspace)
        {
            return;
        }
        var menu = new MenuFlyout();
        foreach (var format in new[] { "pdf", "html" })
        {
            var item = new MenuFlyoutItem { Text = format.ToUpperInvariant() };
            item.Click += async (_, _) => await RunAsync(token => ExportAsync(workspace, document, format, token));
            menu.Items.Add(item);
        }
        var copy = new MenuFlyoutItem { Text = T("Save Markdown copy"), IsEnabled = workspace.Catalog.TryGetSession(document.Path)?.IsModified == true };
        copy.Click += async (_, _) => await RunAsync(SaveDraftAsync);
        menu.Items.Add(copy);
        menu.ShowAt(ExportButton);
    }

    /// <summary>Prepares the selected managed snapshot in the document renderer and reports completion.</summary>
    private async Task ExportAsync(DesktopWorkspace workspace, MarkdownModel document, string format, CancellationToken token)
    {
        if (_model.Workspace != workspace)
        {
            return;
        }
        var result = await Reader().CallAsync<JsonElement>(format == "browser" ? "document.browser" : "document.export",
            new { workspace = workspace.Info, document, preferences = _model.EffectivePreferences, format }, token);
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("saved", out var saved) && saved.ValueKind == JsonValueKind.True)
        {
            SetStatus(T("Exported") + " " + Text(result, "filename"));
        }
    }

    /// <summary>Shows native Find for the current reader or captured source surface.</summary>
    private void OpenFind(object sender, RoutedEventArgs args) => ShowFind();

    /// <summary>Initializes the intermediate find state before showing native controls.</summary>
    private void ShowFind(FileMenuTarget? target = null)
    {
        if (_model.View == "map" || _model.Workspace is not { Document: { } document } workspace)
        {
            return;
        }
        var find = _model.Find;
        find.Target = target ?? new(workspace.Info.Id, document.Path, document.Title, Fingerprint: document.Fingerprint, View: _model.View);
        find.View = find.Target.View == "source" ? "source" : "read";
        FindBar.Visibility = Visibility.Visible;
        if (target?.SelectedText is { Length: > 0 and <= 256 } selection && !selection.Contains('\n'))
        {
            FindQuery.Text = selection;
        }
        FindQuery.Focus(FocusState.Programmatic);
        FindQuery.SelectAll();
    }

    /// <summary>Debounces native text input while the shared renderer performs bounded matching.</summary>
    private async void FindQueryChanged(object sender, TextChangedEventArgs args)
    {
        if (_model.Find.IsOpen)
        {
            _model.Find.Query = FindQuery.Text;
            await RunAsync(token => SearchAsync("query", token));
        }
    }

    /// <summary>Moves to the next or previous result from the native find controls.</summary>
    private async void FindKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Enter)
        {
            args.Handled = true;
            await RunAsync(token => SearchAsync("next", token));
        }
    }

    /// <summary>Moves to the previous match.</summary>
    private async void FindPrevious(object sender, RoutedEventArgs args) => await RunAsync(token => SearchAsync("previous", token));

    /// <summary>Moves to the next match.</summary>
    private async void FindNext(object sender, RoutedEventArgs args) => await RunAsync(token => SearchAsync("next", token));

    /// <summary>Queries the current renderer target and rejects stale results.</summary>
    private async Task SearchAsync(string step, CancellationToken token)
    {
        var find = _model.Find;
        var target = find.Target;
        if (target == null || _reader is not { IsReady: true } reader)
        {
            return;
        }
        _find?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _find = cancellation;
        try
        {
            var revision = ++find.Revision;
            if (step == "query") { await Task.Delay(120, cancellation.Token); }
            var direction = step == "previous" ? -1 : step == "next" ? 1 : 0;
            var result = await reader.CallAsync<DocumentFindResult>("document.find",
                new { target.WorkspaceId, target.Path, query = find.Query, direction, view = find.View }, cancellation.Token);
            if (find.Revision == revision)
            {
                find.Total = result.Total;
                find.Current = result.Current;
                FindCount.Text = find.Query.Length == 0 ? "" : find.Total > 0 ? $"{find.Current} / {find.Total}" : T("No matches");
            }
        }
        finally
        {
            if (ReferenceEquals(_find, cancellation)) { _find = null; }
        }
    }

    /// <summary>Closes native Find and releases its highlight in the shared renderer.</summary>
    private async void CloseFind(object sender, RoutedEventArgs args) => await RunAsync(_ => CloseFindAsync());

    /// <summary>Clears the captured find target and any renderer-side highlight.</summary>
    private async Task CloseFindAsync()
    {
        var target = _model.Find.Target;
        _find?.Cancel();
        _model.Find.Target = null;
        _model.Find.Query = "";
        _model.Find.Revision++;
        FindBar.Visibility = Visibility.Collapsed;
        FindCount.Text = "";
        FindQuery.Text = "";
        if (target != null && _reader is { IsReady: true } reader)
        {
            await reader.CallAsync("document.find", new { target.WorkspaceId, target.Path, query = "", direction = 0, view = target.View }, _lifetime.Token);
        }
    }

    /// <summary>Shows native Settings and publishes changed preferences through the model.</summary>
    private async void ShowSettings(object sender, RoutedEventArgs args) => await RunAsync(SettingsAsync);

    /// <summary>Opens global preferences with a model-backed live apply callback.</summary>
    private async Task SettingsAsync(CancellationToken token)
    {
        await ShowDialogAsync(() => WindowsPreferencesDialog.ShowAsync(Shell.XamlRoot, _model.Preferences, async value =>
        {
            var simpleChanged = value.SimpleSource != _model.Preferences.SimpleSource;
            await _model.SetPreferencesAsync(value, token);
            SetLanguage(value.Language, CultureInfo.CurrentUICulture.Name);
            ApplyPresentation();
            UpdateTree();
            UpdateAiTarget();
            if (simpleChanged && _model.View is "source" or "split") { await RenderCurrentAsync(true, token); }
            else { await RefreshRendererAppearanceAsync(token); }
        }, _connections, _options, token));
        if (_model.Layout.AiOpen && !_options.SmokeTest)
        {
            await _ai.RefreshAvailabilityAsync();
        }
    }

    /// <summary>Handles workspace-scoped reader appearance without rewriting global preferences.</summary>
    private Task ReaderAppearanceAsync(CancellationToken token) => ShowDialogAsync(() =>
        WindowsPreferencesDialog.ShowReaderAsync(Shell.XamlRoot, _model.EffectivePreferences, _model.HasFolderAppearance, async appearance =>
        {
            await _model.SetFolderAppearanceAsync(appearance, token);
            await RefreshRendererAppearanceAsync(token);
        }, token));

    /// <summary>Updates the renderer's appearance while keeping its reading position.</summary>
    private async Task RefreshRendererAppearanceAsync(CancellationToken token)
    {
        var appearance = new { preferences = _model.EffectivePreferences, layout = _model.Layout, dark = IsDark };
        if (_reader is { IsReady: true } reader) { await reader.CallAsync("appearance.apply", appearance, token); }
        if (_preview is { IsReady: true } preview)
        {
            await preview.CallAsync("appearance.apply", new { appearance.preferences, appearance.layout, dark = _model.Preview != null || IsDark }, token);
        }
    }

    /// <summary>Shows native product information and legal links.</summary>
    private async void ShowAbout(object sender, RoutedEventArgs args) => await RunAsync(AboutAsync);

    /// <summary>Opens About without sharing diagnostics until its explicit button is selected.</summary>
    private Task AboutAsync(CancellationToken token) => ShowDialogAsync(() => WindowsAboutDialog.ShowAsync(Shell.XamlRoot,
        _options.SmokeTest ? null : () => Task.Run(DesktopLog.EmailSupport, token)));

    /// <summary>Toggles the transient compact navigator or persists the wide docked sidebar preference.</summary>
    private async void ToggleSidebar(object sender, RoutedEventArgs args) => await RunAsync(async token =>
    {
        if (_compactSidebar)
        {
            _compactSidebarOpen = !_compactSidebarOpen;
            ApplyGeometry();
            return;
        }
        await _model.SetLayoutAsync(_model.Layout with { SidebarOpen = !_model.Layout.SidebarOpen }, token);
        ApplyGeometry();
    });

    /// <summary>Previews sidebar resizing until the native grip is released.</summary>
    private void DragSidebar(object sender, DragDeltaEventArgs args)
    {
        _dragWidth = Math.Clamp(SidebarColumn.Width.Value + args.HorizontalChange, 180, Math.Max(180, Math.Min(520, Shell.ActualWidth * 0.5)));
        SidebarColumn.Width = new GridLength(_dragWidth.Value);
    }

    /// <summary>Commits the final sidebar width to the intermediate layout model.</summary>
    private async void FinishSidebarDrag(object sender, DragCompletedEventArgs args) => await RunAsync(async token =>
    {
        if (_dragWidth is { } width) { await _model.SetLayoutAsync(_model.Layout with { SidebarWidth = width }, token); }
        _dragWidth = null;
        ApplyGeometry();
    });

    /// <summary>Previews the native assistant pane width during dragging.</summary>
    private void DragAi(object sender, DragDeltaEventArgs args)
    {
        _dragWidth = Math.Clamp(AiColumn.Width.Value - args.HorizontalChange, 300, Math.Max(300, Math.Min(600, Shell.ActualWidth * 0.55)));
        AiColumn.Width = new GridLength(_dragWidth.Value);
    }

    /// <summary>Commits the final assistant width to the intermediate layout model.</summary>
    private async void FinishAiDrag(object sender, DragCompletedEventArgs args) => await RunAsync(async token =>
    {
        if (_dragWidth is { } width) { await _model.SetLayoutAsync(_model.Layout with { AiWidth = width }, token); }
        _dragWidth = null;
        ApplyGeometry();
    });

    /// <summary>Resizes the two embedded document surfaces using a native WinUI grip.</summary>
    private void DragSplit(object sender, DragDeltaEventArgs args)
    {
        if (Viewport.ActualWidth <= 0 || _reader is not { IsReady: true } reader)
        {
            return;
        }
        var ratio = Math.Clamp((_dragSourceRatio ?? _model.Layout.SourceRatio) + args.HorizontalChange / Viewport.ActualWidth, 0.2, 0.8);
        _dragSourceRatio = ratio;
        SetSplitGeometry(ratio);
        _ = RunAsync(token => reader.CallAsync("layout.split", new { ratio }, token));
    }

    /// <summary>Stores the completed Split width without writing preferences during a drag.</summary>
    private async void FinishSplitDrag(object sender, DragCompletedEventArgs args) => await RunAsync(async token =>
    {
        if (_dragSourceRatio is { } ratio && !args.Canceled)
        {
            await _model.SetLayoutAsync(_model.Layout with { SourceRatio = ratio }, token);
        }
        _dragSourceRatio = null;
        SetSplitGeometry(_model.Layout.SourceRatio);
        if (_reader is { IsReady: true } reader)
        {
            await reader.CallAsync("layout.split", new { ratio = _model.Layout.SourceRatio }, token);
        }
    });

    /// <summary>Aligns the native grip with the source-to-reader boundary in the rendering viewport.</summary>
    private void SetSplitGeometry(double ratio)
    {
        SplitSourceColumn.Width = new GridLength(ratio, GridUnitType.Star);
        SplitReaderColumn.Width = new GridLength(1 - ratio, GridUnitType.Star);
    }

    /// <summary>Opens or closes the assistant in the native window.</summary>
    private async void ToggleAi(object sender, RoutedEventArgs args) => await RunAsync(token => SetAssistantOpenAsync(!_model.Layout.AiOpen, token));

    /// <summary>Constructs and activates assistant UI only when requested or restored.</summary>
    private async Task SetAssistantOpenAsync(bool open, CancellationToken token)
    {
        if (open) { EnsureAssistantPanel(); }
        await _model.SetLayoutAsync(_model.Layout with { AiOpen = open }, token);
        ApplyGeometry();
        if (open && !_firstAiOpen)
        {
            _firstAiOpen = true;
            await _ai.RefreshAvailabilityAsync();
        }
    }

    /// <summary>Connects native assistant controls to model commands without putting data into the window.</summary>
    private void EnsureAssistantPanel()
    {
        if (_assistantPanel != null) { return; }
        var panel = new WindowsAiPanel(_ai);
        panel.SettingsRequested += () => _ = RunAsync(SettingsAsync);
        panel.UndoRequested += () => _ = RunAsync(token => StepRevisionAsync(DocumentRevisionAction.Undo, token));
        panel.RedoRequested += () => _ = RunAsync(token => StepRevisionAsync(DocumentRevisionAction.Redo, token));
        panel.SaveCopyRequested += () => _ = RunAsync(SaveDraftAsync);
        panel.RestoreRequested += () => _ = RunAsync(RestoreOriginalAsync);
        panel.CloseRequested += () => _ = RunAsync(token => SetAssistantOpenAsync(false, token));
        panel.AddCurrentReferenceRequested += () => _ = RunAsync(async token =>
        {
            token.ThrowIfCancellationRequested();
            if (_model.Document is { } current)
            {
                await _ai.AddReferencesAsync([current.Path]);
            }
        });
        panel.PreviewRequested += document => _ = RunAsync(token =>
        {
            DismissPreview();
            return DisplayPreviewAsync(document, "", false, token);
        });
        panel.OpenReferenceRequested += path => _ = RunAsync(token => NavigateAsync(path, "", null, true, token));
        AiSlot.Children.Add(panel);
        _assistantPanel = panel;
        UpdateAiTarget();
    }

    /// <summary>Shares the current target snapshot with the assistant's independent session model.</summary>
    private void UpdateAiTarget()
    {
        var target = _model.Workspace is { Document: { } document } workspace
            ? new WindowsAiTarget(workspace.Info, document, workspace.Catalog, workspace.Provider, workspace.References) : null;
        _ai.SetTarget(_model.Preferences.AiProvider, target);
    }

    /// <summary>Adds selected map or navigation documents to the AI context without sending a request.</summary>
    private async Task AddAiReferencesAsync(IReadOnlyList<string> paths, CancellationToken token)
    {
        var workspace = _model.Workspace;
        await SetAssistantOpenAsync(true, token);
        if (workspace != null && _model.Workspace == workspace)
        {
            await _ai.AddReferencesAsync(paths);
        }
    }

    /// <summary>Adds or removes one document from the active workspace's assistant context.</summary>
    private Task ToggleMapReferenceAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_model.Workspace?.References.Paths.Contains(path, StringComparer.Ordinal) == true)
        {
            _ai.RemoveReference(path);
            return Task.CompletedTask;
        }
        return AddAiReferencesAsync([path], token);
    }

    /// <summary>Applies a reviewed AI result only to its still-current workspace and document revision.</summary>
    private async Task ApplyAiAsync(WindowsAiTarget target, string source, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_model.Workspace is not { } workspace || workspace.Catalog != target.Catalog || workspace.Info.Id != target.Workspace.Id)
        {
            throw new WorkspaceException("stale-document", "The original workspace is no longer open.");
        }
        await CapturePositionAsync(token);
        _model.ApplyRevision(workspace, target.Document, source, DocumentRevisionAction.Edit);
        DismissPreview();
        UpdateCurrentDocument();
        await RenderCurrentAsync(true, token);
    }

    /// <summary>Updates the visible document after a local AI revision undo or redo.</summary>
    private async Task StepRevisionAsync(DocumentRevisionAction action, CancellationToken token)
    {
        await CapturePositionAsync(token);
        _model.StepRevision(action);
        UpdateCurrentDocument();
        await RenderCurrentAsync(true, token);
    }

    /// <summary>Restores the captured original in memory after confirming the local draft reset.</summary>
    private async Task RestoreOriginalAsync(CancellationToken token)
    {
        var document = _model.Document;
        if (document != null && await ConfirmAsync("Restore original Markdown",
            "Replace the local revision with the original source and clear its undo history?", "Restore") && _model.Document == document)
        {
            await StepRevisionAsync(DocumentRevisionAction.Restore, token);
        }
    }

    /// <summary>Saves an explicitly approved copy and records the exported revision in the catalog.</summary>
    private async Task SaveDraftAsync(CancellationToken token)
    {
        if (_model.Workspace is { Document: { } document } workspace && await _documentFiles.SaveCopyAsync(workspace.Info.Id, document, token))
        {
            _model.MarkExported(workspace, document.Path, document.Source);
            UpdateAiTarget();
            SetStatus("Markdown copy saved.");
        }
    }
}
