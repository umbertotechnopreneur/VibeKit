using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;

namespace OpenAIUsage.Windows;

public sealed partial class MainWindow
{
    private const double SignalWidth = 44;
    private const double SignalHeight = 3 * SignalWidth;
    // Lens positions in the transparent graphite artwork, in green-to-red order.
    private static readonly double[] LensCenters = [.769, .506, .242];
    private static readonly uint[] LensColors = [0x44E85E, 0xFFC336, 0xFF4330];
    private readonly Grid signalHousing = new()
    {
        Width = SignalWidth, Height = 128, Margin = new(0, 0, TileSpacing, 0),
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly Canvas signalIllumination = new() { IsHitTestVisible = false };
    private readonly Grid[] lenses = new Grid[3];
    private string? displayedSignal;
    private double? displayedAllowance;
    private bool signalInitialized;
    private Storyboard? signalTransition;
    private Storyboard? signalFlicker;
    private long nextFlicker = Environment.TickCount64 + 300_000;

    private static Image SignalArtwork() => new()
    {
        Stretch = Stretch.Uniform,
        IsHitTestVisible = false,
        Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Branding", "signal-graphite.png")))
        {
            DecodePixelHeight = 384
        }
    };

    private static Border SignalGlow(uint tint, double diameter, (double Offset, byte Alpha)[] stops)
    {
        var color = Rgb(tint);
        var brush = new RadialGradientBrush();
        foreach (var (offset, alpha) in stops)
            brush.GradientStops.Add(new() { Color = ColorHelper.FromArgb(alpha, color.R, color.G, color.B), Offset = offset });
        return new Border
        {
            Width = diameter, Height = diameter, IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = brush
        };
    }

    private void BuildSignal()
    {
        double auraDiameter = SignalWidth * 1.28;
        for (int i = 0; i < lenses.Length; i++)
        {
            var light = new Grid { Width = auraDiameter, Height = auraDiameter, Opacity = 0 };
            light.Children.Add(SignalGlow(LensColors[i], auraDiameter, [(0, 102), (.32, 74), (.58, 31), (1, 0)]));
            light.Children.Add(SignalGlow(LensColors[i], SignalWidth * .74, [(0, 225), (.55, 184), (.84, 102), (1, 0)]));
            Canvas.SetLeft(light, (SignalWidth - auraDiameter) / 2);
            Canvas.SetTop(light, SignalHeight * LensCenters[i] - auraDiameter / 2);
            signalIllumination.Children.Add(light);
            lenses[i] = light;
        }
        var scene = new Grid { Width = SignalWidth, Height = SignalHeight, Children = { SignalArtwork(), signalIllumination } };
        signalHousing.Children.Add(new Viewbox { Child = scene, Stretch = Stretch.Uniform });
        UpdateSignal(null, false, null);
    }

    private void UpdateSignal(int? allowanceThreshold, bool creditsLow, double? allowance)
    {
        string description = allowance.HasValue
            ? FormattableString.Invariant($"Allowance remaining: {allowance:0.#}%.")
            : "Waiting for allowance data.";
        if (allowanceThreshold.HasValue) description += $" Allowance threshold reached: {allowanceThreshold}%.";
        if (creditsLow) description += $" Low credits: {state.CreditsLabel} remaining.";
        if (displayedSignal != description)
        {
            displayedSignal = description;
            ToolTipService.SetToolTip(signalHousing, description);
            AutomationProperties.SetName(signalHousing, description);
        }
        if (signalInitialized && displayedAllowance == allowance) return;

        signalInitialized = true;
        displayedAllowance = allowance;
        signalHousing.Opacity = allowance.HasValue ? 1 : .35;
        double remaining = Math.Clamp(allowance.GetValueOrDefault(100), 0, 100);
        double amber = Math.Clamp((100 - remaining) / 50, 0, 1);
        double red = Math.Clamp((50 - remaining) / 50, 0, 1);
        double[] glowWeights = [(1 - amber) * (1 - red), amber * (1 - red), red];
        StopSignalTransition();
        var transition = new Storyboard();
        for (int i = 0; i < lenses.Length; i++)
            Blend(lenses[i], allowance.HasValue ? Math.Sqrt(glowWeights[i]) : 0);
        if (transition.Children.Count == 0) return;
        signalTransition = transition;
        transition.Completed += (_, _) => { if (signalTransition == transition) signalTransition = null; };
        transition.Begin();

        void Blend(UIElement item, double opacity)
        {
            double previous = item.Opacity;
            item.Opacity = opacity;
            if (!CanAnimateWidget || previous == opacity) return;
            var animation = new DoubleAnimation
            {
                From = previous, To = opacity, Duration = new(TimeSpan.FromMilliseconds(260)),
                FillBehavior = FillBehavior.Stop
            };
            Storyboard.SetTarget(animation, item);
            Storyboard.SetTargetProperty(animation, nameof(UIElement.Opacity));
            transition.Children.Add(animation);
        }
    }

    private void MaybeFlicker()
    {
        long now = Environment.TickCount64;
        if (now < nextFlicker) return;
        nextFlicker = now + 300_000;
        if (!CanAnimateWidget || !displayedAllowance.HasValue) return;
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        foreach (var (ms, opacity) in new[] { (0, 1d), (45, .93), (115, 1d), (185, .96), (280, 1d) })
            animation.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), Value = opacity });
        Storyboard.SetTarget(animation, signalIllumination);
        Storyboard.SetTargetProperty(animation, nameof(UIElement.Opacity));
        signalFlicker?.Stop();
        signalFlicker = new();
        signalFlicker.Children.Add(animation);
        signalFlicker.Begin();
    }

    private void StopSignalTransition()
    {
        signalTransition?.Stop();
        signalTransition = null;
    }
}
