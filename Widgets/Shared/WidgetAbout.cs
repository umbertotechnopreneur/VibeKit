using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Tools.Widgets;

internal static class WidgetAbout
{
    internal const string Website = "https://umbertogiacobbi.biz";
    internal const string Manifesto = Website + "/vibeware/manifesto";
    internal const string Terms = Website + "/terms/";
    internal const string Privacy = Website + "/privacy/";
    private static readonly FontFamily Monospace = new("Consolas");

    internal static SolidColorBrush Paper() => Brush(255, 244, 220);

    internal static StackPanel Create(FrameworkElement mark, Image initiative, string name, string version,
        string description, string sourceUrl, string notice, UIElement? details = null, bool dark = false, bool followTheme = false,
        string license = "Proprietary")
    {
        var content = new StackPanel { Spacing = 24, MaxWidth = 660 };
        mark.HorizontalAlignment = HorizontalAlignment.Center;
        content.Children.Add(mark);

        var identity = new StackPanel { Spacing = 5 };
        var title = Centered(name, 23, dark: dark, followTheme: followTheme);
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level1);
        identity.Children.Add(title);
        identity.Children.Add(Centered(version, 14, secondary: true, dark: dark, followTheme: followTheme));
        content.Children.Add(identity);
        content.Children.Add(Centered(description, 15, dark: dark, followTheme: followTheme));
        content.Children.Add(Centered(notice, 13, secondary: true, dark: dark, followTheme: followTheme));
        content.Children.Add(Centered("I'm Umberto Giacobbi · " + license, 15, dark: dark, followTheme: followTheme));
        if (details is not null)
            content.Children.Add(details);

        var badge = new HyperlinkButton
        {
            Content = initiative, NavigateUri = new Uri(Manifesto),
            HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(3),
            Margin = new Thickness(0, 10, 0, 0)
        };
        AutomationProperties.SetName(badge, "VibeWare manifesto");
        ToolTipService.SetToolTip(badge, "VibeWare manifesto");
        content.Children.Add(badge);

        var links = new Grid { ColumnSpacing = 8, RowSpacing = 4 };
        links.Children.Add(Link("UmbertoGiacobbi.biz", Website, "\uE774", dark, followTheme));
        var source = Link("Source", sourceUrl, "\uE943", dark, followTheme);
        ToolTipService.SetToolTip(source, "View this utility in the VibeKit repository.");
        links.Children.Add(source);
        links.Children.Add(Link("Terms", Terms, "\uE8A5", dark, followTheme));
        links.Children.Add(Link("Privacy", Privacy, "\uE72E", dark, followTheme));
        ArrangeLinks(links, 1);
        links.SizeChanged += (_, e) => ArrangeLinks(links, e.NewSize.Width >= 550 ? 4 : e.NewSize.Width >= 350 ? 2 : 1);
        content.Children.Add(links);
        return content;
    }

    private static SolidColorBrush Brush(byte red, byte green, byte blue) =>
        new(Windows.UI.Color.FromArgb(255, red, green, blue));

    private static TextBlock Centered(string text, double size, bool secondary = false, bool dark = false, bool followTheme = false)
    {
        var label = new TextBlock
        {
            Text = text, FontSize = size, FontFamily = Monospace, TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center
        };
        if (!followTheme)
            label.Foreground = dark ? (secondary ? Brush(191, 187, 178) : Brush(243, 233, 218)) : (secondary ? Brush(82, 101, 115) : Brush(35, 42, 47));
        return label;
    }

    private static HyperlinkButton Link(string text, string url, string glyph, bool dark, bool followTheme)
    {
        var foreground = dark ? Brush(158, 183, 204) : Brush(58, 106, 150);
        var pair = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var icon = new FontIcon
        {
            Glyph = glyph, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 15,
            VerticalAlignment = VerticalAlignment.Center
        };
        var caption = new TextBlock
        {
            Text = text, FontFamily = Monospace, FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        };
        pair.Children.Add(icon);
        pair.Children.Add(caption);
        var link = new HyperlinkButton
        {
            Content = pair, NavigateUri = new Uri(url),
            HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(4)
        };
        if (!followTheme)
        {
            icon.Foreground = foreground;
            caption.Foreground = foreground;
            link.Foreground = foreground;
        }
        AutomationProperties.SetName(link, text);
        return link;
    }

    private static void ArrangeLinks(Grid links, int columns)
    {
        if (links.ColumnDefinitions.Count == columns + 2) return;
        links.ColumnDefinitions.Clear();
        links.RowDefinitions.Clear();
        links.ColumnDefinitions.Add(new ColumnDefinition());
        for (var column = 0; column < columns; column++)
            links.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        links.ColumnDefinitions.Add(new ColumnDefinition());
        for (var row = 0; row < links.Children.Count / columns; row++)
            links.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var index = 0; index < links.Children.Count; index++)
        {
            var child = (FrameworkElement)links.Children[index];
            Grid.SetColumn(child, index % columns + 1);
            Grid.SetRow(child, index / columns);
        }
    }
}
