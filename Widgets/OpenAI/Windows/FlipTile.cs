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
    private const double CharacterAdvance = .61;
    private static readonly CreaseShadow DefaultShadow = new(5,
        ColorHelper.FromArgb(112, 52, 10, 13), ColorHelper.FromArgb(255, 24, 6, 7));
    private static readonly DigitDelay DefaultDelay = new(300, 1000);
    private static readonly FontFamily NumberFont = new("Consolas");
    private static readonly SolidColorBrush Ink = new(Colors.FloralWhite);
    private static readonly SolidColorBrush Seam = new(Colors.Black);
    private static readonly SolidColorBrush Divider = new(ColorHelper.FromArgb(230, 5, 4, 4));
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
    private readonly Border face;
    private readonly TextBlock caption;
    private readonly Canvas plate = new() { IsHitTestVisible = false };
    private readonly Border? accentBar;
    private readonly DigitDelay digitDelay;
    private readonly Brush? shadowBrush;
    private readonly Brush creaseBrush;
    private readonly double shadowSpread;
    private readonly List<Slot> slots = [];
    private Brush ink = Ink;
    private Layout layout;
    private string current = "—";

    private readonly record struct Layout(double Width, double Font, double Pitch, int Columns, int Rows)
    {
        public double Start => (Width - Columns * Pitch) / 2;
    }

    private sealed class Slot(Grid face, TextBlock glyph, Rectangle divider)
    {
        public Grid Face { get; } = face;
        public TextBlock Glyph { get; } = glyph;
        public Rectangle Divider { get; } = divider;
        public char Value { get; set; } = ' ';
        public char Before { get; set; }
        public long Midpoint { get; set; }
        public Storyboard? Motion { get; set; }
        public Grid? Flaps { get; set; }
        public char Visible(long now) => Motion is not null && now < Midpoint ? Before : Value;
    }

    public FlipTile(string field, double width, double fontSize, Brush captionBrush, string? label = null,
        bool showAccent = false, bool wrapValue = false, bool worn = false, int sizingCharacters = 0,
        CreaseShadow? creaseShadow = null, DigitDelay? digitDelay = null, bool compactAccent = false)
    {
        this.field = field;
        this.fontSize = fontSize;
        preferredWidth = width;
        this.wrapValue = wrapValue;
        this.sizingCharacters = sizingCharacters;
        this.digitDelay = digitDelay ?? DefaultDelay;
        if (this.digitDelay.MinimumMilliseconds < 0 || this.digitDelay.MaximumMilliseconds < this.digitDelay.MinimumMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(digitDelay));
        var shadow = creaseShadow ?? DefaultShadow;
        shadowSpread = shadow.Spread;
        creaseBrush = new SolidColorBrush(shadow.Line);
        if (shadow.Spread > 0)
        {
            var clear = ColorHelper.FromArgb(0, shadow.Color.R, shadow.Color.G, shadow.Color.B);
            shadowBrush = new LinearGradientBrush
            {
                StartPoint = new(0, 0), EndPoint = new(0, 1),
                GradientStops =
                {
                    new() { Color = clear, Offset = 0 },
                    new() { Color = shadow.Color, Offset = .5 },
                    new() { Color = clear, Offset = 1 }
                }
            };
        }
        Width = width;
        VerticalAlignment = VerticalAlignment.Center;
        RowDefinitions.Add(new());
        RowDefinitions.Add(new());
        caption = CreateCaption(label ?? field, captionBrush);
        var surface = new Grid { Background = FaceGradient };
        surface.Children.Add(plate);
        if (worn)
        {
            surface.Children.Add(GrainLayer(41, Colors.Black, .28));
            surface.Children.Add(GrainLayer(67, Colors.White, .13));
        }
        if (showAccent)
        {
            double thickness = compactAccent ? 2 : 3;
            accentBar = new Border { Width = 0, Height = thickness, Margin = new(7, 0, 0, compactAccent ? 2.5 : 3), CornerRadius = new(thickness / 2),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom };
            surface.Children.Add(accentBar);
        }
        face = new Border { Child = surface, Height = FaceHeight, CornerRadius = new(6), BorderThickness = new(1), BorderBrush = Seam };
        Children.Add(caption);
        Children.Add(face);
        SetLabelPosition("above");
        SetAccessibleValue();
        UpdateSlots(current, false, null, 200);
        Unloaded += (_, _) => StopAnimation();
    }

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
        Width = width;
        if (accentBar is not null)
            accentBar.Width = Math.Max(0, Width - 16) * Math.Clamp(progress.GetValueOrDefault() / 100, 0, 1);
        UpdateSlots(current, false, null, 200);
    }

    public void SetInk(Brush? color)
    {
        var next = color ?? Ink;
        if (ink == next) return;
        ink = next;
        foreach (var slot in slots) slot.Glyph.Foreground = ink;
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
        animate = animate && IsLoaded && current is not ("—" or "--");
        UpdateSlots(value, animate, flaps, milliseconds);
        current = value;
        SetAccessibleValue();
    }

    public void StopAnimation()
    {
        foreach (var slot in slots) StopMotion(slot);
    }

    private void SetAccessibleValue() => AutomationProperties.SetName(this, $"{field}: {current}");

    private void UpdateSlots(string value, bool animate, IReadOnlyList<Flap>? flaps, int milliseconds)
    {
        double width = Width - 2;
        double space = Math.Max(1, width - 12);
        int minimumColumns = wrapValue ? 1 : Math.Max(1, Math.Max(value.Length, sizingCharacters));
        double size = Math.Min(fontSize, space / (CharacterAdvance * minimumColumns));
        double pitch = size * CharacterAdvance;
        int columns = Math.Max(minimumColumns, (int)Math.Floor(space / pitch + .00001));
        string[] lines = wrapValue ? Wrap(value, columns).ToArray() : [value];
        var next = new Layout(width, size, pitch, columns, lines.Length);
        long now = Environment.TickCount64;
        bool rebuild = layout != next;
        char[] previousSlots = rebuild && animate ? slots.Select(slot => slot.Visible(now)).ToArray() : [];
        int previousColumns = layout.Columns;
        if (rebuild) BuildSlots(next);

        for (int row = 0; row < lines.Length; row++)
        {
            string line = lines[row];
            int start = (columns - line.Length) / 2;
            for (int column = 0; column < columns; column++)
            {
                var slot = slots[row * columns + column];
                int index = column - start;
                char target = index >= 0 && index < line.Length ? line[index] : ' ';
                slot.Divider.Visibility = index >= 0 && index + 1 < line.Length ? Visibility.Visible : Visibility.Collapsed;
                if (!rebuild && slot.Value == target) continue;

                int previousColumn = column + (previousColumns - columns) / 2;
                int previousIndex = row * previousColumns + previousColumn;
                char before = rebuild
                    ? previousColumn >= 0 && previousColumn < previousColumns && previousIndex < previousSlots.Length
                        ? previousSlots[previousIndex] : ' '
                    : slot.Visible(now);
                StopMotion(slot);
                slot.Value = target;
                slot.Glyph.Text = target.ToString();
                if (!animate || before == target) continue;

                int duration = milliseconds;
                if (index >= 0 && flaps is not null)
                    foreach (var flap in flaps)
                        if (index >= flap.Start && index < flap.Start + flap.Length)
                        {
                            duration = flap.Milliseconds;
                            break;
                        }
                StartMotion(slot, before, duration, now);
            }
        }
    }

    private static IEnumerable<string> Wrap(string value, int columns)
    {
        // Prefer spaces, with a hard break for words wider than a very narrow tile.
        while (value.Length > columns)
        {
            int space = value.LastIndexOf(' ', columns);
            int end = space > 0 ? space : columns;
            yield return value[..end];
            value = value[end..].TrimStart(' ');
        }
        yield return value;
    }

    private void BuildSlots(Layout next)
    {
        StopAnimation();
        slots.Clear();
        plate.Children.Clear();
        layout = next;
        face.Height = FaceHeight * layout.Rows;
        SetLabelPosition(captionPosition);
        for (int row = 0; row < layout.Rows; row++)
        {
            double y = row * FaceHeight;
            for (int column = 0; column < layout.Columns; column++)
            {
                var glyph = TileText(' ');
                var divider = new Rectangle
                {
                    Width = 1, Height = FaceHeight - 8, Fill = Divider,
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center
                };
                var cell = new Grid
                {
                    Width = layout.Pitch, Height = FaceHeight,
                    Clip = new RectangleGeometry { Rect = new(0, 0, layout.Pitch, FaceHeight) },
                    Children = { glyph, divider }
                };
                Place(cell, layout.Start + column * layout.Pitch, y);
                slots.Add(new(cell, glyph, divider));
            }
            if (shadowBrush is not null)
                Place(new Rectangle { Width = layout.Width, Height = shadowSpread * 2, Fill = shadowBrush }, 0, y + FaceHeight / 2 - shadowSpread);
            Place(new Rectangle { Width = layout.Width, Height = 1.5, Fill = creaseBrush }, 0, y + FaceHeight / 2 - .75);
            foreach (double x in new[] { 0d, layout.Width - 3 })
                Place(new Rectangle { Width = 3, Height = 9, Fill = Hinge }, x, y + FaceHeight / 2 - 4.5);
        }

        void Place(FrameworkElement element, double x, double y)
        {
            Canvas.SetLeft(element, x);
            Canvas.SetTop(element, y);
            plate.Children.Add(element);
        }
    }

    private static void StopMotion(Slot slot)
    {
        if (slot.Motion is not { } storyboard) return;
        slot.Motion = null;
        storyboard.Stop();
        slot.Face.Children.Remove(slot.Flaps!);
        slot.Flaps = null;
    }

    private void StartMotion(Slot slot, char before, int milliseconds, long now)
    {
        int delay = (int)Random.Shared.NextInt64(digitDelay.MinimumMilliseconds, (long)digitDelay.MaximumMilliseconds + 1);
        var upperProjection = new PlaneProjection { CenterOfRotationY = 1 };
        var lowerProjection = new PlaneProjection { CenterOfRotationY = 0, RotationX = 90 };
        var oldTop = Half(before, top: true);
        oldTop.Projection = upperProjection;
        var newBottom = Half(slot.Value, top: false);
        newBottom.Projection = lowerProjection;
        var flaps = new Grid { Children = { Half(before, top: false), oldTop, newBottom } };
        slot.Face.Children.Insert(1, flaps);

        var storyboard = new Storyboard();
        var wait = TimeSpan.FromMilliseconds(delay);
        var halfTime = TimeSpan.FromMilliseconds(milliseconds / 2d);
        Fold(upperProjection, 0, -90, wait);
        Fold(lowerProjection, 90, 0, wait + halfTime);
        slot.Motion = storyboard;
        slot.Flaps = flaps;
        slot.Before = before;
        slot.Midpoint = now + delay + milliseconds / 2;
        storyboard.Completed += (_, _) =>
        {
            if (slot.Motion == storyboard) StopMotion(slot);
        };
        storyboard.Begin();

        void Fold(PlaneProjection projection, double from, double to, TimeSpan begins)
        {
            var animation = new DoubleAnimation
            {
                From = from, To = to, BeginTime = begins,
                Duration = new(halfTime), EnableDependentAnimation = true
            };
            Storyboard.SetTarget(animation, projection);
            Storyboard.SetTargetProperty(animation, nameof(PlaneProjection.RotationX));
            storyboard.Children.Add(animation);
        }
    }

    private Grid Half(char value, bool top)
    {
        var content = new Grid
        {
            Width = layout.Pitch, Height = FaceHeight, VerticalAlignment = VerticalAlignment.Top,
            Background = top ? UpperFace : LowerFace,
            RenderTransform = new TranslateTransform { Y = top ? 0 : -FaceHeight / 2 },
            Children = { TileText(value) }
        };
        return new Grid
        {
            Height = FaceHeight / 2, VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom,
            Clip = new RectangleGeometry { Rect = new(0, 0, layout.Pitch, FaceHeight / 2) },
            Children = { content }
        };
    }

    private TextBlock TileText(char value) => new()
    {
        Text = value.ToString(),
        FontSize = layout.Font,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = ink,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        FontFamily = NumberFont
    };
}
