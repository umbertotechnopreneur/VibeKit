/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; native Pages search, progress and filtered tree projection
 * Human guidance: place a content search under the Pages tab and preserve document navigation
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using MarkVibe.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using static MarkVibe.Core.UiStrings;

namespace MarkVibe.Windows;

public sealed partial class DesktopWindow
{
    private PageSearchRequest? _pageSearch;

    /// <summary>Keeps native nodes and folder expansion independent for the full tree and the search results.</summary>
    private sealed class PageTreeState
    {
        public TreeViewNode[] Roots { get; set; } = [];
        public IReadOnlyList<DocumentCard>? Cards { get; set; }
        public Dictionary<string, TreeViewNode> ByPath { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>Scopes progress and the filtered card snapshot to one query and one revision of the workspace.</summary>
    private sealed class PageSearchRequest(DesktopWorkspace workspace, string query)
    {
        private IReadOnlyList<DocumentCard>? _catalogCards;
        private IReadOnlyList<DocumentCard> _matchingCards = [];
        public DesktopWorkspace Workspace { get; } = workspace;
        public string Query { get; } = query;
        public long ContentRevision { get; } = workspace.ContentRevision;
        public int Total { get; } = workspace.Catalog.DocumentCount;
        public int Processed { get; set; }
        public WorkspacePageMatches? Matches { get; set; }
        public CancellationTokenSource? Cancellation { get; set; }

        /// <summary>Uses current titles and canonical card order for the completed content search.</summary>
        public IReadOnlyList<DocumentCard> MatchingCards(IReadOnlyList<DocumentCard> cards)
        {
            if (Matches is not { } matches)
            {
                return [];
            }
            if (!ReferenceEquals(_catalogCards, cards))
            {
                _matchingCards = cards.Where(card => matches.Paths.Contains(card.Path)).ToArray();
                _catalogCards = cards;
            }
            return _matchingCards;
        }
    }

    /// <summary>Replaces or clears the active search as soon as the user edits its text.</summary>
    private void PagesSearchChanged(object sender, TextChangedEventArgs args)
    {
        if (_loaded && !_disposed)
        {
            UpdateTree();
        }
    }

    /// <summary>Drops the previous workspace query when a new workspace becomes authoritative.</summary>
    private void ResetPageSearch()
    {
        CancelPageSearch();
        if (PagesSearchBox.Text.Length != 0)
        {
            PagesSearchBox.Text = "";
        }
    }

    /// <summary>Detaches the request before cancellation so queued progress cannot publish to a replacement.</summary>
    private void CancelPageSearch()
    {
        var previous = _pageSearch;
        if (previous == null)
        {
            return;
        }
        _pageSearch = null;
        _pageMatchesTree = new();
        previous.Cancellation?.Cancel();
    }

    /// <summary>Synchronizes the input and search lifetime with the selected tab, active workspace and query.</summary>
    private void SyncPageSearch()
    {
        PagesSearchPanel.Visibility = _outline ? Visibility.Collapsed : Visibility.Visible;
        var workspace = _model.Workspace;
        PagesSearchBox.IsEnabled = workspace is { InventoryReady: true };
        var query = PagesSearchBox.Text.Trim();
        if (_outline || workspace == null || !workspace.InventoryReady || query.Length == 0)
        {
            CancelPageSearch();
            UpdatePageSearchStatus();
            return;
        }
        if (_pageSearch is not { } current || current.Workspace != workspace ||
            current.ContentRevision != workspace.ContentRevision || current.Query != query)
        {
            CancelPageSearch();
            _pageSearch = new PageSearchRequest(workspace, query);
            _ = RunPageSearchAsync(_pageSearch);
        }
        UpdatePageSearchStatus();
    }

    /// <summary>Debounces typing and only publishes counts or matches for the still-current request.</summary>
    private async Task RunPageSearchAsync(PageSearchRequest request)
    {
        using var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        request.Cancellation = source;
        var token = source.Token;
        try
        {
            await Task.Delay(220, token);
            var progress = new Progress<int>(processed =>
            {
                if (IsCurrentPageSearch(request) && request.Cancellation != null)
                {
                    request.Processed = processed;
                    UpdatePageSearchStatus();
                }
            });
            var matches = await WorkspacePageSearch.FindAsync(request.Workspace, request.Query, progress, token);
            if (IsCurrentPageSearch(request))
            {
                request.Matches = matches;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The next query, workspace or window owns the visible controls.
        }
        catch (Exception error)
        {
            DesktopLog.Error("workspace.search-failed", error);
        }
        finally
        {
            request.Cancellation = null;
            if (IsCurrentPageSearch(request))
            {
                UpdateTree();
            }
        }
    }

    /// <summary>Rejects late results and progress after a revision, workspace or input has changed.</summary>
    private bool IsCurrentPageSearch(PageSearchRequest request) => !_disposed && ReferenceEquals(_pageSearch, request) &&
        _model.Workspace == request.Workspace && request.Workspace.ContentRevision == request.ContentRevision &&
        PagesSearchBox.Text.Trim() == request.Query;

    /// <summary>Updates the native progress meter and a localized result count including unreadable pages.</summary>
    private void UpdatePageSearchStatus()
    {
        var request = _pageSearch;
        var running = request?.Cancellation != null;
        PagesSearchProgress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        PagesSearchStatus.Visibility = request == null ? Visibility.Collapsed : Visibility.Visible;
        if (request == null)
        {
            return;
        }
        if (running)
        {
            PagesSearchProgress.Maximum = Math.Max(1, request.Total);
            PagesSearchProgress.Value = request.Processed;
            PagesSearchStatus.Text = T("Searching {0} of {1} documents…", request.Processed, request.Total);
            return;
        }
        if (request.Matches is not { } matches)
        {
            PagesSearchStatus.Text = T("Document search failed");
            return;
        }
        var status = matches.Paths.Count == 0 ? T("No matching documents") : T("{0} documents", matches.Paths.Count);
        PagesSearchStatus.Text = matches.Unreadable == 0 ? status : status + " · " + T("{0} unreadable", matches.Unreadable);
    }

    /// <summary>Localizes the visible Pages filter and its accessible names when the shell language changes.</summary>
    private void UpdatePageSearchLabels()
    {
        PagesSearchBox.PlaceholderText = T("Search document contents");
        AutomationProperties.SetName(PagesSearchBox, PagesSearchBox.PlaceholderText);
        AutomationProperties.SetName(PagesSearchProgress, T("Search document contents"));
        UpdatePageSearchStatus();
    }
}
