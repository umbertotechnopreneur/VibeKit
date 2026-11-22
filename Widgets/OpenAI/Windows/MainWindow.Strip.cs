using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Diagnostics;
using System.Globalization;

namespace OpenAIUsage.Windows;

public sealed partial class MainWindow
{
    private const double BrandButtonSize = 64;
    private readonly WrappingPanel strip = new();
    private readonly FlipTile remaining = new("Allowance remaining", 148, 24, SecondaryText, showAccent: true, worn: true);
    private readonly FlipTile credits = new("Remaining credits", 136, 24, SecondaryText, showAccent: true, worn: true);
    private readonly FlipTile forecast = new("Estimated time remaining", 178, 24, SecondaryText);
    private readonly FlipTile vouchers = new("Resets available", 122, 25, SecondaryText);
    private readonly FlipTile compactReset = new("First reset", 178, 23, SecondaryText);
    private readonly FlipTile resetClock = new("Next reset date", 430, 28, SecondaryText,
        sizingCharacters: "WEDNESDAY 00 SEPTEMBER 00".Length,
        creaseShadow: new(5, ColorHelper.FromArgb(112, 52, 10, 13), ColorHelper.FromArgb(255, 24, 6, 7)),
        digitDelay: new(300, 1000));
    private readonly Stopwatch clockCycle = new();
    private readonly Button appWebsiteButton = BrandLink(Logo(BrandButtonSize), "Open OpenAI Usage project page", ProjectUrl);
    private readonly Button manifestoButton = BrandLink(new Image
    {
        Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Branding", "VibeWare.png"))),
        Width = BrandButtonSize, Height = BrandButtonSize, Stretch = Stretch.Uniform,
        RenderTransformOrigin = new(.5, .5),
        RenderTransform = new ScaleTransform { ScaleX = (BrandButtonSize + 20) / BrandButtonSize, ScaleY = (BrandButtonSize + 20) / BrandButtonSize }
    }, "VibeWare manifesto", ManifestoUrl, artworkBleed: 3);
    private readonly Button usageLink = Button("", "Open usage and resets in ChatGPT");
    private readonly Button forecastLink = Button("", "Open usage and resets in ChatGPT");
    private readonly Button vouchersButton = Button("", "View reset voucher expiration dates");
    private readonly TextBlock voucherExpirations = Text("", 14);
    private readonly Button resetLink = Button("", "Open usage and resets in ChatGPT");
    private readonly Button countdownLink = Button("", "Open usage and resets in ChatGPT");
    private readonly HashSet<int> notifiedThresholds = [];
    private readonly HashSet<int> soundedThresholds = [];
    private string? alertWindow;
    private int? creditsAlertThreshold;
    private decimal highestCreditBalance;
    private bool creditsBelowThreshold;
    private string? resetCalendarText;
    private bool showingCalendar;

    private void BuildWidgetStrip()
    {
        BuildSignal();
        ConfigureUsageLink(usageLink, remaining);
        ConfigureUsageLink(forecastLink, forecast);
        ConfigureUsageLink(resetLink, compactReset);
        BuildVoucherButton();

        ConfigureUsageLink(countdownLink, resetClock);
    }

    private static Button BrandLink(Image mark, string label, string url, double artworkBleed = 0)
    {
        mark.HorizontalAlignment = HorizontalAlignment.Center;
        mark.VerticalAlignment = VerticalAlignment.Center;
        var imageSlot = new Grid
        {
            Width = BrandButtonSize,
            Height = BrandButtonSize,
            Clip = new RectangleGeometry { Rect = new(-artworkBleed, -artworkBleed, BrandButtonSize + 2 * artworkBleed, BrandButtonSize + 2 * artworkBleed) },
            IsHitTestVisible = false,
            Children = { mark }
        };
        var button = Button(imageSlot, label);
        button.Width = BrandButtonSize;
        button.Height = BrandButtonSize;
        button.Padding = new(0);
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.VerticalContentAlignment = VerticalAlignment.Center;
        button.Click += (_, _) => OpenLink(url);
        return button;
    }

    private Button BuildVoucherButton()
    {
        vouchersButton.Content = vouchers;
        vouchersButton.Padding = new(0);
        vouchersButton.MinHeight = 0;
        vouchersButton.MinWidth = 0;
        vouchersButton.VerticalAlignment = VerticalAlignment.Center;
        vouchersButton.Flyout = new Flyout
        {
            Content = new StackPanel
            {
                MaxWidth = 340,
                Spacing = 8,
                Children =
                {
                    Text("Reset voucher expiration dates", 16, bold: true),
                    new ScrollViewer { Content = voucherExpirations, MaxHeight = 220 }
                }
            }
        };
        return vouchersButton;
    }

    private static Button ConfigureUsageLink(Button link, FrameworkElement content)
    {
        link.Content = content;
        link.Padding = new(0);
        link.MinHeight = 0;
        link.MinWidth = 0;
        link.VerticalAlignment = VerticalAlignment.Center;
        link.Click += (_, _) => OpenLink(UsageUrl);
        return link;
    }

    private bool CanAnimateWidget => root.IsLoaded && AppWindow.IsVisible && systemSettings.AnimationsEnabled;

    private void UpdateSnapshotMetrics()
    {
        bool animate = CanAnimateWidget;
        string allowance = state.Remaining is { } value ? FormattableString.Invariant($"{value:0.#}%") : "—";
        remaining.Set(allowance, animate);
        string resetCount = state.ResetVoucherCount?.ToString(CultureInfo.InvariantCulture) ?? "—";
        vouchers.Set(resetCount, animate && settings.ShowAvailableResets);
        credits.Set(state.CreditsLabel.ToUpperInvariant(), animate && settings.ShowCredits);
        forecast.Set(state.ForecastAvailable ? state.ForecastCompact : "—", animate && settings.ShowEstimatedTime);
        var firstReset = state.ResetDisplays.FirstOrDefault();
        compactReset.Set(firstReset?.Value ?? state.SelectedResetCompact ?? "—", animate);

        string remainingDetail = $"{state.AllowanceLabel}: {allowance}. {state.ResetLabel}";
        DescribeMetric(usageLink, remainingDetail);
        DescribeMetric(forecastLink, $"Estimated time remaining: {(state.ForecastAvailable ? state.ForecastCompact : "—")}. {state.ForecastDetail}");
        DescribeMetric(resetLink, $"First reset: {firstReset?.Detail ?? state.ResetLabel}");
        AutomationProperties.SetName(vouchersButton, $"Resets available: {resetCount}. View reset voucher expiration dates.");
        voucherExpirations.Text = state.ResetVoucherCount switch
        {
            null => "Reset voucher information is unavailable.",
            0 => "There are no reset vouchers available.",
            _ when state.ResetVoucherExpirations.Count == 0 => "Codex did not provide expiration dates.",
            _ => string.Join(Environment.NewLine, state.ResetVoucherExpirations.Select((item, index) => $"Reset {index + 1}: {item.Detail}"))
        };
        if (state.CreditsBalance is { } balance && balance > highestCreditBalance)
            highestCreditBalance = balance;
        double? creditPercent = state.CreditsBalance is { } currentBalance && highestCreditBalance > 0
            ? (double)(currentBalance / highestCreditBalance * 100) : null;
        remaining.SetProgress(state.Remaining, ProgressColor(state.Remaining));
        credits.SetProgress(creditPercent, ProgressColor(creditPercent));
        DescribeMetric(credits, $"Remaining credits: {state.CreditsLabel}. The bar compares the balance with the highest balance seen since the widget opened.");

        resetCalendarText = null;
        if (state.ResetCalendarDate?.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: 4 } parts)
        {
            if (parts[3].Length > 2) parts[3] = parts[3][^2..];
            resetCalendarText = string.Join(" ", parts).ToUpperInvariant();
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
        if (resetCalendarText is null || !seconds.HasValue)
            clockCycle.Reset();
        else
            clockCycle.Start();

        bool showCalendar = (clockCycle.ElapsedMilliseconds / 10_000) % 2 == 1;
        bool animate = CanAnimateWidget && ModuleEnabled("clock");
        if (showCalendar)
        {
            resetClock.Set(resetCalendarText!, animate, null, 320);
        }
        else if (seconds is { } time)
        {
            string days = (time / 86_400).ToString("D2", CultureInfo.InvariantCulture);
            string value = FormattableString.Invariant($"{days}d {time / 3_600 % 24:00}h {time / 60 % 60:00}m {time % 60:00}s");
            int hoursStart = days.Length + 2;
            IReadOnlyList<FlipTile.Flap>? flaps = showingCalendar ? null :
            [
                new(0, days.Length + 1, 300),
                new(hoursStart, 3, 240),
                new(hoursStart + 4, 3, 190),
                new(hoursStart + 8, 3, 145)
            ];
            resetClock.Set(value, animate, flaps, 320);
        }
        else
        {
            resetClock.Set("—", false);
        }
        showingCalendar = showCalendar;
        DescribeMetric(countdownLink, showCalendar
            ? $"Reset date: {state.ResetCalendarDate}"
            : "Countdown until the selected allowance resets.");
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
