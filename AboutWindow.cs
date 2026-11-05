using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace WaterRipple;

internal sealed class AboutWindow : Window
{
    internal AboutWindow(AppSettings settings, Uri? applicationRepository = null)
    {
        Title = "About Water Ripple";
        FitToWorkArea();
        var root = new Grid { Padding = new Thickness(24), RowSpacing = 14 };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;
        Ui.AttachAppearance(this, root, settings);

        // The content scrolls as one column; the Close action remains outside it.
        var content = new StackPanel
        {
            Spacing = 18,
            MaxWidth = 720,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var identity = new StackPanel { Spacing = 7, HorizontalAlignment = HorizontalAlignment.Stretch };
        var logo = new Image
        {
            Source = new BitmapImage(new Uri("ms-appx:///Assets/VibeWare.png")) { DecodePixelWidth = 192 },
            Width = 120,
            Height = 120,
            Stretch = Stretch.Uniform
        };
        var logoLink = new HyperlinkButton
        {
            Content = logo,
            NavigateUri = new Uri(Ui.ManifestoUrl),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        AutomationProperties.SetName(logoLink, "Participates in VibeWare. Read the manifesto");
        ToolTipService.SetToolTip(logoLink, "Participates in VibeWare. Read the manifesto");
        identity.Children.Add(logoLink);
        var name = Centered("WATER RIPPLE", 24);
        name.FontFamily = new FontFamily("Consolas");
        identity.Children.Add(name);
        var version = Centered(VersionText(), 12);
        version.Foreground = Ui.MutedCream;
        identity.Children.Add(version);
        identity.Children.Add(Centered("Water-like desktop ripples with every click.", 14));
        var creator = Centered("Created by Umberto Giacobbi.", 12);
        creator.Foreground = Ui.MutedCream;
        identity.Children.Add(creator);
        content.Children.Add(identity);

        var disclosure = new StackPanel { Spacing = 12 };
        disclosure.Children.Add(Ui.Text("This is VibeWare", 19));
        // The disclosure describes responsibility without claiming completed review.
        disclosure.Children.Add(Ui.Text("I created Water Ripple with AI assistance. I define the purpose, guide the implementation, and take responsibility for checking functionality, performance, and security.", 13));
        disclosure.Children.Add(Ui.Text("VibeWare is my initiative for building useful software openly with AI. To learn what VibeWare means and why I created it, read the manifesto.", 13));
        var tagline = Ui.Text("Human intent, AI execution, and plenty of tokens", 13);
        tagline.FontFamily = new FontFamily("Consolas");
        disclosure.Children.Add(tagline);
        disclosure.Children.Add(NavigationLink("Read the VibeWare manifesto", Ui.ManifestoUrl, "\uE8A5"));
        content.Children.Add(new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(225, 38, 59, 80)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20),
            Child = disclosure
        });

        var links = new Grid { ColumnSpacing = 16, RowSpacing = 6 };
        if (applicationRepository is not null && applicationRepository.Scheme == Uri.UriSchemeHttps)
            links.Children.Add(NavigationLink("Water Ripple on GitHub", applicationRepository.AbsoluteUri, "\uE943"));
        links.Children.Add(NavigationLink("VibeWare on GitHub", Ui.VibeWareRepositoryUrl, "\uE943"));
        links.Children.Add(NavigationLink("umbertogiacobbi.biz", Ui.CreatorUrl, "\uE774"));
        ArrangeLinks(links, 1);
        links.SizeChanged += (_, e) => ArrangeLinks(links, e.NewSize.Width >= 680 ? 3 : e.NewSize.Width >= 450 ? 2 : 1);
        content.Children.Add(links);

        var legal = new TextBlock
        {
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = Ui.MutedCream
        };
        legal.Inlines.Add(LegalLink("Terms", Ui.TermsUrl));
        legal.Inlines.Add(new Run { Text = " · " });
        legal.Inlines.Add(LegalLink("Privacy", Ui.PrivacyUrl));
        content.Children.Add(legal);
        var scroll = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        root.Children.Add(scroll);

        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 100 };
        close.Click += (_, _) => Close();
        var actionRow = new Border
        {
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(100, 111, 125, 139)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 14, 0, 0),
            Child = close
        };
        Grid.SetRow(actionRow, 1);
        root.Children.Add(actionRow);
        root.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape) { e.Handled = true; Close(); }
        };
    }

    private void FitToWorkArea()
    {
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        if (display is null) { AppWindow.Resize(new SizeInt32(840, 950)); return; }
        var area = display.WorkArea;
        double scale = Math.Max(96, GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this))) / 96.0;
        int width = Math.Max(1, Math.Min((int)Math.Round(840 * scale), Math.Max(1, (int)(area.Width * 0.9))));
        int height = Math.Max(1, Math.Min((int)Math.Round(950 * scale), Math.Max(1, (int)(area.Height * 0.9))));
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2,
            area.Y + (area.Height - height) / 2, width, height));
    }

    private static string VersionText()
    {
        try
        {
            var version = Windows.ApplicationModel.Package.Current.Id.Version;
            return $"Version {version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            return $"Version {typeof(AboutWindow).Assembly.GetName().Version?.ToString(3) ?? "unknown"}";
        }
    }

    private static TextBlock Centered(string text, double size)
    {
        var block = Ui.Text(text, size);
        block.TextAlignment = TextAlignment.Center;
        block.HorizontalAlignment = HorizontalAlignment.Stretch;
        return block;
    }

    private static HyperlinkButton NavigationLink(string text, string uri, string glyph)
    {
        var content = new Grid { ColumnSpacing = 7 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center
        });
        var label = Ui.Text(text, 12);
        label.Foreground = Ui.Denim;
        label.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(label, 1);
        content.Children.Add(label);
        var link = new HyperlinkButton
        {
            NavigateUri = new Uri(uri),
            Content = content,
            Foreground = Ui.Denim,
            Padding = new Thickness(0, 5, 0, 5),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(link, text);
        return link;
    }

    private static Hyperlink LegalLink(string text, string uri)
    {
        var link = new Hyperlink { NavigateUri = new Uri(uri), Foreground = Ui.Denim };
        link.Inlines.Add(new Run { Text = text });
        AutomationProperties.SetName(link, text);
        return link;
    }

    private static void ArrangeLinks(Grid links, int requestedColumns)
    {
        int columns = Math.Min(requestedColumns, Math.Max(1, links.Children.Count));
        if (links.ColumnDefinitions.Count == columns) return;
        links.ColumnDefinitions.Clear();
        links.RowDefinitions.Clear();
        for (int column = 0; column < columns; column++) links.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int rows = (links.Children.Count + columns - 1) / columns;
        for (int row = 0; row < rows; row++) links.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int index = 0; index < links.Children.Count; index++)
        {
            if (links.Children[index] is FrameworkElement child)
            {
                Grid.SetColumn(child, index % columns);
                Grid.SetRow(child, index / columns);
            }
        }
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
}
