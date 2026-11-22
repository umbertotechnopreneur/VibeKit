using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Globalization;

namespace OpenAIUsage.Windows;

public sealed partial class MainWindow
{
    private static readonly SolidColorBrush ForecastAmber = new(Rgb(0xFFC233));
    private static readonly SolidColorBrush QuotaRed = new(Rgb(0xF83C24));
    private static readonly SolidColorBrush QuotaGreen = new(Rgb(0x36CD73));
    private readonly Button detailsButton = Button(DetailsIcon(), "Show usage details");
    private bool detailsOpen;
    private readonly FlipTile fiveHour = new("5-hour allowance", 136, 25, SecondaryText, showAccent: true);
    private readonly FlipTile fiveReset = new("5-hour reset in", 104, 25, SecondaryText);
    private readonly FlipTile weekly = new("Weekly allowance", 140, 25, SecondaryText, showAccent: true);
    private readonly FlipTile weeklyReset = new("Weekly reset at", 184, 25, SecondaryText);
    private readonly FlipTile detailsEstimate = new("Estimated time remaining", 174, 25, SecondaryText);
    private readonly FlipTile timeToReset = new("Time to reset", 128, 25, SecondaryText);
    private readonly FlipTile forecastMessage = new("At the current rate", 530, 25, SecondaryText, wrapValue: true);

    private static UIElement DetailsIcon()
    {
        var columns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Height = 16 };
        foreach (double height in new[] { 6d, 10d, 15d })
            columns.Children.Add(new Border { Width = 3, Height = height, Background = PrimaryText, VerticalAlignment = VerticalAlignment.Bottom });
        return columns;
    }

    private void SetDetailsOpen(bool open)
    {
        if (open && !settings.EnableDetailsBar) return;
        detailsOpen = open;
        string description = open ? "Hide usage details" : "Show usage details";
        ToolTipService.SetToolTip(detailsButton, description);
        AutomationProperties.SetName(detailsButton, description);
        ApplySize();
    }

    private static Brush? ProgressColor(double? percent) => percent switch
    {
        null => null,
        <= 20 => QuotaRed,
        <= 50 => ForecastAmber,
        _ => QuotaGreen
    };

    private void UpdateDetails(long now)
    {
        if (!detailsOpen) return;
        bool animate = CanAnimateWidget;
        PresentAllowance(state.FiveHourAllowance, fiveHour, fiveReset, now, animate, countdown: true);
        PresentAllowance(state.WeeklyAllowance, weekly, weeklyReset, now, animate, countdown: false);

        long? reset = state.ResetsAt;
        bool passed = reset <= now;
        detailsEstimate.Set(state.ForecastAvailable && !passed ? state.ForecastCompact : "—", animate);
        timeToReset.Set(reset is { } end ? FormatResetDuration(end - now) : "—", animate);
        bool waiting = passed && state.Remaining is not <= 0;
        forecastMessage.SetInk(!waiting && state.ResetForecast?.Warning == true ? ForecastAmber : null);
        string message = waiting ? "Waiting for reset data" : state.ResetForecast?.Message ?? "Allowance unavailable";
        message = System.Text.RegularExpressions.Regex.Replace(message.ToUpperInvariant(), @"(?<=\d)[DHM]\b",
            unit => unit.Value.ToLowerInvariant());
        forecastMessage.Set(message, animate);
        DescribeMetric(forecastMessage, state.ForecastDetail);
    }

    private void PresentAllowance(AllowanceDisplay? allowance, FlipTile percent, FlipTile reset, long now, bool animate, bool countdown)
    {
        percent.Set(allowance is null ? "—" : FormattableString.Invariant($"{allowance.Remaining:0.#}%"), animate);
        percent.SetProgress(allowance?.Remaining, ProgressColor(allowance?.Remaining));
        long minutes = (Math.Max(0, (allowance?.ResetsAt ?? now) - now) + 59) / 60;
        string? value = countdown && allowance?.ResetsAt is not null
            ? FormattableString.Invariant($"{minutes / 60:00}:{minutes % 60:00}")
            : allowance?.ResetCompact;
        reset.Set(value ?? "—", animate);
        DescribeMetric(reset, allowance?.ResetCompact ?? "Reset time unavailable");
    }

    private static string FormatResetDuration(long seconds)
    {
        long minutes = (Math.Max(0, seconds) + 59) / 60, hours = minutes / 60;
        return hours >= 24
            ? FormattableString.Invariant($"{hours / 24:00}d {hours % 24:00}h")
            : FormattableString.Invariant($"{hours:00}h {minutes % 60:00}m");
    }
}
