using OpenAIUsage;

internal static class SettingsChecks
{
    internal static async Task RunAsync(Action<bool, string> check, string directory)
    {
        var storage = new Storage(directory);
        File.WriteAllText(storage.SettingsPath, "{\"timeZoneId\":\"UTC\"}");
        var old = storage.LoadSettings();
        check(old.LabelPosition == "above" && old.ShowAvailableResets && old.ShowCredits && old.ShowEstimatedTime &&
            old.AlertSound && old.CreditsAlertThreshold is null, "Existing settings receive compatible presentation defaults");
        check(old.DockPosition is null, "Existing profiles open as a floating widget");
        storage.SaveSettings(new WidgetSettings
        {
            TimeZoneId = "UTC", LabelPosition = "hidden", ShowAvailableResets = false, ShowEstimatedTime = false,
            AlertSound = false, CreditsAlertThreshold = 0, DockPosition = "bottom",
            HiddenFields = ["credits-percentage"], FieldOrder = ["credits-percentage", "credits"]
        });
        var savedPreferences = storage.LoadSettings();
        check(savedPreferences.LabelPosition == "hidden" && !savedPreferences.ShowAvailableResets && savedPreferences.ShowCredits &&
            !savedPreferences.ShowEstimatedTime && !savedPreferences.AlertSound && savedPreferences.CreditsAlertThreshold == 0,
            "Independent metrics, hidden labels and the optional credits threshold persist");
        check(savedPreferences.Copy().DockPosition == "bottom", "Docking survives persistence and editing");
        check(savedPreferences.ShowCredits && savedPreferences.HiddenFields.SequenceEqual(["credits-percentage"]) &&
            savedPreferences.FieldOrder.SequenceEqual(["credits-percentage", "credits"]),
            "Credits percentage visibility and order persist independently of the balance");
        foreach (string invalid in new[] { "{broken", "null", "{\"timeZoneId\":\"Invalid/Fixture\"}",
            "{\"labelPosition\":\"side\"}", "{\"dockPosition\":\"side\"}", "{\"creditsAlertThreshold\":-1}",
            new string('x', Storage.MaximumSettingsBytes + 1) })
        {
            File.WriteAllText(storage.SettingsPath, invalid);
            var editable = storage.LoadSettingsForEditing();
            check(editable.RecoveryMessage is not null && File.ReadAllText(storage.SettingsPath) == invalid,
                "Invalid settings remain untouched while defaults are offered for editing");
            storage.SaveSettings(new WidgetSettings { TimeZoneId = "UTC" });
            check(storage.LoadSettings().TimeZoneId == "UTC" &&
                Directory.GetFiles(directory, "settings.json.invalid-*").Any(path => File.ReadAllText(path) == invalid),
                "Explicit correction preserves the original settings file");
        }
        var saved = new List<WidgetSettings>();
        var writer = new SettingsSaveQueue(settings => saved.Add(settings), TimeSpan.FromMinutes(1));
        var latest = new WidgetSettings { TimeZoneId = "UTC" };
        for (int revision = 0; revision < 20; revision++)
        {
            latest.CodexPath = "fixture-" + revision;
            writer.Queue(latest);
        }
        latest.CodexPath = "mutated-after-queue";
        await writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
        check(saved.Count == 1 && saved[0].CodexPath == "fixture-19", "Rapid edits coalesce and queued snapshots are immutable");

        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int attempts = 0, concurrent = 0, peak = 0, notifications = 0;
        saved.Clear();
        writer = new SettingsSaveQueue(settings =>
        {
            peak = Math.Max(peak, Interlocked.Increment(ref concurrent));
            if (Interlocked.Increment(ref attempts) == 1)
            {
                started.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Fixture writer was not released");
            }
            saved.Add(settings);
            Interlocked.Decrement(ref concurrent);
        }, TimeSpan.Zero);
        writer.Saved += _ => throw new InvalidOperationException("Fixture UI observer failure");
        writer.Saved += _ => notifications++;
        writer.Queue(new() { CodexPath = "first", TimeZoneId = "UTC" });
        check(await Task.Run(() => started.Wait(TimeSpan.FromSeconds(5))), "Synthetic writer starts");
        writer.Queue(new() { CodexPath = "latest", TimeZoneId = "UTC" });
        var closing = writer.FlushAsync();
        check(!closing.IsCompleted, "Exit waits for the current settings write");
        release.Set();
        await closing.WaitAsync(TimeSpan.FromSeconds(5));
        check(saved.Count == 2 && saved[^1].CodexPath == "latest" && peak == 1,
            "Exit drains the latest revision with only one writer");
        check(notifications == 2, "A throwing observer cannot suppress other observers or strand pending edits");
        writer.Queue(new() { CodexPath = "after-observer-error", TimeZoneId = "UTC" });
        await writer.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
        check(saved[^1].CodexPath == "after-observer-error", "Future edits still save after an observer failure");

        bool fail = true;
        attempts = 0;
        writer = new SettingsSaveQueue(_ => { attempts++; if (fail) throw new IOException("Fixture disk failure"); }, TimeSpan.Zero);
        writer.Queue(new() { TimeZoneId = "UTC" });
        bool rejected = false;
        try { await writer.FlushAsync(); } catch (IOException) { rejected = true; }
        check(rejected && attempts == 1, "A failed save prevents exit without an automatic retry loop");
        fail = false;
        await writer.FlushAsync();
        check(attempts == 2, "Explicit exit retries the unsaved revision");

        var lines = new BoundedTextReader(new StringReader("one\r\ntwo\nlast"), 4);
        check(await lines.ReadLineAsync() == "one" && await lines.ReadLineAsync() == "two" &&
            await lines.ReadLineAsync() == "last" && await lines.ReadLineAsync() is null,
            "Bounded framing retains successive lines and a final unterminated line");
        rejected = false;
        try { await new BoundedTextReader(new StringReader(new string('x', 8192)), 16).ReadLineAsync(); }
        catch (InvalidDataException) { rejected = true; }
        check(rejected, "Oversized unterminated protocol lines are rejected before accumulation");
        rejected = false;
        try { await BoundedTextReader.ReadToEndAsync(new StringReader(new string('x', 8192)), 16); }
        catch (InvalidDataException) { rejected = true; }
        check(rejected, "Oversized settings requests are rejected before parsing");
    }
}
