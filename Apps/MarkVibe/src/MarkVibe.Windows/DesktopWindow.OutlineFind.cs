/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; native outline match navigation
 * Human guidance: find text in the current document and navigate highlighted passages
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using MarkVibe.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using static MarkVibe.Core.UiStrings;

namespace MarkVibe.Windows;

public sealed partial class DesktopWindow
{
    private bool _outlineFindActive;
    private bool _openingFindReader;
    private bool _updatingMatches;
    private DocumentFindExcerpt[] _shownMatches = [];

    /// <summary>Shows either the document structure or its matching passages in the same sidebar.</summary>
    private void RefreshOutlineSearch()
    {
        OutlineFindPanel.Visibility = _outline ? Visibility.Visible : Visibility.Collapsed;
        OutlineFindQuery.IsEnabled = _model.Document != null;
        var searching = _outline && OutlineFindQuery.Text.Length > 0;
        OutlineFindActions.Visibility = OutlineFindResults.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        NavigationTree.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Targets the open document, entering its reader when a search starts on the map.</summary>
    private async void OutlineFindQueryChanged(object sender, TextChangedEventArgs args)
    {
        RefreshOutlineSearch();
        if (!_outline || _openingFindReader || _model.Workspace is not { Document: { } document } workspace) { return; }
        var query = OutlineFindQuery.Text;
        if (query.Length == 0)
        {
            if (_outlineFindActive) { await RunAsync(_ => CloseFindAsync()); }
            return;
        }
        await RunAsync(async token =>
        {
            if (_model.View == "map")
            {
                _openingFindReader = true;
                try { await SetViewAsync("read", token); }
                finally { _openingFindReader = false; }
                query = OutlineFindQuery.Text;
                if (!_outline || _model.View == "map" || query.Length == 0 || _model.Workspace != workspace ||
                    _model.Document?.Path != document.Path) { return; }
            }
            _outlineFindActive = true;
            FindBar.Visibility = Visibility.Collapsed;
            var find = _model.Find;
            find.Target = new(workspace.Info.Id, document.Path, document.Title, Fingerprint: document.Fingerprint, View: _model.View);
            find.View = _model.View == "source" ? "source" : "read";
            find.Query = query;
            await SearchAsync("query", token);
        });
    }

    /// <summary>Refreshes one bounded page of excerpts and marks the active result without firing navigation.</summary>
    private void DisplayOutlineMatches(DocumentFindResult result)
    {
        _updatingMatches = true;
        try
        {
            var excerpts = result.Excerpts ?? [];
            if (!_shownMatches.SequenceEqual(excerpts))
            {
                OutlineFindResults.Items.Clear();
                foreach (var excerpt in excerpts)
                {
                    var line = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxLines = 3,
                        TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 5, 0, 5) };
                    line.Inlines.Add(new Run { Text = excerpt.Before });
                    line.Inlines.Add(new Run { Text = excerpt.Match, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                    line.Inlines.Add(new Run { Text = excerpt.After });
                    var item = new ListViewItem { Tag = excerpt.Index, Content = line, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                    AutomationProperties.SetName(item, $"{excerpt.Index}. {excerpt.Before}{excerpt.Match}{excerpt.After}");
                    OutlineFindResults.Items.Add(item);
                }
                _shownMatches = excerpts;
            }
            OutlineFindResults.SelectedIndex = Array.FindIndex(excerpts, excerpt => excerpt.Index == result.Current);
            if (OutlineFindResults.SelectedItem is { } selected) { OutlineFindResults.ScrollIntoView(selected); }
            OutlineFindCount.Text = result.Total > 0 ? $"{result.Current} / {result.Total}" : T("No matches");
            OutlinePrevious.IsEnabled = OutlineNext.IsEnabled = result.Total > 1;
            RefreshOutlineSearch();
        }
        finally { _updatingMatches = false; }
    }

    /// <summary>Reveals the selected occurrence using its exact index in the renderer's text.</summary>
    private async void OutlineMatchClicked(object sender, ItemClickEventArgs args)
    {
        if (_compactSidebarOpen && args.ClickedItem is ListViewItem { Tag: int index } && index == _model.Find.Current)
        {
            CloseCompactSidebar();
            await RunAsync(token => SearchAsync("select", token, index));
        }
    }

    /// <summary>Moves the reader to an occurrence selected using the mouse, keyboard or UI Automation.</summary>
    private async void OutlineMatchSelected(object sender, SelectionChangedEventArgs args)
    {
        if (!_updatingMatches && _outlineFindActive && OutlineFindResults.SelectedItem is ListViewItem { Tag: int index })
        {
            CloseCompactSidebar();
            await RunAsync(token => SearchAsync("select", token, index));
        }
    }
}
