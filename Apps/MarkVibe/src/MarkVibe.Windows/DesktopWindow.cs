/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; WinUI 3 native MarkVibe shell
 * Human guidance: migrate in place with an independent data model and focused WebView2 renderers
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using System.Globalization;
using System.IO;
using MarkVibe.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;
using static MarkVibe.Core.UiStrings;

namespace MarkVibe.Windows;

/// <summary>Presentational Windows shell over the workspace, map and AI models.</summary>
public sealed partial class DesktopWindow : Window
{
    private const double CompactSidebarBreakpoint = 760;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DesktopOptions _options;
    private readonly DesktopWorkspaceModel _model;
    private readonly DesktopDocumentFiles _documentFiles;
    private readonly CurrentDocumentWatcher _watcher;
    private readonly AiConnectionService _connections;
    private readonly WindowsAiSession _ai;
    private DocumentWebView? _reader;
    private DocumentWebView? _preview;
    private WindowsMapView? _map;
    private WindowsAiPanel? _assistantPanel;
    private DesktopWorkspace? _mapWorkspace;
    private CancellationTokenSource? _navigation;
    private CancellationTokenSource? _find;
    private CancellationTokenSource? _previewCancellation;
    private double? _dragWidth;
    private double? _dragSourceRatio;
    private bool _loaded;
    private bool _outline;
    private bool _compactSidebar;
    private bool _compactSidebarOpen;
    private bool _changingWorkspace;
    private bool _closing;
    private bool _closeApproved;
    private bool _disposed;
    private bool _firstAiOpen;
    private bool _showingDialog;

    /// <summary>Shows native controls before creating a renderer or scanning an opened folder.</summary>
    internal DesktopWindow(DesktopOptions options)
    {
        _options = options;
        _model = new DesktopWorkspaceModel(options.DataDirectory);
        var language = CultureInfo.CurrentUICulture.Name;
        SetLanguage(_model.Preferences.Language, language);
        InitializeComponent();
        Viewport.SizeChanged += (_, _) => QueuePreviewPlacement();
        AppWindow.Resize(new SizeInt32(1350, 880));
        _documentFiles = new DesktopDocumentFiles(options, _model.Files, WindowNative.GetWindowHandle(this));
        _watcher = new CurrentDocumentWatcher(_model.Files, DispatcherQueue, HandleDiskChange);
        _connections = new AiConnectionService(options.DataDirectory);
        _ai = new WindowsAiSession(_connections, ApplyAiAsync, _lifetime.Token, disabled: options.SmokeTest);
        _ai.Changed += SyncMapReferencePaths;
        _model.CatalogChanged += CatalogChanged;

        var footer = new WindowsFooter();
        footer.AboutRequested += () => _ = RunAsync(AboutAsync);
        FooterSlot.Children.Add(footer);
        ApplyPresentation();
        if (_model.Layout.AiOpen) { EnsureAssistantPanel(); }
        SyncControls();
        InstallShortcuts();
        Shell.ActualThemeChanged += (_, _) => _ = RunAsync(RefreshRendererAppearanceAsync);
        AppWindow.Closing += Closing;
        Closed += ClosedWindow;
    }

    /// <summary>Restores the native session or handles a file association after the first shell layout.</summary>
    private async void ShellLoaded(object sender, RoutedEventArgs args)
    {
        if (_loaded)
        {
            return;
        }
        _loaded = true;
        ApplyGeometry();
        var sample = Path.Combine(_options.AssetsDirectory, "demo");
        SampleButton.Visibility = Directory.Exists(sample) ? Visibility.Visible : Visibility.Collapsed;
        if (_options.Document is { } requested)
        {
            await RunAsync(token => OpenLocationAsync(requested, "files", false, token));
        }
        else if (_model.Preferences.RememberLastDocument && _model.Files.Session is { Entry: { } } session)
        {
            ResumeButton.Visibility = Visibility.Visible;
            await RunAsync(token => SwitchWorkspaceAsync(session.Root, session.Entry.Path, session.Entry, token));
        }
        if (!_disposed)
        {
            await RunAsync(_ => MarkdownFileAssociations.OfferAsync(_options, Shell.XamlRoot));
        }
    }

    /// <summary>Applies native shortcuts while leaving the renderer responsible for text selection and editing keys.</summary>
    private void InstallShortcuts()
    {
        AddShortcut(VirtualKey.O, VirtualKeyModifiers.Control, () => _ = RunAsync(PickFileAsync));
        AddShortcut(VirtualKey.O, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => _ = RunAsync(PickFolderAsync));
        AddShortcut(VirtualKey.F, VirtualKeyModifiers.Control, () => ShowFind());
        AddShortcut(VirtualKey.F3, VirtualKeyModifiers.None, () => _ = RunAsync(token => SearchAsync("next", token)));
        AddShortcut(VirtualKey.F3, VirtualKeyModifiers.Shift, () => _ = RunAsync(token => SearchAsync("previous", token)));
        AddShortcut(VirtualKey.Left, VirtualKeyModifiers.Menu, () =>
            _ = RunAsync(HasMapPreviewFocus() ? BackMapPreviewAsync : token => GoHistoryAsync(-1, token)));
        AddShortcut(VirtualKey.Right, VirtualKeyModifiers.Menu, () => _ = RunAsync(token => GoHistoryAsync(1, token)));
        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (accelerator, args) =>
        {
            if (CloseCompactSidebar())
            {
                SidebarToggle.Focus(FocusState.Programmatic);
                args.Handled = true;
            }
            else if (PreviewOverlay.Visibility == Visibility.Visible)
            {
                DismissPreview();
                args.Handled = true;
            }
            else if (FindBar.Visibility == Visibility.Visible)
            {
                _ = RunAsync(_ => CloseFindAsync());
                args.Handled = true;
            }
        };
        Shell.KeyboardAccelerators.Add(escape);
    }

    /// <summary>Registers one native command accelerator on the owning shell.</summary>
    private void AddShortcut(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var shortcut = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        shortcut.Invoked += (_, args) => { action(); args.Handled = true; };
        Shell.KeyboardAccelerators.Add(shortcut);
    }

    /// <summary>Runs a user command under the window lifetime and displays only meaningful failures.</summary>
    private async Task RunAsync(Func<CancellationToken, Task> operation)
    {
        if (_disposed)
        {
            return;
        }
        try
        {
            await operation(_lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            // Closing or a newer command supersedes this action.
        }
        catch (Exception error)
        {
            DesktopLog.Error("desktop.command-failed", error);
            if (!_disposed)
            {
                SetStatus(error.Message);
            }
        }
    }

    /// <summary>Sets native theme and persisted pane geometry.</summary>
    private void ApplyPresentation()
    {
        var preferences = _model.Preferences;
        Shell.RequestedTheme = preferences.Appearance switch
        {
            "dark" => ElementTheme.Dark,
            "light" => ElementTheme.Light,
            _ => ElementTheme.Default
        };
        if (preferences.Acrylic && SystemBackdrop == null)
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
        }
        else if (!preferences.Acrylic)
        {
            SystemBackdrop = null;
        }
        if (UiFontCatalog.Normalize(preferences.UiFont) is { } font && font != "system")
        {
            Shell.Resources["ContentControlThemeFontFamily"] = new FontFamily(font);
        }
        else
        {
            Shell.Resources.Remove("ContentControlThemeFontFamily");
        }
        ApplyGeometry();
        UpdateLabels();
    }

    /// <summary>Fits the docked panes or shows the transient full-width document navigator in compact windows.</summary>
    private void ApplyGeometry()
    {
        var layout = _model.Layout;
        var width = Shell.ActualWidth;
        var compact = width > 0 && width < CompactSidebarBreakpoint;
        if (compact != _compactSidebar)
        {
            _compactSidebar = compact;
            _compactSidebarOpen = false;
        }
        var sidebar = !compact && layout.SidebarOpen
            ? Math.Min(layout.SidebarWidth, width > 0 ? Math.Max(155, width * 0.42) : 520)
            : 0;
        var assistant = layout.AiOpen ? Math.Min(layout.AiWidth, width > 0 ? Math.Max(220, width * 0.47) : 600) : 0;
        var overlay = compact && _compactSidebarOpen;
        var assistantVisible = assistant > 0 && !overlay;
        Grid.SetColumnSpan(Sidebar, overlay ? WorkArea.ColumnDefinitions.Count : 1);
        SidebarColumn.Width = new GridLength(sidebar);
        SidebarGripColumn.Width = new GridLength(sidebar > 0 ? 6 : 0);
        SidebarGrip.Visibility = sidebar > 0 ? Visibility.Visible : Visibility.Collapsed;
        Sidebar.Visibility = sidebar > 0 || overlay ? Visibility.Visible : Visibility.Collapsed;
        DocumentArea.Visibility = overlay ? Visibility.Collapsed : Visibility.Visible;
        AiColumn.Width = new GridLength(assistantVisible ? assistant : 0);
        AiGripColumn.Width = new GridLength(assistantVisible ? 6 : 0);
        AiGrip.Visibility = assistantVisible ? Visibility.Visible : Visibility.Collapsed;
        AiSlot.Visibility = assistantVisible ? Visibility.Visible : Visibility.Collapsed;
        AiButton.IsChecked = layout.AiOpen;
        SetSplitGeometry(_dragSourceRatio ?? layout.SourceRatio);
    }

    /// <summary>Closes the transient navigator after activation or Escape while leaving the docked preference intact.</summary>
    private bool CloseCompactSidebar()
    {
        if (!_compactSidebarOpen)
        {
            return false;
        }
        _compactSidebarOpen = false;
        ApplyGeometry();
        return true;
    }

    /// <summary>Updates localized shell labels after a preference change.</summary>
    private void UpdateLabels()
    {
        OpenFileButton.Label = T("Open file");
        OpenFolderButton.Label = T("Open folder");
        RecentButton.Label = T("Recent");
        SettingsButton.Label = T("Settings");
        AboutButton.Label = T("About");
        ReadButton.Label = T("Read");
        SourceButton.Label = T("Source");
        SplitButton.Label = T("Split");
        MapButton.Label = T("Map");
        FindButton.Label = T("Find in document");
        ExportButton.Label = T("Export");
        PagesButton.Content = T("Pages");
        OutlineButton.Content = T("Outline");
        WorkspaceNamesWarning.Text = T("Some files or folders were skipped because of unsupported or ambiguous names. Rename them and reopen the workspace.");
        AutomationProperties.SetName(ParentButton, T("Go up one folder"));
        ToolTipService.SetToolTip(ParentButton, T("Go up one folder"));
        AutomationProperties.SetName(RefreshButton, T("Refresh folder"));
        ToolTipService.SetToolTip(RefreshButton, T("Refresh folder"));
        FindQuery.PlaceholderText = T("Find in document");
        ResumeButton.Content = T("Reopen last workspace");
    }

    /// <summary>Mirrors only capabilities, labels and selection from the authoritative model.</summary>
    private void SyncControls()
    {
        var workspace = _model.Workspace;
        var document = workspace?.Document;
        var view = _model.View;
        var hasDocument = document != null;
        WorkspaceName.Text = workspace?.Info.Name ?? T("No workspace");
        WorkspaceNamesWarning.Visibility = workspace?.Info.SkippedNames == true ? Visibility.Visible : Visibility.Collapsed;
        DocumentTitle.Text = document?.Title ?? "MarkVibe";
        Title = hasDocument ? document!.Title + " — MarkVibe" : "MarkVibe";
        ParentButton.IsEnabled = workspace is { Info.Capabilities.CanOpenParent: true } && !IsSample(workspace);
        RefreshButton.IsEnabled = workspace != null;
        ReadButton.IsEnabled = hasDocument;
        SourceButton.IsEnabled = hasDocument;
        SplitButton.IsEnabled = hasDocument && (Shell.ActualWidth == 0 || Shell.ActualWidth >= 1050);
        MapButton.IsEnabled = workspace is { InventoryReady: true } && workspace.Catalog.Cards.Count > 0;
        FindButton.IsEnabled = hasDocument && view != "map";
        ExportButton.IsEnabled = hasDocument;
        ReadButton.IsChecked = view == "read";
        SourceButton.IsChecked = view == "source";
        SplitButton.IsChecked = view == "split";
        MapButton.IsChecked = view == "map";
        BackButton.IsEnabled = _model.History.CanBack || (_model.History.Current != null && !hasDocument);
        ForwardButton.IsEnabled = _model.History.CanForward;
        Welcome.Visibility = hasDocument || view == "map" ? Visibility.Collapsed : Visibility.Visible;
        ReaderSlot.Visibility = hasDocument || _reader != null ? Visibility.Visible : Visibility.Collapsed;
        ReaderSlot.Opacity = view == "map" ? 0 : 1;
        ReaderSlot.IsHitTestVisible = view != "map";
        SplitChrome.Visibility = hasDocument && view == "split" ? Visibility.Visible : Visibility.Collapsed;
        MapSlot.Visibility = view == "map" ? Visibility.Visible : Visibility.Collapsed;
        PreviewBackButton.IsEnabled = _model.CanPreviewBack;
        if (view != "map")
        {
            _map?.SuspendInteractions();
        }
    }

    /// <summary>Refreshes the visible projection when the model publishes catalog indexing progress.</summary>
    private void CatalogChanged()
    {
        if (_disposed)
        {
            return;
        }
        UpdateTree();
        SyncControls();
        if (_mapWorkspace == _model.Workspace && _mapWorkspace != null)
        {
            _map?.Refresh(_model.Document?.Path);
            SyncMapReferencePaths();
        }
    }

    /// <summary>Keeps pane widths responsive and falls back to Read below the Split breakpoint.</summary>
    private void ShellSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (!_loaded)
        {
            return;
        }
        ApplyGeometry();
        UpdatePreviewPlacement();
        if (args.NewSize.Width < 1050 && _model.View == "split")
        {
            _ = RunAsync(token => SetViewAsync("read", token));
        }
        SyncControls();
    }

    /// <summary>Runs a dialog without attempting to stack multiple ContentDialogs on the window.</summary>
    private async Task ShowDialogAsync(Func<Task> show)
    {
        if (_showingDialog || _disposed)
        {
            return;
        }
        _showingDialog = true;
        try { await show(); }
        finally { _showingDialog = false; }
    }

    /// <summary>Returns affirmative consent for a one-off native confirmation.</summary>
    private async Task<bool> ConfirmAsync(string title, string message, string action)
    {
        var result = false;
        await ShowDialogAsync(async () =>
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Shell.XamlRoot, Title = T(title), Content = new TextBlock { Text = T(message), TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = T(action), CloseButtonText = T("Cancel"), DefaultButton = ContentDialogButton.Close
            };
            result = await dialog.ShowAsync() == ContentDialogResult.Primary;
        });
        return result;
    }

    /// <summary>Reports a short native status without changing document data.</summary>
    private void SetStatus(string message) => Status.Text = T(message);

    /// <summary>Flushes the active session after confirming any outstanding workspace draft or map change.</summary>
    private async void Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeApproved)
        {
            return;
        }
        args.Cancel = true;
        if (_closing)
        {
            return;
        }
        _closing = true;
        await RunAsync(async token =>
        {
            if (await ConfirmLeaveWorkspaceAsync(token))
            {
                await CapturePositionAsync(token);
                await RememberAsync(token);
                _closeApproved = true;
                Close();
            }
        });
        _closing = false;
    }

    /// <summary>Cancels model and renderer work after WinUI closes the window.</summary>
    private void ClosedWindow(object sender, WindowEventArgs args)
    {
        _disposed = true;
        _lifetime.Cancel();
        _ai.Changed -= SyncMapReferencePaths;
        _ai.Cancel();
        _watcher.Dispose();
        _reader?.Dispose();
        _preview?.Dispose();
        _map?.Dispose();
        _model.CatalogChanged -= CatalogChanged;
        _model.Dispose();
        _lifetime.Dispose();
    }
}
