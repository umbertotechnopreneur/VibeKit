using System.Text.Json;

namespace OpenAIUsage;

public record QuotaWindow(string Key, string Label, double Used, int Minutes, long? ResetsAt);
public record TokenDay(string Date, long? Tokens)
{
    public string CompactTokens => Tokens is { } count ? Analytics.Compact(count) : "—";
}
public record AccountUsage(string Identity, string Plan, List<QuotaWindow> Windows,
    decimal? Credits, bool UnlimitedCredits, bool? OrdinaryUsageAllowed, bool SpendBlocked,
    List<TokenDay> Tokens, string? TokenError)
{
    public ResetVoucherBalance? ResetVouchers { get; init; }
}
public record ResetVoucherBalance(int AvailableCount, IReadOnlyList<long?> Expirations);
public record ResetVoucherDisplay(string Value, string Detail, long? ExpiresAt = null);
public record Sample(long At, double Used, long? Reset, string Plan);
public record QuotaDay(string Date, double? Used, double Coverage);
public record Forecast(double? Average, double? DaysLeft, int CompleteDays, string Headline, string Detail);
public record ResetDisplay(string Heading, string Value, string Detail, long ResetsAt);
public record AllowanceDisplay(double Remaining, long? ResetsAt, string? ResetCompact);
public record ResetForecastDisplay(string Message, bool Warning);

public sealed class WidgetSettings
{
    public string CodexPath { get; set; } = "codex";
    public string? SelectedWindow { get; set; }
    public string TimeZoneId { get; set; } = TimeZoneInfo.Local.Id;
    public bool AlwaysOnTop { get; set; }
    public string? DockPosition { get; set; }
    public bool ShowInTaskbar { get; set; } = true;
    public bool BigSize { get; set; }
    public int WindowOpacityPercent { get; set; } = 100;
    public bool Alert50 { get; set; }
    public bool Alert20 { get; set; }
    public bool Alert5 { get; set; }
    public string LabelPosition { get; set; } = "above";
    public bool ShowAvailableResets { get; set; } = true;
    public bool ShowCredits { get; set; } = true;
    public bool ShowEstimatedTime { get; set; } = true;
    public bool EnableDetailsBar { get; set; }
    public string[] HiddenFields { get; set; } = [];
    public string[] FieldOrder { get; set; } = [];
    public bool AlertSound { get; set; } = true;
    public int? CreditsAlertThreshold { get; set; }
    public WidgetSettings Copy() => (WidgetSettings)MemberwiseClone();
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(CodexPath)) throw new ArgumentException("Choose a Codex executable.");
        _ = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
        if (LabelPosition is not ("above" or "below" or "hidden"))
            throw new ArgumentException("Choose whether field labels appear above, below, or are hidden.");
        if (DockPosition is not (null or "top" or "bottom"))
            throw new ArgumentException("Choose top, bottom, or a floating widget.");
        if (CreditsAlertThreshold is < 0)
            throw new ArgumentException("Choose a nonnegative credits alert threshold.");
    }
}

public sealed class WidgetState
{
    public bool Connected { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string Status { get; set; } = "Connecting to Codex…";
    public string Plan { get; set; } = "Unavailable";
    public List<QuotaWindow> Windows { get; set; } = [];
    public string? SelectedWindow { get; set; }
    public string AllowanceLabel { get; set; } = "Allowance remaining";
    public double? Remaining { get; set; }
    public long? ResetsAt { get; set; }
    public string ResetLabel { get; set; } = "Reset unavailable";
    public string? SelectedResetCompact { get; set; }
    public string? ResetCalendarDate { get; set; }
    public List<ResetDisplay> ResetDisplays { get; set; } = [];
    public AllowanceDisplay? FiveHourAllowance { get; set; }
    public AllowanceDisplay? WeeklyAllowance { get; set; }
    public ResetForecastDisplay? ResetForecast { get; set; }
    public int? ResetVoucherCount { get; set; }
    public IReadOnlyList<ResetVoucherDisplay> ResetVoucherExpirations { get; set; } = [];
    public string CreditsLabel { get; set; } = "Unavailable";
    public decimal? CreditsBalance { get; set; }
    public string? CreditAccountKey { get; set; }
    public string AccessNote { get; set; } = "";
    public string AverageLabel { get; set; } = "Collecting quota history";
    public bool ForecastAvailable { get; set; }
    public string ForecastCompact { get; set; } = "ⓘ";
    public string ForecastLabel { get; set; } = "Not enough history";
    public string ForecastDetail { get; set; } = "Keep the widget open to record allowance usage.";
    public string CoverageLabel { get; set; } = "0 of 7 complete days";
    public List<QuotaDay> QuotaDays { get; set; } = [];
    public List<TokenDay> TokenDays { get; set; } = [];
    public string? TokenEndDate { get; set; }
    public string TodayTokensLabel { get; set; } = "—";
    public string TodayTokensNote { get; set; } = "Today's service bucket is not available yet.";
    public string TokensLabel { get; set; } = "Unavailable";
    public string PreviousTokensLabel { get; set; } = "Unavailable";
    public string TokenAverageLabel { get; set; } = "Unavailable";
    public string TokenNote { get; set; } = "Service calendar dates; missing days are not zero.";
}

public static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
    public static readonly JsonSerializerOptions Compact = new(Default) { WriteIndented = false };
}
