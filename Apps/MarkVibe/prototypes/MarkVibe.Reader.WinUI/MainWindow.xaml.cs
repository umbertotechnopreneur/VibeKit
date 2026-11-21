/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; folder selection and cancellable reader navigation
 * Human guidance: prioritize an immediate native window and a single reader pane
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using MarkVibe.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace MarkVibe.Reader.WinUI;

/// <summary>Owns local folder selection and the lifetime of the single Markdown reader.</summary>
public sealed partial class MainWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarkVibe", "ReaderPrototype");
    private ReaderWorkspace? _workspace;
    private ReaderView? _reader;
    private CancellationTokenSource? _navigation;
    private bool _settingSelection;
    private bool _closed;

    /// <summary>Builds the native shell without creating a WebView2 control or inspecting the filesystem.</summary>
    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new SizeInt32(1200, 800));
        Closed += OnClosed;
    }

    /// <summary>Loads the selected folder off the UI thread and opens its README or first Markdown file.</summary>
    private async void OpenFolder(object sender, RoutedEventArgs args)
    {
        OpenButton.IsEnabled = false;
        Documents.IsEnabled = false;
        var token = _lifetime.Token;
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder == null || token.IsCancellationRequested)
            {
                return;
            }

            CancelNavigation();
            Status.Text = "Finding Markdown files…";
            var workspace = await Task.Run(() => ReaderWorkspace.Open(_dataDirectory, folder.Path, token), token);
            token.ThrowIfCancellationRequested();
            _workspace = workspace;
            Documents.ItemsSource = workspace.Documents;
            var first = workspace.Documents.FirstOrDefault(entry => entry.Path.Equals("README.md", StringComparison.OrdinalIgnoreCase))
                ?? workspace.Documents.FirstOrDefault();
            if (first == null)
            {
                _reader?.Clear();
                Placeholder.Visibility = Visibility.Visible;
                Placeholder.Text = "No Markdown files in this folder.";
                Status.Text = workspace.Info.Name;
                return;
            }

            SetSelection(first);
            await ShowDocumentAsync(first, "");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Closing the window cancels enumeration and makes UI updates unnecessary.
        }
        catch (Exception error)
        {
            if (!_closed)
            {
                Status.Text = error.Message;
            }
        }
        finally
        {
            if (!_closed)
            {
                OpenButton.IsEnabled = true;
                Documents.IsEnabled = true;
            }
        }
    }

    /// <summary>Loads a document selected through native mouse or keyboard input.</summary>
    private async void SelectDocument(object sender, SelectionChangedEventArgs args)
    {
        if (!_settingSelection && Documents.IsEnabled && Documents.SelectedItem is WorkspaceEntry entry)
        {
            await ShowDocumentAsync(entry, "");
        }
    }

    /// <summary>Resolves reader navigation against the current native file list.</summary>
    private async void NavigateFromReader(string path, string anchor)
    {
        if (!OpenButton.IsEnabled || _workspace == null)
        {
            return;
        }

        var entry = _workspace.Documents.FirstOrDefault(item => item.Path.Equals(path, StringComparison.Ordinal))
            ?? _workspace.Documents.FirstOrDefault(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (entry == null)
        {
            Status.Text = "The linked Markdown file is unavailable in this folder.";
            return;
        }

        SetSelection(entry);
        await ShowDocumentAsync(entry, anchor);
    }

    /// <summary>Synchronizes native selection without starting a second navigation.</summary>
    private void SetSelection(WorkspaceEntry entry)
    {
        _settingSelection = true;
        Documents.SelectedItem = entry;
        Documents.ScrollIntoView(entry);
        _settingSelection = false;
    }

    /// <summary>Opens the browser lazily while the selected page is read and parsed in parallel.</summary>
    private async Task ShowDocumentAsync(WorkspaceEntry entry, string anchor)
    {
        var workspace = _workspace;
        if (workspace == null || _closed)
        {
            return;
        }

        CancelNavigation();
        using var navigation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _navigation = navigation;
        var token = navigation.Token;
        Status.Text = "Opening " + entry.Path + "…";
        try
        {
            _reader ??= CreateReader();
            var document = Task.Run(() => workspace.ReadDocumentAsync(entry.Path, token), token);
            var ready = _reader.WaitUntilReadyAsync(token);
            await Task.WhenAll(document, ready);
            token.ThrowIfCancellationRequested();
            await _reader.ShowAsync(workspace, entry, document.Result, anchor, token);
            token.ThrowIfCancellationRequested();
            Placeholder.Visibility = Visibility.Collapsed;
            Status.Text = workspace.Info.Name + "  /  " + entry.Path;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A newer selection or window closure owns the status and viewport now.
        }
        catch (Exception error)
        {
            if (_closed || token.IsCancellationRequested)
            {
                return;
            }

            _reader?.Clear();
            Placeholder.Text = "The document could not be opened.";
            Placeholder.Visibility = Visibility.Visible;
            Status.Text = error.Message;
            if (_reader is { IsReady: false } failingReader)
            {
                ReaderPane.Children.Remove(failingReader.Control);
                failingReader.Dispose();
                _reader = null;
            }
        }
        finally
        {
            if (ReferenceEquals(_navigation, navigation))
            {
                _navigation = null;
            }
        }
    }

    /// <summary>Creates a single embedded reader and connects its scoped navigation callbacks.</summary>
    private ReaderView CreateReader()
    {
        var reader = new ReaderView(_dataDirectory, Shell.ActualTheme == ElementTheme.Dark, _lifetime.Token);
        reader.Navigate += NavigateFromReader;
        reader.Status += text => Status.Text = text;
        ReaderPane.Children.Add(reader.Control);
        return reader;
    }

    /// <summary>Stops applying results owned by an earlier document selection.</summary>
    private void CancelNavigation() => _navigation?.Cancel();

    /// <summary>Cancels outstanding native and browser work as the window closes.</summary>
    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _lifetime.Cancel();
        _reader?.Dispose();
        _lifetime.Dispose();
    }
}
