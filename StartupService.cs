using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Serilog;

namespace WaterRipple;

// StartupTask is the source of truth; do not mirror OS policy in a JSON preference.
internal sealed class StartupService : IDisposable
{
    private readonly AppState state;
    private bool updating, disposed;
    internal StartupService(AppState state) { this.state = state; state.PropertyChanged += Changed; }
    internal Task RefreshAsync() => UpdateAsync(null);
    private async void Changed(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(AppState.StartWithWindows) && !updating && !disposed)
            await UpdateAsync(state.StartWithWindows);
    }
    private async Task UpdateAsync(bool? enabled)
    {
        if (updating || disposed) return;
        updating = true;
        state.CanManageStartup = false;
        try
        {
            var task = await StartupTask.GetAsync("WaterRippleStartup");
            var current = task.State;
            if (enabled == true && current == StartupTaskState.Disabled) current = await task.RequestEnableAsync();
            else if (enabled == false && current == StartupTaskState.Enabled) { task.Disable(); current = task.State; }
            if (disposed) return;
            state.StartWithWindows = current == StartupTaskState.Enabled || current == StartupTaskState.EnabledByPolicy;
            state.CanManageStartup = current == StartupTaskState.Disabled || current == StartupTaskState.Enabled;
            state.StartupStatus = current switch
            {
                StartupTaskState.DisabledByUser => "Disabled in Windows Startup Apps. Re-enable it there to allow startup.",
                StartupTaskState.DisabledByPolicy => "Startup is disabled by your Windows administrator.",
                StartupTaskState.EnabledByPolicy => "Startup is enabled by your Windows administrator.",
                StartupTaskState.Enabled => "Water Ripple will start in the notification area and apply click ripples when you sign in.",
                _ => "Water Ripple will open only when you launch it."
            };
            if (enabled.HasValue) Log.Information("Windows startup preference changed State={State}", current);
        }
        catch (Exception ex)
        {
            if (!disposed)
            {
                state.StartWithWindows = false;
                state.CanManageStartup = false;
                state.StartupStatus = "Install the MSIX package to manage Windows startup.";
            }
            Log.Warning(ex, "Windows startup task is unavailable");
        }
        finally { updating = false; }
    }
    public void Dispose() { disposed = true; state.PropertyChanged -= Changed; }
}
