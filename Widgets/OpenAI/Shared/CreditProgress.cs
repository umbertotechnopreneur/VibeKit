using System.Globalization;

namespace OpenAIUsage;

public static class CreditProgress
{
    public const int LensCount = 16;

    public static decimal? Percentage(decimal? balance, int? budget)
    {
        if (balance is not { } value || value < 0 || budget is not > 0) return null;
        if (value == 0) return 0;
        if (value >= budget.Value) return 100;
        return Math.Max(1e-28m, value / budget.Value * 100);
    }

    public static int LitLenses(decimal? percentage) =>
        (int)Math.Ceiling(percentage.GetValueOrDefault() * LensCount / 100);

    public static string Format(decimal? percentage, CultureInfo culture)
    {
        if (percentage is not { } value) return "—";
        if (value is 0 or 100) return value.ToString("0", culture) + "%";
        decimal rounded = Math.Round(value, 1, MidpointRounding.AwayFromZero);
        return Math.Clamp(rounded, .1m, 99.9m).ToString("0.#", culture) + "%";
    }
}
