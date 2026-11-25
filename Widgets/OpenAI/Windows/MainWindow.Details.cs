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
    private bool detailsOpen;
    private bool IsCompact => !detailsOpen;
    private readonly FlipTile fiveHour = new("5-hour allowance", 112, 20, SecondaryText, showAccent: true);
    private readonly FlipTile fiveReset = new("5-hour auto reset in", 125, 20, SecondaryText, label: "5-hour reset in", sizingCharacters: 7);
    private readonly FlipTile weekly = new("Weekly allowance", 118, 20, SecondaryText, showAccent: true);
    private readonly FlipTile weeklyReset = new("Weekly auto reset", 152, 20, SecondaryText, sizingCharacters: 11);
    private readonly FlipTile detailsEstimate = new("Run-out estimate", 158, 20, SecondaryText);
    private readonly FlipTile timeToReset = new("Selected auto reset in", 132, 20, SecondaryText, label: "Auto reset in", sizingCharacters: 7);
    private readonly FlipTile forecastMessage = new("Will the quota last?", 442, 20, SecondaryText,
        wrapValue: true, sizingCharacters: "EXPECTED TO LAST UNTIL RESET".Length);

    private void SetDetailsOpen(bool open)
    {
        if (open != detailsOpen)
        {
            if (IsDocked) RequestWidgetFit();
            else
            {
                if (!open) expandedSize = AppWindow.ClientSize;
                RequestWidgetFit(open ? expandedSize : null);
                if (open) expandedSize = null;
            }
        }
        detailsOpen = open;
        RefreshMenuState();
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
        if (!detailsOpen || !settings.EnableDetailsBar) return;
        bool animate = CanAnimateWidget;
        PresentAllowance(state.FiveHourAllowance, fiveHour, fiveReset, now, animate, countdown: true);
        PresentAllowance(state.WeeklyAllowance, weekly, weeklyReset, now, animate, countdown: false);

        long? reset = state.ResetsAt;
        bool passed = reset <= now;
        detailsEstimate.Set(state.ForecastAvailable && !passed ? state.ForecastCompact : "—", animate);
        timeToReset.Set(reset is { } end ? FormatResetDuration(end - now) : "—", animate);
        DescribeMetric(timeToReset, $"Time left until the {SelectedQuotaName} renews automatically. {state.ResetLabel}");
        DescribeMetric(detailsEstimate, $"Estimated time until the {SelectedQuotaName} runs out at your observed usage rate. {state.ForecastDetail}");
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
            ? FormattableString.Invariant($"{minutes / 60:00}h {minutes % 60:00}m")
            : allowance?.ResetCompact;
        reset.Set(value ?? "—", animate);
        string quota = countdown ? "five-hour quota" : "weekly quota";
        DescribeMetric(reset, allowance?.ResetCompact is { } timestamp
            ? $"The {quota} renews automatically on {timestamp} ({settings.TimeZoneId})."
            : $"Automatic renewal time for the {quota} is unavailable.");
        DescribeMetric(percent, $"Percentage remaining in the {quota}.");
    }

    private static string FormatResetDuration(long seconds)
    {
        long minutes = (Math.Max(0, seconds) + 59) / 60, hours = minutes / 60;
        return hours >= 24
            ? FormattableString.Invariant($"{hours / 24:00}d {hours % 24:00}h")
            : FormattableString.Invariant($"{hours:00}h {minutes % 60:00}m");
    }
}
