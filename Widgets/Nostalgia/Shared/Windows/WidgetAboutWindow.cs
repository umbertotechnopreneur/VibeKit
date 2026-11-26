using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace Tools.Widgets;

/// <summary>Shows the family About surface with the owning product’s identity and diagnostics.</summary>
public sealed class WidgetAboutWindow : Window
{
    private static WidgetAboutWindow? current;

    /// <summary>Open or reactivate the current product’s About window.</summary>
    public static async void Show(Window owner, WidgetIdentity product, Action<Window, Grid>? appearance = null)
    {
        try
        {
            product.Validate();
            if (current is null)
            {
                var window = new WidgetAboutWindow(owner, product, appearance);
                current = window;
                void CloseWithOwner(object sender, WindowEventArgs args) => window.Close();
                owner.Closed += CloseWithOwner;
                window.Closed += (_, _) => { owner.Closed -= CloseWithOwner; current = null; };
            }
            if (current.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
                presenter.Restore();
            current.Activate();
        }
        catch (Exception error) when (error is ArgumentException or FileNotFoundException or COMException or InvalidOperationException)
        {
            owner.AppWindow.Show();
            owner.Activate();
            var host = (FrameworkElement)owner.Content;
            await new ContentDialog { Title = "Could not open About", Content = error.Message,
                CloseButtonText = "Close", XamlRoot = host.XamlRoot, RequestedTheme = host.ActualTheme }.ShowAsync();
        }
    }

    private WidgetAboutWindow(Window owner, WidgetIdentity product, Action<Window, Grid>? appearance)
    {
        Title = "About " + product.DisplayName;
        var iconPath = Path.Combine(AppContext.BaseDirectory, product.IconPath);
        if (!File.Exists(iconPath)) throw new FileNotFoundException("Product icon is missing: " + iconPath);
        AppWindow.SetIcon(iconPath);
        FitToWorkArea(owner);

        var root = new Grid { Padding = new Thickness(24), RowSpacing = 10 };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;
        if (appearance is not null) appearance(this, root);
        else
        {
            root.Style = (Style)Application.Current.Resources["WindowSurfaceStyle"];
            if (owner.Content is FrameworkElement host) root.RequestedTheme = host.RequestedTheme;
            try
            {
                if (owner.SystemBackdrop is DesktopAcrylicBackdrop)
                {
                    SystemBackdrop = new DesktopAcrylicBackdrop();
                    root.Background = new SolidColorBrush(Colors.Transparent);
                }
            }
            catch (COMException) { SystemBackdrop = null; }
        }

        var content = WidgetAbout.Create(
            ProductIcon(iconPath),
            new Image { Width = 64, Height = 64, Stretch = Stretch.Uniform,
                Source = new BitmapImage(new Uri("ms-appx:///Nostalgia.WinUI/Assets/VibeWare.png")) },
            product.DisplayName, "Version " + product.Version.ToString(3),
            product.Description, product.ProductUrl,
            "I provide this software independently and as-is, without warranty.",
            details: DiagnosticAboutActions.Create(product.DiagnosticsId, product.DisplayName), followTheme: true, license: product.License);
        content.Margin = new Thickness(0, 12, 0, 12);
        var scroll = new ScrollViewer
        {
            Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        root.Children.Add(scroll);
        ScrollBarBehavior.HoverScrollBar(scroll, scroll, root);

        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 100 };
        close.Click += (_, _) => Close();
        Grid.SetRow(close, 1);
        root.Children.Add(close);
        root.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape) { e.Handled = true; Close(); }
        };
    }

    /// <summary>Decode the largest frame of the actual Windows icon for high-DPI displays.</summary>
    private static Grid ProductIcon(string path)
    {
        var mark = new Image { Width = 106, Height = 106, Stretch = Stretch.Uniform };
        var container = new Grid();
        container.Children.Add(mark);
        mark.Loaded += LoadIcon;
        return container;

        async void LoadIcon(object sender, RoutedEventArgs args)
        {
            mark.Loaded -= LoadIcon;
            try
            {
                // ICO frame order differs between projects. Use the largest frame even at high DPI.
                var file = await StorageFile.GetFileFromPathAsync(path);
                using var stream = await file.OpenReadAsync();
                var decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.IcoDecoderId, stream);
                var largest = await decoder.GetFrameAsync(0);
                for (uint index = 1; index < decoder.FrameCount; index++)
                {
                    var frame = await decoder.GetFrameAsync(index);
                    if (frame.PixelWidth > largest.PixelWidth) largest = frame;
                }
                using var bitmap = await largest.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                var source = new SoftwareBitmapSource();
                await source.SetBitmapAsync(bitmap);
                if (mark.IsLoaded) mark.Source = source;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or COMException or ArgumentException)
            {
                if (!mark.IsLoaded) return;
                container.Children.Clear();
                container.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Error,
                    Title = "Could not load the product icon", Message = error.Message });
            }
        }
    }

    private void FitToWorkArea(Window owner)
    {
        var display = DisplayArea.GetFromWindowId(owner.AppWindow.Id, DisplayAreaFallback.Nearest);
        if (display is null) throw new InvalidOperationException("Could not determine the display for the About window.");
        var area = display.WorkArea;
        uint dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(owner));
        if (dpi == 0) throw new InvalidOperationException("Could not determine the DPI of the studio window.");
        double scale = dpi / 96.0;
        int width = Math.Max(1, Math.Min((int)Math.Round(640 * scale), Math.Max(1, (int)(area.Width * 0.9))));
        int height = Math.Max(1, Math.Min((int)Math.Round(620 * scale), Math.Max(1, (int)(area.Height * 0.9))));
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2,
            area.Y + (area.Height - height) / 2, width, height));
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
}
