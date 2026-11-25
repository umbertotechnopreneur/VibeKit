using System.Globalization;

namespace OpenAIUsage;

public sealed class CreditProgress
{
    public const int LensCount = 16;
    private string? accountKey;
    private decimal highestBalance;

    public decimal? Observe(string? key, decimal? balance)
    {
        if (!string.IsNullOrEmpty(key) && key != accountKey)
        {
            accountKey = key;
            highestBalance = 0;
        }
        if (balance is not { } value || value < 0) return null;
        if (value == 0) return 0;

        highestBalance = Math.Max(highestBalance, value);
        return Math.Max(1e-28m, value / highestBalance * 100);
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
