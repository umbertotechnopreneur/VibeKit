/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; native map card customization and compact item interactions
 * Human guidance: restore the previous graph's appearance gallery and direct context actions
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using MarkVibe.Core;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using static MarkVibe.Core.UiStrings;

namespace MarkVibe.Windows;

internal sealed partial class WindowsMapView
{
    private Flyout? _cardFlyout;
    private (WindowsMapModel Model, string Path)? _compactPlainTap;

    /// <summary>Stops pending pointer, keyboard and camera effects when the desktop hides the map.</summary>
    public void SuspendInteractions()
    {
        _compactPlainTap = null;
        _cardFlyout?.Hide();
        _canvas.SuspendInteractions();
    }

    /// <summary>Shows the full appearance gallery at the graph coordinate that requested it.</summary>
    private void ShowCardMenu(string path, Point point) => ShowCardMenu(path, _canvas, point);

    /// <summary>Renders a native editor for the captured card, with appearance changes reflected in place.</summary>
    private void ShowCardMenu(string path, FrameworkElement anchor, Point? point = null)
    {
        if (_model is not { } model || !model.Layout.Nodes.ContainsKey(path) || model.Document(path) is not { } card)
        {
            return;
        }
        _cardFlyout?.Hide();
        var presenter = new Style(typeof(FlyoutPresenter));
        presenter.Setters.Add(new Setter(Control.BackgroundProperty, MapAcrylic()));
        presenter.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12)));
        var menu = new Flyout { FlyoutPresenterStyle = presenter };
        _cardFlyout = menu;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_cardFlyout, menu))
            {
                _cardFlyout = null;
            }
        };

        var width = XamlRoot?.Size.Width ?? 380;
        var contents = new StackPanel { Spacing = 10, RequestedTheme = ElementTheme.Dark, Width = Math.Clamp(width - 44, 220, 330) };
        var title = new Grid { ColumnSpacing = 8 };
        title.ColumnDefinitions.Add(new ColumnDefinition());
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        title.Children.Add(Title(T("Customize card")));
        var close = ButtonFor("×", menu.Hide, T("Close"), localized: false);
        Grid.SetColumn(close, 1);
        title.Children.Add(close);
        contents.Children.Add(title);

        var previewIcon = new ContentControl();
        var previewRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
        previewRow.Children.Add(previewIcon);
        previewRow.Children.Add(new TextBlock { Text = card.Title, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 245, TextTrimming = TextTrimming.CharacterEllipsis });
        var previewContent = new StackPanel { Spacing = 6 };
        previewContent.Children.Add(previewRow);
        previewContent.Children.Add(new TextBlock { Text = path, FontFamily = new FontFamily("Consolas"), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
        var previewCard = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Child = previewContent };
        contents.Children.Add(previewCard);

        var colorCaption = Label("");
        var iconCaption = Label("");
        var colorPanel = new NativeWrapPanel { Spacing = 6 };
        var iconPanel = new NativeWrapPanel { Spacing = 6 };
        var colors = new Dictionary<string, ToggleButton>(StringComparer.Ordinal);
        var icons = new Dictionary<string, ToggleButton>(StringComparer.Ordinal);
        contents.Children.Add(colorCaption);
        contents.Children.Add(colorPanel);
        foreach (var color in MapLayoutCodec.Colors)
        {
            var swatch = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(WindowsGraphCanvas.ParseColor(color.Value)) };
            var choice = GalleryChoice(color.Label, swatch);
            choice.Click += (_, _) => UpdateAppearance(color.Value, null);
            colorPanel.Children.Add(choice);
            colors.Add(color.Value, choice);
        }
        contents.Children.Add(iconCaption);
        contents.Children.Add(iconPanel);
        foreach (var icon in MapLayoutCodec.Icons)
        {
            var choice = GalleryChoice(icon.Label, CardIcon(icon.Value, Color.FromArgb(255, 228, 237, 251)));
            choice.Click += (_, _) => UpdateAppearance(null, icon.Value);
            iconPanel.Children.Add(choice);
            icons.Add(icon.Value, choice);
        }

        var navigation = new NativeWrapPanel { Spacing = 6 };
        navigation.Children.Add(ButtonFor("Read here", () => Navigate(() => PreviewDocument?.Invoke(path, ""))));
        navigation.Children.Add(ButtonFor("Open document", () => Navigate(() => OpenDocument?.Invoke(path, ""))));
        navigation.Children.Add(ButtonFor(ReferenceLabel(path), () => Navigate(() => ToggleAiContext?.Invoke(path))));
        contents.Children.Add(navigation);
        var target = new FileMenuTarget(model.WorkspaceId, path, card.Title);
        var fileActions = new NativeWrapPanel { Spacing = 6 };
        foreach (var command in FileActions?.Invoke(target) ?? [])
        {
            fileActions.Children.Add(ButtonFor(command.Label, () => Navigate(() => RequestFileAction(model, target, command.Action))));
        }
        if (fileActions.Children.Count > 0)
        {
            contents.Children.Add(fileActions);
        }

        menu.Content = new ScrollViewer
        {
            Content = contents,
            MaxHeight = Math.Min(630, Math.Max(230, ActualHeight - 24)),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        RefreshChoices();
        if (point is { } position)
        {
            menu.ShowAt(anchor, new FlyoutShowOptions { Position = position });
        }
        else
        {
            menu.ShowAt(anchor);
        }

        /// <summary>Applies one choice against the current layout and reads back accepted state.</summary>
        void UpdateAppearance(string? color, string? icon)
        {
            if (ReferenceEquals(_model, model) && model.Layout.Nodes.TryGetValue(path, out var current))
            {
                Change(state => state.Style(path, color ?? current.Color, icon ?? current.Icon));
                RefreshChoices();
            }
        }

        /// <summary>Updates the live card and accessible selection marks from the managed layout.</summary>
        void RefreshChoices()
        {
            var current = model.Layout.Nodes[path];
            var tint = WindowsGraphCanvas.ParseColor(current.Color);
            previewCard.BorderBrush = new SolidColorBrush(tint);
            previewCard.Background = new SolidColorBrush(Color.FromArgb(44, tint.R, tint.G, tint.B));
            previewIcon.Content = CardIcon(current.Icon, tint);
            colorCaption.Text = T("Card color") + " · " + T(MapLayoutCodec.Colors.First(item => item.Value == current.Color).Label);
            iconCaption.Text = T("Card icon") + " · " + T(MapLayoutCodec.Icons.First(item => item.Value == current.Icon).Label);
            UpdateGallerySelection(colors, current.Color);
            UpdateGallerySelection(icons, current.Icon);
        }

        /// <summary>Closes the popup before navigating or showing a confirmation dialog.</summary>
        void Navigate(Action action)
        {
            menu.Hide();
            if (ReferenceEquals(_model, model))
            {
                action();
            }
        }
    }

    /// <summary>Routes an advertised file command only while the originating map and card still exist.</summary>
    private void RequestFileAction(WindowsMapModel model, FileMenuTarget target, string action)
    {
        if (ReferenceEquals(_model, model) && model.Document(target.Path) != null)
        {
            FileActionRequested?.Invoke(target, action);
        }
    }

    /// <summary>Creates a keyboard-accessible gallery choice with a localized name and tooltip.</summary>
    private static ToggleButton GalleryChoice(string label, FrameworkElement glyph)
    {
        var caption = T(label);
        var button = new ToggleButton { Content = glyph, Width = 39, Height = 39, Padding = new Thickness(5), BorderThickness = new Thickness(2) };
        AutomationProperties.SetName(button, caption);
        ToolTipService.SetToolTip(button, caption);
        return button;
    }

    /// <summary>Marks the chosen swatch or icon without changing keyboard focus.</summary>
    private static void UpdateGallerySelection(Dictionary<string, ToggleButton> choices, string value)
    {
        foreach (var (key, button) in choices)
        {
            var selected = key == value;
            button.IsChecked = selected;
            button.BorderBrush = new SolidColorBrush(selected ? Color.FromArgb(255, 98, 184, 255) : Color.FromArgb(0, 0, 0, 0));
            AutomationProperties.SetHelpText(button, selected ? T("Selected") : "");
        }
    }

    /// <summary>Displays the same local vector used by Win2D.</summary>
    private static FrameworkElement CardIcon(string icon, Color tint)
    {
        if (WindowsGraphCanvas.IconSvg(icon, tint) is not { } svg)
        {
            return new FontIcon { Glyph = WindowsGraphCanvas.Glyph(icon), FontFamily = new FontFamily("Segoe Fluent Icons"), Foreground = new SolidColorBrush(tint) };
        }
        var image = new Image { Width = 22, Height = 22, Stretch = Stretch.Uniform };
        _ = LoadCardIconAsync(image, svg);
        return image;
    }

    /// <summary>Loads the validated application SVG through the native image decoder.</summary>
    private static async Task LoadCardIconAsync(Image image, string svg)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteString(svg);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var source = new SvgImageSource();
            if (await source.SetSourceAsync(stream) == SvgImageSourceLoadStatus.Success)
            {
                image.Source = source;
            }
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            DesktopLog.Error("map.icon-load-failed", error);
        }
    }

    /// <summary>Installs compact document cards with ordinary scrolling and the same selection and context commands.</summary>
    private void ConfigureCompactList()
    {
        AutomationProperties.SetName(_compact, T("Map documents"));
        _compactActions.Children.Add(ButtonFor("Read here", () =>
        {
            if (_model is { SelectedPath.Length: > 0 } model) { PreviewDocument?.Invoke(model.SelectedPath, ""); }
        }));
        _compactActions.Children.Add(ButtonFor("Open document", () =>
        {
            if (_model is { SelectedPath.Length: > 0 } model) { OpenDocument?.Invoke(model.SelectedPath, ""); }
        }));
        _compactActions.Children.Add(ButtonFor("Customize card", () =>
        {
            if (_model is { SelectedPath.Length: > 0 } model) { ShowCardMenu(model.SelectedPath, _compactActions); }
        }));
        _compact.ItemTemplate = (DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                          xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <StackPanel Spacing="5" Margin="6,5,6,8">
                    <TextBlock Text="{Binding Title}" FontWeight="SemiBold" TextWrapping="Wrap" />
                    <TextBlock Text="{Binding Introduction}" TextWrapping="Wrap" MaxLines="2" TextTrimming="CharacterEllipsis" />
                    <TextBlock Text="{Binding Path}" FontFamily="Consolas" FontSize="11" TextTrimming="CharacterEllipsis" />
                    <ContentControl x:Name="MapInlinePreviewHost" IsTabStop="False" HorizontalContentAlignment="Stretch" />
                </StackPanel>
            </DataTemplate>
            """);
        _compact.ItemClick += (_, args) =>
        {
            if (args.ClickedItem is DocumentCard card && _model is { } model)
            {
                var picking = IsCompactPicking(model);
                _compactPlainTap = picking ? null : (model, card.Path);
                ActivateCompactCard(model, card.Path, picking);
            }
        };
        _compact.DoubleTapped += (_, args) =>
        {
            var firstTap = _compactPlainTap;
            _compactPlainTap = null;
            if (_model is { } model && CompactRow(args.OriginalSource) is { Content: DocumentCard card })
            {
                var picking = IsCompactPicking(model);
                if (!picking && firstTap is { } first && ReferenceEquals(first.Model, model) && first.Path == card.Path)
                {
                    OpenDocument?.Invoke(card.Path, "");
                }
                else
                {
                    // WinUI delivers the second input as DoubleTapped rather than another ItemClick.
                    ActivateCompactCard(model, card.Path, picking);
                }
                args.Handled = true;
            }
        };
        _compact.PreviewKeyDown += (_, args) =>
        {
            if (args.Key is not (VirtualKey.Enter or VirtualKey.Space))
            {
                return;
            }
            var card = (ReferenceEquals(args.OriginalSource, _compact) ? _compact.SelectedItem : CompactRow(args.OriginalSource)?.Content) as DocumentCard;
            if (card != null && _model is { } model)
            {
                _compactPlainTap = null;
                if (args.Key == VirtualKey.Enter)
                {
                    OpenDocument?.Invoke(card.Path, "");
                }
                else
                {
                    ActivateCompactCard(model, card.Path, IsCompactPicking(model));
                }
                args.Handled = true;
            }
        };
        _compact.ContextRequested += (_, args) =>
        {
            if (CompactRow(args.OriginalSource) is { Content: DocumentCard card } row)
            {
                var point = args.TryGetPosition(_compact, out var position) ? position : row.TransformToVisual(_compact).TransformPoint(new Point(20, row.ActualHeight / 2));
                ShowCardMenu(card.Path, _compact, point);
                args.Handled = true;
            }
        };
        _compact.ContainerContentChanging += (_, args) =>
        {
            if (args.InRecycleQueue && args.ItemContainer is ListViewItem row && InlineHost(row) is { Content: not null } host)
            {
                InlinePreviewHostRecycled?.Invoke(host);
            }
        };
    }

    /// <summary>Checks the same picking modifiers for a compact row click or Space activation.</summary>
    private static bool IsCompactPicking(WindowsMapModel model) =>
        model.Exploration.SelectionEnabled || CompactModifierDown(VirtualKey.Control) || CompactModifierDown(VirtualKey.Shift) ||
        CompactModifierDown(VirtualKey.LeftWindows) || CompactModifierDown(VirtualKey.RightWindows);

    /// <summary>Reads a modifier while the current routed row action is still being handled.</summary>
    private static bool CompactModifierDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    /// <summary>Selects a row or toggles its picked state without changing the model's selected document.</summary>
    private void ActivateCompactCard(WindowsMapModel model, string path, bool picking)
    {
        if (!picking)
        {
            Select(path, false);
            return;
        }
        Change(state => state.TogglePick(path));
        DispatcherQueue.TryEnqueue(() =>
        {
            // The native list applies its own selection after ItemClick returns.
            if (ReferenceEquals(_model, model) && _compact.Visibility == Visibility.Visible)
            {
                _compact.SelectedItem = model.Document(model.SelectedPath);
            }
        });
    }

    /// <summary>Finds the list row owning a pointer or keyboard event.</summary>
    private ListViewItem? CompactRow(object? source)
    {
        for (var element = source as DependencyObject; element != null && element != _compact; element = VisualTreeHelper.GetParent(element))
        {
            if (element is ContentControl content && IsInlineHost(content))
            {
                return null;
            }
            if (element is ListViewItem row)
            {
                return row;
            }
        }
        return null;
    }
}
