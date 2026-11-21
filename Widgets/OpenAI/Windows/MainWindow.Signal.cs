using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace OpenAIUsage.Windows;

public sealed partial class MainWindow
{
    private const double SignalHeight = 62;
    private readonly Grid signal = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock signalCaption = FlipTile.CreateCaption("Alert", SecondaryText);
    private readonly Border signalHousing = new()
    {
        Width = 33, Height = SignalHeight, CornerRadius = new(8),
        BorderThickness = new(1.5), BorderBrush = new SolidColorBrush(Rgb(0x080808)),
        HorizontalAlignment = HorizontalAlignment.Center,
        Background = new LinearGradientBrush
        {
            StartPoint = new(0, 0), EndPoint = new(1, 1),
            GradientStops = { new() { Color = Rgb(0x313131) }, new() { Offset = .63, Color = Rgb(0x141414) },
                new() { Offset = 1, Color = Rgb(0x202020) } }
        }
    };
    private readonly Grid[] litLamps = [new(), new(), new()];
    private int displayedLamp = -1;
    private string? displayedSignal;
    private long nextFlicker = NextFlickerTime();
    private Storyboard? flicker;

    private void BuildSignal()
    {
        var lights = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        uint[] dim = [0x6D2019, 0x6B470C, 0x1B5340];
        uint[] lit = [0xF83C24, 0xFFC233, 0x36CD73];
        for (int index = 0; index < litLamps.Length; index++)
        {
            var cell = new Grid { Height = 19, Width = 29 };
            cell.Children.Add(Lens(dim[index]));
            var on = litLamps[index];
            on.Opacity = 0;
            var tint = Rgb(lit[index]);
            var glow = new RadialGradientBrush
            {
                RadiusX = .5, RadiusY = .5,
                GradientStops =
                {
                    new() { Color = ColorHelper.FromArgb(144, tint.R, tint.G, tint.B), Offset = .35 },
                    new() { Color = ColorHelper.FromArgb(0, tint.R, tint.G, tint.B), Offset = 1 }
                }
            };
            on.Children.Add(new Border { Background = glow });
            on.Children.Add(Lens(lit[index]));
            cell.Children.Add(on);
            lights.Children.Add(cell);
        }
        signalHousing.Child = lights;
        signal.RowDefinitions.Add(new());
        signal.RowDefinitions.Add(new());
        signal.Children.Add(signalCaption);
        signal.Children.Add(signalHousing);
        strip.Children.Add(signal);
        UpdateSignal(null, false, null);
    }

    private static Border Lens(uint color) => new()
    {
        Width = 17, Height = 17, CornerRadius = new(8.5), BorderThickness = new(1.25),
        BorderBrush = new SolidColorBrush(Rgb(0x0A0A0A)),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        Background = new RadialGradientBrush
        {
            Center = new(.48, .48), GradientOrigin = new(.34, .26), RadiusX = .55, RadiusY = .55,
            GradientStops =
            {
                new() { Color = ColorHelper.FromArgb(255, (byte)(0x33 + ((color >> 16) & 0xff) * .8),
                    (byte)(0x33 + ((color >> 8) & 0xff) * .8), (byte)(0x33 + (color & 0xff) * .8)) },
                new() { Color = Rgb(color), Offset = .46 },
                new() { Color = Rgb(0x100B0A), Offset = 1 }
            }
        }
    };

    private void UpdateSignal(int? allowanceThreshold, bool creditsLow, double? allowance)
    {
        int lamp = allowanceThreshold == 5 ? 0 : allowanceThreshold.HasValue ? 1 : allowance.HasValue ? 2 : -1;
        string description = allowance.HasValue
            ? FormattableString.Invariant($"Allowance remaining: {allowance:0.#}%.")
            : "Waiting for allowance data.";
        if (allowanceThreshold.HasValue) description += $" Allowance threshold reached: {allowanceThreshold}%.";
        if (creditsLow) description += $" Low credits: {state.CreditsLabel} remaining.";
        if (displayedLamp != lamp)
        {
            StopFlicker();
            for (int index = 0; index < litLamps.Length; index++) litLamps[index].Opacity = index == lamp ? 1 : 0;
            displayedLamp = lamp;
        }
        if (displayedSignal == description) return;
        displayedSignal = description;
        ToolTipService.SetToolTip(signal, description);
        AutomationProperties.SetName(signal, description);
    }

    private static long NextFlickerTime() => Environment.TickCount64 + Random.Shared.Next(480_000, 1_080_001);

    private void MaybeFlicker()
    {
        bool animate = CanAnimateWidget;
        if (!animate) StopFlicker();
        if (Environment.TickCount64 < nextFlicker) return;
        nextFlicker = NextFlickerTime();
        if (!animate || displayedLamp < 0) return;

        var dim = new DoubleAnimationUsingKeyFrames { EnableDependentAnimation = true, FillBehavior = FillBehavior.Stop };
        (double milliseconds, double brightness)[] points = [(0, 1), (55, .58), (165, 1), (285, 1), (337, .75), (455, 1)];
        foreach (var (milliseconds, brightness) in points)
            dim.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(milliseconds)), Value = brightness });
        Storyboard.SetTarget(dim, litLamps[displayedLamp]);
        Storyboard.SetTargetProperty(dim, nameof(UIElement.Opacity));
        var effect = new Storyboard();
        effect.Children.Add(dim);
        effect.Completed += (_, _) => { if (flicker == effect) flicker = null; };
        flicker = effect;
        effect.Begin();
    }

    private void StopFlicker()
    {
        flicker?.Stop();
        flicker = null;
    }
}
