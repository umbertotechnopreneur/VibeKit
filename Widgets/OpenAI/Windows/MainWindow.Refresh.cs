using Microsoft.UI.Xaml.Controls;

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
            if (next.ResetVoucherCount != state.ResetVoucherCount ||
                next.ResetVoucherExpirations.Count != state.ResetVoucherExpirations.Count)
                voucherCycle.Reset();
            var previousRemaining = state.Remaining;
            state = next;
            remaining.Text=state.Remaining is {} value ? $"{value:0.#}%" : "—";
            if (state.Remaining != previousRemaining) UpdateUsageColor(animate: previousRemaining.HasValue);
            credits.Text = state.CreditsLabel;
            CheckAlerts();
            var creditDetail="Credits remaining as reported by Codex: "+state.CreditsLabel+". The displayed balance is rounded to a whole number.";
            ToolTipService.SetToolTip(credits,creditDetail);
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
