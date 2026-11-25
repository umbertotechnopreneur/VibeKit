using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace OpenAIUsage.Windows;

internal sealed class CreditPercentageTile : Grid, IWidgetTile
{
    internal const string FieldKey = "credits-percentage";
    internal const string FieldLabel = "Remaining Credits Percentage";
    private const double ValueGap = 8, LensInsets = 8, ValueShare = .34;
    private const int RedLenses = 2, OrangeLenses = 5;
    private static readonly Brush LensRim = new SolidColorBrush(ColorHelper.FromArgb(255, 28, 26, 26));
    private static readonly Brush Crease = new SolidColorBrush(ColorHelper.FromArgb(217, 24, 6, 7));
    private static readonly Brush Off = Glass(0x65666B, 0x37383C, 0x17171A);
    private static readonly Brush Green = Glass(0xA4F9C4, 0x31C67D, 0x065239);
    private static readonly Brush Orange = Glass(0xFFD997, 0xFF922E, 0x963100);
    private static readonly Brush Red = Glass(0xFFB1AB, 0xFF4640, 0x8B1218);

    private readonly FlipTile value;
    private readonly TextBlock caption;
    private readonly Grid face = new() { ColumnSpacing = ValueGap };
    private readonly Ellipse[] lenses = new Ellipse[CreditProgress.LensCount];
    private int litLenses;

    public CreditPercentageTile(Brush captionBrush)
    {
        value = new(FieldLabel, 88, 20, captionBrush, sizingCharacters: 5);
        VerticalAlignment = VerticalAlignment.Center;
        RowDefinitions.Add(new());
        RowDefinitions.Add(new());
        caption = FlipTile.CreateCaption(FieldLabel, captionBrush);
        value.SetLabelPosition("hidden");
        AutomationProperties.SetAccessibilityView(value, AccessibilityView.Raw);
        var track = new Grid();
        for (int index = 0; index < lenses.Length; index++)
        {
            track.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            var lens = new Ellipse
            {
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Fill = Off, Stroke = LensRim, StrokeThickness = .8
            };
            lenses[index] = lens;
            var tile = new Grid
            {
                Margin = new(.5, 0, .5, 0), Background = FlipTile.FlapGradient,
                Children = { new Rectangle { Height = 1.2, Fill = Crease, VerticalAlignment = VerticalAlignment.Center }, lens }
            };
            SetColumn(tile, index);
            track.Children.Add(tile);
        }
        var housing = new Border
        {
            Child = track, CornerRadius = new(6), Background = FlipTile.FaceGradient,
            BorderBrush = FlipTile.FaceRim, BorderThickness = new(1), Padding = new(3, 2, 3, 2)
        };
        face.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        face.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        face.Children.Add(value);
        SetColumn(housing, 1);
        face.Children.Add(housing);
        Children.Add(caption);
        Children.Add(face);
        AutomationProperties.SetAutomationId(this, "CreditPercentage");
        Set(null, false);
        FitWidth(PreferredWidth, 20);
        SetLabelPosition("above");
    }

    public double MinimumTileWidth => 120;
    public double PreferredWidth => value.PreferredWidth + ValueGap + LensInsets + lenses.Length * 12;

    public double FontLimit(double available) => value.FontLimit(ValueWidth(available));
    public double WidthForFont(double fontSize) =>
        Math.Clamp(ValueGap + value.WidthForFont(fontSize) / ValueShare, MinimumTileWidth, PreferredWidth);

    public void FitWidth(double available, double fontSize)
    {
        double width = Math.Clamp(available, MinimumTileWidth, PreferredWidth);
        double valueWidth = ValueWidth(width);
        value.FitWidth(valueWidth, fontSize);
        if (Width == width) return;
        Width = width;
        double diameter = Math.Min(15, (width - valueWidth - ValueGap - LensInsets) / lenses.Length - 2);
        foreach (var lens in lenses)
            lens.Width = lens.Height = diameter;
    }

    public void SetLabelPosition(string position) => FlipTile.PlaceCaption(this, caption, face, position);

    public void Set(decimal? percentage, bool animate)
    {
        string text = CreditProgress.Format(percentage, CultureInfo.CurrentCulture);
        value.Set(text, animate);
        AutomationProperties.SetName(this, $"{FieldLabel}: {text}");
        int count = CreditProgress.LitLenses(percentage);
        if (count == litLenses) return;
        litLenses = count;

        for (int fromRight = 0; fromRight < lenses.Length; fromRight++)
        {
            lenses[^(fromRight + 1)].Fill = fromRight >= count ? Off : fromRight switch
            {
                < RedLenses => Red,
                < RedLenses + OrangeLenses => Orange,
                _ => Green
            };
        }
    }

    private double ValueWidth(double totalWidth) =>
        Math.Clamp((totalWidth - ValueGap) * ValueShare, 40, value.PreferredWidth);

    private static RadialGradientBrush Glass(uint highlight, uint face, uint edge) => new()
    {
        GradientOrigin = new(.40, .30), Center = new(.50, .50), RadiusX = .56, RadiusY = .56,
        GradientStops =
        {
            new() { Color = Rgb(highlight), Offset = 0 },
            new() { Color = Rgb(face), Offset = .66 },
            new() { Color = Rgb(edge), Offset = 1 }
        }
    };

    private static global::Windows.UI.Color Rgb(uint color) =>
        ColorHelper.FromArgb(255, (byte)(color >> 16), (byte)(color >> 8), (byte)color);
}
