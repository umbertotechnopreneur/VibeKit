/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; WinUI 3 native MarkVibe shell
 * Human guidance: migrate in place with an independent data model and focused WebView2 renderers
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
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
using Windows.UI.ViewManagement;
using WinRT.Interop;
using static MarkVibe.Core.UiStrings;

namespace MarkVibe.Windows;

/// <summary>Presentational Windows shell over the workspace, map and AI models.</summary>
public sealed partial class DesktopWindow : Window
{
    private const double CompactSidebarBreakpoint = 760;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly AccessibilitySettings _accessibility = new();
    private bool _tracksContrastChanges;
    private readonly DesktopOptions _options;
    private readonly DesktopWorkspaceModel _model;
    private readonly DesktopDocumentFiles _documentFiles;
    private readonly CurrentDocumentWatcher _watcher;
    private readonly WindowsFooter _footer;
    private AiConnectionService? _connections;
    private WindowsAiSession? _ai;
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
    private bool _syncingViewSelector;
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
        ApplyShellMenuIcons();
        WindowsPaperSurface.Attach(ToolbarLayout);
        WindowsPaperSurface.AttachFlyout(ShellMenu);
        WindowsPaperSurface.AttachFlyout(FolderCommandsMenu);
        SystemBackdrop = new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt };
        Viewport.SizeChanged += (_, _) => QueuePreviewPlacement();
        ResizeForDisplay();
        _documentFiles = new DesktopDocumentFiles(options, _model.Files, WindowNative.GetWindowHandle(this));
        _watcher = new CurrentDocumentWatcher(_model.Files, DispatcherQueue, HandleDiskChange);
        _model.CatalogChanged += CatalogChanged;

        _footer = new WindowsFooter();
        _footer.AboutRequested += () => _ = RunAsync(AboutAsync);
        FooterSlot.Children.Add(_footer);
        ApplyPresentation();
        SyncControls();
        InstallShortcuts();
        Shell.ActualThemeChanged += (_, _) =>
        {
            ApplyWindowColors();
            _ = RunAsync(RefreshRendererAppearanceAsync);
        };
        try
        {
            _accessibility.HighContrastChanged += OnHighContrastChanged;
            _tracksContrastChanges = true;
        }
        catch (COMException error) when (error.HResult == unchecked((int)0x80070490))
        {
            // Some desktops can report contrast at layout time but do not expose its notification service.
        }
        AppWindow.Closing += Closing;
        Closed += ClosedWindow;
    }

    /// <summary>Fits the initial workspace to its monitor while preserving its reading width at higher DPI.</summary>
    private void ResizeForDisplay()
    {
        var scale = GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96d;
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        AppWindow.Resize(new SizeInt32(
            Math.Min(work.Width, (int)Math.Round(1350 * scale)),
            Math.Min(work.Height, (int)Math.Round(880 * scale))));
    }

    /// <summary>Returns the effective DPI of this per-monitor-aware window before its XAML root loads.</summary>
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(IntPtr window);

    /// <summary>Restores the native session or handles a file association after the first shell layout.</summary>
    private async void ShellLoaded(object sender, RoutedEventArgs args)
    {
        if (_loaded)
        {
            return;
        }
        _loaded = true;
        WindowsUiFont.ApplyTo(Shell);
        ApplyGeometry();
        if (_model.Layout.AiOpen)
        {
            EnsureAssistantPanel();
        }
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
            else if (_model.Find.IsOpen)
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
        ApplyWindowColors();
        _map?.SetAppearance(IsDark, preferences.Acrylic, preferences.GraphPalette, preferences.GraphFont);
        if (WindowsUiFont.Select(preferences.UiFont))
        {
            WindowsUiFont.ApplyTo(Shell);
        }
        ApplyGeometry();
        UpdateLabels();
    }

    /// <summary>Matches the Windows caption to the Mica shell's current theme.</summary>
    private void ApplyWindowColors()
    {
        var dark = Shell.ActualTheme == ElementTheme.Dark;
        AppWindow.TitleBar.PreferredTheme = dark ? TitleBarTheme.Dark : TitleBarTheme.Light;
    }

    /// <summary>Updates the shell on its UI thread when Windows changes the contrast setting.</summary>
    private void OnHighContrastChanged(AccessibilitySettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_disposed)
            {
                ApplyWindowColors();
                var preferences = _model.Preferences;
                _map?.SetAppearance(IsDark, preferences.Acrylic, preferences.GraphPalette, preferences.GraphFont);
            }
        });

    /// <summary>Fits the docked panes or gives an active compact pane the full work area.</summary>
    private void ApplyGeometry()
    {
        var layout = _model.Layout;
        var width = Shell.ActualWidth;
        var wrapToolbar = width > 0 && width < 800;
        Grid.SetRow(DocumentToolbar, wrapToolbar ? 1 : 0);
        Grid.SetColumn(DocumentToolbar, wrapToolbar ? 0 : 1);
        Grid.SetColumnSpan(DocumentToolbar, wrapToolbar ? 3 : 1);
        Grid.SetColumn(Header, wrapToolbar ? 1 : 2);
        Grid.SetColumnSpan(Header, wrapToolbar ? 2 : 1);
        Header.DefaultLabelPosition = width >= 1000 ? CommandBarDefaultLabelPosition.Right : CommandBarDefaultLabelPosition.Collapsed;
        var compact = width > 0 && width < CompactSidebarBreakpoint;
        if (compact != _compactSidebar)
        {
            _compactSidebar = compact;
            _compactSidebarOpen = false;
        }
        var sidebar = !compact && layout.SidebarOpen ? Math.Min(layout.SidebarWidth, SidebarWidthLimit) : 0;
        var overlay = compact && _compactSidebarOpen;
        var assistantOverlay = compact && layout.AiOpen && !overlay;
        var dockedAssistant = !compact && layout.AiOpen;
        var assistantWidth = dockedAssistant ? Math.Min(layout.AiWidth, width > 0 ? Math.Min(AssistantWidthLimit, width * .47) : 600) : 0;
        Grid.SetColumnSpan(Sidebar, overlay ? WorkArea.ColumnDefinitions.Count : 1);
        SidebarColumn.Width = new GridLength(sidebar);
        SidebarGrip.Visibility = sidebar > 0 ? Visibility.Visible : Visibility.Collapsed;
        var sidebarVisible = sidebar > 0 || overlay;
        Sidebar.Visibility = sidebarVisible ? Visibility.Visible : Visibility.Collapsed;
        DocumentArea.Visibility = overlay || assistantOverlay ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumn(AiSlot, assistantOverlay ? 0 : WorkArea.ColumnDefinitions.Count - 1);
        Grid.SetColumnSpan(AiSlot, assistantOverlay ? WorkArea.ColumnDefinitions.Count : 1);
        AiColumn.Width = new GridLength(assistantWidth);
        AiGrip.Visibility = dockedAssistant ? Visibility.Visible : Visibility.Collapsed;
        AiSlot.Visibility = assistantOverlay || dockedAssistant ? Visibility.Visible : Visibility.Collapsed;
        AiButton.Text = T(layout.AiOpen ? "Close assistant" : "Open assistant");
        Hint(AiButton, layout.AiOpen ? "Close assistant" : "Open assistant");
        SetSplitGeometry(_dragSourceRatio ?? layout.SourceRatio);
        // Allow room for the footer padding and a status caption on the shared wide row.
        var footerWidth = width > 0 ? Math.Max(0, width - 24 - (compact ? 0 : 128)) : double.PositiveInfinity;
        FooterSlot.MaxWidth = footerWidth;
        _footer.FitWidth(footerWidth);
        Grid.SetColumnSpan(Status, compact ? 2 : 1);
        Grid.SetRow(FooterSlot, compact ? 1 : 0);
        Grid.SetColumn(FooterSlot, compact ? 0 : 1);
        Grid.SetColumnSpan(FooterSlot, compact ? 2 : 1);
        if (_loaded && sidebarVisible && _navigationRefreshPending)
        {
            UpdateTree();
        }
    }

    /// <summary>Bounds the sidebar consistently while dragging, restoring or resizing by keyboard.</summary>
    private double SidebarWidthLimit => Shell.ActualWidth > 0 ? Math.Clamp(Shell.ActualWidth * .42, 180, 520) : 520;

    /// <summary>Bounds the preferred assistant width, which can be displayed narrower in compact windows.</summary>
    private double AssistantWidthLimit => Shell.ActualWidth > 0 ? Math.Clamp(Shell.ActualWidth * .47, 300, 600) : 600;

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
        RecentButton.Text = T("Recent");
        SettingsButton.Label = T("Settings");
        AboutButton.Text = T("About MarkVibe");
        ReadButton.Text = T("Read");
        SourceButton.Text = T("Source");
        SplitButton.Text = T("Split");
        MapButton.Text = T("Map");
        FindButton.Label = T("Find in document");
        ExportButton.Text = T("Export");
        PagesButton.Text = T("Pages");
        UpdatePageSearchLabels();
        OutlineButton.Text = T("Outline");
        WorkspaceNamesWarning.Text = T("Some files or folders were skipped because of unsupported or ambiguous names. Rename them and reopen the workspace.");
        AutomationProperties.SetName(FolderCommandsButton, T("Folder commands"));
        GoUpFolderMenuItem.Text = T("Go up one folder");
        RefreshFolderMenuItem.Text = T("Refresh folder");
        WindowsUiFont.ApplyTo(FolderCommandsMenu);
        FindQuery.PlaceholderText = T("Find in document");
        OutlineFindQuery.PlaceholderText = T("Find in document");
        AutomationProperties.SetName(OutlineFindQuery, T("Find in document"));
        AutomationProperties.SetName(OutlineFindResults, T("Search results"));
        AutomationProperties.SetName(OutlinePrevious, T("Previous match"));
        AutomationProperties.SetName(OutlineNext, T("Next match"));
        Hint(OutlinePrevious, "Previous match", "Shift+F3");
        Hint(OutlineNext, "Next match", "F3");
        ResumeButton.Content = T("Reopen last workspace");
        SampleButton.Content = T("Open sample workspace");
        PreviewCloseButton.Label = T("Close");
        PreviewOpenButton.Label = T("Open document");
        PreviewBackButton.Label = T("Back");
        AiButton.Text = T(_model.Layout.AiOpen ? "Close assistant" : "Open assistant");
        ShellMenuButton.Label = T("More commands");
        Hint(ShellMenuButton, "More commands");
        WindowsUiFont.ApplyTo(ShellMenu);
        Hint(SidebarToggle, "Toggle sidebar");
        Hint(BackButton, "Back", "Alt+←");
        Hint(ForwardButton, "Forward", "Alt+→");
        Hint(OpenFileButton, "Open file", "Ctrl+O");
        Hint(OpenFolderButton, "Open folder", "Ctrl+Shift+O");
        Hint(FindButton, "Find in document", "Ctrl+F");
        Hint(SettingsButton, "Settings");
        Hint(RecentButton, "Recent");
        Hint(ExportButton, "Export");
        Hint(AiButton, _model.Layout.AiOpen ? "Close assistant" : "Open assistant");
        Hint(AboutButton, "About MarkVibe");
        Hint(ReadButton, "Read Markdown");
        Hint(SourceButton, "Show Markdown source");
        Hint(SplitButton, "Source and preview");
        Hint(MapButton, "Document map");
        Hint(PagesButton, "Pages");
        Hint(OutlineButton, "Outline");
        Hint(FolderCommandsButton, "Folder commands");
        Hint(GoUpFolderMenuItem, "Go up one folder");
        Hint(RefreshFolderMenuItem, "Refresh folder");
        Hint(SampleButton, "Open sample workspace");
        Hint(ResumeButton, "Reopen last workspace");
        Hint(FindPreviousButton, "Previous match", "Shift+F3");
        Hint(FindNextButton, "Next match", "F3");
        Hint(FindCloseButton, "Close", "Esc");
        Hint(PreviewCloseButton, "Close", "Esc");
        Hint(PreviewBackButton, "Back", "Alt+←");
        Hint(PreviewOpenButton, "Open document");
    }

    /// <summary>Keeps pointer hints and screen-reader help aligned with the active language and shortcut.</summary>
    private static void Hint(FrameworkElement control, string label, string? shortcut = null)
    {
        var description = shortcut == null ? T(label) : $"{T(label)} ({shortcut})";
        ToolTipService.SetToolTip(control, description);
        AutomationProperties.SetHelpText(control, description);
    }

    /// <summary>Mirrors only capabilities, labels and selection from the authoritative model.</summary>
    private void SyncControls()
    {
        var workspace = _model.Workspace;
        var document = workspace?.Document;
        var view = _model.View;
        var hasDocument = document != null;
        RefreshOutlineSearch();
        WorkspaceName.Text = workspace?.Info.Name ?? T("No workspace");
        ToolTipService.SetToolTip(WorkspaceName, WorkspaceName.Text);
        WorkspaceNamesWarning.Visibility = workspace?.Info.SkippedNames == true ? Visibility.Visible : Visibility.Collapsed;
        Title = hasDocument ? document!.Title + " — MarkVibe" : "MarkVibe";
        GoUpFolderMenuItem.IsEnabled = workspace is { Info.Capabilities.CanOpenParent: true } && !IsSample(workspace);
        RefreshFolderMenuItem.IsEnabled = workspace != null;
        FolderCommandsButton.IsEnabled = GoUpFolderMenuItem.IsEnabled || RefreshFolderMenuItem.IsEnabled;
        ReadButton.IsEnabled = hasDocument;
        SourceButton.IsEnabled = hasDocument;
        SplitButton.IsEnabled = hasDocument && (Shell.ActualWidth == 0 || Shell.ActualWidth >= 1050);
        MapButton.IsEnabled = workspace is { InventoryReady: true } && workspace.Catalog.DocumentCount > 0;
        FindButton.IsEnabled = hasDocument && view != "map";
        ExportButton.IsEnabled = hasDocument;
        _syncingViewSelector = true;
        DocumentToolbar.SelectedItem = view switch
        {
            "source" => SourceButton,
            "split" => SplitButton,
            "map" => MapButton,
            _ => ReadButton
        };
        _syncingViewSelector = false;
        DocumentToolbar.Visibility = hasDocument || MapButton.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        BackButton.IsEnabled = _model.History.CanBack || (_model.History.Current != null && !hasDocument);
        ForwardButton.IsEnabled = _model.History.CanForward;
        Welcome.Visibility = hasDocument || view == "map" ? Visibility.Collapsed : Visibility.Visible;
        ReaderSlot.Visibility = hasDocument || _reader != null ? Visibility.Visible : Visibility.Collapsed;
        ReaderSlot.Opacity = view == "map" ? 0 : 1;
        ReaderSlot.IsHitTestVisible = view != "map";
        if (_reader is { } reader)
        {
            // Keep the renderer available for exports while the map owns keyboard input.
            reader.Control.IsTabStop = view != "map";
        }
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
        // ShowMapAsync refreshes an existing projection when the user returns to it.
        if (_model.View == "map" && _mapWorkspace == _model.Workspace && _mapWorkspace != null)
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
            result = await WindowsDialogs.ShowAsync(dialog) == ContentDialogResult.Primary;
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
        if (_tracksContrastChanges)
        {
            _accessibility.HighContrastChanged -= OnHighContrastChanged;
        }
        _lifetime.Cancel();
        if (_ai is { } assistant)
        {
            assistant.Changed -= SyncMapReferencePaths;
            assistant.Cancel();
        }
        _watcher.Dispose();
        _reader?.Dispose();
        _preview?.Dispose();
        _map?.Dispose();
        _model.CatalogChanged -= CatalogChanged;
        _model.Dispose();
        _lifetime.Dispose();
    }
}
