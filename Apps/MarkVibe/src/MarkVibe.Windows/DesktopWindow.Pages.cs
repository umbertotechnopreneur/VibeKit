/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; bounded native navigation indentation and accessible Pages labels
 * Human guidance: keep nested folder and document names readable within the sidebar
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace MarkVibe.Windows;

public sealed partial class DesktopWindow
{
    private const double NavigationIconColumnWidth = 18;
    private const double NavigationRowSpacing = 4;
    private static readonly Thickness NavigationRowPadding = new(0, 2, 5, 2);
    private static readonly FontFamily NavigationIconFont = new("Segoe Fluent Icons,Segoe MDL2 Assets");

    /// <summary>Distinguishes directories from files while allowing long labels to wrap within the sidebar.</summary>
    private static Grid CreatePageNavigationRow(DesktopNavigationItem item)
    {
        var row = new Grid { ColumnSpacing = NavigationRowSpacing, Padding = NavigationRowPadding };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NavigationIconColumnWidth) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(CreateNavigationGlyph(item.IsFolder ? "\uE8B7" : "\uE8A5"));

        var label = new TextBlock
        {
            FontSize = 13,
            FontWeight = item.IsFolder ? FontWeights.SemiBold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        WindowsUiFont.ApplyTo(row);
        UpdatePageNavigationRow(row, item);
        return row;
    }

    /// <summary>Sizes the glyph itself to the shared gutter using the WinUI symbol font fallback.</summary>
    private static FontIcon CreateNavigationGlyph(string glyph)
    {
        var icon = new FontIcon
        {
            Glyph = glyph,
            FontFamily = NavigationIconFont,
            FontSize = 14,
            IsTextScaleFactorEnabled = false,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
        return icon;
    }

    /// <summary>Preserves the full title and relative path in pointer and accessibility descriptions.</summary>
    private static void UpdatePageNavigationRow(Grid row, DesktopNavigationItem item)
    {
        if (row.Tag is not DesktopNavigationItem previous || previous.Label != item.Label || previous.Tooltip != item.Tooltip)
        {
            ((TextBlock)row.Children[1]).Text = item.Label;
            UpdateNavigationContainer(row, item);
        }
        row.Tag = item;
    }

    /// <summary>Applies the label, full description and wrapped tooltip to the keyboard-focusable native row.</summary>
    private static void UpdateNavigationContainer(DependencyObject element, DesktopNavigationItem item)
    {
        var parent = element;
        while (parent != null && parent is not TreeViewItem)
        {
            parent = VisualTreeHelper.GetParent(parent);
        }
        if (parent is not TreeViewItem container)
        {
            return;
        }

        AutomationProperties.SetName(container, item.Label);
        AutomationProperties.SetHelpText(container, item.Tooltip);
        if (ToolTipService.GetToolTip(container) is ToolTip { Content: TextBlock tip })
        {
            tip.Text = item.Tooltip;
            WindowsUiFont.ApplyTo(tip);
        }
        else
        {
            var tipText = new TextBlock { Text = item.Tooltip, MaxWidth = 360, TextWrapping = TextWrapping.Wrap };
            WindowsUiFont.ApplyTo(tipText);
            ToolTipService.SetToolTip(container, new ToolTip { Content = tipText });
        }
    }

    /// <summary>Applies shared layout and accessible metadata when the native tree realizes a row.</summary>
    private void NavigationPresenterLoaded(object sender, RoutedEventArgs args)
    {
        var presenter = (ContentPresenter)sender;
        UpdateNavigationPresenterDescription(presenter);
        if (ApplyNavigationIndent(presenter))
        {
            return;
        }
        // Some WinUI presenters finish constructing their container on the next turn.
        presenter.DispatcherQueue.TryEnqueue(() =>
        {
            if (presenter.IsLoaded)
            {
                ApplyNavigationIndent(presenter);
                UpdateNavigationPresenterDescription(presenter);
            }
        });
    }

    /// <summary>Replaces the old accessible label when virtualization binds a presenter to another node.</summary>
    private void NavigationPresenterContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        var presenter = (ContentPresenter)sender;
        if (NavigationItem(args.NewValue) is { } item)
        {
            UpdateNavigationContainer(presenter, item);
        }
        else if (presenter.IsLoaded)
        {
            // When the content binding runs after DataContextChanged, resolve it on the next turn.
            presenter.DispatcherQueue.TryEnqueue(() => UpdateNavigationPresenterDescription(presenter));
        }
    }

    /// <summary>Reads the currently presented model once its content binding has resolved.</summary>
    private static void UpdateNavigationPresenterDescription(ContentPresenter presenter)
    {
        if (NavigationItem(presenter.Content) is { } item)
        {
            UpdateNavigationContainer(presenter, item);
        }
    }

    /// <summary>Applies one idempotent converter per native template, including when WinUI reuses a row.</summary>
    private static bool ApplyNavigationIndent(ContentPresenter presenter)
    {
        var parent = VisualTreeHelper.GetParent(presenter);
        while (parent != null && parent is not TreeViewItem)
        {
            if (parent is Grid indentation && indentation.GetBindingExpression(Grid.PaddingProperty)?.ParentBinding is
                { Path.Path: "TreeViewItemTemplateSettings.Indentation" } binding)
            {
                if (binding.Converter is not NavigationIndentConverter)
                {
                    // Both tree modes use this template. Keep WinUI's relative source so
                    // it continues to track the native container as items are recycled.
                    indentation.SetBinding(Grid.PaddingProperty, new Binding
                    {
                        RelativeSource = binding.RelativeSource,
                        Path = new PropertyPath(binding.Path.Path),
                        Mode = BindingMode.OneWay,
                        Converter = NavigationIndentConverter.Instance
                    });
                    // If the pinned SDK's disclosure part changes, leave its native width.
                    var chevron = indentation.Children.OfType<Grid>().FirstOrDefault(child => child.Name == "ExpandCollapseChevron");
                    if (chevron != null)
                    {
                        chevron.Padding = new Thickness(9, chevron.Padding.Top, 9, chevron.Padding.Bottom);
                    }
                }
                return true;
            }
            parent = VisualTreeHelper.GetParent(parent);
        }
        return false;
    }

    /// <summary>Preserves early depth steps and bounds the horizontal space that deep items consume.</summary>
    private sealed class NavigationIndentConverter : IValueConverter
    {
        public static NavigationIndentConverter Instance { get; } = new();

        /// <summary>Reduces each horizontal inset in either flow direction while keeping vertical padding.</summary>
        public object Convert(object value, Type targetType, object parameter, string language) =>
            value is Thickness inset
                ? new Thickness(Compress(inset.Left), inset.Top, Compress(inset.Right), inset.Bottom)
                : value;

        /// <summary>Keeps the first two depth steps distinct and spends four DIPs on each deeper level.</summary>
        private static double Compress(double inset) => Math.Min(36, Math.Min(inset, 32) * .55 + Math.Max(0, inset - 32) * .25);

        /// <summary>Leaves the SDK's read-only template settings unchanged.</summary>
        public object ConvertBack(object value, Type targetType, object parameter, string language) => DependencyProperty.UnsetValue;
    }
}
