using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Tools.Widgets;

internal static class WidgetFooter
{
    private const string Website = "https://umbertogiacobbi.biz";
    private const string Manifesto = Website + "/vibeware/manifesto";

    internal static Grid Create(string product, Action showAbout)
    {
        var footer = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var links = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center
        };
        var about = TextLink("About");
        AutomationProperties.SetName(about, $"About {product}");
        about.Click += (_, _) => showAbout();
        links.Children.Add(about);
        links.Children.Add(TextLink("Vibe", Manifesto));
        links.Children.Add(TextLink("Terms", Website + "/terms/"));
        links.Children.Add(TextLink("Privacy", Website + "/privacy/"));
        footer.Children.Add(links);

        var badge = new HyperlinkButton
        {
            NavigateUri = new Uri(Manifesto), Padding = new Thickness(0),
            Content = new Image
            {
                Source = new BitmapImage(new Uri("ms-appx:///Assets/VibeWare.png")),
                Width = 70, Height = 70, Stretch = Stretch.Uniform
            }
        };
        AutomationProperties.SetName(badge, "VibeWare manifesto");
        Grid.SetColumn(badge, 1);
        footer.Children.Add(badge);
        return footer;
    }

    internal static Button CreateCompact(string product, Action showAbout)
    {
        var menu = new MenuFlyout();
        var about = new MenuFlyoutItem { Text = "About " + product };
        about.Click += (_, _) => showAbout();
        menu.Items.Add(about);
        void AddLink(string title, string url)
        {
            var item = new MenuFlyoutItem { Text = title };
            item.Click += async (_, _) => await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
            menu.Items.Add(item);
        }
        AddLink("VibeWare manifesto", Manifesto);
        AddLink("Terms", Website + "/terms/");
        AddLink("Privacy", Website + "/privacy/");
        var button = new Button { Content = "VibeWare", Flyout = menu, FontSize = 12,
            Padding = new Thickness(8, 4, 8, 4), MinWidth = 0,
            Style = (Style)Application.Current.Resources["StudioBrandStyle"] };
        AutomationProperties.SetName(button, "VibeWare · About and links");
        AutomationProperties.SetAutomationId(button, "VibeWareMenu");
        return button;
    }

    private static HyperlinkButton TextLink(string text, string? url = null)
    {
        var link = new HyperlinkButton
        {
            Content = text, FontSize = 11, Padding = new Thickness(0), MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (url is not null) link.NavigateUri = new Uri(url);
        return link;
    }
}
