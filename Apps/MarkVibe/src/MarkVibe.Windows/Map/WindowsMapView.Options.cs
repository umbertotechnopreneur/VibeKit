/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; responsive native map settings and accessible action rows
 * Human guidance: follow the RetroTerminal and WaterRipple settings layout for map options
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using MarkVibe.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Windows.System;
using static MarkVibe.Core.UiStrings;

namespace MarkVibe.Windows;

internal sealed partial class WindowsMapView
{
    private static readonly string[] AttentionModes = ["all", "attention", "missing", "isolated", "changed"];
    private readonly Grid _optionsPanel = new() { RowSpacing = 12 };
    private readonly List<Action> _optionTranslations = [];
    private readonly TextBlock _summary = Label("", 12);
    private readonly ComboBox _distance = new();
    private readonly ComboBox _attention = new();
    private readonly ComboBox _views = new();
    private readonly TextBox _viewName = new() { MaxLength = 64 };
    private readonly ToggleSwitch _depth = new() { IsOn = true };
    private readonly ToggleSwitch _advanced = new();
    private readonly ToggleSwitch _lens = new();
    private readonly ToggleSwitch _picking = new();
    private readonly Button _referenceButton = new();
    private readonly TextBlock _referenceCaption = Label("");
    private readonly StackPanel _trail = new();
    private StackPanel _trailSection = null!;
    private Button _restoreView = null!;
    private Button _deleteView = null!;
    private Button _saveLayout = null!;
    private Button _discardLayout = null!;

    /// <summary>Creates the map settings surface with a pinned heading and one vertically scrolling list.</summary>
    private Flyout BuildMapOptions()
    {
        var presenter = new Style(typeof(FlyoutPresenter));
        presenter.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0d));
        presenter.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 600d));
        presenter.Setters.Add(new Setter(FrameworkElement.MaxHeightProperty, 760d));
        presenter.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(16)));
        presenter.Setters.Add(new Setter(ScrollViewer.HorizontalScrollModeProperty, ScrollMode.Disabled));
        presenter.Setters.Add(new Setter(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled));
        presenter.Setters.Add(new Setter(ScrollViewer.VerticalScrollModeProperty, ScrollMode.Disabled));
        presenter.Setters.Add(new Setter(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled));
        var flyout = new Flyout { Content = _optionsPanel, FlyoutPresenterStyle = presenter };
        flyout.Opening += (_, _) => SizeMapOptions();
        AutomationProperties.SetAutomationId(_optionsPanel, "MapOptionsPanel");

        _optionsPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _optionsPanel.RowDefinitions.Add(new RowDefinition());
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var captions = new StackPanel { Spacing = 3 };
        var title = Title("");
        title.FontSize = 22;
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);
        captions.Children.Add(title);
        captions.Children.Add(_summary);
        header.Children.Add(captions);
        var close = new Button { Content = new SymbolIcon(Symbol.Cancel), VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetAutomationId(close, "MapOptionsClose");
        close.Click += (_, _) => flyout.Hide();
        Grid.SetColumn(close, 1);
        header.Children.Add(close);
        _optionsPanel.Children.Add(header);
        _optionTranslations.Add(() =>
        {
            title.Text = T("Map options");
            AutomationProperties.SetName(_optionsPanel, title.Text);
            SetToolbarHint(close, T("Close"), "Close");
        });

        var groups = new StackPanel { Spacing = 16, Padding = new Thickness(0, 0, 8, 0) };
        var links = AddOptionGroup(groups, "Links");
        AddOptionRow(links, "Distance", _distance, "MapDistance", "Focus on this page");
        AddOptionRow(links, "Attention highlights", _attention, "MapAttention");
        AddOptionRow(links, "Relation lens", _lens, "MapRelationLens");
        var documents = AddOptionGroup(groups, "Documents");
        AddOptionRow(documents, "Show connection depth", _depth, "MapDepth");
        AddOptionRow(documents, "Advanced view", _advanced, "MapAdvanced", "Show file size, estimated tokens, characters and words");
        AddOptionRow(documents, "Select documents", _picking, "MapSelectDocuments", "Select documents. Ctrl-click cards or Shift-drag empty canvas.");
        StyleMapAction(_referenceButton, Symbol.Link, _referenceCaption);
        AutomationProperties.SetAutomationId(_referenceButton, "MapAiContext");
        _referenceButton.Click += (_, _) =>
        {
            if (_model is { SelectedPath.Length: > 0 } model)
            {
                ToggleAiContext?.Invoke(model.SelectedPath);
            }
        };
        documents.Children.Add(_referenceButton);

        AddViewOptions(groups);
        var layout = AddOptionGroup(groups, "Map arrangement");
        _saveLayout = AddMapAction(layout, ButtonFor("Save layout", () => WithModelAsync(model => SaveQuietlyAsync(model, force: true))),
            "MapSaveLayout", Symbol.Save);
        AddMapAction(layout, ButtonFor("Download layout", () => WithModelAsync(async model => { await SaveCopyAsync(model); })),
            "MapDownloadLayout", Symbol.Download);
        _discardLayout = AddMapAction(layout, ButtonFor("Discard changes", ReloadAsync), "MapDiscardLayout", Symbol.Cancel);
        AddMapAction(layout, ButtonFor("Reset all", ResetAsync), "MapResetLayout", Symbol.Refresh,
            "Restore default card positions, sizes, colors and icons. Your documents and saved viewpoints are kept.");
        _trailSection = AddOptionGroup(groups, "Recent pages");
        _trailSection.Children.Add(_trail);
        _trailSection.Visibility = Visibility.Collapsed;

        var scroll = new ScrollViewer
        {
            Content = groups,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        AutomationProperties.SetAutomationId(scroll, "MapOptionsScroll");
        Grid.SetRow(scroll, 1);
        _optionsPanel.Children.Add(scroll);

        _depth.Toggled += (_, _) => Change(model => model.SetDepth(_depth.IsOn));
        _advanced.Toggled += (_, _) => Change(model => model.SetAdvanced(_advanced.IsOn));
        _lens.Toggled += (_, _) => Change(model => model.SetLens(_lens.IsOn));
        _picking.Toggled += (_, _) => Change(model => model.SetPicking(_picking.IsOn));
        _distance.SelectionChanged += (_, _) =>
        {
            if (_distance.SelectedIndex >= 0)
            {
                Change(model => model.SetDistance(_distance.SelectedIndex));
            }
        };
        _attention.SelectionChanged += (_, _) =>
        {
            if (_attention.SelectedIndex >= 0)
            {
                Change(model => model.SetAttention(AttentionModes[_attention.SelectedIndex]));
            }
        };
        return flyout;
    }

    /// <summary>Connects saved camera positions to the same model commands as keyboard entry and map navigation.</summary>
    private void AddViewOptions(StackPanel groups)
    {
        var saved = AddOptionGroup(groups, "Saved views");
        AddOptionRow(saved, "View name", _viewName, "MapViewName");
        AddMapAction(saved, ButtonFor("Save view", SaveCurrentView), "MapSaveView", Symbol.Save);
        _viewName.KeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Enter)
            {
                SaveCurrentView();
                args.Handled = true;
            }
        };
        AddOptionRow(saved, "Saved views", _views, "MapSavedViews");
        _restoreView = AddMapAction(saved, ButtonFor("Restore view", () =>
        {
            if (_views.SelectedItem is MapViewpoint view)
            {
                Change(model => _canvas.SetCamera(model.RestoreView(view)));
            }
        }), "MapRestoreView", Symbol.Go);
        _deleteView = AddMapAction(saved, ButtonFor("Delete saved view", () =>
        {
            if (_views.SelectedItem is MapViewpoint view)
            {
                Change(model => model.DeleteView(view));
            }
        }), "MapDeleteView", Symbol.Delete);
        _views.SelectionChanged += (_, _) => UpdateViewCommands();
        UpdateViewCommands();
    }

    /// <summary>Creates a flat section with a translated heading and a theme-aware divider.</summary>
    private StackPanel AddOptionGroup(StackPanel parent, string key)
    {
        var group = new StackPanel();
        group.Children.Add((Border)XamlReader.Load("""
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    Height="1" Margin="0,0,0,8" Background="{ThemeResource DividerStrokeColorDefaultBrush}" />
            """));
        var heading = Title("");
        heading.FontSize = 16;
        heading.Margin = new Thickness(0, 0, 0, 4);
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        group.Children.Add(heading);
        _optionTranslations.Add(() => heading.Text = T(key));
        parent.Children.Add(group);
        return group;
    }

    /// <summary>Lays out a labeled field beside its description, moving wide editors below the label when space is limited.</summary>
    private void AddOptionRow(StackPanel parent, string label, Control editor, string id, string? help = null)
    {
        var row = new Grid { Padding = new Thickness(0, 5, 0, 5), ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var captions = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var caption = Label("");
        captions.Children.Add(caption);
        var hint = help == null ? null : Label("", 12);
        if (hint != null)
        {
            captions.Children.Add(hint);
        }
        row.Children.Add(captions);
        Grid.SetColumn(editor, 1);
        editor.MinWidth = 0;
        editor.VerticalAlignment = VerticalAlignment.Center;
        if (editor is ToggleSwitch toggle)
        {
            toggle.OnContent = null;
            toggle.OffContent = null;
            toggle.HorizontalAlignment = HorizontalAlignment.Right;
        }
        else
        {
            editor.HorizontalAlignment = HorizontalAlignment.Stretch;
            row.ColumnDefinitions[1].Width = new GridLength(1.2, GridUnitType.Star);
            row.SizeChanged += (_, args) =>
            {
                var stacked = args.NewSize.Width < 420;
                row.ColumnSpacing = stacked ? 0 : 12;
                row.RowSpacing = stacked ? 4 : 0;
                row.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(1.2, GridUnitType.Star);
                Grid.SetColumnSpan(captions, stacked ? 2 : 1);
                Grid.SetColumn(editor, stacked ? 0 : 1);
                Grid.SetRow(editor, stacked ? 1 : 0);
                Grid.SetColumnSpan(editor, stacked ? 2 : 1);
            };
        }
        if (editor is ComboBox choice)
        {
            choice.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            var property = ReferenceEquals(choice, _views) ? " Name" : "";
            choice.ItemTemplate = (DataTemplate)XamlReader.Load($$"""
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                    <TextBlock Text="{Binding{{property}}}" TextWrapping="Wrap" ToolTipService.ToolTip="{Binding{{property}}}" />
                </DataTemplate>
                """);
        }
        row.Children.Add(editor);
        AutomationProperties.SetAutomationId(editor, id);
        _optionTranslations.Add(() =>
        {
            caption.Text = T(label);
            if (hint != null && help != null)
            {
                hint.Text = T(help);
            }
            SetToolbarHint(editor, caption.Text, help ?? label);
        });
        parent.Children.Add(row);
    }

    /// <summary>Registers a translated command row and keeps the existing button's activation and error handling.</summary>
    private Button AddMapAction(StackPanel parent, Button command, string id, Symbol symbol, string? help = null)
    {
        var key = (string)command.Tag;
        var caption = Label("");
        StyleMapAction(command, symbol, caption);
        AutomationProperties.SetAutomationId(command, id);
        _optionTranslations.Add(() =>
        {
            caption.Text = T(key);
            SetToolbarHint(command, caption.Text, help ?? key);
        });
        parent.Children.Add(command);
        return command;
    }

    /// <summary>Uses a full-width native button with a wrapping caption and a secondary glyph.</summary>
    private static void StyleMapAction(Button command, Symbol symbol, TextBlock caption)
    {
        var content = new Grid { ColumnSpacing = 12 };
        content.ColumnDefinitions.Add(new ColumnDefinition());
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.Children.Add(caption);
        var icon = new SymbolIcon(symbol);
        AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
        Grid.SetColumn(icon, 1);
        content.Children.Add(icon);
        command.Content = content;
        command.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        command.HorizontalAlignment = HorizontalAlignment.Stretch;
        command.Padding = new Thickness(8, 6, 8, 6);
        if (Application.Current.Resources.TryGetValue("SubtleButtonStyle", out var style) && style is Style actionStyle)
        {
            command.Style = actionStyle;
        }
    }

    /// <summary>Refreshes map options and all choice translations while the model update guard is active.</summary>
    private void LocalizeMapOptions()
    {
        foreach (var translate in _optionTranslations)
        {
            translate();
        }
        _distance.ItemsSource = new[] { T("All documents"), T("1 link away"), T("2 links away"), T("3 links away") };
        _attention.ItemsSource = new[] { T("Off"), T("Needs attention"), T("Missing links"), T("Isolated documents"), T("Changed this session") };
    }

    /// <summary>Sizes the popup inside the available viewport, including presenter padding and placement margins.</summary>
    private void SizeMapOptions()
    {
        _optionsPanel.Width = Math.Clamp(ActualWidth - 48, 0, 540);
        _optionsPanel.MaxHeight = Math.Clamp(ActualHeight - _chrome.ActualHeight - 56, 0, 700);
    }
}
