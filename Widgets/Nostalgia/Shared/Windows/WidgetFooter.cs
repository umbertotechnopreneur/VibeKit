using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Tools.Widgets;

/// <summary>Creates the common VibeWare footer menu.</summary>
public static class WidgetFooter
{
    private const string Website = "https://umbertogiacobbi.biz";
    private const string Manifesto = Website + "/vibeware/manifesto";

    /// <summary>Opens the product About window or a VibeWare site from the footer.</summary>
    public static Button Create(string product, Action showAbout)
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
}
