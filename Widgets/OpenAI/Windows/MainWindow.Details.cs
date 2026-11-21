using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Globalization;

namespace OpenAIUsage.Windows;

public sealed partial class MainWindow
{
    private static readonly SolidColorBrush DetailsBackground = new();
    private static readonly SolidColorBrush ForecastAmber = new(Rgb(0xFFC233));
    private static readonly SolidColorBrush QuotaRed = new(Rgb(0xF83C24));
    private static readonly SolidColorBrush QuotaGreen = new(Rgb(0x36CD73));
    private readonly StackPanel widgetBands = new() { Spacing = 4 };
    private readonly Button detailsButton = Button(DetailsIcon(), "Show usage details");
    private readonly Border detailsBand = new()
    {
        Background = DetailsBackground, BorderBrush = new SolidColorBrush(Rgb(0xA36B4A)),
        BorderThickness = new(1), CornerRadius = new(15), Margin = new(8, 0, 8, 8),
        Padding = new(11, 6, 12, 6), Visibility = Visibility.Collapsed
    };
    private readonly ComboBox detailsSelector = new() { Width = 149, ItemsSource = new[] { "Allowances", "Reset forecast" }, SelectedIndex = 0 };
    private readonly StackPanel allowancesBand = new() { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly StackPanel forecastBand = new() { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly FlipTile fiveHour = new("5-hour allowance", 123, 22, SecondaryText, showAccent: true);
    private readonly FlipTile fiveReset = new("Resets in", 91, 22, SecondaryText);
    private readonly FlipTile weekly = new("Weekly allowance", 126, 22, SecondaryText, showAccent: true);
    private readonly FlipTile weeklyReset = new("Resets at", 149, 20, SecondaryText);
    private readonly FlipTile detailsEstimate = new("Est. time Left", 110, 20, SecondaryText);
    private readonly FlipTile timeToReset = new("Time to reset", 106, 20, SecondaryText);
    private readonly FlipTile forecastMessage = new("At the current rate", 303, 19, SecondaryText);

    private static UIElement DetailsIcon()
    {
        var columns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Height = 16 };
        foreach (double height in new[] { 6d, 10d, 15d })
            columns.Children.Add(new Border { Width = 3, Height = height, Background = PrimaryText, VerticalAlignment = VerticalAlignment.Bottom });
        return columns;
    }

    private void BuildDetailsBand()
    {
        allowancesBand.Children.Add(fiveHour);
        allowancesBand.Children.Add(fiveReset);
        allowancesBand.Children.Add(new Border { Width = 1, Height = 32, Background = SecondaryText, Opacity = .38, VerticalAlignment = VerticalAlignment.Center });
        allowancesBand.Children.Add(weekly);
        allowancesBand.Children.Add(weeklyReset);
        forecastBand.Children.Add(detailsEstimate);
        forecastBand.Children.Add(timeToReset);
        forecastBand.Children.Add(forecastMessage);

        AutomationProperties.SetName(detailsSelector, "Usage details view");
        detailsSelector.VerticalAlignment = VerticalAlignment.Center;
        var view = new Grid();
        view.Children.Add(allowancesBand);
        view.Children.Add(forecastBand);
        var content = new Grid { ColumnSpacing = 12 };
        content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        content.Children.Add(detailsSelector);
        Grid.SetColumn(view, 1);
        content.Children.Add(view);
        detailsBand.Child = content;
        widgetBands.Children.Add(strip);
        widgetBands.Children.Add(detailsBand);
        detailsSelector.SelectionChanged += (_, _) =>
        {
            bool showForecast = detailsSelector.SelectedIndex == 1;
            allowancesBand.Visibility = showForecast ? Visibility.Collapsed : Visibility.Visible;
            forecastBand.Visibility = showForecast ? Visibility.Visible : Visibility.Collapsed;
            UpdateDetails(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        };
    }

    private void ApplyDetailsSettings()
    {
        detailsButton.Visibility = settings.EnableDetailsBar ? Visibility.Visible : Visibility.Collapsed;
        actions.ColumnDefinitions[1].Width = new(settings.EnableDetailsBar ? ToolbarColumnWidth : 0);
        if (!settings.EnableDetailsBar && detailsBand.Visibility == Visibility.Visible)
            ShowDetailsBand(false);
        foreach (var tile in new[] { fiveHour, fiveReset, weekly, weeklyReset, detailsEstimate, timeToReset, forecastMessage })
            tile.SetLabelPosition(settings.LabelPosition);
        UpdateDetails(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    private void SetDetailsOpen(bool open)
    {
        if (!settings.EnableDetailsBar) return;
        ShowDetailsBand(open);
        ApplySize();
    }

    private void ShowDetailsBand(bool open)
    {
        detailsBand.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        string description = open ? "Hide usage details" : "Show usage details";
        ToolTipService.SetToolTip(detailsButton, description);
        AutomationProperties.SetName(detailsButton, description);
    }

    private void UpdateDetails(long now)
    {
        if (detailsBand.Visibility != Visibility.Visible) return;
        bool animate = CanAnimateWidget;
        if (detailsSelector.SelectedIndex != 1)
        {
            PresentAllowance(state.FiveHourAllowance, fiveHour, fiveReset, now, animate, countdown: true);
            PresentAllowance(state.WeeklyAllowance, weekly, weeklyReset, now, animate, countdown: false);
            return;
        }

        long? reset = state.ResetsAt;
        bool passed = reset <= now;
        detailsEstimate.Set(state.ForecastAvailable && !passed ? state.ForecastCompact : "—", animate);
        timeToReset.Set(reset is { } end ? FormatResetDuration(end - now) : "—", animate);
        bool waiting = passed && state.Remaining is not <= 0;
        forecastMessage.SetInk(!waiting && state.ResetForecast?.Warning == true ? ForecastAmber : null);
        forecastMessage.Set(waiting ? "WAITING FOR RESET DATA" : state.ResetForecast?.Message ?? "ALLOWANCE UNAVAILABLE", animate);
        DescribeMetric(forecastMessage, state.ForecastDetail);
    }

    private void PresentAllowance(AllowanceDisplay? allowance, FlipTile percent, FlipTile reset, long now, bool animate, bool countdown)
    {
        percent.Set(allowance is null ? "—" : FormattableString.Invariant($"{allowance.Remaining:0.#}%"), animate);
        Brush? accent = allowance?.Remaining switch
        {
            null => null,
            <= 5 when settings.Alert5 => QuotaRed,
            <= 20 when settings.Alert20 => ForecastAmber,
            <= 50 when settings.Alert50 => ForecastAmber,
            _ => QuotaGreen
        };
        percent.SetAccent(accent);
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
