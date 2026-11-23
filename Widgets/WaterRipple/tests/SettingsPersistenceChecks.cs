using System.Text.Json;
using WaterRipple;

internal static class SettingsPersistenceChecks
{
    internal static void Run(string directory) => RunAsync(directory).GetAwaiter().GetResult();

    private static async Task RunAsync(string directory)
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static double Strength(string path)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.GetProperty("Settings").GetProperty("Strength").GetDouble();
        }

        string path = Path.Combine(directory, "coalesced-settings.json");
        int writes = 0;
        var settings = new AppSettings();
        await using (var store = new SettingsStore(path, onSaved: () => Interlocked.Increment(ref writes),
            saveDelay: TimeSpan.FromHours(1)))
        {
            store.Attach(settings);
            for (int index = 0; index < 120; index++) settings.Strength = 1 + index / 100.0;
            Check(writes == 0 && !File.Exists(path), "A bound setter performed disk I/O before the quiet period.");
            Check(await store.FlushAsync(), "Explicit settings flush failed.");
            Check(writes == 1 && Strength(path) == 2.19, "A burst was not saved as one final snapshot.");
            settings.Strength = 7;
        }
        Check(writes == 2 && Strength(path) == 7, "Async disposal did not flush the final snapshot.");

        string preferencesPath = Path.Combine(directory, "legacy-settings.json");
        File.WriteAllText(preferencesPath, """{"Version":1,"Settings":{"TargetDisplay":"legacy-display"}}""");
        using (var store = new SettingsStore(preferencesPath))
        {
            var preferences = store.Load();
            Check(preferences.ShowGpuJokes, "Older settings must load the default for GPU jokes.");
            Check(preferences.TargetIndex == 0, "Older settings must keep applying ripples to wallpaper and windows.");
            Check(preferences.TargetDisplay == "legacy-display" && preferences.TargetDisplays is null,
                "An older monitor choice must remain distinguishable from an explicit empty selection.");
            store.Attach(preferences);
            preferences.ShowGpuJokes = false;
            preferences.TargetIndex = 2;
            string[] monitorIds = ["monitor-b", "monitor-a", "MONITOR-B"];
            preferences.TargetDisplays = monitorIds;
            monitorIds[1] = "unselected-monitor";
        }
        using (var store = new SettingsStore(preferencesPath))
        {
            var preferences = store.Load();
            Check(!preferences.ShowGpuJokes, "GPU joke opt-out must survive a restart.");
            Check(preferences.TargetIndex == 2, "The ripple target must survive a restart.");
            Check(preferences.TargetDisplays?.SequenceEqual(["monitor-a", "monitor-b"]) == true,
                "Multiple monitor ids must persist as a case-insensitive snapshot independent of input mutations.");
            store.Attach(preferences);
            preferences.TargetDisplays = [];
        }
        using (var store = new SettingsStore(preferencesPath))
        {
            Check(store.Load().TargetDisplays is { Count: 0 },
                "Turning all monitors off must survive a restart without restoring the default selection.");
        }

        // Hold the worker after its first commit while the UI delivers more edits.
        using var firstWrite = new ManualResetEventSlim();
        using var releaseWriter = new ManualResetEventSlim();
        writes = 0;
        path = Path.Combine(directory, "serialized-settings.json");
        await using (var store = new SettingsStore(path, onSaved: () =>
        {
            if (Interlocked.Increment(ref writes) != 1) return;
            firstWrite.Set();
            if (!releaseWriter.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Writer gate timed out.");
        }, saveDelay: TimeSpan.FromHours(1)))
        {
            store.Attach(settings);
            settings.Strength = 3;
            Task<bool> saving = store.FlushAsync();
            Check(firstWrite.Wait(TimeSpan.FromSeconds(5)), "Background writer did not start.");
            try
            {
                for (int index = 0; index < 120; index++) settings.Strength = 4 + index / 100.0;
                Check(!saving.IsCompleted && writes == 1, "Writers overlapped or the flush returned before completion.");
            }
            finally { releaseWriter.Set(); }
            Check(await saving && writes == 2 && Math.Abs(Strength(path) - 5.19) < 1e-10,
                "The in-flight writer did not serialize the latest pending snapshot.");
        }

        string blocker = Path.Combine(directory, "settings-blocker");
        File.WriteAllText(blocker, "not a directory");
        int errors = 0;
        await using (var store = new SettingsStore(Path.Combine(blocker, "settings.json"),
            onError: _ => Interlocked.Increment(ref errors), saveDelay: TimeSpan.FromHours(1)))
        {
            store.Attach(settings);
            settings.Strength = 11;
            Check(!await store.FlushAsync() && errors == 1 && store.LastError is not null,
                "A background save failure was not reported.");
            File.Delete(blocker);
            Check(await store.FlushAsync() && store.LastError is null && Strength(store.FilePath) == 11,
                "An explicit retry did not save the failed snapshot or clear the error.");
        }

        // Reserve the task without executing it: discard must also handle the startup gap.
        var queuedWriter = new QueuedScheduler();
        path = Path.Combine(directory, "discard-before-start.json");
        await using (var store = new SettingsStore(path, saveDelay: TimeSpan.FromHours(1), writerScheduler: queuedWriter))
        {
            store.Attach(settings);
            settings.Strength = 17;
            Task<bool> saving = store.FlushAsync();
            Task discarding = store.DiscardAsync().AsTask();
            Check(!saving.IsCompleted && !discarding.IsCompleted, "The test did not hold the queued writer.");
            queuedWriter.RunNext();
            await discarding;
            Check(await saving && !File.Exists(path), "Discard before worker startup serialized an empty snapshot.");
        }

        using var committed = new ManualResetEventSlim();
        using var finishCommit = new ManualResetEventSlim();
        writes = 0;
        path = Path.Combine(directory, "discard-during-write.json");
        await using (var store = new SettingsStore(path, onSaved: () =>
        {
            Interlocked.Increment(ref writes);
            committed.Set();
            finishCommit.Wait(TimeSpan.FromSeconds(5));
        }, saveDelay: TimeSpan.FromHours(1)))
        {
            store.Attach(settings);
            settings.Strength = 18;
            Task<bool> saving = store.FlushAsync();
            Check(committed.Wait(TimeSpan.FromSeconds(5)), "The first commit did not start.");
            Task discarding;
            try
            {
                settings.Strength = 19;
                discarding = store.DiscardAsync().AsTask();
                Check(!discarding.IsCompleted, "Discard failed to wait for the active writer.");
            }
            finally { finishCommit.Set(); }
            await discarding;
            Check(await saving && writes == 1 && Strength(path) == 18,
                "Discard did not preserve the active commit and cancel the pending snapshot.");
        }
        Console.WriteLine("PASS async settings: coalescing, serialized writes, flush/retry, discard before startup and during writes");
    }

    private sealed class QueuedScheduler : TaskScheduler
    {
        private readonly Queue<Task> tasks = new();
        protected override IEnumerable<Task> GetScheduledTasks() => tasks.ToArray();
        protected override void QueueTask(Task task) => tasks.Enqueue(task);
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
        internal void RunNext() => TryExecuteTask(tasks.Dequeue());
    }
}
