using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace WaterRipple;

internal static class Ui
{
    internal sealed class BooleanVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language) =>
            value is true ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object value, Type targetType, object parameter, string language) => DependencyProperty.UnsetValue;
    }

    // Root-level pointer movement prevents a scrollbar staying visible after a child handles input.
    internal static Action HoverScrollBar(ScrollViewer scroll, FrameworkElement region, UIElement eventRoot)
    {
        scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        ScrollBar? vertical = null;
        Microsoft.UI.Xaml.Media.Animation.Storyboard? animation = null;
        bool hovered = false;
        ScrollBar? FindBar(DependencyObject node)
        {
            if (node is ScrollBar bar && bar.Orientation == Orientation.Vertical) return bar;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var found = FindBar(VisualTreeHelper.GetChild(node, i));
                if (found is not null) return found;
            }
            return null;
        }
        void Fade(bool inside)
        {
            if (inside == hovered) return;
            hovered = inside;
            if (vertical is null)
            {
                scroll.VerticalScrollBarVisibility = inside ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden;
                return;
            }
            double opacity = vertical.Opacity;
            animation?.Stop();
            vertical.Opacity = opacity;
            vertical.IsHitTestVisible = inside;
            var fade = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = opacity, To = inside ? 1 : 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(500))
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fade, vertical);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fade, "Opacity");
            animation = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            animation.Children.Add(fade);
            animation.Begin();
        }
        void Update(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
        {
            var point = args.GetCurrentPoint(region).Position;
            Fade(point.X >= 0 && point.Y >= 0 && point.X < region.ActualWidth && point.Y < region.ActualHeight);
        }
        void Loaded(object sender, RoutedEventArgs args)
        {
            // Reserve the scrollbar's normal width to keep content steady during the fade.
            scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            scroll.ApplyTemplate();
            scroll.UpdateLayout();
            vertical = FindBar(scroll);
            bool wasHovered = hovered;
            hovered = false;
            if (vertical is not null) { vertical.Opacity = 0; vertical.IsHitTestVisible = false; }
            else scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            if (wasHovered) Fade(true);
        }
        scroll.Loaded += Loaded;
        eventRoot.AddHandler(UIElement.PointerMovedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Update), true);
        eventRoot.AddHandler(UIElement.PointerEnteredEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Update), true);
        eventRoot.AddHandler(UIElement.PointerExitedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Update), true);
        region.Unloaded += (_, _) =>
        {
            animation?.Stop();
            scroll.Loaded -= Loaded;
            eventRoot.RemoveHandler(UIElement.PointerMovedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Update));
            eventRoot.RemoveHandler(UIElement.PointerEnteredEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Update));
            eventRoot.RemoveHandler(UIElement.PointerExitedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Update));
        };
        return () => Fade(false);
    }

    internal const string ManifestoUrl = "https://umbertogiacobbi.biz/vibeware/manifesto";
    internal const string VibeWareRepositoryUrl = "https://github.com/umbertotechnopreneur/VibeWare";
    internal const string CreatorUrl = "https://umbertogiacobbi.biz";
    internal const string TermsUrl = "https://umbertogiacobbi.biz/terms/";
    internal const string PrivacyUrl = "https://umbertogiacobbi.biz/privacy/";

    private static readonly NumberLabelConverter NumberLabel = new();
    private static readonly HexEditorConverter HexEditor = new();

    internal static SolidColorBrush Cream => Brush(243, 233, 218);
    internal static SolidColorBrush MutedCream => Brush(191, 187, 178);
    internal static SolidColorBrush Denim => Brush(158, 183, 204);
    internal static SolidColorBrush Graphite => Brush(25, 28, 32);

    internal static void Bind(DependencyObject target, DependencyProperty property, object source,
        string path, BindingMode mode = BindingMode.OneWay, IValueConverter? converter = null, object? parameter = null)
    {
        if (target is not FrameworkElement element)
            throw new ArgumentException("A binding target must be a FrameworkElement.", nameof(target));
        element.SetBinding(property, new Binding
        {
            Source = source,
            Path = new PropertyPath(path),
            Mode = mode,
            Converter = converter,
            ConverterParameter = parameter,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
        });
    }

    internal static Grid BrandHeader(string title, string subtitle)
    {
        var header = new Grid { ColumnSpacing = 18 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var words = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        var eyebrow = Text("VIBEWARE / WATER RIPPLE", 11);
        eyebrow.Foreground = Denim;
        words.Children.Add(eyebrow);
        words.Children.Add(Text(title, 28));
        var description = Text(subtitle, 13);
        description.Foreground = MutedCream;
        words.Children.Add(description);
        header.Children.Add(words);

        var logo = new Image
        {
            Source = new BitmapImage(new Uri("ms-appx:///Assets/VibeWare.png")) { DecodePixelWidth = 96 },
            Width = 64,
            Height = 64,
            Stretch = Stretch.Uniform
        };
        var manifesto = new HyperlinkButton
        {
            Content = logo,
            NavigateUri = new Uri(ManifestoUrl),
            Padding = new Thickness(4),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        AutomationProperties.SetName(manifesto, "Participates in VibeWare. Read the manifesto");
        ToolTipService.SetToolTip(manifesto, "Participates in VibeWare. Read the manifesto");
        Grid.SetColumn(manifesto, 1);
        header.Children.Add(manifesto);
        return header;
    }

    internal static FrameworkElement DiagnosticRows(AppState state, string property)
    {
        var rows = new StackPanel { Spacing = 4 };
        void Refresh()
        {
            rows.Children.Clear();
            string value = property == nameof(AppState.MetricsText) ? state.MetricsText : state.ResourcesText;
            foreach (string line in value.Split('\n'))
            {
                var parts = line.Split('\t', 2);
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var label = Text(parts[0], 12); label.Foreground = MutedCream; row.Children.Add(label);
                if (parts.Length == 2)
                {
                    var number = Text(parts[1], 12); number.TextAlignment = TextAlignment.Right;
                    Grid.SetColumn(number, 1); row.Children.Add(number);
                }
                rows.Children.Add(row);
            }
        }
        void Changed(object? sender, PropertyChangedEventArgs args) { if (args.PropertyName == property) Refresh(); }
        rows.Loaded += (_, _) => { state.PropertyChanged += Changed; Refresh(); };
        rows.Unloaded += (_, _) => state.PropertyChanged -= Changed;
        Refresh();
        return rows;
    }

    internal static void AttachAppearance(Window window, Grid root, AppSettings settings)
    {
        root.RequestedTheme = ElementTheme.Dark;
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "WaterRipple.ico");
        if (File.Exists(iconPath)) window.AppWindow.SetIcon(iconPath);
        void Apply()
        {
            try
            {
                window.SystemBackdrop = settings.UseAcrylic ? new DesktopAcrylicBackdrop() : new MicaBackdrop();
                root.Background = settings.UseAcrylic ? new SolidColorBrush(Colors.Transparent) : Graphite;
            }
            catch (COMException)
            {
                // Let remote/unsupported graphics sessions retain a readable solid surface.
                window.SystemBackdrop = null;
                root.Background = Graphite;
            }
        }
        void Changed(object? sender, PropertyChangedEventArgs args)
        {
            if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName == nameof(AppSettings.UseAcrylic))
                Apply();
        }
        void Closed(object sender, WindowEventArgs args)
        {
            settings.PropertyChanged -= Changed;
            window.Closed -= Closed;
            window.SystemBackdrop = null;
        }
        Apply();
        settings.PropertyChanged += Changed;
        window.Closed += Closed;
    }

    internal static Slider Slider(Panel parent, string label, AppSettings settings, string property,
        double minimum, double maximum, double step, string unit)
    {
        var group = new StackPanel { Spacing = 1 };
        var heading = new Grid();
        var name = Text(label, 13);
        name.Margin = new Thickness(0, 0, 68, 0);
        heading.Children.Add(name);
        var value = Text("", 12);
        value.Foreground = Denim;
        value.HorizontalAlignment = HorizontalAlignment.Right;
        Bind(value, TextBlock.TextProperty, settings, property, converter: NumberLabel, parameter: unit);
        heading.Children.Add(value);
        group.Children.Add(heading);
        var slider = new Slider
        {
            Minimum = minimum,
            Maximum = maximum,
            StepFrequency = step,
            MinHeight = 28,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(slider, label);
        Bind(slider, RangeBase.ValueProperty, settings, property, BindingMode.TwoWay);
        group.Children.Add(slider);
        parent.Children.Add(group);
        return slider;
    }

    internal static ToggleSwitch Toggle(Panel parent, string label, object source, string property)
    {
        var toggle = new ToggleSwitch { Header = label, OffContent = "Off", OnContent = "On" };
        AutomationProperties.SetName(toggle, label);
        Bind(toggle, ToggleSwitch.IsOnProperty, source, property, BindingMode.TwoWay);
        parent.Children.Add(toggle);
        return toggle;
    }

    internal static TextBox HexColor(Panel parent, string label, AppSettings settings, string property)
    {
        var editor = new TextBox
        {
            Header = label,
            PlaceholderText = "#RRGGBB",
            MaxLength = 7,
            IsSpellCheckEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(editor, label);
        Bind(editor, TextBox.TextProperty, settings, property, BindingMode.TwoWay, HexEditor);
        parent.Children.Add(editor);
        return editor;
    }

    internal static TextBlock Text(string text, double size) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = Cream,
        TextWrapping = TextWrapping.Wrap
    };

    internal static UIElement Footer(Action? showAbout = null)
    {
        var footer = new StackPanel { Spacing = 5, Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        HyperlinkButton FooterLink(string label, string uri)
        {
            var link = Link(label, uri);
            link.FontSize = 11;
            link.VerticalAlignment = VerticalAlignment.Center;
            return link;
        }
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
        var about = new HyperlinkButton { Content = "About Water Ripple", Padding = new Thickness(0),
            MinWidth = 0, MinHeight = 0, FontSize = 11, Foreground = Denim, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        about.Click += (_, _) =>
        {
            if (showAbout is not null) showAbout();
            else new AboutWindow().Activate();
        };
        navigation.Children.Add(about);
        navigation.Children.Add(Text("·", 11));
        navigation.Children.Add(FooterLink("Terms", TermsUrl));
        navigation.Children.Add(Text("·", 11));
        navigation.Children.Add(FooterLink("Privacy", PrivacyUrl));
        footer.Children.Add(navigation);
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
        brand.Children.Add(Text("This is VibeWare ·", 11));
        brand.Children.Add(FooterLink("Manifesto", ManifestoUrl));
        footer.Children.Add(brand);
        return footer;
    }

    internal static HyperlinkButton Link(string label, string uri) => new()
    {
        Content = label,
        NavigateUri = new Uri(uri),
        Foreground = Denim,
        Padding = new Thickness(0),
        MinWidth = 0, MinHeight = 0,
        HorizontalAlignment = HorizontalAlignment.Left
    };

    private static TextBlock NavigationLine() => new()
    {
        TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 11,
        Foreground = MutedCream
    };
    private static Hyperlink InlineLink(string text, string? uri = null)
    {
        var link = new Hyperlink { Foreground = Denim };
        link.Inlines.Add(new Run { Text = text });
        if (uri is not null) link.NavigateUri = new Uri(uri);
        AutomationProperties.SetName(link, text);
        return link;
    }
    private static void AddSeparator(TextBlock line) => line.Inlines.Add(new Run { Text = " · " });
    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Windows.UI.Color.FromArgb(255, r, g, b));

    private sealed class NumberLabelConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is not double number) return "";
            string unit = parameter as string ?? "";
            if (unit == "%") return (number * 100).ToString("0", CultureInfo.CurrentCulture) + "%";
            string digits = unit == "s" ? "0.00" : "0.##";
            return number.ToString(digits, CultureInfo.CurrentCulture) + (unit.Length > 0 ? " " + unit : "");
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language) => DependencyProperty.UnsetValue;
    }

    private sealed class HexEditorConverter : IValueConverter
    {
        private static readonly Regex Valid = new(@"^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant);
        public object Convert(object value, Type targetType, object parameter, string language) => value as string ?? "";
        public object ConvertBack(object value, Type targetType, object parameter, string language) =>
            value is string text && Valid.IsMatch(text) ? text.ToUpperInvariant() : DependencyProperty.UnsetValue;
    }
}
