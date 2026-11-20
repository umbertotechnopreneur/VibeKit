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

namespace WaterRipple;

internal static class Ui
{
    internal sealed class BooleanVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language) =>
            value is true ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object value, Type targetType, object parameter, string language) => DependencyProperty.UnsetValue;
    }

    // Keep the native scroll range and hit testing; only the bar itself reveals on hover.
    internal static Action HoverScrollBar(ScrollViewer scroll, FrameworkElement region, UIElement eventRoot)
    {
        scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        ScrollBar? vertical = null;
        uint? draggingPointer = null;
        bool attached = false;

        ScrollBar? FindBar(DependencyObject node)
        {
            // Do not accidentally select a scrollbar belonging to nested content.
            if (node is ScrollViewer nested && !ReferenceEquals(nested, scroll)) return null;
            if (node is ScrollBar bar && bar.Name == "VerticalScrollBar" && bar.Orientation == Orientation.Vertical) return bar;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var found = FindBar(VisualTreeHelper.GetChild(node, i));
                if (found is not null) return found;
            }
            return null;
        }
        bool IsOverBar(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
        {
            if (vertical is null || scroll.ScrollableHeight <= 0) return false;
            var point = args.GetCurrentPoint(vertical).Position;
            return point.X >= 0 && point.Y >= 0 && point.X < vertical.ActualWidth && point.Y < vertical.ActualHeight;
        }
        void Update(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
        {
            if (vertical is not null) vertical.Opacity = draggingPointer.HasValue || IsOverBar(args) ? 1 : 0;
        }
        void Pressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
        {
            if (IsOverBar(args)) draggingPointer = args.Pointer.PointerId;
            Update(sender, args);
        }
        void Released(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
        {
            if (draggingPointer == args.Pointer.PointerId) draggingPointer = null;
            Update(sender, args);
        }
        void Loaded(object sender, RoutedEventArgs args)
        {
            if (attached) return;
            attached = true;
            scroll.ApplyTemplate();
            scroll.UpdateLayout();
            vertical = FindBar(scroll);
            // Opacity does not remove the hit target or alter the page width.
            if (vertical is not null) vertical.Opacity = 0;
            eventRoot.AddHandler(UIElement.PointerMovedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Update), true);
            eventRoot.AddHandler(UIElement.PointerEnteredEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Update), true);
            eventRoot.AddHandler(UIElement.PointerExitedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Update), true);
            eventRoot.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Pressed), true);
            eventRoot.AddHandler(UIElement.PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Released), true);
            eventRoot.AddHandler(UIElement.PointerCaptureLostEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Released), true);
            eventRoot.AddHandler(UIElement.PointerCanceledEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Released), true);
        }
        scroll.Loaded += Loaded;
        region.Unloaded += (_, _) =>
        {
            if (!attached) return;
            attached = false;
            vertical = null;
            draggingPointer = null;
            eventRoot.RemoveHandler(UIElement.PointerMovedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Update));
            eventRoot.RemoveHandler(UIElement.PointerEnteredEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Update));
            eventRoot.RemoveHandler(UIElement.PointerExitedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Update));
            eventRoot.RemoveHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Pressed));
            eventRoot.RemoveHandler(UIElement.PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Released));
            eventRoot.RemoveHandler(UIElement.PointerCaptureLostEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Released));
            eventRoot.RemoveHandler(UIElement.PointerCanceledEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(Released));
        };
        return () => { draggingPointer = null; if (vertical is not null) vertical.Opacity = 0; };
    }

    internal const string ManifestoUrl = "https://umbertogiacobbi.biz/vibeware/manifesto";
    internal const string VibeWareRepositoryUrl = "https://github.com/umbertotechnopreneur/VibeWare";
    internal const string CreatorUrl = "https://umbertogiacobbi.biz";
    internal const string TermsUrl = "https://umbertogiacobbi.biz/terms/";
    internal const string PrivacyUrl = "https://umbertogiacobbi.biz/privacy/";

    private static readonly NumberLabelConverter NumberLabel = new();
    private static readonly HexEditorConverter HexEditor = new();

    internal static Style SecondaryTextStyle => (Style)Application.Current.Resources["SecondaryTextStyle"];

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

    internal static FrameworkElement DiagnosticRows(AppState state, string property)
    {
        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 6 };
        for (int i = 0; i < 4; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Refresh()
        {
            string value = property == nameof(AppState.MetricsText) ? state.MetricsText : state.ResourcesText;
            string[] lines = value.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            grid.Children.Clear(); grid.RowDefinitions.Clear();
            for (int i = 0; i < lines.Length; i++)
            {
                int row = i / 2, column = (i % 2) * 2;
                if (column == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                string[] parts = lines[i].Split('\t', 2);
                var label = Text(parts[0], 12); label.Style = SecondaryTextStyle; label.TextAlignment = TextAlignment.Right;
                var number = Text(parts.Length == 2 ? parts[1] : "", 12); number.TextAlignment = TextAlignment.Left;
                Grid.SetRow(label, row); Grid.SetColumn(label, column);
                Grid.SetRow(number, row); Grid.SetColumn(number, column + 1);
                grid.Children.Add(label); grid.Children.Add(number);
            }
        }
        void Changed(object? sender, PropertyChangedEventArgs args) { if (args.PropertyName == property) Refresh(); }
        grid.Loaded += (_, _) => { state.PropertyChanged += Changed; Refresh(); };
        grid.Unloaded += (_, _) => state.PropertyChanged -= Changed;
        Refresh(); return grid;
    }
    internal static void AttachAppearance(Window window, Grid root, AppSettings settings)
    {
        root.Style = (Style)Application.Current.Resources["WindowSurfaceStyle"];
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "WaterRipple.ico");
        if (File.Exists(iconPath)) window.AppWindow.SetIcon(iconPath);
        void ApplyTheme() => root.RequestedTheme = settings.ThemeIndex switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        void ApplyBackdrop()
        {
            root.ClearValue(Grid.BackgroundProperty);
            try
            {
                window.SystemBackdrop = settings.UseAcrylic ? new DesktopAcrylicBackdrop() : null;
                if (window.SystemBackdrop is not null) root.Background = new SolidColorBrush(Colors.Transparent);
            }
            catch (COMException)
            {
                // Let remote/unsupported graphics sessions retain a readable solid surface.
                window.SystemBackdrop = null;
            }
        }
        void Changed(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName is null or "" or nameof(AppSettings.ThemeIndex))
                ApplyTheme();
            if (args.PropertyName is null or "" or nameof(AppSettings.UseAcrylic))
                ApplyBackdrop();
        }
        void Closed(object sender, WindowEventArgs args)
        {
            settings.PropertyChanged -= Changed;
            window.Closed -= Closed;
            window.SystemBackdrop = null;
        }
        ApplyTheme();
        ApplyBackdrop();
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
        value.Style = SecondaryTextStyle;
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

    internal static Grid PreferenceSlider(string label, string description, AppSettings settings, string property,
        double minimum, double maximum, double step, string unit)
    {
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
        var value = Text("", 12);
        Bind(value, TextBlock.TextProperty, settings, property, converter: NumberLabel, parameter: unit);
        return Tools.Widgets.EffectStudioLayout.SliderRow(label, description, slider, value);
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
        TextWrapping = TextWrapping.Wrap
    };

    private static TextBlock NavigationLine() => new()
    {
        TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 11,
        Style = SecondaryTextStyle
    };
    private static Hyperlink InlineLink(string text, string? uri = null)
    {
        var link = new Hyperlink();
        link.Inlines.Add(new Run { Text = text });
        if (uri is not null) link.NavigateUri = new Uri(uri);
        AutomationProperties.SetName(link, text);
        return link;
    }
    private static void AddSeparator(TextBlock line) => line.Inlines.Add(new Run { Text = " · " });
    internal static SolidColorBrush Brush(byte r, byte g, byte b) => new(Windows.UI.Color.FromArgb(255, r, g, b));

    private sealed class NumberLabelConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is not double number) return "";
            string unit = parameter as string ?? "";
            if (unit == "%") return number.ToString("0", CultureInfo.CurrentCulture) + "%";
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
