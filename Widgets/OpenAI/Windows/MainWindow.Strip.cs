using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using System.Globalization;

namespace OpenAIUsage.Windows;

public sealed partial class MainWindow
{
    private readonly WrappingPanel strip = new(TileSpacing);
    private readonly FlipTile remaining = new("Allowance remaining", 115, 20, SecondaryText, label: "Allowance left", showAccent: true, worn: true, compactAccent: true);
    private readonly FlipTile credits = new("Remaining Credits", 108, 20, SecondaryText, sizingCharacters: 7, showAccent: true, worn: true, compactAccent: true);
    private readonly CreditPercentageTile creditPercentage = new(SecondaryText);
    private readonly FlipTile forecast = new("Time to run out", 152, 20, SecondaryText);
    private readonly FlipTile vouchers = new("Available vouchers", 104, 20, SecondaryText, label: "Vouchers");
    private readonly FlipTile voucherExpiry = new("Next voucher expiry", 160, 20, SecondaryText, sizingCharacters: 11);
    private readonly FlipTile resetClock = new("Auto reset clock", 403, 20, SecondaryText,
        sizingCharacters: "WEDNESDAY 00 SEPTEMBER 00".Length);
    private readonly Stopwatch clockCycle = new();
    private readonly Button usageLink = Button("", "Open usage and resets in ChatGPT");
    private readonly Button forecastLink = Button("", "Open usage and resets in ChatGPT");
    private readonly Button vouchersButton = Button("", "Show voucher expiration dates");
    private readonly TextBlock voucherExpirations = Text("", 14);
    private readonly Button countdownLink = Button("", "Open usage and resets in ChatGPT");
    private readonly HashSet<int> notifiedThresholds = [];
    private readonly HashSet<int> soundedThresholds = [];
    private string? alertWindow;
    private int? creditsAlertThreshold;
    private readonly CreditProgress creditProgress = new();
    private bool creditsBelowThreshold;
    private string? resetCalendarText;

    private void BuildWidgetStrip()
    {
        BuildSignal();
        ConfigureUsageLink(usageLink, remaining);
        ConfigureUsageLink(forecastLink, forecast);
        BuildVoucherButton();

        ConfigureUsageLink(countdownLink, resetClock);
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
                    Text("Voucher expiration dates", 16, bold: true),
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
        credits.Set(state.CreditsLabel == "Unavailable" ? "—" : state.CreditsLabel.ToUpperInvariant(), animate && settings.ShowCredits);
        forecast.Set(state.ForecastAvailable ? state.ForecastCompact : "—", animate && settings.ShowEstimatedTime);
        UpdateVoucherExpiry(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        string remainingDetail = $"Percentage available in the {SelectedQuotaName}: {allowance}. {state.ResetLabel}";
        DescribeMetric(usageLink, remainingDetail);
        DescribeMetric(forecastLink, $"Estimated time until the {SelectedQuotaName} runs out at your observed usage rate. {state.ForecastDetail}");
        AutomationProperties.SetName(vouchersButton, $"Vouchers available to use now: {resetCount}. Show voucher expiration dates.");
        voucherExpirations.Text = state.ResetVoucherCount switch
        {
            null => "Voucher balance unavailable.",
            0 => "No vouchers are available.",
            _ when state.ResetVoucherExpirations.Count == 0 => "Codex did not provide expiration dates.",
            _ => string.Join(Environment.NewLine, state.ResetVoucherExpirations.Select((item, index) => $"Voucher {index + 1}: {item.Detail}"))
        };
        decimal? percentage = creditProgress.Observe(state.CreditAccountKey, state.CreditsBalance);
        double? creditPercent = (double?)percentage;
        remaining.SetProgress(state.Remaining, ProgressColor(state.Remaining));
        credits.SetProgress(creditPercent, ProgressColor(creditPercent));
        creditPercentage.Set(percentage, animate);
        DescribeMetric(credits, $"Remaining credits: {state.CreditsLabel}. The bar compares the balance with the highest balance seen since the widget opened.");

        resetCalendarText = null;
        if (state.ResetCalendarDate?.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: 4 } parts)
        {
            if (parts[3].Length > 2) parts[3] = parts[3][^2..];
            resetCalendarText = string.Join(" ", parts).ToUpperInvariant();
        }
    }

    private string SelectedQuotaName => state.Windows.FirstOrDefault(window => window.Key == state.SelectedWindow)?.Minutes switch
    {
        300 => "five-hour quota",
        10080 => "weekly quota",
        _ => "selected quota"
    };

    private void UpdateVoucherExpiry(long now)
    {
        var next = state.ResetVoucherCount > 0
            ? state.ResetVoucherExpirations.Where(item => item.ExpiresAt > now).MinBy(item => item.ExpiresAt)
            : null;
        voucherExpiry.Set(next?.Value ?? "—", false);
        DescribeMetric(voucherExpiry, next is not null
            ? "Earliest reported expiration of an available voucher: " + next.Detail
            : state.ResetVoucherCount == 0
                ? "No vouchers are available."
                : "No upcoming expiration date was reported for an available voucher.");
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
            IReadOnlyList<FlipTile.Flap> flaps =
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
        DescribeMetric(countdownLink, $"Automatic renewal of the {SelectedQuotaName}. {state.ResetLabel} This display alternates between the date and the time remaining.");
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
