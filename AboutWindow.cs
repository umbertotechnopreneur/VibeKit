using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Tools.Widgets;
using Windows.Graphics;

namespace WaterRipple;

internal sealed class AboutWindow : Window
{
    internal AboutWindow()
    {
        Title = "About Water Ripple";
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "WaterRipple.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        FitToWorkArea();

        var root = new Grid { Padding = new Thickness(24), RowSpacing = 10, Background = WidgetAbout.Paper(), RequestedTheme = ElementTheme.Light };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var content = WidgetAbout.Create(
            ImageAsset("WaterRipple.png", 106),
            ImageAsset("VibeWare.png", 64),
            "Water Ripple", VersionText(),
            "I built Water Ripple with AI assistance to add water-like ripples to my desktop. I guide the work and take responsibility for it.",
            "https://github.com/umbertotechnopreneur/Tools/tree/main/Widgets/WaterRipple",
            "I provide this software independently and as-is, without warranty.");
        content.Margin = new Thickness(0, 12, 0, 12);
        root.Children.Add(new ScrollViewer
        {
            Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        });

        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 100 };
        close.Click += (_, _) => Close();
        Grid.SetRow(close, 1);
        root.Children.Add(close);
        root.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape) { e.Handled = true; Close(); }
        };
    }

    private static Image ImageAsset(string name, double size) => new()
    {
        Source = new BitmapImage(new Uri($"ms-appx:///Assets/{name}")),
        Width = size, Height = size, Stretch = Stretch.Uniform
    };

    private void FitToWorkArea()
    {
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        if (display is null) { AppWindow.Resize(new SizeInt32(640, 620)); return; }
        var area = display.WorkArea;
        double scale = Math.Max(96, GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this))) / 96.0;
        int width = Math.Max(1, Math.Min((int)Math.Round(640 * scale), Math.Max(1, (int)(area.Width * 0.9))));
        int height = Math.Max(1, Math.Min((int)Math.Round(620 * scale), Math.Max(1, (int)(area.Height * 0.9))));
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

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
}
