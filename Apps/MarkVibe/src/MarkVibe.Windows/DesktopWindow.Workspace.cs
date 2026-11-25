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
    private (string? Path, string? Fingerprint, string? Language) _navigationOutline;
    private (string Path, string Anchor, TreeViewNode? Node)? _outlineSelection;
    private PageTreeState _pagesTree = new();
    private PageTreeState _pageMatchesTree = new();
    private PageTreeState? _displayedPages;
    private TreeViewNode[] _outlineNodes = [];
    private bool _navigationRefreshPending = true;

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
            if (_documentEnvironment == null && requested != null && WorkspacePath.IsMarkdown(requested))
            {
                _ = PrepareDocumentEnvironmentAsync();
            }
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
        if (_model.Workspace is { Info.Capabilities.CanOpenParent: true } workspace && !IsSample(workspace) &&
            Directory.GetParent(_model.Files.Root(workspace.Info.Id)) is { } parent)
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
        WindowsPaperSurface.AttachFlyout(menu);
        foreach (var (group, kind) in new[] { ("Recent files", "files"), ("Recent folders", "folder") })
        {
            var submenu = new MenuFlyoutSubItem { Text = T(group), Icon = MenuIcon(kind == "folder" ? "reveal" : "copyFile") };
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
        var sample = new MenuFlyoutItem { Text = T("Open sample workspace"), Icon = MenuIcon("sample"), IsEnabled = SampleButton.Visibility == Visibility.Visible };
        sample.Click += OpenSample;
        menu.Items.Add(sample);
        WindowsUiFont.ApplyTo(menu);
        menu.ShowAt(ShellMenuButton);
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

    /// <summary>Switches the shared tree between workspace paths and the current document's outline.</summary>
    private void SelectNavigationMode(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem?.Tag is string mode && _outline != (mode == "outline"))
        {
            _outline = mode == "outline";
            if (!_outline && _outlineFindActive) { _ = RunAsync(_ => CloseFindAsync()); }
            RefreshOutlineSearch();
            if (_loaded)
            {
                UpdateTree();
            }
        }
    }

    /// <summary>Updates the visible model projection while retaining existing rows and expansion state.</summary>
    private void UpdateTree()
    {
        if (!ReferenceEquals(_navigationWorkspace, _model.Workspace))
        {
            _navigationWorkspace = _model.Workspace;
            ResetPageSearch();
            _navigationOutline = default;
            _outlineSelection = null;
            _pagesTree = new();
            _pageMatchesTree = new();
            _displayedPages = null;
            _outlineNodes = [];
            NavigationTree.RootNodes.Clear();
        }
        _navigationRefreshPending = Sidebar.Visibility != Visibility.Visible;
        SyncPageSearch();
        if (_navigationRefreshPending)
        {
            return;
        }
        if (!_outline)
        {
            _model.EnsureCatalogIndex();
        }

        AutomationProperties.SetName(NavigationTree, T(_outline ? "Current document outline" : "Workspace documents"));
        var nodes = NavigationTree.RootNodes;
        var pages = _pageSearch == null ? _pagesTree : _pageMatchesTree;
        var visiblePages = _outline ? null : pages;
        if (!ReferenceEquals(_displayedPages, visiblePages))
        {
            _displayedPages = visiblePages;
            nodes.Clear();
            foreach (var node in visiblePages?.Roots ?? _outlineNodes)
            {
                nodes.Add(node);
            }
        }

        var document = _model.Document;
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
                _outlineSelection = null;
            }
        }
        else
        {
            var cards = _navigationWorkspace?.Catalog.Cards ?? [];
            RefreshPageNodes(nodes, pages, _pageSearch?.MatchingCards(cards) ?? cards, _pageSearch != null);
        }
        var anchor = _outline ? _model.History.Current?.Anchor ?? "" : "";
        var selected = document == null ? null
            : _outline ? FindOutlineSelection(nodes, document.Path, anchor)
            : pages.ByPath.GetValueOrDefault(document.Path);
        if (!ReferenceEquals(NavigationTree.SelectedNode, selected))
        {
            for (var parent = selected?.Parent; parent != null; parent = parent.Parent)
            {
                parent.IsExpanded = true;
            }
            NavigationTree.SelectedNode = selected;
        }
    }

    /// <summary>Rebuilds folder structure only when membership changes and otherwise refreshes changed titles in place.</summary>
    private static void RefreshPageNodes(IList<TreeViewNode> nodes, PageTreeState pages, IReadOnlyList<DocumentCard> cards, bool expandFolders)
    {
        var previous = pages.Cards;
        if (ReferenceEquals(previous, cards))
        {
            return;
        }
        pages.Cards = cards;
        if (previous == null || !SamePagePaths(previous, cards))
        {
            ReconcileNavigationNodes(nodes, DesktopNavigation.Pages(cards));
            pages.Roots = nodes.ToArray();
            pages.ByPath.Clear();
            IndexPageNodes(nodes, pages.ByPath, expandFolders);
            return;
        }

        for (var index = 0; index < cards.Count; index++)
        {
            var card = cards[index];
            if (card.Title != previous[index].Title && pages.ByPath.TryGetValue(card.Path, out var node))
            {
                UpdatePageNavigationRow((Grid)node.Content, DesktopNavigation.Page(card));
            }
        }
    }

    /// <summary>Compares the authoritative path-ordered snapshots without assigning meaning to other indexed metadata.</summary>
    private static bool SamePagePaths(IReadOnlyList<DocumentCard> previous, IReadOnlyList<DocumentCard> current)
    {
        if (previous.Count != current.Count)
        {
            return false;
        }
        for (var index = 0; index < current.Count; index++)
        {
            if (previous[index].Path != current[index].Path)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Indexes file nodes from a reconciled hierarchy so selection does not scan every folder.</summary>
    private static void IndexPageNodes(IEnumerable<TreeViewNode> nodes, Dictionary<string, TreeViewNode> byPath, bool expandFolders)
    {
        foreach (var node in nodes)
        {
            if (NavigationItem(node) is { IsFolder: false } item)
            {
                byPath[item.Path] = node;
            }
            else
            {
                if (expandFolders)
                {
                    node.IsExpanded = true;
                }
                IndexPageNodes(node.Children, byPath, expandFolders);
            }
        }
    }

    /// <summary>Keeps native nodes when their semantic identity is still present in the same branch.</summary>
    private static void ReconcileNavigationNodes(IList<TreeViewNode> nodes, IReadOnlyList<DesktopNavigationItem> items, bool expandHeadings = false)
    {
        if (items.Count == 0)
        {
            if (nodes.Count > 0)
            {
                nodes.Clear();
            }
            return;
        }

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
                var row = item.Target == null ? CreatePageNavigationRow(item) : CreateNavigationRow(item);
                node = new TreeViewNode { Content = row, IsExpanded = expandHeadings && item.Children.Count > 0 };
                nodes.Insert(index, node);
            }
            else
            {
                if (nodes[index] != node)
                {
                    nodes.Remove(node);
                    nodes.Insert(index, node);
                }
                if (item.Target == null)
                {
                    UpdatePageNavigationRow((Grid)node.Content, item);
                }
                else
                {
                    UpdateNavigationRow((Grid)node.Content, item);
                }
            }
            ReconcileNavigationNodes(node.Children, item.Children);
        }
    }

    /// <summary>Reuses the selected outline row across unrelated index notifications.</summary>
    private TreeViewNode? FindOutlineSelection(IList<TreeViewNode> nodes, string path, string anchor)
    {
        if (_outlineSelection is { } selection && selection.Path == path && selection.Anchor == anchor)
        {
            return selection.Node;
        }
        var selected = anchor.Length == 0 ? null : FindOutlineNode(nodes, path, anchor);
        _outlineSelection = (path, anchor, selected);
        return selected;
    }

    /// <summary>Finds the native row for a semantic element or direct heading link.</summary>
    private static TreeViewNode? FindOutlineNode(IEnumerable<TreeViewNode> nodes, string path, string anchor)
    {
        foreach (var node in nodes)
        {
            if (NavigationItem(node) is { Target: { } target } item && item.Path == path &&
                (target.ElementId == anchor || target.HeadingId == anchor))
            {
                return node;
            }
            if (FindOutlineNode(node.Children, path, anchor) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    /// <summary>Creates an outline row with a compact marker and room for two lines of heading text.</summary>
    private static Grid CreateNavigationRow(DesktopNavigationItem item)
    {
        var row = new Grid { ColumnSpacing = NavigationRowSpacing, Padding = NavigationRowPadding };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NavigationIconColumnWidth) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var headingLevel = item.Target?.HeadingLevel ?? 0;
        var heading = headingLevel > 0;
        if (heading)
        {
            var marker = new TextBlock
            {
                Text = $"H{headingLevel}", FontSize = 11, Margin = new Thickness(0, 2, 0, 0),
                VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center
            };
            AutomationProperties.SetAccessibilityView(marker, AccessibilityView.Raw);
            row.Children.Add(marker);
        }
        else
        {
            row.Children.Add(CreateNavigationGlyph(item.Icon switch
            {
                "image" => "\uE8B9",
                "paragraph" => "\uE8E4",
                "table" => "\uE8A9",
                "code" => "\uE943",
                _ => "\uE8A5"
            }));
        }
        var label = new TextBlock
        {
            FontSize = heading ? 13 : 12,
            MaxLines = heading ? 2 : 1,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = heading ? TextWrapping.Wrap : TextWrapping.NoWrap
        };
        if (heading)
        {
            label.FontWeight = new global::Windows.UI.Text.FontWeight { Weight = 600 };
        }
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        WindowsUiFont.ApplyTo(row);
        UpdateNavigationRow(row, item);
        return row;
    }

    /// <summary>Updates captions and tooltips in place while rebinding context actions to the current projection.</summary>
    private static void UpdateNavigationRow(Grid row, DesktopNavigationItem item)
    {
        if (row.Tag is not DesktopNavigationItem previous || previous.Label != item.Label || previous.Tooltip != item.Tooltip)
        {
            ((TextBlock)row.Children[1]).Text = item.Label;
            UpdateNavigationContainer(row, item);
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
