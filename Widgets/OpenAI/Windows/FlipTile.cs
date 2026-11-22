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
    public readonly record struct CreaseShadow(double Spread, global::Windows.UI.Color Color, global::Windows.UI.Color Line);
    public readonly record struct DigitDelay(int MinimumMilliseconds, int MaximumMilliseconds);
    public readonly record struct Flap(int Start, int Length, int Milliseconds);

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
    private readonly double preferredWidth;
    private readonly bool wrapValue;
    private readonly int sizingCharacters;
    private readonly TextBlock number;
    private readonly Border face;
    private readonly TextBlock caption;
    private readonly Grid overlays = new() { IsHitTestVisible = false };
    private readonly Border? accentBar;
    private readonly DigitDelay digitDelay;
    private readonly Dictionary<int, DigitFlip> digitFlips = [];
    private Brush ink = Ink;
    private Storyboard? running;
    private string current = "—";

    private sealed record DigitFlip(Grid Panel, Storyboard Animation, char Before, long Midpoint);

    public FlipTile(string field, double width, double fontSize, Brush captionBrush, string? label = null,
        bool showAccent = false, bool wrapValue = false, bool worn = false, int sizingCharacters = 0,
        CreaseShadow? creaseShadow = null, DigitDelay digitDelay = default)
    {
        this.field = field;
        this.fontSize = fontSize;
        preferredWidth = width;
        this.wrapValue = wrapValue;
        this.sizingCharacters = sizingCharacters;
        if (digitDelay.MinimumMilliseconds < 0 || digitDelay.MaximumMilliseconds < digitDelay.MinimumMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(digitDelay));
        this.digitDelay = digitDelay;
        Width = width;
        VerticalAlignment = VerticalAlignment.Center;
        RowDefinitions.Add(new());
        RowDefinitions.Add(new());
        caption = CreateCaption(label ?? field, captionBrush);
        number = TileText(current);
        var surface = new Grid { Background = FaceGradient };
        surface.Children.Add(number);
        if (worn)
        {
            surface.Children.Add(GrainLayer(41, Colors.Black, .28));
            surface.Children.Add(GrainLayer(67, Colors.White, .13));
        }
        surface.Children.Add(overlays);
        if (creaseShadow is { Spread: > 0 } shadow)
        {
            var clear = ColorHelper.FromArgb(0, shadow.Color.R, shadow.Color.G, shadow.Color.B);
            surface.Children.Add(new Rectangle
            {
                Height = 2 * shadow.Spread, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
                Fill = new LinearGradientBrush
                {
                    StartPoint = new(0, 0), EndPoint = new(0, 1),
                    GradientStops =
                    {
                        new() { Color = clear, Offset = 0 },
                        new() { Color = shadow.Color, Offset = .5 },
                        new() { Color = clear, Offset = 1 }
                    }
                }
            });
        }
        surface.Children.Add(new Rectangle
        {
            Height = 1.5, Fill = creaseShadow is { } crease ? new SolidColorBrush(crease.Line) : Seam,
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
        });
        foreach (var side in new[] { HorizontalAlignment.Left, HorizontalAlignment.Right })
            surface.Children.Add(new Border { Width = 3, Height = 9, Background = Hinge, HorizontalAlignment = side, VerticalAlignment = VerticalAlignment.Center });
        if (showAccent)
        {
            accentBar = new Border { Width = 0, Height = 3, Margin = new(7, 0, 0, 3), CornerRadius = new(1.5),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom };
            surface.Children.Add(accentBar);
        }
        face = new Border { Child = surface, Height = FaceHeight, CornerRadius = new(6), BorderThickness = new(1), BorderBrush = Seam };
        Children.Add(caption);
        Children.Add(face);
        SetLabelPosition("above");
        SetAccessibleValue();
    }

    public bool IsTextTrimmed => number.IsTextTrimmed;

    private static GeometryGroup Grain(int seed)
    {
        var random = new Random(seed);
        var geometry = new GeometryGroup();
        for (int i = 0; i < 135; i++)
            geometry.Children.Add(new RectangleGeometry
            {
                Rect = new(random.NextDouble() * 150, random.NextDouble() * 44,
                    .35 + random.NextDouble() * 1.4, .35 + random.NextDouble() * .8)
            });
        return geometry;
    }

    private static Microsoft.UI.Xaml.Shapes.Path GrainLayer(int seed, global::Windows.UI.Color color, double opacity) => new()
    {
        Data = Grain(seed), Stretch = Stretch.Fill, Fill = new SolidColorBrush(color),
        Margin = new(4, 2, 4, 2), Opacity = opacity, IsHitTestVisible = false
    };

    private double? progress;

    public void SetProgress(double? percent, Brush? color)
    {
        if (accentBar is null) return;
        progress = percent;
        accentBar.Background = color;
        accentBar.Width = Math.Max(0, Width - 16) * Math.Clamp(percent.GetValueOrDefault() / 100, 0, 1);
    }

    public void FitWidth(double available)
    {
        double width = Math.Clamp(available, 32, preferredWidth);
        if (Width == width) return;
        StopAnimation();
        Width = width;
        if (accentBar is not null)
            accentBar.Width = Math.Max(0, Width - 16) * Math.Clamp(progress.GetValueOrDefault() / 100, 0, 1);
        number.FontSize = FittedSize(current);
        FitValueHeight();
        SetLabelPosition(captionPosition);
    }

    public void SetInk(Brush? color)
    {
        ink = color ?? Ink;
        number.Foreground = ink;
    }

    private string captionPosition = "above";

    public void SetLabelPosition(string position)
    {
        captionPosition = position;
        PlaceCaption(this, caption, face, position, face.Height);
    }

    public static TextBlock CreateCaption(string label, Brush foreground) => new()
    {
        Text = label,
        FontSize = 14,
        Foreground = foreground,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.NoWrap,
        TextTrimming = TextTrimming.CharacterEllipsis
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

    public void Set(string value, bool animate) => Set(value, animate, null, 200);

    public void Set(string value, bool animate, IReadOnlyList<Flap>? flaps, int milliseconds)
    {
        if (value == current)
        {
            if (!animate) StopAnimation();
            return;
        }
        string previous = current;
        long now = Environment.TickCount64;
        string visible = VisibleValue(now);
        current = value;
        number.Text = current;
        number.FontSize = FittedSize(current);
        FitValueHeight();
        SetAccessibleValue();
        if (!animate || previous is "—" or "--" || face.ActualWidth <= 2)
        {
            StopAnimation();
            return;
        }
        if (flaps is not null && !wrapValue && previous.Length == value.Length && number.ActualWidth > 0)
            FlipDigits(previous, visible, flaps, now);
        else
            FlipWhole(visible, milliseconds);
    }

    public void StopAnimation()
    {
        running?.Stop();
        running = null;
        foreach (var flip in digitFlips.Values) flip.Animation.Stop();
        digitFlips.Clear();
        overlays.Children.Clear();
    }

    private void SetAccessibleValue() => AutomationProperties.SetName(this, $"{field}: {current}");

    private string VisibleValue(long now)
    {
        if (digitFlips.Count == 0) return current;
        var visible = current.ToCharArray();
        foreach (var (index, flip) in digitFlips)
            if (now < flip.Midpoint)
                visible[index] = flip.Before;
        return new string(visible);
    }

    private void FlipDigits(string previous, string visible, IReadOnlyList<Flap> flaps, long now)
    {
        if (running is not null) StopAnimation();
        double width = face.ActualWidth - 2;
        double characterWidth = number.ActualWidth / current.Length;
        double lineLeft = (width - number.ActualWidth) / 2;
        foreach (var flap in flaps)
        {
            int end = flap.Start + flap.Length;
            for (int index = flap.Start; index < end; index++)
            {
                if (previous[index] == current[index]) continue;
                if (digitFlips.Remove(index, out var oldFlip))
                {
                    oldFlip.Animation.Stop();
                    overlays.Children.Remove(oldFlip.Panel);
                }
                if (visible[index] == current[index]) continue;
                double left = Math.Clamp(lineLeft + index * characterWidth, 0, width);
                double right = Math.Clamp(lineLeft + (index + 1) * characterWidth, left, width);
                if (right == left) continue;
                int delay = (int)Random.Shared.NextInt64(digitDelay.MinimumMilliseconds, (long)digitDelay.MaximumMilliseconds + 1);
                var storyboard = new Storyboard();
                var panel = AddFlap(storyboard, visible, left, right - left, flap.Milliseconds, delay);
                int position = index;
                digitFlips.Add(position, new(panel, storyboard, visible[position], now + delay + flap.Milliseconds / 2));
                storyboard.Completed += (_, _) =>
                {
                    if (!digitFlips.TryGetValue(position, out var completed) || completed.Animation != storyboard) return;
                    digitFlips.Remove(position);
                    overlays.Children.Remove(panel);
                };
                storyboard.Begin();
            }
        }
    }

    private void FlipWhole(string previous, int milliseconds)
    {
        StopAnimation();
        if (previous == current) return;
        var storyboard = new Storyboard();
        var panel = AddFlap(storyboard, previous, 0, face.ActualWidth - 2, milliseconds);
        storyboard.Completed += (_, _) =>
        {
            if (running != storyboard) return;
            running = null;
            overlays.Children.Remove(panel);
        };
        running = storyboard;
        storyboard.Begin();
    }

    private Grid AddFlap(Storyboard storyboard, string previous, double left, double width, int milliseconds, int delay = 0)
    {
        // The old top folds into the center hinge, then the new bottom unfolds.
        var segment = new Grid { Clip = new RectangleGeometry { Rect = new(left, 0, width, face.Height - 2) } };
        var oldBottom = Half(previous, top: false);
        var oldTop = Half(previous, top: true);
        var newBottom = Half(current, top: false);
        var upperProjection = new PlaneProjection { CenterOfRotationY = 1 };
        var lowerProjection = new PlaneProjection { CenterOfRotationY = 0, RotationX = 90 };
        oldTop.Projection = upperProjection;
        newBottom.Projection = lowerProjection;
        segment.Children.Add(oldBottom);
        segment.Children.Add(oldTop);
        segment.Children.Add(newBottom);
        overlays.Children.Add(segment);
        var halfTime = TimeSpan.FromMilliseconds(milliseconds / 2d);
        var wait = TimeSpan.FromMilliseconds(delay);
        var upper = new DoubleAnimation { From = 0, To = -90, BeginTime = wait, Duration = new(halfTime), EnableDependentAnimation = true };
        var lower = new DoubleAnimation { From = 90, To = 0, BeginTime = wait + halfTime, Duration = new(halfTime), EnableDependentAnimation = true };
        Storyboard.SetTarget(upper, upperProjection);
        Storyboard.SetTargetProperty(upper, nameof(PlaneProjection.RotationX));
        Storyboard.SetTarget(lower, lowerProjection);
        Storyboard.SetTargetProperty(lower, nameof(PlaneProjection.RotationX));
        storyboard.Children.Add(upper);
        storyboard.Children.Add(lower);
        var hide = new DoubleAnimationUsingKeyFrames
        {
            KeyFrames = { new DiscreteDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(wait + halfTime * 2), Value = 0 } }
        };
        Storyboard.SetTarget(hide, segment);
        Storyboard.SetTargetProperty(hide, nameof(Opacity));
        storyboard.Children.Add(hide);
        return segment;
    }

    private Grid Half(string value, bool top)
    {
        double width = face.ActualWidth - 2;
        double halfHeight = (face.Height - 2) / 2;
        var half = new Grid
        {
            Width = width, Height = halfHeight, VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom,
            Clip = new RectangleGeometry { Rect = new(0, 0, width, halfHeight) }
        };
        var full = new Grid
        {
            Width = width, Height = face.Height - 2, VerticalAlignment = VerticalAlignment.Top,
            Background = top ? UpperFace : LowerFace,
            RenderTransform = new TranslateTransform { Y = top ? 0 : -halfHeight }
        };
        full.Children.Add(TileText(value));
        half.Children.Add(full);
        return half;
    }

    private void FitValueHeight()
    {
        if (!wrapValue) return;
        int capacity = Math.Max(1, (int)((Width - 16) / (.61 * fontSize)));
        int lines = 1, used = 0;
        foreach (string word in current.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (used > 0 && used + word.Length + 1 > capacity)
            {
                lines++;
                used = 0;
            }
            used += word.Length + (used > 0 ? 1 : 0);
        }
        double height = Math.Max(FaceHeight, 10 + lines * (fontSize + 4));
        if (face.Height == height) return;
        face.Height = height;
        SetLabelPosition(captionPosition);
    }

    private double FittedSize(string value) => wrapValue
        ? fontSize
        : Math.Min(fontSize, (Width - 14) / (.61 * Math.Max(Math.Max(1, value.Length), sizingCharacters)));

    private TextBlock TileText(string value) => new()
    {
        Text = value,
        FontSize = FittedSize(value),
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = ink,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        FontFamily = NumberFont,
        TextAlignment = TextAlignment.Center,
        TextWrapping = wrapValue ? TextWrapping.Wrap : TextWrapping.NoWrap,
        MaxLines = wrapValue ? 0 : 1,
        Margin = wrapValue ? new Thickness(6, 0, 6, 0) : new Thickness(0),
        TextTrimming = TextTrimming.CharacterEllipsis
    };
}
