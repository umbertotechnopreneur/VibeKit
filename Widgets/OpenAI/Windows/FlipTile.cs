using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;

namespace OpenAIUsage.Windows;

public sealed class FlipTile : Grid
{
    private const double FaceHeight = 44;
    private static readonly FontFamily NumberFont = new("Consolas");
    private static readonly SolidColorBrush Ink = new(Colors.FloralWhite);
    private static readonly SolidColorBrush Seam = new(Colors.Black);
    private static readonly SolidColorBrush Hinge = new(ColorHelper.FromArgb(255, 114, 116, 119));
    private static readonly LinearGradientBrush FaceGradient = new()
    {
        StartPoint = new(0, 0), EndPoint = new(0, 1),
        GradientStops =
        {
            new() { Color = ColorHelper.FromArgb(255, 35, 35, 35), Offset = 0 },
            new() { Color = ColorHelper.FromArgb(255, 10, 10, 10), Offset = 1 }
        }
    };
    private static readonly SolidColorBrush UpperFace = new(ColorHelper.FromArgb(255, 30, 30, 30));
    private static readonly SolidColorBrush LowerFace = new(ColorHelper.FromArgb(255, 15, 15, 15));
    private readonly string field;
    private readonly double fontSize;
    private readonly TextBlock number;
    private readonly Border face;
    private readonly TextBlock caption;
    private readonly Grid overlays = new() { IsHitTestVisible = false };
    private readonly Border? accentBar;
    private Brush ink = Ink;
    private Storyboard? running;
    private string current = "—";

    public FlipTile(string field, double width, double fontSize, Brush captionBrush, string? label = null, bool showAccent = false)
    {
        this.field = field;
        this.fontSize = fontSize;
        Width = width;
        VerticalAlignment = VerticalAlignment.Center;
        RowDefinitions.Add(new());
        RowDefinitions.Add(new());
        caption = CreateCaption(label ?? field, captionBrush);
        number = TileText(current);
        var surface = new Grid { Background = FaceGradient };
        surface.Children.Add(number);
        surface.Children.Add(overlays);
        surface.Children.Add(new Rectangle { Height = 1.5, Fill = Seam, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false });
        foreach (var side in new[] { HorizontalAlignment.Left, HorizontalAlignment.Right })
            surface.Children.Add(new Border { Width = 3, Height = 9, Background = Hinge, HorizontalAlignment = side, VerticalAlignment = VerticalAlignment.Center });
        if (showAccent)
        {
            accentBar = new Border { Width = width * .62, Height = 2, Margin = new(0, 0, 0, 2), CornerRadius = new(1),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom };
            surface.Children.Add(accentBar);
        }
        face = new Border { Child = surface, Height = FaceHeight, CornerRadius = new(6), BorderThickness = new(1), BorderBrush = Seam };
        Children.Add(caption);
        Children.Add(face);
        SetLabelPosition("above");
        SetAccessibleValue();
    }

    public bool IsTextTrimmed => number.IsTextTrimmed;

    public void SetAccent(Brush? color)
    {
        if (accentBar is not null) accentBar.Background = color;
    }

    public void SetInk(Brush? color)
    {
        ink = color ?? Ink;
        number.Foreground = ink;
    }

    public void SetLabelPosition(string position) => PlaceCaption(this, caption, face, position);

    public static TextBlock CreateCaption(string label, Brush foreground) => new()
    {
        Text = label,
        FontSize = 14,
        Foreground = foreground,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.NoWrap
    };

    public static void PlaceCaption(Grid parent, TextBlock caption, FrameworkElement value, string position, double valueHeight = FaceHeight)
    {
        bool above = position == "above";
        bool visible = position != "hidden";
        var captionHeight = new GridLength(visible ? 19 : 0);
        var faceHeight = new GridLength(valueHeight);
        parent.RowSpacing = visible ? 4 : 0;
        parent.RowDefinitions[0].Height = above ? captionHeight : faceHeight;
        parent.RowDefinitions[1].Height = above ? faceHeight : captionHeight;
        caption.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SetRow(caption, above ? 0 : 1);
        SetRow(value, above ? 1 : 0);
    }

    public void Set(string value, bool animate)
    {
        if (value == current) return;
        string previous = current;
        current = value;
        number.Text = current;
        number.FontSize = FittedSize(current);
        SetAccessibleValue();
        if (animate && previous is not "—" and not "--")
            Flip(previous);
        else
            StopAnimation();
    }

    public void Reveal()
    {
        if (current is not "—" and not "--")
            Flip("");
    }

    public void StopAnimation()
    {
        running?.Stop();
        running = null;
        overlays.Children.Clear();
    }

    private void SetAccessibleValue() => AutomationProperties.SetName(this, $"{field}: {current}");

    private void Flip(string previous)
    {
        StopAnimation();
        if (face.ActualWidth <= 2) return;

        // The old top folds into the center hinge, then the new bottom unfolds.
        var oldBottom = Half(previous, top: false);
        var oldTop = Half(previous, top: true);
        var newBottom = Half(current, top: false);
        var upperProjection = new PlaneProjection { CenterOfRotationY = 1 };
        var lowerProjection = new PlaneProjection { CenterOfRotationY = 0, RotationX = 90 };
        oldTop.Projection = upperProjection;
        newBottom.Projection = lowerProjection;
        overlays.Children.Add(oldBottom);
        overlays.Children.Add(oldTop);
        overlays.Children.Add(newBottom);
        var upper = new DoubleAnimation { From = 0, To = -90, Duration = new(TimeSpan.FromMilliseconds(100)), EnableDependentAnimation = true };
        var lower = new DoubleAnimation { From = 90, To = 0, BeginTime = TimeSpan.FromMilliseconds(100), Duration = new(TimeSpan.FromMilliseconds(100)), EnableDependentAnimation = true };
        Storyboard.SetTarget(upper, upperProjection);
        Storyboard.SetTargetProperty(upper, nameof(PlaneProjection.RotationX));
        Storyboard.SetTarget(lower, lowerProjection);
        Storyboard.SetTargetProperty(lower, nameof(PlaneProjection.RotationX));
        var storyboard = new Storyboard();
        storyboard.Children.Add(upper);
        storyboard.Children.Add(lower);
        storyboard.Completed += (_, _) =>
        {
            if (running != storyboard) return;
            running = null;
            overlays.Children.Clear();
        };
        running = storyboard;
        storyboard.Begin();
    }

    private Grid Half(string value, bool top)
    {
        double width = face.ActualWidth - 2;
        const double halfHeight = (FaceHeight - 2) / 2;
        var half = new Grid
        {
            Width = width, Height = halfHeight, VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom,
            Clip = new RectangleGeometry { Rect = new(0, 0, width, halfHeight) }
        };
        var full = new Grid
        {
            Width = width, Height = FaceHeight - 2, VerticalAlignment = VerticalAlignment.Top,
            Background = top ? UpperFace : LowerFace,
            RenderTransform = new TranslateTransform { Y = top ? 0 : -halfHeight }
        };
        full.Children.Add(TileText(value));
        half.Children.Add(full);
        return half;
    }

    private double FittedSize(string value) => Math.Min(fontSize, (Width - 14) / (.61 * Math.Max(1, value.Length)));

    private TextBlock TileText(string value) => new()
    {
        Text = value,
        FontSize = FittedSize(value),
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = ink,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        FontFamily = NumberFont,
        TextTrimming = TextTrimming.CharacterEllipsis
    };
}
