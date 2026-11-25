/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; WinUI map toolbar, inspector and native graph interactions
 * Human guidance: retain the complete document navigation and layout tools in the main Windows application
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using MarkVibe.Core;
using MarkVibe.Native;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.Devices.Input;
using Windows.Foundation;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using static MarkVibe.Core.UiStrings;

namespace MarkVibe.Windows;

/// <summary>Presents the Windows map model through native toolbars, controls and a Win2D drawing surface.</summary>
internal sealed partial class WindowsMapView : UserControl, IDisposable
{
    private readonly WindowsGraphCanvas _canvas = new();
    private readonly bool _touchOnly = new TouchCapabilities().TouchPresent > 0 && new MouseCapabilities().MousePresent == 0;
    private readonly CommandBar _toolbar = new()
    {
        DefaultLabelPosition = CommandBarDefaultLabelPosition.Right,
        OverflowButtonVisibility = CommandBarOverflowButtonVisibility.Auto,
        IsDynamicOverflowEnabled = true,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalContentAlignment = VerticalAlignment.Center,
        Background = new SolidColorBrush(Colors.Transparent),
        Padding = new Thickness(0)
    };
    private readonly AppBarButton _fitButton = new() { Icon = new SymbolIcon(Symbol.FullScreen), DynamicOverflowOrder = 4 };
    private readonly AppBarButton _arrangeButton = new() { Icon = new SymbolIcon(Symbol.Sort), DynamicOverflowOrder = 2 };
    private readonly AppBarButton _optionsButton = new() { Icon = new SymbolIcon(Symbol.Setting), DynamicOverflowOrder = 1 };
    private readonly AppBarToggleButton _inspectorToggle = new() { Icon = new FontIcon { Glyph = "\uE946" }, DynamicOverflowOrder = 3 };
    private readonly StackPanel _legend = new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 14,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false
    };
    private readonly TextBlock _incomingCaption = new() { FontSize = 12 };
    private readonly TextBlock _outgoingCaption = new() { FontSize = 12 };
    private readonly SolidColorBrush _incomingInk = new();
    private readonly SolidColorBrush _outgoingInk = new();
    private readonly ThemeShadow _toolbarShadow = new();
    private bool _toolbarPointerOver;
    private bool _toolbarFocused;
    private bool _highContrast;
    private readonly NativeWrapPanel _arrangements = new() { Spacing = 6 };
    private readonly StackPanel _picked = new() { Orientation = Orientation.Horizontal, Spacing = 5 };
    private readonly ScrollViewer _pickedScroll;
    private readonly StackPanel _inspector = new() { Spacing = 7, Margin = new Thickness(12) };
    private readonly Border _chrome = new()
    {
        Padding = new Thickness(12, 2, 12, 2),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Top
    };
    private readonly HashSet<string> _referencePaths = new(StringComparer.Ordinal);
    private readonly AutoSuggestBox _search = new() { Width = 220, TextMemberPath = "Title", QueryIcon = new SymbolIcon(Symbol.Find) };
    private readonly TextBlock _searchFeedback = new() { Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly ToggleButton _folders = new() { Content = "Folders" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ListView _compact = new() { IsItemClickEnabled = true, Visibility = Visibility.Collapsed };
    private readonly Border _comparison = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(10), Padding = new Thickness(12), CornerRadius = new CornerRadius(8) };
    private readonly StackPanel _comparisonDocuments = new() { Orientation = Orientation.Horizontal, Spacing = 12 };
    private Button? _comparisonClose;
    private IReadOnlyList<MapExplorationDocument> _displayedComparison = [];
    private IReadOnlyList<MapExplorationDocument> _displayedPicked = [];
    private IReadOnlyList<MapExplorationDocument> _displayedTrail = [];
    private readonly ColumnDefinition _inspectorColumn = new() { Width = new GridLength(292) };
    private readonly Thumb _split = new()
    {
        Style = (Style)Application.Current.Resources["PaneResizeGripStyle"],
        HorizontalAlignment = HorizontalAlignment.Left,
        Margin = new Thickness(-3, 0, 0, 0)
    };
    private readonly ScrollViewer _inspectorScroll;
    private CancellationToken _workspaceLifetime;
    private WindowsMapModel? _model;
    private bool _refreshing;
    private bool _reloading;
    private bool _details;
    private bool _compactDetails;
    private bool _incomingOpen;
    private bool _outgoingOpen;
    private bool _outlineOpen;
    private bool _routeOpen;
    private bool _actionsOpen;
    private bool _acrylic;
    private string _graphFont = "system";
    private WindowsMapPalette _palette = WindowsMapPalette.Dark;
    private double _inspectorWidth = 292;
    private int _loadGeneration;
    private MapSnapshot? _inspectorSnapshot;
    private (string? Path, string? Color, string? Icon, bool Advanced, MapAtlasSettings? Atlas) _inspectorPresentation;

    public event Action<string, string>? OpenDocument;
    public event Action<string, string>? PreviewDocument;
    public Func<FileMenuTarget, IReadOnlyList<DocumentMenuItem>>? FileActions { get; set; }
    public Func<FileMenuTarget, MenuFlyout?>? CreateLinkMenu { get; set; }
    public event Action<FileMenuTarget, string>? FileActionRequested;
    public event Action<string>? DocumentSelected;
    public event Action<IReadOnlyList<string>>? AddToAiContext;
    public event Action<string>? ToggleAiContext;
    public event Action<string>? StatusChanged;
    public event Action<double>? InspectorWidthChanged;
    public Func<bool>? IsPreviewOpen { get; set; }
    public double PreviewTop => _chrome.ActualHeight + _chrome.Margin.Top + _chrome.Margin.Bottom;

    /// <summary>Creates native map chrome and routes every graph gesture to an application model command.</summary>
    public WindowsMapView()
    {
        var root = new Grid();
        var chrome = new StackPanel { Spacing = 3 };
        chrome.Children.Add(_toolbar);
        chrome.Children.Add(_searchFeedback);
        _pickedScroll = ScrollRow(_picked);
        _pickedScroll.Visibility = Visibility.Collapsed;
        chrome.Children.Add(_pickedScroll);
        chrome.Children.Add(_status);
        _status.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        _chrome.Child = chrome;
        WindowsPaperSurface.Attach(_chrome);
        _chrome.BorderThickness = new Thickness(0, 0, 0, 1);
        _chrome.Loaded += (_, _) => UpdateChromeInset();
        _chrome.SizeChanged += (_, _) => UpdateChromeInset();

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.ColumnDefinitions.Add(_inspectorColumn);
        body.Children.Add(_canvas);
        body.Children.Add(_compact);
        BuildComparison();
        body.Children.Add(_comparison);
        Grid.SetColumn(_split, 1);
        AutomationProperties.SetAutomationId(_split, "MapInspectorGrip");
        AutomationProperties.SetName(_split, "Resize map inspector");
        _split.DragDelta += (_, args) =>
        {
            RestoreInspectorWidth(_inspectorWidth - args.HorizontalChange);
        };
        _split.DragCompleted += (_, _) => InspectorWidthChanged?.Invoke(_inspectorWidth);
        _split.KeyDown += (_, args) =>
        {
            if (args.Key is VirtualKey.Left or VirtualKey.Right)
            {
                RestoreInspectorWidth(_inspectorWidth + (args.Key == VirtualKey.Left ? 20 : -20));
                InspectorWidthChanged?.Invoke(_inspectorWidth);
                args.Handled = true;
            }
        };
        _inspectorScroll = new ScrollViewer
        {
            Content = _inspector,
            Visibility = Visibility.Collapsed,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        _inspectorScroll.KeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Escape && _compact.Visibility == Visibility.Visible && _compactDetails)
            {
                HideCompactInspector();
                _inspectorToggle.Focus(FocusState.Keyboard);
                args.Handled = true;
            }
        };
        Grid.SetColumn(_inspectorScroll, 1);
        body.Children.Add(_inspectorScroll);
        body.Children.Add(_chrome);
        body.Children.Add(_legend);
        body.Children.Add(_split);
        _toolbarShadow.Receivers.Add(_canvas);
        _toolbarShadow.Receivers.Add(_compact);
        _chrome.PointerEntered += (_, _) => { _toolbarPointerOver = true; UpdateToolbarElevation(); };
        _chrome.PointerExited += (_, _) => { _toolbarPointerOver = false; UpdateToolbarElevation(); };
        _chrome.GotFocus += (_, _) => { _toolbarFocused = true; UpdateToolbarElevation(); };
        _chrome.LostFocus += (_, _) => { _toolbarFocused = false; UpdateToolbarElevation(); };
        root.Children.Add(body);
        Content = root;
        SizeChanged += (_, _) => UpdateViewport();
        _canvas.SizeChanged += (_, args) =>
        {
            if (_details && args.NewSize.Width < args.PreviousSize.Width && _canvas.Visibility == Visibility.Visible)
            {
                _canvas.KeepSelectionVisible(args.PreviousSize, args.NewSize);
            }
        };
        Unloaded += (_, _) => SuspendInteractions();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            if (Visibility == Visibility.Collapsed)
            {
                SuspendInteractions();
            }
        });
        ConfigureCompactList();

        BuildToolbar();
        SetAppearance(ActualTheme == ElementTheme.Dark, acrylic: false);
        ApplyLanguage();
        UiStrings.Changed += ApplyLanguage;
        _canvas.Selected += path => Select(path, false);
        _canvas.Opened += path => OpenDocument?.Invoke(path, "");
        _canvas.PreviewLink += edge => PreviewDocument?.Invoke(edge.Target, edge.Anchor);
        _canvas.ReadHere += path => PreviewDocument?.Invoke(path, "");
        _canvas.Moved += (path, x, y) => Change(model => model.Move(path, x, y));
        _canvas.Resized += (path, width, height) => Change(model => model.Resize(path, width, height));
        _canvas.Picked += path => Change(model => model.TogglePick(path));
        _canvas.RectanglePicked += (paths, append) => Change(model => model.Pick(paths, append));
        _canvas.FolderToggled += path => Change(model => model.ToggleFolder(path));
        _canvas.ContextMenu += ShowCardMenu;
        _canvas.ViewportDrawn += () => PreviewPlacementChanged?.Invoke();
    }

    /// <summary>Restores the shell model's preferred inspector width within schema limits.</summary>
    public void RestoreInspectorWidth(double width)
    {
        _inspectorWidth = double.IsFinite(width) ? Math.Clamp(width, 220, 600) : 292;
        UpdateViewport();
    }

    /// <summary>Refreshes reference membership only when the authoritative workspace collection changes.</summary>
    public void SetReferencePaths(IReadOnlyList<string> paths)
    {
        if (_referencePaths.SetEquals(paths))
        {
            return;
        }
        _referencePaths.Clear();
        _referencePaths.UnionWith(paths);
        _canvas.SetReferencePaths(paths);
        _inspectorSnapshot = null;
        RenderModel();
    }

    /// <summary>Loads the workspace layout after the shell has a complete catalog inventory.</summary>
    public async Task LoadAsync(NativeFiles files, WorkspaceInfo workspace, DocumentCatalog catalog, string? selectedPath,
        IReadOnlyList<string> visitedPaths, CancellationToken token)
    {
        if (_model?.IsCurrent(workspace, catalog) == true)
        {
            Refresh(selectedPath);
            return;
        }
        var generation = ++_loadGeneration;
        var model = new WindowsMapModel(files, workspace, catalog);
        model.SetDepth(_depth.IsOn);
        model.SetAdvanced(_advanced.IsOn);
        model.SeedTrail(visitedPaths);
        await model.ReloadAsync(selectedPath, token);
        token.ThrowIfCancellationRequested();
        if (_loadGeneration != generation)
        {
            return;
        }
        _workspaceLifetime = token;
        _model = model;
        _incomingOpen = _outgoingOpen = _outlineOpen = _routeOpen = _actionsOpen = false;
        ClearSearch();
        RenderModel();
        _canvas.Fit();
        _canvas.AnimateEntrance();
        await SaveQuietlyAsync(model);
    }

    /// <summary>Refreshes map metadata or records a successfully opened document without reading source from the view.</summary>
    public void Refresh(string? selectedPath)
    {
        if (_model != null)
        {
            Change(current => current.Refresh(selectedPath));
        }
    }

    /// <summary>Detaches a workspace after the shell has resolved outstanding map edits.</summary>
    public void Clear()
    {
        SuspendInteractions();
        _referencePaths.Clear();
        _canvas.SetReferencePaths([]);
        _loadGeneration++;
        _model = null;
        UpdateLegendVisibility();
        _canvas.Clear();
        _compact.ItemsSource = null;
        _inspector.Children.Clear();
        _inspectorSnapshot = null;
        _inspectorPresentation = default;
        _picked.Children.Clear();
        _trail.Children.Clear();
        _pickedScroll.Visibility = _trailSection.Visibility = Visibility.Collapsed;
        _comparison.Visibility = Visibility.Collapsed;
        _displayedComparison = _displayedPicked = _displayedTrail = [];
        ClearSearch();
        PresentStatus("");
        _summary.Text = "";
    }

    /// <summary>Flushes pending edits and gives the owner an explicit way to keep or discard unpublished layout changes.</summary>
    public async Task<bool> ConfirmLeaveAsync(CancellationToken token)
    {
        if (_model is not { } model)
        {
            return true;
        }
        await model.SaveAsync(token);
        if (!model.Dirty)
        {
            return true;
        }
        var choice = await WindowsDialogs.ShowAsync(new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Unsaved map layout",
            Content = "Save a separate copy or explicitly discard your layout changes before leaving this workspace.",
            PrimaryButtonText = "Save a copy…",
            SecondaryButtonText = "Discard",
            CloseButtonText = "Stay here",
            DefaultButton = ContentDialogButton.Close
        });
        return choice == ContentDialogResult.Secondary || choice == ContentDialogResult.Primary && await SaveCopyAsync(model);
    }

    /// <summary>Populates native arrangement, exploration, search and viewpoint controls in keyboard order.</summary>
    private void BuildToolbar()
    {
        _toolbar.Content = _search;
        _search.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(_toolbar, "MapToolbar");
        AutomationProperties.SetAutomationId(_search, "MapSearch");
        AutomationProperties.SetAutomationId(_fitButton, "Fit");
        AutomationProperties.SetAutomationId(_arrangeButton, "MapArrange");
        AutomationProperties.SetAutomationId(_optionsButton, "MapOptions");
        AutomationProperties.SetAutomationId(_inspectorToggle, "MapDetails");
        AutomationProperties.SetAutomationId(_legend, "MapRelationLegend");
        _toolbar.PrimaryCommands.Add(_fitButton);
        _toolbar.PrimaryCommands.Add(_arrangeButton);
        _toolbar.PrimaryCommands.Add(_optionsButton);
        _toolbar.PrimaryCommands.Add(_inspectorToggle);
        _fitButton.Click += (_, _) => _canvas.Fit();
        _arrangeButton.Flyout = MapMenu(_arrangements);
        _optionsButton.Flyout = BuildMapOptions();
        WindowsPaperSurface.AttachFlyout(_arrangeButton.Flyout);
        WindowsPaperSurface.AttachFlyout(_optionsButton.Flyout);
        _inspectorToggle.Click += (_, _) => ToggleInspector();
        _legend.Children.Add(CreateLegendKey(_incomingCaption, _incomingInk, incoming: true));
        _legend.Children.Add(CreateLegendKey(_outgoingCaption, _outgoingInk, incoming: false));

        foreach (var preset in new[] { "Star", "Circle", "Grid", "Tree" })
        {
            _arrangements.Children.Add(ButtonFor(preset, () => Arrange(preset.ToLowerInvariant())));
        }
        _arrangements.Children.Add(_folders);
        _folders.Click += (_, _) => Arrange("folders");
        _search.ItemTemplate = (DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <StackPanel Spacing="2" AutomationProperties.Name="{Binding Path}" ToolTipService.ToolTip="{Binding Path}">
                    <TextBlock Text="{Binding Title}" TextTrimming="CharacterEllipsis" />
                    <TextBlock Text="{Binding Path}" FontSize="11" Opacity="0.75" TextTrimming="CharacterEllipsis" />
                </StackPanel>
            </DataTemplate>
            """);
        _search.TextChanged += (_, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            {
                return;
            }
            var query = _search.Text.Trim();
            var results = _model?.Search(query) ?? [];
            _search.ItemsSource = results;
            _searchFeedback.Visibility = query.Length > 0 && results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        _search.KeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Escape && _search.Text.Length > 0)
            {
                ClearSearch();
                args.Handled = true;
            }
        };
        _search.QuerySubmitted += (_, args) =>
        {
            var card = args.ChosenSuggestion as DocumentCard ?? _model?.Search(args.QueryText).FirstOrDefault();
            if (card != null)
            {
                _searchFeedback.Visibility = Visibility.Collapsed;
                Select(card.Path, true);
            }
            else if (!string.IsNullOrWhiteSpace(args.QueryText))
            {
                _searchFeedback.Visibility = Visibility.Visible;
            }
        };

    }

    /// <summary>Applies an arrangement and animates the measured viewport into the resulting layout.</summary>
    private void Arrange(string preset)
    {
        Change(model => model.Arrange(preset), animateArrangement: true);
        _canvas.Fit();
        _canvas.AnimateEntrance();
    }

    /// <summary>Clears the query and its feedback without changing the selected page.</summary>
    private void ClearSearch()
    {
        _search.Text = "";
        _search.ItemsSource = null;
        _search.IsSuggestionListOpen = false;
        _searchFeedback.Visibility = Visibility.Collapsed;
    }

    /// <summary>Disables viewpoint actions when they have no target.</summary>
    private void UpdateViewCommands()
    {
        _restoreView.IsEnabled = _views.SelectedItem is MapViewpoint;
        _deleteView.IsEnabled = _restoreView.IsEnabled;
    }

    /// <summary>Toggles the shared inspector in the current responsive layout.</summary>
    private void ToggleInspector()
    {
        if (_compact.Visibility == Visibility.Visible)
        {
            _compactDetails = !_compactDetails;
        }
        else
        {
            _details = !_details;
        }
        UpdateViewport();
    }

    /// <summary>Clears the compact inspector overlay so the inline reader can take its place.</summary>
    public void HideCompactInspector()
    {
        if (_compactDetails)
        {
            _compactDetails = false;
            UpdateViewport();
        }
    }

    /// <summary>Updates native labels from the shared language catalog while leaving document titles literal.</summary>
    private void ApplyLanguage()
    {
        _refreshing = true;
        _inspectorSnapshot = null;
        _displayedComparison = _displayedPicked = _displayedTrail = [];
        try
        {
            foreach (var button in _arrangements.Children.OfType<Button>())
            {
                if (button.Tag is string label)
                {
                    if (button.Content is string)
                    {
                        button.Content = T(label);
                    }
                    SetCommandHint(button, CommandHint(label));
                }
            }
            _search.PlaceholderText = T("Find a document");
            AutomationProperties.SetName(_toolbar, T("Map toolbar"));
            _fitButton.Label = T("Fit map");
            _arrangeButton.Label = T("Arrange");
            _optionsButton.Label = T("Map options");
            _inspectorToggle.Label = T("Details");
            SetToolbarHint(_fitButton, _fitButton.Label, "Fit document map");
            SetToolbarHint(_arrangeButton, _arrangeButton.Label, "Map arrangement");
            SetToolbarHint(_optionsButton, _optionsButton.Label, "Map options");
            SetToolbarHint(_inspectorToggle, _inspectorToggle.Label, "Document preview");
            _incomingCaption.Text = T("Incoming");
            _outgoingCaption.Text = T("Outgoing");
            _searchFeedback.Text = T("No matching documents");
            AutomationProperties.SetName(_split, T("Resize document preview"));
            _folders.Content = T("Folders");
            SetCommandHint(_folders, "Group by folder");
            SetCommandHint(_split, "Resize document preview");
            SetToolbarHint(_search, _search.PlaceholderText, "Find a document");
            LocalizeMapOptions();
        }
        finally
        {
            _refreshing = false;
        }
        RenderModel();
    }

    /// <summary>Updates one document selection and optionally reveals and navigates its camera location.</summary>
    private void Select(string path, bool center)
    {
        DocumentSelected?.Invoke(path);
        Change(model => model.Select(path, center));
        if (center)
        {
            if (_compact.Visibility == Visibility.Visible && _model?.Document(path) is { } card)
            {
                _compact.ScrollIntoView(card);
            }
            else
            {
                _canvas.Center(path);
            }
        }
    }

    /// <summary>Invokes a managed edit and refreshes the view from the accepted model snapshot.</summary>
    private async void Change(Action<WindowsMapModel> command, bool animateArrangement = false)
    {
        if (_refreshing || _reloading || _model is not { } model)
        {
            return;
        }
        try
        {
            command(model);
            RenderModel(animateArrangement);
            await SaveQuietlyAsync(model);
        }
        catch (Exception error) when (error is WorkspaceException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            PresentStatus(error.Message);
            _canvas.Show(model);
        }
    }

    /// <summary>Updates local controls, the inspector and graph from the current intermediate data model.</summary>
    private void RenderModel(bool animateArrangement = false)
    {
        if (_model is not { } model)
        {
            return;
        }
        _refreshing = true;
        try
        {
            var stats = model.Exploration;
            _depth.IsOn = model.Depth;
            _advanced.IsOn = model.Advanced;
            _lens.IsOn = stats.LensEnabled;
            UpdateLegendVisibility();
            _picking.IsOn = stats.SelectionEnabled;
            _folders.IsChecked = model.Atlas.FolderGroups;
            var referenceLabel = ReferenceLabel(model.SelectedPath);
            _referenceCaption.Text = T(referenceLabel);
            _referenceButton.IsEnabled = model.SelectedPath.Length > 0;
            SetToolbarHint(_referenceButton, T(referenceLabel), referenceLabel);
            _distance.SelectedIndex = model.Atlas.FocusDepth;
            _attention.SelectedIndex = Math.Max(0, Array.IndexOf(AttentionModes, stats.AttentionMode));
            var views = model.Atlas.Views ?? [];
            if (!ReferenceEquals(_views.ItemsSource, views))
            {
                var selectedView = (_views.SelectedItem as MapViewpoint)?.Name;
                _views.ItemsSource = views;
                _views.SelectedItem = views.FirstOrDefault(view => view.Name == selectedView) ?? views.FirstOrDefault();
                _views.IsEnabled = views.Count > 0;
                UpdateViewCommands();
            }
            var snapshot = model.Snapshot;
            var culture = System.Globalization.CultureInfo.GetCultureInfo(UiStrings.Language);
            _summary.Text = T("{0} of {1} documents · {2} links", snapshot.Nodes.Count.ToString("N0", culture),
                snapshot.TotalDocuments.ToString("N0", culture), snapshot.Edges.Count.ToString("N0", culture)) +
                (stats.IndexComplete ? "" : " · " + T("Index incomplete")) +
                (stats.AttentionMode == "all" ? "" : " · " + T("{0} flagged", stats.AttentionCount.ToString("N0", culture)));
            if (!ReferenceEquals(_compact.ItemsSource, model.Cards))
            {
                _compact.ItemsSource = model.Cards;
            }
            _compact.SelectedItem = model.Document(model.SelectedPath);
            _canvas.Show(model, animateArrangement);
            RenderShelves(stats);
            RenderComparison(stats);
            UpdateInspector(model);
            PresentStatus(model.Message);
            UpdateLayoutCommands(model);
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>Shows selected and recently visited documents without changing catalog or visit history.</summary>
    private void RenderShelves(MapExplorationSnapshot stats)
    {
        if (!stats.Picked.SequenceEqual(_displayedPicked))
        {
            _displayedPicked = stats.Picked;
            _picked.Children.Clear();
            _pickedScroll.Visibility = stats.Picked.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (stats.Picked.Count > 0)
            {
                _picked.Children.Add(Label(T("{0} selected", stats.Picked.Count)));
                foreach (var document in stats.Picked)
                {
                    _picked.Children.Add(ButtonFor(document.Title, () => Select(document.Path, true), localized: false));
                    _picked.Children.Add(ButtonFor("×", () => Change(model => model.TogglePick(document.Path)), "Remove " + document.Title));
                }
                _picked.Children.Add(ButtonFor("Clear", () => Change(model => model.Pick([], false))));
                if (stats.Picked.Count >= 2)
                {
                    _picked.Children.Add(ButtonFor("Compare documents", () => Change(model => model.SetCompare(true))));
                }
                _picked.Children.Add(ButtonFor("Add to AI context", () => AddToAiContext?.Invoke(stats.Picked.Select(document => document.Path).ToArray())));
            }
        }

        if (stats.Trail.SequenceEqual(_displayedTrail))
        {
            return;
        }
        _displayedTrail = stats.Trail;
        _trail.Children.Clear();
        _trailSection.Visibility = stats.Trail.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var document in stats.Trail)
        {
            var command = ButtonFor(document.Title, () => Select(document.Path, true), localized: false);
            StyleMapAction(command, Symbol.Document, Label(document.Title));
            AutomationProperties.SetName(command, document.Title);
            ToolTipService.SetToolTip(command, document.Path);
            _trail.Children.Add(command);
        }
    }

    /// <summary>Refreshes the visible inspector only when its data or presentation changed.</summary>
    private void UpdateInspector(WindowsMapModel model)
    {
        if (_inspectorScroll.Visibility != Visibility.Visible)
        {
            return;
        }
        var cardStyle = model.Layout.Nodes.GetValueOrDefault(model.SelectedPath);
        var presentation = (model.SelectedPath, cardStyle?.Color, cardStyle?.Icon, model.Advanced, model.Layout.Atlas);
        if (!ReferenceEquals(_inspectorSnapshot, model.Snapshot) || _inspectorPresentation != presentation)
        {
            RenderInspector(model);
            _inspectorSnapshot = model.Snapshot;
            _inspectorPresentation = presentation;
        }
    }

    /// <summary>Renders the current document, headings, graph links and related native commands.</summary>
    private void RenderInspector(WindowsMapModel model)
    {
        _inspector.Children.Clear();
        if (model.Document(model.SelectedPath) is not { } card)
        {
            _inspector.Children.Add(Label("No documents in this map"));
            return;
        }
        var heading = Label(card.Title, 20);
        heading.FontWeight = FontWeights.Light;
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        _inspector.Children.Add(heading);
        _inspector.Children.Add(Label(card.Path, 13));
        if (card.Introduction.Length > 0)
        {
            _inspector.Children.Add(Label(card.Introduction));
        }
        if (model.Advanced)
        {
            _inspector.Children.Add(Label(WindowsGraphCanvas.Statistics(card.Statistics), 12));
        }
        var primary = new NativeWrapPanel { Spacing = 8, Margin = new Thickness(0, 3, 0, 5) };
        primary.Children.Add(InspectorButton("Open document", () => OpenDocument?.Invoke(card.Path, "")));
        primary.Children.Add(InspectorButton("Read here", () => PreviewDocument?.Invoke(card.Path, "")));
        _inspector.Children.Add(primary);

        var map = model.Snapshot;
        var relations = model.DocumentRelations(card.Path);
        var incoming = relations.Incoming.Select(document => (document.Title, document.Path, "")).ToArray();
        var outgoing = relations.Outgoing.Select(link => (model.Document(link.Path)?.Title ?? Path.GetFileName(link.Path), link.Path, link.Anchor)).ToArray();
        _inspector.Children.Add(LinkSection("Links to this page", true, incoming));
        _inspector.Children.Add(LinkSection("Links from this page", false, outgoing));

        var headings = card.Headings.Take(12).ToArray();
        if (headings.Length > 0)
        {
            var caption = T("Outline") + " · " + headings.Length.ToString(System.Globalization.CultureInfo.CurrentCulture);
            _inspector.Children.Add(InspectorSection(caption, BuildOutline, _outlineOpen, open => _outlineOpen = open));
        }

        var start = model.RouteStart.Length > 0 ? T("Route start: {0}", model.RouteStart) : null;
        var result = map.RouteMessage.Length > 0 ? T(map.RouteMessage, Math.Max(0, (map.Route?.Count ?? 1) - 1)) : null;
        _inspector.Children.Add(InspectorSection(T("Route"), BuildRoute, _routeOpen, open => _routeOpen = open, result ?? start));
        _inspector.Children.Add(InspectorSection(T("More actions"), BuildActions, _actionsOpen, open => _actionsOpen = open));

        if (model.Atlas.FolderGroups && map.Folders is { Count: > 0 })
        {
            var folders = new StackPanel { Spacing = 4 };
            foreach (var folder in map.Folders)
            {
                var collapsed = model.Atlas.CollapsedFolders?.Contains(folder.Path, StringComparer.Ordinal) == true;
                var name = folder.Path.Length == 0 ? "Workspace" : folder.Path;
                folders.Children.Add(ButtonFor($"{(collapsed ? "Expand" : "Collapse")} {name} ({folder.Count})", () => Change(state => state.ToggleFolder(folder.Path))));
            }
            _inspector.Children.Add(Section("Folder islands", folders, false));
        }

        /// <summary>Shows the selected page's bounded heading list with the existing reader and preview behavior.</summary>
        StackPanel BuildOutline()
        {
            var rows = new StackPanel { Spacing = 4 };
            foreach (var heading in headings)
            {
                var row = InspectorButton(heading.Text, () => OpenFromInspector(card.Path, heading.Id), localized: false);
                row.HorizontalAlignment = HorizontalAlignment.Stretch;
                row.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                rows.Children.Add(row);
            }
            return rows;
        }

        /// <summary>Creates route commands and clickable steps only when the route section is expanded.</summary>
        StackPanel BuildRoute()
        {
            var panel = new StackPanel { Spacing = 5 };
            if (result != null && start != null)
            {
                panel.Children.Add(Label(start));
            }
            var commands = new NativeWrapPanel { Spacing = 6 };
            commands.Children.Add(InspectorButton("Set route start", () => Change(state => state.StartRoute())));
            if (start != null)
            {
                commands.Children.Add(InspectorButton("Find route to this document", () => Change(state => state.FinishRoute())));
                commands.Children.Add(InspectorButton("Clear route", () => Change(state => state.ClearRoute())));
            }
            panel.Children.Add(commands);
            if (result != null)
            {
                panel.Children.Add(Label(T("Connections in either direction")));
                foreach (var path in map.Route ?? [])
                {
                    panel.Children.Add(LinkRow(model.Document(path)?.Title ?? path, path, "", incoming: false, center: true));
                }
            }
            return panel;
        }

        /// <summary>Exposes card appearance and the current AI context action for the selected document.</summary>
        StackPanel BuildActions()
        {
            var panel = new StackPanel { Spacing = 4 };
            panel.Children.Add(InspectorButton("Customize card", () => ShowCardMenu(card.Path, _inspectorScroll)));
            panel.Children.Add(InspectorButton(ReferenceLabel(card.Path), () => ToggleAiContext?.Invoke(card.Path)));
            return panel;
        }
    }

    /// <summary>Shows a native disclosure with an optional summary that remains visible when collapsed.</summary>
    private static Expander InspectorSection(string title, Func<FrameworkElement> buildContent, bool expanded,
        Action<bool> expansionChanged, string? summary = null)
    {
        var header = new StackPanel { Spacing = 2 };
        header.Children.Add(Label(title));
        if (summary != null)
        {
            header.Children.Add(Label(summary, 12));
        }
        var section = new Expander
        {
            Header = header,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(section, summary == null ? title : title + " · " + summary);
        section.Expanding += (_, _) =>
        {
            section.Content ??= buildContent();
            expansionChanged(true);
        };
        section.Collapsed += (_, _) => expansionChanged(false);
        if (expanded)
        {
            section.Content = buildContent();
            section.IsExpanded = true;
        }
        return section;
    }

    /// <summary>Creates a keyboard-focusable command whose native caption wraps in the narrow inspector.</summary>
    private static Button InspectorButton(string label, Action action, bool localized = true)
    {
        var caption = localized ? T(label) : label;
        var button = ButtonFor(caption, action, caption, localized: false);
        button.Content = Label(caption);
        return button;
    }

    /// <summary>Displays direction and known link counts, creating rows only when the section is expanded.</summary>
    private Expander LinkSection(string title, bool incoming, IReadOnlyList<(string Title, string Path, string Anchor)> links)
    {
        var count = links.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);
        var section = new Expander
        {
            Header = Label((incoming ? "← " : "→ ") + T(title) + " · " + count),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(section, T(title) + " · " + count);
        section.Expanding += (_, _) =>
        {
            section.Content ??= BuildRows();
            if (incoming) { _incomingOpen = true; }
            else { _outgoingOpen = true; }
        };
        section.Collapsed += (_, _) =>
        {
            if (incoming) { _incomingOpen = false; }
            else { _outgoingOpen = false; }
        };
        if (incoming ? _incomingOpen : _outgoingOpen)
        {
            section.Content = BuildRows();
            section.IsExpanded = true;
        }
        return section;

        /// <summary>Materializes links in batches so a highly connected page can expose all links without creating every control at once.</summary>
        StackPanel BuildRows()
        {
            var rows = new StackPanel { Spacing = 5 };
            if (links.Count == 0)
            {
                rows.Children.Add(Label(T("No links yet")));
                return rows;
            }
            const int batchSize = 40;
            var rendered = 0;
            var more = new Button { Content = T("Show more links") };
            more.Click += (_, _) => AppendBatch(moveFocus: true);
            AppendBatch(moveFocus: false);
            return rows;

            /// <summary>Appends the next stable batch and moves focus away from a removed footer button.</summary>
            void AppendBatch(bool moveFocus)
            {
                rows.Children.Remove(more);
                FrameworkElement? first = null;
                var end = Math.Min(rendered + batchSize, links.Count);
                for (; rendered < end; rendered++)
                {
                    var (label, path, anchor) = links[rendered];
                    var row = LinkRow(label, path, anchor, incoming);
                    first ??= row;
                    rows.Children.Add(row);
                }
                if (rendered < links.Count)
                {
                    AutomationProperties.SetName(more, T("Show more links") + " · " + T("{0} of {1}", rendered, links.Count));
                    rows.Children.Add(more);
                }
                if (moveFocus && first is Control control)
                {
                    control.Focus(FocusState.Programmatic);
                }
            }
        }
    }

    /// <summary>Builds a keyboard-focusable link row with native file actions.</summary>
    private FrameworkElement LinkRow(string title, string path, string anchor, bool incoming, bool center = false)
    {
        var available = _model?.Document(path) != null;
        var row = ButtonFor(title, () =>
        {
            if (!available) { PresentStatus(T("Missing document") + " · " + path); }
            else if (center) { Select(path, true); }
            else { OpenFromInspector(path, anchor); }
        }, localized: false);
        var text = new StackPanel { Spacing = 3 };
        text.Children.Add(Label(title));
        var reference = path + (anchor.Length > 0 ? "#" + anchor : "");
        text.Children.Add(new TextBlock { Text = reference, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
        var content = new Grid { ColumnSpacing = 8 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition());
        content.Children.Add(CardIcon(MapLayoutCodec.DefaultIcon, incoming ? Color.FromArgb(255, 113, 215, 241) : Color.FromArgb(255, 175, 131, 239)));
        Grid.SetColumn(text, 1);
        content.Children.Add(text);
        row.Content = content;
        row.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        row.HorizontalAlignment = HorizontalAlignment.Stretch;
        var description = available ? reference : reference + " · " + T("Missing document");
        AutomationProperties.SetName(row, title + " · " + description);
        ToolTipService.SetToolTip(row, description);
        if (!available)
        {
            text.Children.Add(Label(T("Missing document")));
            return row;
        }
        if (_model is { } model)
        {
            var target = new FileMenuTarget(model.WorkspaceId, path, title);
            row.ContextRequested += (_, args) =>
            {
                if (!ReferenceEquals(_model, model) || CreateLinkMenu?.Invoke(target) is not { } menu)
                {
                    return;
                }
                menu.Items.Insert(0, MenuItem("Focus on this page", () => Select(path, true)));
                menu.Items.Insert(0, MenuItem("Open document", () => OpenDocument?.Invoke(path, anchor)));
                menu.Items.Insert(0, MenuItem("Read here", () => PreviewDocument?.Invoke(path, anchor)));
                if (args.TryGetPosition(row, out var point)) { menu.ShowAt(row, new FlyoutShowOptions { Position = point }); }
                else { menu.ShowAt(row); }
                args.Handled = true;
            };
        }
        return row;
    }

    /// <summary>Follows inspector links in the open preview, or opens the normal reader if that preview is closed.</summary>
    private void OpenFromInspector(string path, string anchor)
    {
        if (IsPreviewOpen?.Invoke() == true)
        {
            PreviewDocument?.Invoke(path, anchor);
        }
        else
        {
            OpenDocument?.Invoke(path, anchor);
        }
    }

    /// <summary>Hosts the native comparison beside the graph's existing navigation and shelves.</summary>
    private void BuildComparison()
    {
        var panel = new Grid { RowSpacing = 8 };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition());
        var header = new Grid();
        header.Children.Add(Title(T("Compare documents")));
        _comparisonClose = ButtonFor("×", () => Change(model => model.SetCompare(false)), T("Close"), localized: false);
        _comparisonClose.HorizontalAlignment = HorizontalAlignment.Right;
        header.Children.Add(_comparisonClose);
        panel.Children.Add(header);
        var scroll = new ScrollViewer { Content = _comparisonDocuments, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1);
        panel.Children.Add(scroll);
        _comparison.BorderThickness = new Thickness(1);
        _comparison.Child = panel;
        _comparison.KeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Escape)
            {
                Change(model => model.SetCompare(false));
                args.Handled = true;
            }
        };
    }

    /// <summary>Shows bounded side-by-side comparison using only the catalog's previews.</summary>
    private void RenderComparison(MapExplorationSnapshot stats)
    {
        var wasOpen = _comparison.Visibility == Visibility.Visible;
        _comparison.Visibility = stats.CompareOpen ? Visibility.Visible : Visibility.Collapsed;
        if (!stats.CompareOpen || (wasOpen && stats.Picked.SequenceEqual(_displayedComparison)))
        {
            return;
        }
        _displayedComparison = stats.Picked;
        _comparisonDocuments.Children.Clear();
        foreach (var document in stats.Picked)
        {
            var panel = new StackPanel { Width = 228, Spacing = 8 };
            panel.Children.Add(Title(document.Title));
            panel.Children.Add(Label(document.Path));
            panel.Children.Add(Label(document.Introduction));
            panel.Children.Add(Label(WindowsGraphCanvas.Statistics(document.Statistics)));
            panel.Children.Add(ButtonFor("Open document", () => OpenDocument?.Invoke(document.Path, "")));
            _comparisonDocuments.Children.Add(panel);
        }
        if (!wasOpen)
        {
            _comparisonClose?.Focus(FocusState.Programmatic);
        }
    }

    /// <summary>Saves the camera and selects the matching viewpoint for immediate restore or delete.</summary>
    private void SaveCurrentView()
    {
        var name = _viewName.Text.Trim();
        if (name.Length == 0)
        {
            _viewName.Focus(FocusState.Programmatic);
            return;
        }
        Change(model => model.SaveView(name, _canvas.CaptureCamera()));
        if (_model?.Atlas.Views?.FirstOrDefault(view => view.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } saved)
        {
            _views.SelectedItem = saved;
        }
    }

    /// <summary>Confirms a complete appearance and coordinate reset before applying it to the model.</summary>
    private async Task ResetAsync()
    {
        if (_model is not { } model || !await ConfirmAsync("Reset map layout?", "Restore default card positions, sizes, colors and icons. Your documents and saved viewpoints are kept.", "Reset all") || _model != model)
        {
            return;
        }
        Change(current => current.Reset(), animateArrangement: true);
        _canvas.Fit();
    }

    /// <summary>Returns to the on-disk revision only after confirmation to discard the pending changes.</summary>
    private async Task ReloadAsync()
    {
        if (_model is not { } model || !await ConfirmAsync("Discard layout changes?", "Reload the map layout currently stored in this folder.", "Discard changes") || _model != model)
        {
            return;
        }
        _reloading = true;
        IsEnabled = false;
        SuspendInteractions();
        try
        {
            await model.ReloadAsync(model.SelectedPath, _workspaceLifetime);
            if (_model == model)
            {
                RenderModel();
                _canvas.Fit();
            }
        }
        finally
        {
            IsEnabled = true;
            _reloading = false;
        }
    }

    /// <summary>Asks for a separate location and writes a validated map copy atomically.</summary>
    private async Task<bool> SaveCopyAsync(WindowsMapModel model)
    {
        if (XamlRoot is not { } root)
        {
            return false;
        }
        var picker = new FileSavePicker { SuggestedFileName = "markvibe-map" };
        picker.FileTypeChoices.Add("MarkVibe map layout", [".json"]);
        var id = root.ContentIslandEnvironment.AppWindowId;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Win32Interop.GetWindowFromWindowId(id));
        var destination = await picker.PickSaveFileAsync();
        if (destination == null)
        {
            return false;
        }
        try
        {
            await model.SaveCopyAsync(destination.Path, _workspaceLifetime);
        }
        catch (Exception error) when (error is WorkspaceException or IOException or UnauthorizedAccessException)
        {
            PresentStatus(error.Message);
            return false;
        }
        PresentStatus("Layout copy saved");
        return true;
    }

    /// <summary>Drains model autosaves without applying messages from a replaced workspace.</summary>
    private async Task SaveQuietlyAsync(WindowsMapModel model, bool force = false)
    {
        if (!force && !model.Dirty)
        {
            return;
        }
        var token = _workspaceLifetime;
        try
        {
            await model.SaveAsync(token, force);
            if (ReferenceEquals(_model, model))
            {
                PresentStatus(model.Message);
                UpdateLayoutCommands(model);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The session has already been replaced or the window closed.
        }
    }

    /// <summary>Reflects the current write capability and whether changes can be discarded.</summary>
    private void UpdateLayoutCommands(WindowsMapModel model)
    {
        _saveLayout.Visibility = model.CanWrite ? Visibility.Visible : Visibility.Collapsed;
        _discardLayout.Visibility = model.Dirty ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Publishes the latest model status in the map and the desktop shell.</summary>
    private void PresentStatus(string message)
    {
        _status.Text = T(message);
        _status.Visibility = message.Length > 0 && message != "Layout saved" ? Visibility.Visible : Visibility.Collapsed;
        if (message.Length > 0)
        {
            StatusChanged?.Invoke(message);
        }
    }

    /// <summary>Switches to list scrolling in narrow windows and sizes the search and inspector to the viewport.</summary>
    private void UpdateViewport()
    {
        var compact = _touchOnly || ActualWidth < 640;
        var menuWidth = Math.Clamp(ActualWidth - 56, 220, 420);
        SizeMapOptions();
        _arrangements.Width = Math.Min(320, menuWidth);
        if (compact && _canvas.Visibility == Visibility.Visible)
        {
            SuspendInteractions();
        }
        _compact.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        _canvas.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        _fitButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        var showInspector = compact ? _compactDetails : _details;
        var dockInspector = showInspector && !compact;
        _inspectorScroll.Visibility = showInspector ? Visibility.Visible : Visibility.Collapsed;
        _inspectorToggle.IsChecked = showInspector;
        Grid.SetColumn(_inspectorScroll, compact ? 0 : 1);
        _inspectorScroll.HorizontalAlignment = compact ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        _inspectorScroll.Width = compact ? Math.Min(_inspectorWidth, Math.Max(0, ActualWidth - 20)) : double.NaN;
        _split.Visibility = dockInspector ? Visibility.Visible : Visibility.Collapsed;
        var inspectorWidth = dockInspector ? Math.Min(_inspectorWidth, Math.Max(220, ActualWidth * .48)) : 0;
        _inspectorColumn.Width = new GridLength(inspectorWidth);
        var chromeWidth = ActualWidth - inspectorWidth - _chrome.Margin.Left - _chrome.Margin.Right;
        var toolWidth = Math.Max(0, chromeWidth - _chrome.Padding.Left - _chrome.Padding.Right - _chrome.BorderThickness.Left - _chrome.BorderThickness.Right);
        _toolbar.MaxWidth = toolWidth;
        _search.Width = Math.Min(Math.Max(0, toolWidth - 48), Math.Clamp(toolWidth * .32, 120, 220));
        UpdateLegendVisibility();
        if (showInspector && _model is { } model)
        {
            UpdateInspector(model);
        }
        UpdateChromeInset();
    }

    /// <summary>Places the transparent legend over the graph and reserves only the toolbar strip for layout and input.</summary>
    private void UpdateChromeInset()
    {
        var inset = PreviewTop;
        var covered = _chrome.IsLoaded && _canvas.IsLoaded && _canvas.Visibility == Visibility.Visible
            ? _chrome.TransformToVisual(_canvas).TransformBounds(new Rect(0, 0, _chrome.ActualWidth, _chrome.ActualHeight))
            : default;
        _canvas.SetToolbarArea(inset, covered);
        _legend.Margin = new Thickness(16, inset + 6, 0, 0);
        _compact.Margin = new Thickness(0, inset, 0, 0);
        _inspectorScroll.Margin = _compact.Visibility == Visibility.Visible ? new Thickness(0, inset, 10, 10) : new Thickness(0);
        _comparison.Margin = new Thickness(10, inset, 10, 10);
        PreviewPlacementChanged?.Invoke();
    }

    /// <summary>Applies the selected palette, material and independent graph font without changing the map.</summary>
    public void SetAppearance(bool dark, bool acrylic, string graphPalette = "system", string graphFont = "system")
    {
        var family = UiFontCatalog.Normalize(graphFont);
        if (_graphFont != family)
        {
            _graphFont = family;
            _canvas.SetGraphFont(family);
            if (family == "system")
            {
                _compact.ClearValue(Control.FontFamilyProperty);
            }
            else
            {
                _compact.FontFamily = new FontFamily(family);
            }
        }
        var palette = WindowsMapPalette.Resolve(graphPalette, dark);
        var highContrast = new global::Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
        if (ReferenceEquals(_palette, palette) && _acrylic == acrylic && _highContrast == highContrast && _chrome.Background != null)
        {
            return;
        }
        _palette = palette;
        _acrylic = acrylic;
        _highContrast = highContrast;
        var theme = dark ? ElementTheme.Dark : ElementTheme.Light;
        _arrangements.RequestedTheme = theme;
        _optionsPanel.RequestedTheme = theme;
        _canvas.SetAppearance(palette, MapSurface(palette.Canvas));
        var panelStyle = (Style)Application.Current.Resources[acrylic ? "AcrylicMapPanelStyle" : "MapPanelStyle"];
        _chrome.Style = _comparison.Style = panelStyle;
        _inspectorScroll.Style = (Style)Application.Current.Resources[acrylic ? "AcrylicMapInspectorStyle" : "MapInspectorStyle"];
        if (highContrast)
        {
            var systemColors = new global::Windows.UI.ViewManagement.UISettings();
            var foreground = systemColors.GetColorValue(global::Windows.UI.ViewManagement.UIColorType.Foreground);
            _incomingInk.Color = _outgoingInk.Color = foreground;
            _incomingCaption.Foreground = _outgoingCaption.Foreground = _incomingInk;
        }
        else
        {
            _incomingInk.Color = palette.IncomingColor;
            _outgoingInk.Color = palette.OutgoingColor;
            _incomingCaption.Foreground = _outgoingCaption.Foreground = new SolidColorBrush(palette.CardText);
        }
        _chrome.Shadow = highContrast ? null : _toolbarShadow;
        UpdateToolbarElevation();
        _compact.Background = new SolidColorBrush(palette.Canvas);
    }

    /// <summary>Provides graph material with a matching opaque fallback when transparency is unavailable.</summary>
    private Brush MapSurface(Color color) => _acrylic
        ? new AcrylicBrush { TintColor = color, TintOpacity = .78, FallbackColor = color }
        : new SolidColorBrush(color);

    /// <summary>Groups secondary graph commands in a native, scrollable flyout.</summary>
    private static Flyout MapMenu(FrameworkElement content) => new()
    {
        Content = new ScrollViewer
        {
            Content = content,
            MaxHeight = 460,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        }
    };

    /// <summary>Draws one direction key with the same stroke convention used for the selected document's edges.</summary>
    private static StackPanel CreateLegendKey(TextBlock caption, Brush ink, bool incoming)
    {
        var geometry = incoming
            ? "M2,6 H7 M10,6 H14 M17,6 H22 M6,2 L2,6 L6,10"
            : "M2,6 H22 M18,2 L22,6 L18,10";
        var marker = (Microsoft.UI.Xaml.Shapes.Path)XamlReader.Load($"""
            <Path xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  Data="{geometry}" Width="24" Height="12" StrokeThickness="1.6"
                  StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round" />
            """);
        marker.Stroke = ink;
        marker.IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(marker, AccessibilityView.Raw);
        var key = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        key.Children.Add(marker);
        key.Children.Add(caption);
        return key;
    }

    /// <summary>Shows the relation key only when the graph can display links to a selected document.</summary>
    private void UpdateLegendVisibility()
    {
        var visible = _canvas.Visibility == Visibility.Visible && _lens.IsOn
            && _model is { SelectedPath.Length: > 0, Snapshot.Edges.Count: > 0 };
        _legend.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Raises the toolbar's native shadow for pointer and keyboard interaction without changing layout.</summary>
    private void UpdateToolbarElevation()
    {
        var elevation = _toolbarPointerOver || _toolbarFocused ? 10 : 3;
        _chrome.Translation = new System.Numerics.Vector3(0, 0, _highContrast ? 0 : elevation);
    }

    /// <summary>Pairs a localized visible command name with native hover and screen-reader help.</summary>
    private static void SetToolbarHint(FrameworkElement element, string caption, string help)
    {
        var description = T(help);
        AutomationProperties.SetName(element, caption);
        AutomationProperties.SetHelpText(element, description);
        ToolTipService.SetToolTip(element, description);
    }

    /// <summary>Uses the same membership label for toolbar, inspector and card menu actions.</summary>
    private string ReferenceLabel(string path) => _referencePaths.Contains(path) ? "Remove from AI context" : "Add selected document to AI context";

    /// <summary>Gives each layout preset the descriptive keyboard and pointer label used by the graph.</summary>
    private static string CommandHint(string label) => label switch
    {
        "Star" => "Arrange around the most connected page",
        "Circle" => "Arrange in a circle",
        "Grid" => "Arrange in a grid",
        "Tree" => "Arrange as a tree",
        "Folders" => "Group by folder",
        _ => label
    };

    /// <summary>Provides a localized tooltip and accessible command name.</summary>
    private static void SetCommandHint(FrameworkElement element, string label)
    {
        ToolTipService.SetToolTip(element, T(label));
        AutomationProperties.SetName(element, T(label));
    }

    /// <summary>Invokes an asynchronous native action for the current map while handling UI-bound failures.</summary>
    private async Task WithModelAsync(Func<WindowsMapModel, Task> action)
    {
        if (_model is { } model)
        {
            await action(model);
        }
    }

    /// <summary>Prompts for an explicit destructive graph action using the owning window's XAML root.</summary>
    private async Task<bool> ConfirmAsync(string title, string message, string command) => await WindowsDialogs.ShowAsync(new ContentDialog
    {
        XamlRoot = XamlRoot, Title = T(title), Content = T(message), PrimaryButtonText = T(command),
        CloseButtonText = T("Cancel"), DefaultButton = ContentDialogButton.Close
    }) == ContentDialogResult.Primary;

    /// <summary>Allows long, optional action lists to scroll without widening the map.</summary>
    private static ScrollViewer ScrollRow(StackPanel row) => new()
    {
        Content = row, HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
    };

    /// <summary>Builds a readable native action with an optional descriptive automation name.</summary>
    private static Button ButtonFor(string label, Action action, string? automationName = null, bool localized = true)
    {
        var button = new Button { Content = localized ? T(label) : label, Tag = localized ? label : null, VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(button, automationName ?? (localized ? T(CommandHint(label)) : label));
        if (automationName != null)
        {
            AutomationProperties.SetName(button, automationName);
        }
        button.Click += (_, _) => action();
        return button;
    }

    /// <summary>Creates an asynchronous native action and reports expected local failures in the map status.</summary>
    private Button ButtonFor(string label, Func<Task> action)
    {
        var button = new Button { Content = T(label), Tag = label, VerticalAlignment = VerticalAlignment.Center };
        SetCommandHint(button, CommandHint(label));
        button.Click += async (_, _) =>
        {
            try
            {
                await action();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or WorkspaceException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                PresentStatus(error.Message);
            }
            catch (OperationCanceledException) when (_workspaceLifetime.IsCancellationRequested)
            {
                // The current workspace no longer owns this command.
            }
        };
        return button;
    }

    /// <summary>Creates one native flyout item with a scoped presentation command.</summary>
    private static MenuFlyoutItem MenuItem(string label, Action action)
    {
        var item = new MenuFlyoutItem { Text = T(label) };
        ToolTipService.SetToolTip(item, item.Text);
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>Creates a wrapping native label for document metadata and status.</summary>
    private static TextBlock Label(string text, double size = 14) => new()
    {
        Text = text, FontSize = size, FontWeight = FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
    };

    /// <summary>Creates a native section heading.</summary>
    private static TextBlock Title(string text) => new() { Text = text, FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };

    /// <summary>Creates a keyboard-accessible native disclosure for secondary controls.</summary>
    private static Expander Section(string title, FrameworkElement content, bool expanded) => new()
    {
        Header = title, Content = content, IsExpanded = expanded, HorizontalAlignment = HorizontalAlignment.Stretch
    };

    /// <summary>Releases Win2D resources when the owning native desktop window closes.</summary>
    public void Dispose()
    {
        UiStrings.Changed -= ApplyLanguage;
        _canvas.Dispose();
    }
}
