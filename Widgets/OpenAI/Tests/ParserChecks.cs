using OpenAIUsage;
using System.Text.Json;

internal static class ParserChecks
{
    internal static void Run(Action<bool, string> check)
    {
        using var account = JsonDocument.Parse("""{"type":"chatgpt","email":"fixture@example.test","planType":"pro"}""");
        foreach (string reset in new[] { "null", "\"bad\"", "true", "{}", "[]", "-1", "1.5", "253402300800" })
        {
            using var limits = JsonDocument.Parse("""{"rateLimits":{"primary":{"usedPercent":20,"windowDurationMins":300,"resetsAt":RESET},"credits":{"balance":"12.5"}}}""".Replace("RESET", reset));
            var value = CodexClient.Parse(account.RootElement, limits.RootElement, null);
            check(value.Windows.Count == 1 && value.Windows[0].Used == 20 && value.Windows[0].ResetsAt is null && value.Credits == 12.5m,
                "Invalid optional reset preserves validated quota and credits");
        }
        using var mixedLimits = JsonDocument.Parse("""{"rateLimitsByLimitId":{"broken":null,"codex":{"primary":{"usedPercent":25,"windowDurationMins":10080}}}}""");
        foreach (string tokens in new[] { "null", "[]", "true", "{\"dailyUsageBuckets\":[null,{},true,{\"startDate\":\"2026-11-01\",\"tokens\":\"bad\"},{\"startDate\":\"2026-11-02\",\"tokens\":10}]}" })
        {
            using var usage = JsonDocument.Parse(tokens);
            var value = CodexClient.Parse(account.RootElement, mixedLimits.RootElement, usage.RootElement);
            check(value.Windows.Count == 1 && value.Windows[0].Used == 25, "Malformed optional token data and buckets do not discard valid quota");
            check(value.Tokens.All(day => day.Date == "2026-11-02" && day.Tokens == 10), "Only validated token buckets are retained");
        }
    }
}
