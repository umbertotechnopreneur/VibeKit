using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System.Diagnostics;
using System.Globalization;

namespace OpenAIUsage.Windows;

public sealed partial class MainWindow
{
    private const double ClockWidth = 335, ClockGap = 5;
    private readonly StackPanel strip = new() { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new(12, 8, 23, 10), VerticalAlignment = VerticalAlignment.Center };
    private readonly FlipTile remaining = new("Allowance remaining", 145, 25, SecondaryText);
    private readonly FlipTile vouchers = new("Available resets", 111, 25, SecondaryText);
    private readonly FlipTile credits = new("Credits left", 107, 25, SecondaryText);
    private readonly FlipTile forecast = new("Est. time Left", 106, 24, SecondaryText);
    private readonly FlipTile compactReset = new("First reset", 154, 23, SecondaryText);
    private readonly FlipTile[] countdownTiles =
    [
        new("Days", (ClockWidth - 3 * ClockGap) / 4, 26, SecondaryText, "DAYS"),
        new("Hours", (ClockWidth - 3 * ClockGap) / 4, 26, SecondaryText, "HRS"),
        new("Minutes", (ClockWidth - 3 * ClockGap) / 4, 26, SecondaryText, "MIN"),
        new("Seconds", (ClockWidth - 3 * ClockGap) / 4, 26, SecondaryText, "SEC")
    ];
    private readonly FlipTile[] calendarTiles =
    [
        new("Weekday", 111, 19, SecondaryText, ""),
        new("Day of month", 41, 23, SecondaryText, ""),
        new("Month", 111, 19, SecondaryText, ""),
        new("Year", 57, 21, SecondaryText, "")
    ];
    private readonly Stopwatch clockCycle = new();
    private readonly Grid clockStage = new() { Width = ClockWidth, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel countdownFace = new() { Orientation = Orientation.Horizontal, Spacing = ClockGap };
    private readonly StackPanel calendarFace = new() { Orientation = Orientation.Horizontal, Spacing = ClockGap, Visibility = Visibility.Collapsed };
    private readonly Button profile = Button(Logo(30), "Open Profile");
    private readonly Button usageLink = Button("", "Open usage and resets in ChatGPT");
    private readonly Button forecastLink = Button("", "Open usage and resets in ChatGPT");
    private readonly Button resetLink = Button("", "Open usage and resets in ChatGPT");
    private readonly Button countdownLink = Button("", "Open usage and resets in ChatGPT");
    private readonly HashSet<int> notifiedThresholds = [];
    private readonly HashSet<int> soundedThresholds = [];
    private string? alertWindow;
    private int? creditsAlertThreshold;
    private bool creditsBelowThreshold;
    private string[]? calendarParts;
    private Storyboard? clockFade;
    private bool showingCalendar, wantsCalendar;

    private void BuildWidgetStrip()
    {
        profile.Padding = new(0);
        profile.Width = 30;
        profile.VerticalAlignment = VerticalAlignment.Center;
        profile.Click += (_, _) => OpenLink("https://chatgpt.com/profile");
        strip.Children.Add(profile);

        BuildSignal();

        strip.Children.Add(ConfigureUsageLink(usageLink, remaining));
        strip.Children.Add(vouchers);
        strip.Children.Add(ConfigureUsageLink(forecastLink, forecast));
        strip.Children.Add(credits);
        strip.Children.Add(ConfigureUsageLink(resetLink, compactReset));

        foreach (var tile in countdownTiles) countdownFace.Children.Add(tile);
        foreach (var tile in calendarTiles) calendarFace.Children.Add(tile);
        clockStage.Children.Add(countdownFace);
        clockStage.Children.Add(calendarFace);
        strip.Children.Add(ConfigureUsageLink(countdownLink, clockStage));
        SwitchClockFace(false);
    }

    private static Button ConfigureUsageLink(Button link, FrameworkElement content)
    {
        link.Content = content;
        link.Padding = new(0);
        link.MinHeight = 0;
        link.VerticalAlignment = VerticalAlignment.Center;
        link.Click += (_, _) => OpenLink(UsageUrl);
        return link;
    }

    private FrameworkElement[] TargetsForPointerInput() =>
        [profile, signal, usageLink, vouchers, forecastLink, credits, resetLink, countdownLink];

    private void ApplySize()
    {
        foreach (var tile in new[] { remaining, vouchers, forecast, credits, compactReset }.Concat(countdownTiles).Concat(calendarTiles))
            tile.SetLabelPosition(settings.LabelPosition);
        FlipTile.PlaceCaption(signal, signalCaption, signalHousing, settings.LabelPosition, SignalHeight);
        signal.Width = settings.LabelPosition == "hidden" ? 34 : 42;

        vouchers.Visibility = settings.ShowAvailableResets ? Visibility.Visible : Visibility.Collapsed;
        forecastLink.Visibility = settings.ShowEstimatedTime ? Visibility.Visible : Visibility.Collapsed;
        credits.Visibility = settings.ShowCredits ? Visibility.Visible : Visibility.Collapsed;
        ApplyDetailsSettings();
        UpdateSnapshotMetrics();
        if (!root.IsLoaded) return;

        widgetBands.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
        var size = widgetBands.DesiredSize;
        double scale = root.XamlRoot.RasterizationScale * (settings.BigSize ? 1.25 : 1) * .89;
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        if (display is not null)
            scale = Math.Min(scale, (display.WorkArea.Width - 16) / size.Width);
        int width = (int)Math.Ceiling(size.Width * scale), height = (int)Math.Ceiling(size.Height * scale);
        if (detailsBand.Visibility == Visibility.Visible && display is not null)
        {
            var area = display.WorkArea;
            var position = AppWindow.Position;
            int x = Math.Clamp(position.X, area.X, Math.Max(area.X, area.X + area.Width - width));
            int y = Math.Clamp(position.Y, area.Y, Math.Max(area.Y, area.Y + area.Height - height));
            AppWindow.MoveAndResize(new(x, y, width, height));
        }
        else AppWindow.Resize(new(width, height));
        UpdateDragRegions();
    }

    private bool CanAnimateWidget => root.IsLoaded && AppWindow.IsVisible && systemSettings.AnimationsEnabled;

    private void UpdateSnapshotMetrics()
    {
        bool animate = CanAnimateWidget;
        string allowance = state.Remaining is { } value ? FormattableString.Invariant($"{value:0.#}%") : "—";
        remaining.Set(allowance, animate);
        vouchers.Set(state.ResetVoucherCount?.ToString("D2", CultureInfo.InvariantCulture) ?? "—", animate && settings.ShowAvailableResets);
        credits.Set(state.CreditsLabel, animate && settings.ShowCredits);
        forecast.Set(state.ForecastAvailable ? state.ForecastCompact : "—", animate && settings.ShowEstimatedTime);
        var firstReset = state.ResetDisplays.FirstOrDefault();
        compactReset.Set(firstReset?.Value ?? state.SelectedResetCompact ?? "—", animate);

        string remainingDetail = $"{state.AllowanceLabel}: {allowance}. {state.ResetLabel}";
        DescribeMetric(usageLink, remainingDetail);
        DescribeMetric(forecastLink, $"Est. time Left: {(state.ForecastAvailable ? state.ForecastCompact : "—")}. {state.ForecastDetail}");
        DescribeMetric(resetLink, $"First reset: {firstReset?.Detail ?? state.ResetLabel}");
        var expiryDetails = state.ResetVoucherExpirations.Select((item, index) => $"Reset {index + 1}: {item.Detail}");
        DescribeMetric(vouchers, string.Join(Environment.NewLine, expiryDetails.Prepend(
            state.ResetVoucherCount is { } count ? $"{count} available reset vouchers." : "Available reset vouchers are unavailable.")));
        DescribeMetric(credits, $"Credits left: {state.CreditsLabel}. The displayed balance is rounded to a whole number.");

        calendarParts = state.ResetCalendarDate?.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: 4 } parts ? parts : null;
        if (calendarParts is not null)
        {
            for (int i = 0; i < calendarTiles.Length; i++)
                calendarTiles[i].Set(calendarParts[i], animate && showingCalendar);
            if (showingCalendar) DescribeMetric(countdownLink, $"Reset date: {state.ResetCalendarDate}");
        }
    }

    private static void DescribeMetric(FrameworkElement element, string detail)
    {
        if (ToolTipService.GetToolTip(element) as string == detail) return;
        ToolTipService.SetToolTip(element, detail);
        if (element is Button)
            AutomationProperties.SetName(element, detail + " Open usage and resets in ChatGPT.");
        else
            AutomationProperties.SetHelpText(element, detail);
    }

    private void UpdateResetClock(long? seconds)
    {
        if (calendarParts is null || !seconds.HasValue)
            clockCycle.Reset();
        else
            clockCycle.Start();

        bool showCalendar = (clockCycle.ElapsedMilliseconds / 10_000) % 2 == 1;
        bool animate = CanAnimateWidget;
        string[] values = seconds is { } time
            ? [(time / 86_400).ToString("D2", CultureInfo.InvariantCulture),
                (time / 3_600 % 24).ToString("D2", CultureInfo.InvariantCulture),
                (time / 60 % 60).ToString("D2", CultureInfo.InvariantCulture),
                (time % 60).ToString("D2", CultureInfo.InvariantCulture)]
            : ["--", "--", "--", "--"];
        for (int i = 0; i < countdownTiles.Length; i++)
            countdownTiles[i].Set(values[i], animate && !showingCalendar && !showCalendar);

        if (showCalendar == wantsCalendar && (animate || clockFade is null)) return;
        wantsCalendar = showCalendar;
        StopClockFade();
        if (!animate)
        {
            SwitchClockFace(showCalendar);
            return;
        }

        FadeClock(1, 0, () =>
        {
            SwitchClockFace(showCalendar);
            clockStage.UpdateLayout();
            foreach (var tile in showCalendar ? calendarTiles : countdownTiles) tile.Reveal();
            FadeClock(0, 1, () => { });
        });
    }

    private void SwitchClockFace(bool calendar)
    {
        showingCalendar = calendar;
        countdownFace.Visibility = calendar ? Visibility.Collapsed : Visibility.Visible;
        calendarFace.Visibility = calendar ? Visibility.Visible : Visibility.Collapsed;
        DescribeMetric(countdownLink, calendar
            ? $"Reset date: {state.ResetCalendarDate}"
            : "Countdown until the selected allowance resets.");
    }

    private void FadeClock(double from, double to, Action completed)
    {
        clockStage.Opacity = from;
        var animation = new DoubleAnimation
        {
            From = from, To = to, Duration = new(TimeSpan.FromMilliseconds(160)),
            FillBehavior = FillBehavior.Stop, EnableDependentAnimation = true
        };
        var transition = new Storyboard();
        Storyboard.SetTarget(animation, clockStage);
        Storyboard.SetTargetProperty(animation, nameof(UIElement.Opacity));
        transition.Children.Add(animation);
        transition.Completed += (_, _) =>
        {
            if (clockFade != transition) return;
            clockFade = null;
            clockStage.Opacity = to;
            completed();
        };
        clockFade = transition;
        transition.Begin();
    }

    private void StopClockFade()
    {
        clockFade?.Stop();
        clockFade = null;
        clockStage.Opacity = 1;
    }

    private void CheckAlerts()
    {
        double? allowance = state.Connected && state.SelectedWindow is not null ? state.Remaining : null;
        decimal? balance = state.Connected ? state.CreditsBalance : null;
        bool hasCredits = balance.HasValue || (state.Connected && state.CreditsLabel == "Unlimited");

        if (allowance.HasValue && alertWindow != state.SelectedWindow)
        {
            notifiedThresholds.Clear();
            soundedThresholds.Clear();
            alertWindow = state.SelectedWindow;
        }

        int[] enabled = [.. new[] { (Value: 50, Active: settings.Alert50), (Value: 20, Active: settings.Alert20), (Value: 5, Active: settings.Alert5) }
            .Where(threshold => threshold.Active).Select(threshold => threshold.Value)];
        int[] reached = [.. enabled.Where(threshold => allowance <= threshold)];
        int? allowanceThreshold = reached.Length > 0 ? reached.Min() : null;

        if (creditsAlertThreshold != settings.CreditsAlertThreshold)
        {
            creditsAlertThreshold = settings.CreditsAlertThreshold;
            creditsBelowThreshold = false;
        }
        bool creditsLow = creditsAlertThreshold.HasValue && balance <= creditsAlertThreshold.Value;
        UpdateSignal(allowanceThreshold, creditsLow, allowance);

        notifiedThresholds.IntersectWith(allowance.HasValue ? reached : enabled);
        soundedThresholds.IntersectWith(allowance.HasValue ? reached : enabled);
        bool newlyReachedAllowance = reached.Any(threshold => !soundedThresholds.Contains(threshold));
        bool newlyReachedCredits = creditsLow && !creditsBelowThreshold;
        soundedThresholds.UnionWith(reached);
        if (hasCredits) creditsBelowThreshold = creditsLow;

        if (settings.AlertSound && (newlyReachedAllowance || newlyReachedCredits)) PlayAlertDing();

        int[] pending = [.. reached.Where(threshold => !notifiedThresholds.Contains(threshold))];
        string? allowanceMessage = pending.Length > 0
            ? FormattableString.Invariant($"{state.AllowanceLabel}: {allowance:0.#}% remaining (at or below {pending.Min()}%).")
            : null;
        string? creditsMessage = newlyReachedCredits ? $"Credits left: {state.CreditsLabel} (at or below {settings.CreditsAlertThreshold})." : null;
        if (allowanceMessage is null && creditsMessage is null) return;
        string message = allowanceMessage is null ? creditsMessage! : creditsMessage is null ? allowanceMessage : $"{allowanceMessage}\n{creditsMessage}";
        if (tray?.NotifyQuota(message, includesCredits: creditsMessage is not null) == true)
            notifiedThresholds.UnionWith(pending);
    }

}
