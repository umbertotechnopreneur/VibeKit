namespace OpenAIUsage;

/// <summary>Coalesces edits, serializes disk writes, and drains the latest revision on explicit exit.</summary>
public sealed class SettingsSaveQueue(Action<WidgetSettings> persist, TimeSpan? debounce = null)
{
    private readonly object gate = new();
    private readonly TimeSpan delay = debounce ?? TimeSpan.FromMilliseconds(300);
    private WidgetSettings? pending;
    private Task? worker;
    private CancellationTokenSource? waiting;
    private Exception? failure;
    private bool flushRequested;

    // Raised on the worker thread; native clients marshal feedback to their dispatcher.
    public event Action<Exception?>? Saved;

    public void Queue(WidgetSettings settings)
    {
        settings.Validate();
        lock (gate)
        {
            pending = settings.Copy();
            failure = null;
            waiting?.Cancel();
            worker ??= Task.Run(DrainAsync);
        }
    }

    public async Task FlushAsync()
    {
        Task? active;
        lock (gate)
        {
            // Explicit exit retries a failed revision; failures never start a retry loop.
            failure = null;
            flushRequested = true;
            waiting?.Cancel();
            if (pending is not null) worker ??= Task.Run(DrainAsync);
            active = worker;
        }
        while (active is not null)
        {
            await active;
            lock (gate) active = worker;
        }
        lock (gate)
        {
            flushRequested = false;
            if (failure is not null) throw new IOException("Could not save settings; the app remains open.", failure);
        }
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            CancellationTokenSource pause;
            bool flush;
            lock (gate)
            {
                pause = waiting = new();
                flush = flushRequested;
            }
            if (!flush)
            {
                try { await Task.Delay(delay, pause.Token); }
                catch (OperationCanceledException) { }
            }
            WidgetSettings snapshot;
            lock (gate)
            {
                bool editedWhileWaiting = pause.IsCancellationRequested && !flushRequested;
                if (waiting == pause) waiting = null;
                pause.Dispose();
                if (editedWhileWaiting) continue;
                if (pending is null) { worker = null; return; }
                snapshot = pending;
                pending = null;
            }
            Exception? error = null;
            try { persist(snapshot); }
            catch (Exception caught) { error = caught; }
            bool stop;
            lock (gate)
            {
                failure = error;
                stop = pending is null;
                if (stop)
                {
                    if (error is not null) pending = snapshot;
                    worker = null;
                }
            }
            PublishResult(error);
            if (stop) return;
        }
    }

    private void PublishResult(Exception? error)
    {
        foreach (var observer in Saved?.GetInvocationList() ?? [])
        {
            try { ((Action<Exception?>)observer)(error); }
            catch (Exception)
            {
                // UI feedback is best-effort. A broken observer must not strand a newer
                // pending revision, suppress other observers, or poison the next flush.
            }
        }
    }
}
