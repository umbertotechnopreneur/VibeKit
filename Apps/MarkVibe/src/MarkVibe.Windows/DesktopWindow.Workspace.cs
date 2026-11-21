/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; bind native navigation and file commands to the workspace model
 * Modified with AI: OpenAI Codex; preserve native sidebar rows and restore source-backed outline actions
 * Human guidance: keep files, catalog state and history outside the shell
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using System.IO;
using MarkVibe.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using static MarkVibe.Core.UiStrings;

namespace MarkVibe.Windows;

public sealed partial class DesktopWindow
{
    private DesktopWorkspace? _navigationWorkspace;
    private IReadOnlyList<DocumentCard>? _navigationCards;
    private (string? Path, string? Fingerprint, string? Language) _navigationOutline;
    private TreeViewNode[] _pageNodes = [];
    private TreeViewNode[] _outlineNodes = [];
    private bool _displayingOutline;

    /// <summary>Opens the native picker for one Markdown file.</summary>
    private async void OpenFile(object sender, RoutedEventArgs args) => await RunAsync(PickFileAsync);

    /// <summary>Opens the native picker for one workspace folder.</summary>
    private async void OpenFolder(object sender, RoutedEventArgs args) => await RunAsync(PickFolderAsync);

    /// <summary>Opens the selected file and grants only its containing folder.</summary>
    private async Task PickFileAsync(CancellationToken token)
    {
        if (!_changingWorkspace && await _documentFiles.PickDocumentAsync() is { } path)
        {
            await OpenLocationAsync(path, "files", true, token);
        }
    }

    /// <summary>Opens a folder selected by the user.</summary>
    private async Task PickFolderAsync(CancellationToken token)
    {
        if (!_changingWorkspace && await _documentFiles.PickFolderAsync() is { } path)
        {
            await OpenLocationAsync(path, "folder", true, token);
        }
    }

    /// <summary>Resolves an explicit selection and records it only after it opens successfully.</summary>
    private async Task OpenLocationAsync(string path, string kind, bool rememberRecent, CancellationToken token)
    {
        var file = kind == "files";
        if (file && !WorkspacePath.IsMarkdown(path))
        {
            throw new WorkspaceException("unsupported-file", "Choose a Markdown document.");
        }
        var directory = file ? Path.GetDirectoryName(path)! : path;
        var opened = await SwitchWorkspaceAsync(directory, file ? Path.GetFileName(path) : null, null, token);
        if (opened && rememberRecent)
        {
            try { await _model.Recent.RememberAsync(path, kind, token); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                DesktopLog.Write("warning", "recent-locations.save-failed");
            }
        }
    }

    /// <summary>Replaces the active model only after pending local drafts and map saves are addressed.</summary>
    private async Task<bool> SwitchWorkspaceAsync(string directory, string? requested, HistoryEntry? visit, CancellationToken token)
    {
        if (_changingWorkspace)
        {
            return false;
        }
        _changingWorkspace = true;
        try
        {
            var previous = _model.Workspace;
            if (previous != null && requested != null && Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory))
                .Equals(_model.Files.Root(previous.Info.Id), StringComparison.OrdinalIgnoreCase))
            {
                await NavigateAsync(requested, visit?.Anchor ?? "", visit, true, token, inWorkspaceSwitch: true);
                return true;
            }
            if (!await ConfirmLeaveWorkspaceAsync(token))
            {
                return false;
            }
            _navigation?.Cancel();
            await CapturePositionAsync(token);
            SetStatus("Opening folder…");
            var readOnly = SampleDirectoryEquals(directory);
            var presented = false;
            Exception? renderingError = null;
            await _model.OpenAsync(directory, requested, visit, readOnly, PresentAsync, token);
            if (!presented)
            {
                await PresentAsync();
            }
            CatalogChanged();
            await RememberAsync(token);
            if (renderingError != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(renderingError).Throw();
            }
            SetStatus(_model.Document == null ? T("No Markdown files in this folder.") : _model.Document.Path);
            return true;

            async Task PresentAsync()
            {
                presented = true;
                if (_model.Document != null)
                {
                    CloseCompactSidebar();
                }
                DismissPreview();
                await CloseFindAsync();
                _model.SetView("read");
                _map?.Clear();
                _mapWorkspace = null;
                UpdateCurrentDocument();
                try { await RenderCurrentAsync(visit != null, token); }
                catch (Exception error) when (!token.IsCancellationRequested) { renderingError = error; }
            }
        }
        finally
        {
            _changingWorkspace = false;
        }
    }

    /// <summary>Checks for unsaved draft copies and any local map save conflict before leaving.</summary>
    private async Task<bool> ConfirmLeaveWorkspaceAsync(CancellationToken token)
    {
        if (_model.HasDrafts && !await ConfirmAsync("Unsaved Markdown drafts", "Discard unsaved Markdown drafts in this workspace?", "Discard drafts"))
        {
            return false;
        }
        return _map == null || _mapWorkspace != _model.Workspace || await _map.ConfirmLeaveAsync(token);
    }

    /// <summary>Changes document after capturing the previous reading position and cancelling superseded work.</summary>
    private async Task NavigateAsync(string path, string anchor, HistoryEntry? visit, bool forceReader, CancellationToken token,
        bool inWorkspaceSwitch = false, ReadingPosition? previewPosition = null)
    {
        if (_changingWorkspace && !inWorkspaceSwitch)
        {
            return;
        }
        _navigation?.Cancel();
        using var navigation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _navigation = navigation;
        var operation = navigation.Token;
        try
        {
            await CapturePositionAsync(operation);
            await _model.NavigateAsync(path, anchor, visit, operation);
            CloseCompactSidebar();
            if (previewPosition != null && _model.Workspace is { Document: { } document } workspace)
            {
                _model.CapturePosition(workspace.Info.Id, document.Path, previewPosition);
            }
            DismissPreview();
            await CloseFindAsync();
            if (forceReader || _model.View == "map")
            {
                _model.SetView("read");
            }
            UpdateCurrentDocument();
            await RenderCurrentAsync(visit != null || previewPosition != null, operation);
            await RememberAsync(operation);
            SetStatus(_model.Document?.Path ?? "");
        }
        finally
        {
            if (ReferenceEquals(_navigation, navigation))
            {
                _navigation = null;
            }
        }
    }

    /// <summary>Reflects the active document in native controls and attaches a contained on-disk watcher.</summary>
    private void UpdateCurrentDocument()
    {
        _watcher.Stop();
        var workspace = _model.Workspace;
        if (workspace?.Document is { } document)
        {
            try { _watcher.Start(workspace.Info.Id, document.Path, Guid.NewGuid().ToString("N"), workspace.DiskFingerprint); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or WorkspaceException)
            {
                DesktopLog.Write("warning", "document.watch-unavailable");
            }
        }
        UpdateAiTarget();
        CatalogChanged();
    }

    /// <summary>Rescans files through the model and refreshes the retained map and editor projections.</summary>
    private async void RefreshWorkspace(object sender, RoutedEventArgs args) => await RunAsync(RefreshAsync);

    /// <summary>Refreshes the authoritative catalog and reconciles the same workspace's map.</summary>
    private async Task RefreshAsync(CancellationToken token)
    {
        if (_changingWorkspace)
        {
            return;
        }
        _changingWorkspace = true;
        try
        {
            _navigation?.Cancel();
            SetStatus("Refreshing folder…");
            await _model.RefreshAsync(token);
            if (_model.Workspace is { } workspace && _model.Document == null && workspace.Catalog.Cards.FirstOrDefault() is { } first)
            {
                await _model.NavigateAsync(first.Path, "", null, token);
            }
            UpdateCurrentDocument();
            await RenderCurrentAsync(true, token);
            await RememberAsync(token);
            SetStatus("Folder refreshed.");
        }
        finally { _changingWorkspace = false; }
    }

    /// <summary>Opens a workspace's containing folder after the user selects that navigation command.</summary>
    private async void OpenParent(object sender, RoutedEventArgs args) => await RunAsync(async token =>
    {
        if (_model.Workspace is { } workspace && Directory.GetParent(_model.Files.Root(workspace.Info.Id)) is { } parent)
        {
            await OpenLocationAsync(parent.FullName, "folder", true, token);
        }
    });

    /// <summary>Reopens the last native session when automatic restoration could not complete.</summary>
    private async void ResumeWorkspace(object sender, RoutedEventArgs args) => await RunAsync(async token =>
    {
        if (_model.Files.Session is { } session)
        {
            await SwitchWorkspaceAsync(session.Root, session.Entry?.Path, session.Entry, token);
        }
    });

    /// <summary>Opens the installed sample as a read-only workspace.</summary>
    private async void OpenSample(object sender, RoutedEventArgs args) => await RunAsync(async token =>
        await SwitchWorkspaceAsync(Path.Combine(_options.AssetsDirectory, "demo"), "README.md", null, token));

    /// <summary>Identifies the bundled sample directory without relying on its title.</summary>
    private bool SampleDirectoryEquals(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))
        .Equals(Path.GetFullPath(Path.Combine(_options.AssetsDirectory, "demo")), StringComparison.OrdinalIgnoreCase);

    /// <summary>Indicates when native file mutations should be unavailable.</summary>
    private bool IsSample(DesktopWorkspace workspace) => SampleDirectoryEquals(_model.Files.Root(workspace.Info.Id));

    /// <summary>Displays native recent file and folder menus using remembered model entries.</summary>
    private void ShowRecent(object sender, RoutedEventArgs args)
    {
        var menu = new MenuFlyout();
        foreach (var (group, kind) in new[] { ("Recent files", "files"), ("Recent folders", "folder") })
        {
            var submenu = new MenuFlyoutSubItem { Text = T(group) };
            foreach (var location in _model.Recent.List(kind))
            {
                var item = new MenuFlyoutItem { Text = location.Path };
                item.Click += async (_, _) => await RunAsync(async token =>
                {
                    var selected = _model.ResolveRecent(location.Path, kind);
                    await OpenLocationAsync(selected.Path, kind, true, token);
                });
                submenu.Items.Add(item);
            }
            if (submenu.Items.Count > 0)
            {
                menu.Items.Add(submenu);
            }
        }
        var sample = new MenuFlyoutItem { Text = T("Open sample workspace"), IsEnabled = SampleButton.Visibility == Visibility.Visible };
        sample.Click += OpenSample;
        menu.Items.Add(sample);
        menu.ShowAt(RecentButton);
    }

    /// <summary>Selects the uncommitted previous history entry.</summary>
    private async void Back(object sender, RoutedEventArgs args) => await RunAsync(token => GoHistoryAsync(-1, token));

    /// <summary>Selects the retained next history entry.</summary>
    private async void Forward(object sender, RoutedEventArgs args) => await RunAsync(token => GoHistoryAsync(1, token));

    /// <summary>Restores a retained reading position in the current or an earlier granted workspace.</summary>
    private async Task GoHistoryAsync(int direction, CancellationToken token)
    {
        var entry = _model.AdjacentVisit(direction);
        if (entry == null && direction < 0 && _model.Document == null)
        {
            entry = _model.History.Current;
        }
        if (entry == null)
        {
            return;
        }
        if (_model.Workspace?.Info.Id == entry.WorkspaceId)
        {
            await NavigateAsync(entry.Path, entry.Anchor, entry, false, token);
        }
        else
        {
            await SwitchWorkspaceAsync(_model.Files.Root(entry.WorkspaceId), entry.Path, entry, token);
        }
    }

    /// <summary>Displays the workspace page hierarchy.</summary>
    private void ShowPages(object sender, RoutedEventArgs args)
    {
        _outline = false;
        UpdateTree();
    }

    /// <summary>Displays headings and source-backed content for the current document.</summary>
    private void ShowOutline(object sender, RoutedEventArgs args)
    {
        _outline = true;
        UpdateTree();
    }

    /// <summary>Updates the visible model projection while retaining existing rows and expansion state.</summary>
    private void UpdateTree()
    {
        PagesButton.IsChecked = !_outline;
        OutlineButton.IsChecked = _outline;
        AutomationProperties.SetName(NavigationTree, T(_outline ? "Current document outline" : "Workspace documents"));
        var nodes = NavigationTree.RootNodes;
        if (!ReferenceEquals(_navigationWorkspace, _model.Workspace))
        {
            _navigationWorkspace = _model.Workspace;
            _navigationCards = null;
            _navigationOutline = default;
            _pageNodes = [];
            _outlineNodes = [];
            nodes.Clear();
        }
        if (_displayingOutline != _outline)
        {
            _displayingOutline = _outline;
            nodes.Clear();
            foreach (var node in _outline ? _outlineNodes : _pageNodes)
            {
                nodes.Add(node);
            }
        }

        var document = _model.Document;
        var catalog = _navigationWorkspace?.Catalog;
        if (_outline)
        {
            var outline = (Path: document?.Path, Fingerprint: document?.Fingerprint, Language);
            if (_navigationOutline != outline)
            {
                // Element identities are scoped to a document even when two paths contain the same source.
                if (_navigationOutline.Path != outline.Path)
                {
                    nodes.Clear();
                }
                ReconcileNavigationNodes(nodes, DesktopNavigation.Outline(document), expandHeadings: true);
                _outlineNodes = nodes.ToArray();
                _navigationOutline = outline;
            }
        }
        else if (!ReferenceEquals(_navigationCards, catalog?.Cards))
        {
            _navigationCards = catalog?.Cards;
            ReconcileNavigationNodes(nodes, DesktopNavigation.Pages(catalog));
            _pageNodes = nodes.ToArray();
        }
        var anchor = _outline ? _model.History.Current?.Anchor ?? "" : "";
        var selected = document == null ? null : FindTreeNode(nodes, document.Path, anchor);
        if (!ReferenceEquals(NavigationTree.SelectedNode, selected))
        {
            for (var parent = selected?.Parent; parent != null; parent = parent.Parent)
            {
                parent.IsExpanded = true;
            }
            NavigationTree.SelectedNode = selected;
        }
    }

    /// <summary>Keeps native nodes when their semantic identity is still present in the same branch.</summary>
    private static void ReconcileNavigationNodes(IList<TreeViewNode> nodes, IReadOnlyList<DesktopNavigationItem> items, bool expandHeadings = false)
    {
        var keys = items.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        for (var index = nodes.Count - 1; index >= 0; index--)
        {
            if (NavigationItem(nodes[index]) is not { } item || !keys.Contains(item.Key))
            {
                nodes.RemoveAt(index);
            }
        }
        var kept = nodes.ToDictionary(node => NavigationItem(node)!.Key, StringComparer.Ordinal);
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (!kept.TryGetValue(item.Key, out var node))
            {
                node = new TreeViewNode { Content = CreateNavigationRow(item), IsExpanded = expandHeadings && item.Children.Count > 0 };
                nodes.Insert(index, node);
            }
            else
            {
                if (nodes[index] != node)
                {
                    nodes.Remove(node);
                    nodes.Insert(index, node);
                }
                UpdateNavigationRow((Grid)node.Content, item);
            }
            ReconcileNavigationNodes(node.Children, item.Children);
        }
    }

    /// <summary>Finds the native row for a file, semantic element or direct heading link.</summary>
    private static TreeViewNode? FindTreeNode(IEnumerable<TreeViewNode> nodes, string path, string anchor)
    {
        foreach (var node in nodes)
        {
            if (NavigationItem(node) is { IsFolder: false } item && item.Path == path &&
                (item.Target == null ? anchor.Length == 0 : anchor.Length > 0 && (item.Target.ElementId == anchor || item.Target.HeadingId == anchor)))
            {
                return node;
            }
            if (FindTreeNode(node.Children, path, anchor) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    /// <summary>Creates a native row with a fixed-width icon or heading level and a truncating title.</summary>
    private static Grid CreateNavigationRow(DesktopNavigationItem item)
    {
        var row = new Grid { ColumnSpacing = 6, Padding = new Thickness(2, 1, 6, 1) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(25) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        FrameworkElement icon = item.Target is { HeadingLevel: > 0 } target
            ? new TextBlock { Text = $"H{target.HeadingLevel}", FontSize = 12 }
            : item.Icon == "code"
                ? new FontIcon { FontFamily = new FontFamily("Segoe Fluent Icons"), Glyph = "\uE943", FontSize = 16 }
                : new SymbolIcon(NavigationSymbol(item.Icon)) { Width = 18, Height = 18 };
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
        row.Children.Add(icon);
        var label = new TextBlock
        {
            FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        UpdateNavigationRow(row, item);
        return row;
    }

    /// <summary>Uses native glyphs for the navigation kinds shared with the preceding sidebar.</summary>
    private static Symbol NavigationSymbol(string icon) => icon switch
    {
        "folder" => Symbol.Folder,
        "image" => Symbol.Pictures,
        "paragraph" => Symbol.AlignLeft,
        "table" => Symbol.ViewAll,
        _ => Symbol.Document
    };

    /// <summary>Updates captions and tooltips in place while rebinding context actions to the current projection.</summary>
    private static void UpdateNavigationRow(Grid row, DesktopNavigationItem item)
    {
        if (row.Tag is not DesktopNavigationItem previous || previous.Label != item.Label || previous.Tooltip != item.Tooltip)
        {
            ((TextBlock)row.Children[1]).Text = item.Label;
            ToolTipService.SetToolTip(row, item.Tooltip);
            AutomationProperties.SetName(row, item.Target == null ? item.Path : item.Tooltip);
        }
        row.Tag = item;
    }

    /// <summary>Gets the model associated with a tree node or its realized row.</summary>
    private static DesktopNavigationItem? NavigationItem(object? item) =>
        (item is TreeViewNode node ? node.Content : item) is Grid { Tag: DesktopNavigationItem row } ? row : null;

    /// <summary>Navigates to the selected file, rendered outline element or precise source line.</summary>
    private async void ActivateTreeItem(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (NavigationItem(args.InvokedItem) is not { IsFolder: false } item)
        {
            return;
        }
        var target = item.Target;
        if (!MatchesOutlineDocument(item))
        {
            return;
        }
        // Reader and Split consume the exact rendered element anchor. Source restores the precise parsed line.
        await RunAsync(token => NavigateAsync(item.Path, target?.ElementId ?? "", null, false, token,
            previewPosition: _model.View == "source" ? target?.Position : null));
    }

    /// <summary>Addresses the pointer row or the keyboard-focused row instead of acting on the current document.</summary>
    private void TreeContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (_model.Workspace is not { } workspace)
        {
            return;
        }
        var pointer = args.TryGetPosition(NavigationTree, out _);
        var focused = !pointer && NavigationTree.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as DependencyObject : null;
        var row = FindTreeContainer(focused ?? args.OriginalSource as DependencyObject);
        if (row == null && !pointer && NavigationTree.SelectedNode is { } selection)
        {
            row = NavigationTree.ContainerFromNode(selection) as TreeViewItem;
        }
        if (row == null || NavigationItem(NavigationTree.NodeFromContainer(row)) is not { } item)
        {
            return;
        }
        if (!MatchesOutlineDocument(item))
        {
            return;
        }
        var target = item.Target;
        ShowFileMenu(row, new(workspace.Info.Id, item.Path, item.Title, item.IsFolder, Fingerprint: target?.Fingerprint ?? "",
            SourceLine: (int)(target?.Position.SourceLine ?? 0), ElementId: target?.ElementId ?? "", HeadingId: target?.HeadingId ?? "",
            View: _model.View));
        args.Handled = true;
    }

    /// <summary>Rejects commands from outline rows whose document or effective revision has changed.</summary>
    private bool MatchesOutlineDocument(DesktopNavigationItem item) => item.Target == null ||
        _model.Document is { } document && document.Path == item.Path && document.Fingerprint == item.Target.Fingerprint;

    /// <summary>Walks only the native tree's realized visual branch to locate an item container.</summary>
    private TreeViewItem? FindTreeContainer(DependencyObject? element)
    {
        while (element != null && element != NavigationTree)
        {
            if (element is TreeViewItem row && NavigationTree.NodeFromContainer(row) != null)
            {
                return row;
            }
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    /// <summary>Writes the current native session unless it points at the installed read-only sample.</summary>
    private Task RememberAsync(CancellationToken token) =>
        _model.Workspace is { } workspace && IsSample(workspace) ? Task.CompletedTask : _model.RememberAsync(token);

    /// <summary>Prompts for an externally changed current file without overwriting a local draft.</summary>
    private void HandleDiskChange(DocumentDiskChange change) => _ = RunAsync(async token =>
    {
        if (_changingWorkspace || _showingDialog || _model.Workspace is not { Document: { } document } workspace ||
            workspace.Info.Id != change.WorkspaceId || document.Path != change.Path)
        {
            return;
        }
        if (change.Missing)
        {
            SetStatus("This file is unavailable on disk. You can still read the open revision.");
            return;
        }
        var draft = workspace.Catalog.TryGetSession(document.Path)?.NeedsSave == true;
        var message = draft ? "This file changed on disk. Reloading will discard its local Markdown draft." : "This file changed on disk. Reload the document?";
        if (await ConfirmAsync("Document changed", message, "Reload") && _model.Workspace == workspace && _model.Document == document)
        {
            await _model.ReloadAsync(token);
            UpdateCurrentDocument();
            await RenderCurrentAsync(true, token);
            SetStatus("The document was reloaded.");
        }
    });
}
