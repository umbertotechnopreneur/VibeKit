using System;
using System.ComponentModel;
using System.IO;
using System.Security;
using Serilog;
using Tools.Widgets;

namespace WaterRipple;

internal sealed class StartupService : IDisposable
{
    internal static readonly WindowsSignIn SignIn = new("Water Ripple");
    private readonly AppState state;
    private bool updating;
    internal StartupService(AppState state) { this.state = state; state.PropertyChanged += Changed; }

    internal void Refresh() => Update(null);

    private void Changed(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(AppState.StartWithWindows) && !updating)
            Update(state.StartWithWindows);
    }

    private void Update(bool? enabled)
    {
        updating = true;
        try
        {
            if (enabled.HasValue) SignIn.SetEnabled(enabled.Value);
            var current = SignIn.Read();
            state.StartWithWindows = current.Enabled;
            state.CanManageStartup = !current.Blocked;
            state.StartupStatus = current.Blocked
                ? "Disabled in Windows Startup Apps. Re-enable it there to allow startup."
                : current.Enabled
                    ? "Water Ripple will start in the notification area and apply click ripples when you sign in."
                    : "Water Ripple will open only when you launch it.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException)
        {
            state.StartWithWindows = enabled == false;
            state.CanManageStartup = enabled.HasValue;
            state.StartupStatus = "Could not update the Windows startup setting.";
            Log.Warning("Windows startup setting unavailable Type={Type}", ex.GetType().Name);
        }
        finally { updating = false; }
    }

    public void Dispose() => state.PropertyChanged -= Changed;
}
