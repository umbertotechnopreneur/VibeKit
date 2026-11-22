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
    private readonly Grid signalHousing = new()
    {
        Width = 44, Height = 128, Margin = new(12, 8, 10, 0),
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly Grid signalPictures = new();
    private readonly Image[] signalLayers = [SignalLayer("green"), SignalLayer("amber"), SignalLayer("red")];
    private readonly Border[] glows = [SignalGlow(0x44E85E), SignalGlow(0xFFC336), SignalGlow(0xFF4330)];
    private string? displayedSignal;
    private double? displayedAllowance;
    private Storyboard? signalTransition;
    private Storyboard? signalFlicker;
    private long nextFlicker = Environment.TickCount64 + 300_000;

    private static Image SignalLayer(string color) => new()
    {
        Opacity = 0,
        Stretch = Stretch.Uniform,
        Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Branding", $"signal-{color}.png")))
        {
            DecodePixelHeight = 384
        }
    };

    private static Border SignalGlow(uint tint)
    {
        var color = Rgb(tint);
        return new Border
        {
            Width = 30, Height = 29, Opacity = 0, IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new(.5, .5),
            RenderTransform = new ScaleTransform { ScaleX = 1.95, ScaleY = 1.8 },
            Background = new RadialGradientBrush
            {
                GradientStops =
                {
                    new() { Color = ColorHelper.FromArgb(170, color.R, color.G, color.B), Offset = 0 },
                    new() { Color = ColorHelper.FromArgb(125, color.R, color.G, color.B), Offset = .33 },
                    new() { Color = ColorHelper.FromArgb(74, color.R, color.G, color.B), Offset = .55 },
                    new() { Color = ColorHelper.FromArgb(0, color.R, color.G, color.B), Offset = 1 }
                }
            }
        };
    }

    private void BuildSignal()
    {
        var glowLayout = new Grid { IsHitTestVisible = false };
        for (int i = 0; i < glows.Length; i++)
        {
            glowLayout.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
            Grid.SetRow(glows[i], 2 - i);
            glowLayout.Children.Add(glows[i]);
        }
        foreach (var layer in signalLayers) signalPictures.Children.Add(layer);
        signalHousing.Children.Add(signalPictures);
        signalHousing.Children.Add(glowLayout);
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
        if (displayedAllowance == allowance && signalLayers[0].Opacity != 0) return;

        displayedAllowance = allowance;
        signalHousing.Opacity = allowance.HasValue ? 1 : .35;
        double remaining = Math.Clamp(allowance.GetValueOrDefault(100), 0, 100);
        // Source-over blend of the two adjacent states. The housing stays opaque.
        double[] next = [1, Math.Clamp((100 - remaining) / 50, 0, 1), Math.Clamp((50 - remaining) / 50, 0, 1)];
        double[] glowWeights = [(1 - next[1]) * (1 - next[2]), next[1] * (1 - next[2]), next[2]];
        StopSignalTransition();
        var transition = new Storyboard();
        for (int i = 0; i < signalLayers.Length; i++)
        {
            Blend(signalLayers[i], next[i]);
            Blend(glows[i], allowance.HasValue ? Math.Sqrt(glowWeights[i]) : 0);
        }
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
        Storyboard.SetTarget(animation, signalPictures);
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
