using OpenAIUsage;
using System.Text.Json;

internal static class StorageChecks
{
    internal static void Run(Action<bool, string> check, string directory, long now)
    {
        var store = new Storage(directory);
        var window = new QuotaWindow("codex:10080", "Weekly", 10, 10080, now + 86400);
        store.Record("fixture", window, "pro", now - 60);
        string path = Directory.GetFiles(directory, "*.history.json").Single();
        string valid = JsonSerializer.Serialize(new[] { new Sample(now - 60, 5, window.ResetsAt, "pro") }, JsonOptions.Default);
        foreach (string invalid in new[] {
            "null", "[null]", "{}", "[{}]", "[] trailing",
            valid.Replace("\"used\": 5", "\"used\": -1"), valid.Replace("\"used\": 5", "\"used\": 101"),
            valid.Replace("\"used\": 5", "\"used\": null"), valid.Replace("\"used\": 5,", ""),
            valid.Replace("\"plan\": \"pro\"", "\"plan\": \"plus\""),
            valid.Replace($"\"reset\": {window.ResetsAt}", "\"reset\": \"bad\""),
            "[" + valid.Trim()[1..^1] + "," + valid.Trim()[1..^1] + "]"
        })
        {
            File.WriteAllText(path, invalid);
            bool rejected = false;
            try { store.Record("fixture", window, "pro", now); }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException) { rejected = true; }
            check(rejected && File.ReadAllText(path) == invalid, "Invalid history is rejected without altering its source");
        }
        string corrupt = File.ReadAllText(path);
        var account = new AccountUsage("fixture", "pro", [window], null, false, null, false, [], null);
        var state = new UsageService(store).Build(account, new WidgetSettings { TimeZoneId = "UTC" }, DateTimeOffset.FromUnixTimeSeconds(now));
        check(state.Connected && state.Remaining == 90 && state.Status.Contains("Local history unavailable") && File.ReadAllText(path) == corrupt,
            "Invalid history disables forecasting without discarding live account data");
        File.WriteAllText(path, valid);
        var history = store.Record("fixture", window, "pro", now);
        check(history.Count == 2 && history[0].Used == 5, "Existing history format remains readable and appendable");
        string before = File.ReadAllText(path);
        using (var locked = new FileStream(Directory.GetFiles(directory, "*.lock").Single(), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            bool rejected = false;
            try { store.Record("fixture", window, "pro", now + 60); }
            catch (IOException) { rejected = true; }
            check(rejected && File.ReadAllText(path) == before, "Busy history remains unchanged");
        }
        File.WriteAllText(path, "[]");
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write)) stream.SetLength(8 * 1024 * 1024 + 1);
        bool oversized = false;
        try { store.Record("fixture", window, "pro", now); }
        catch (InvalidDataException) { oversized = true; }
        check(oversized && new FileInfo(path).Length == 8 * 1024 * 1024 + 1, "Oversized history is bounded and preserved");
        File.WriteAllText(path, valid);
        bool badInput = false;
        try { store.Record("fixture", window with { Used = double.NaN }, "pro", now); }
        catch (InvalidDataException) { badInput = true; }
        check(badInput && File.ReadAllText(path) == valid, "Invalid new observations do not damage valid history");
    }
}
