/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; directional map link sections and document rows; 2026-10-16
 * Human guidance: Umberto Giacobbi; elegant incoming/outgoing navigation with document icons
 * Modified with AI: OpenAI Codex; expandable link sections with retained session expansion; 2026-10-16
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using MarkVibe.Core;

namespace MarkVibe.Controls;

/// <summary>Builds directional document links in the map inspector.</summary>
public partial class DocumentMapView
{
    private readonly Dictionary<string, bool> _linkSectionExpansion = new(StringComparer.Ordinal);

    /// <summary>Shows the catalog count and creates navigation controls on first expansion of this snapshot.</summary>
    private void AddLinks(string title, string direction, int visibleCount, int totalCount,
        IEnumerable<(string Path, string Anchor, string Title)> links)
    {
        var section = new Expander
        {
            Style = (Style)Application.Current.Resources["MinimalExpander"],
            Foreground = (Brush)Resources["MapChromeText"],
            Header = CreateLinkHeading(title, direction, visibleCount, totalCount),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(section, UiStrings.T(title));
        section.Expanded += (_, _) =>
        {
            _linkSectionExpansion[title] = true;
            section.Content ??= CreateLinkRows(links);
        };
        section.Collapsed += (_, _) => _linkSectionExpansion[title] = false;
        if (_linkSectionExpansion.TryGetValue(title, out var expanded) && expanded)
        {
            section.Content = CreateLinkRows(links);
            section.IsExpanded = true;
        }

        Links.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(11, 13, 18)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(35, 41, 52)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 4, 0, 12),
            Child = section
        });
    }

    /// <summary>Builds the bounded set of rows for a visible section, preserving the catalog's target order.</summary>
    private StackPanel CreateLinkRows(IEnumerable<(string Path, string Anchor, string Title)> links)
    {
        var rows = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var link in links)
        {
            rows.Children.Add(CreateLinkRow(link.Path, link.Anchor, link.Title));
        }
        if (rows.Children.Count == 0)
        {
            var empty = new TextBlock
            {
                FontSize = 11,
                Foreground = LinkMutedBrush(),
                Margin = new Thickness(4, 4, 4, 6)
            };
            AppLocalization.SetText(empty, "No links yet");
            rows.Children.Add(empty);
        }
        return rows;
    }

    /// <summary>Displays the known distinct-target total, identifying the visible subset when the inspector is bounded.</summary>
    private Grid CreateLinkHeading(string title, string direction, int visibleCount, int totalCount)
    {
        var heading = new Grid { Margin = new Thickness(4, 2, 4, 2) };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(new VectorIcon
        {
            Symbol = direction,
            Width = 16,
            Height = 16,
            Foreground = (Brush)Application.Current.Resources["SystemAccent"],
            VerticalAlignment = VerticalAlignment.Center
        });

        var label = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Foreground = (Brush)Resources["MapChromeText"],
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        AppLocalization.SetText(label, title);
        Grid.SetColumn(label, 1);
        heading.Children.Add(label);

        var count = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 10,
            Foreground = LinkMutedBrush(),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        if (visibleCount < totalCount)
        {
            AppLocalization.SetText(count, "{0} of {1}", visibleCount, totalCount);
        }
        else
        {
            AppLocalization.SetRaw(count, totalCount.ToString(CultureInfo.CurrentCulture));
        }
        Grid.SetColumn(count, 2);
        heading.Children.Add(count);
        return heading;
    }

    /// <summary>Shows a readable document title and its path while retaining navigation and native context actions.</summary>
    private Button CreateLinkRow(string path, string anchor, string title)
    {
        var button = new Button { Style = (Style)Resources["MapLinkButton"] };
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(25) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(new VectorIcon
        {
            Symbol = "file", Width = 16, Height = 16,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0)
        });
        var details = new StackPanel();
        var caption = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        caption.SetBinding(TextBlock.ForegroundProperty, new Binding("Foreground") { Source = button });
        AppLocalization.SetRaw(caption, title);
        details.Children.Add(caption);
        var location = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"), FontSize = 10,
            Foreground = LinkMutedBrush(), TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 4, 0, 0)
        };
        AppLocalization.SetRaw(location, path + (anchor.Length > 0 ? "#" + anchor : ""));
        details.Children.Add(location);
        Grid.SetColumn(details, 1);
        content.Children.Add(details);
        button.Content = content;
        AppLocalization.SetRaw(button);
        AutomationProperties.SetName(button, title + " · " + path);
        ToolTipService.SetToolTip(button, path + (anchor.Length > 0 ? "#" + anchor : ""));
        button.MouseEnter += (_, _) => button.Foreground = new SolidColorBrush(System.Windows.Media.Colors.White);
        button.MouseLeave += (_, _) => button.Foreground = (Brush)Resources["MapChromeText"];
        var workspaceId = _workspaceId;
        var target = new FileMenuTarget(workspaceId, path, title);
        button.MouseRightButtonDown += (_, arguments) =>
        {
            if (target.WorkspaceId.StartsWith("native:", StringComparison.Ordinal))
            {
                arguments.Handled = true;
                FileMenuRequested?.Invoke(target, arguments.GetPosition(Application.Current.RootVisual));
            }
        };
        button.Click += async (_, _) => await RunActionAsync(async () =>
        {
            if (_workspaceId == workspaceId)
            {
                await NavigateFromInspectorAsync(workspaceId, path, anchor);
            }
        });
        return button;
    }

    /// <summary>Uses a restrained secondary text color with readable contrast on the black map chrome.</summary>
    private static SolidColorBrush LinkMutedBrush() => new(Color.FromRgb(142, 154, 174));
}
