using System.Text.Json;
using WaterRipple;

internal static class CloseSequenceChecks
{
    internal static void Run(string directory) => RunAsync(directory).GetAwaiter().GetResult();

    private static async Task RunAsync(string directory)
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static double ReadStrength(string path)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.GetProperty("Settings").GetProperty("Strength").GetDouble();
        }
        using var writing = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        bool stopped = false, closed = false;
        int writes = 0;
        string path = Path.Combine(directory, "close-settings.json");
        await using (var store = new SettingsStore(path, onSaved: () =>
        {
            if (Interlocked.Increment(ref writes) == 1)
            {
                writing.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            }
        }, saveDelay: TimeSpan.FromHours(1)))
        {
            var settings = new AppSettings();
            store.Attach(settings);
            settings.Strength = 10;
            var sequence = new CloseSequence(() => stopped = true, store.FlushAsync,
                () => closed = true, () => throw new Exception("Unexpected close failure."));
            Task<bool> closing = sequence.TryCloseAsync();
            try
            {
                Check(writing.Wait(TimeSpan.FromSeconds(5)), "Close never started the writer.");
                Check(stopped && !closed && sequence.IsPending, "Window closed before saving or effect remained active.");
                Check(!await sequence.TryCloseAsync(), "A second close started during the pending save.");
                settings.Strength = 13;
            }
            finally { release.Set(); }
            Check(await closing && closed && ReadStrength(path) == 13,
                "Close did not wait for the latest settings supplied during the save.");
        }

        string blocker = Path.Combine(directory, "close-blocker");
        File.WriteAllText(blocker, "not a directory");
        closed = false;
        int failures = 0;
        await using (var store = new SettingsStore(Path.Combine(blocker, "settings.json"), saveDelay: TimeSpan.FromHours(1)))
        {
            var settings = new AppSettings();
            store.Attach(settings);
            settings.Strength = 12;
            var sequence = new CloseSequence(() => stopped = true, store.FlushAsync,
                () => closed = true, () => failures++);
            Check(!await sequence.TryCloseAsync() && !closed && stopped && failures == 1,
                "A failed save closed the window instead of leaving it available for retry.");
            File.Delete(blocker);
            settings.Strength = 14;
            Check(await sequence.TryCloseAsync() && closed && ReadStrength(store.FilePath) == 14,
                "An explicit close retry lost the latest edited value.");
        }
        Console.WriteLine("PASS close ordering: immediate effect stop, awaited latest save, failure keeps window open, explicit retry");
    }
}
