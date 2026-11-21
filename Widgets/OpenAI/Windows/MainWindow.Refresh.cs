namespace OpenAIUsage.Windows;

public sealed partial class MainWindow
{
    private async Task RefreshAsync()
    {
        if (closed) return;
        if (busy)
        {
            refreshPending = true;
            return;
        }
        busy = true;
        try
        {
            // The service also persists quota history; keep its process and disk work off the UI thread.
            var currentSettings = settings.Copy();
            var next = await Task.Run(() => new UsageService(storage).RefreshAsync(currentSettings, lifetime.Token));
            if (closed) return;
            if(state.Connected!=next.Connected || state.Status!=next.Status)
            {
                Diagnostics.Record(next.Connected?"connection.ready":"connection.failed",next.Status);
                if(!next.Connected)tray?.NotifyError(next.Status);
            }
            state = next;
            UpdateSnapshotMetrics();
            CheckAlerts();
            Tick();
        }
        finally
        {
            busy = false;
            if (refreshPending && !closed)
            {
                refreshPending = false;
                _ = RefreshAsync();
            }
        }
    }
}
