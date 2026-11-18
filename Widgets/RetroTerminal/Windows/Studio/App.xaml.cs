using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Serilog;
using System.Threading;
using Tools.Widgets;

namespace RetroStudio;
public partial class App : Application
{
    private MainWindow? window;
    private EventWaitHandle? activationRequested;
    private RegisteredWaitHandle? activationWait;

    [STAThread] private static void Main()
    {
        using var activation = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\VibeWare.RetroTerminal.Activate");
        using var singleton = new Mutex(false, @"Local\VibeWare.RetroTerminal", out bool created);
        if (!created)
        {
            activation.Set();
            return;
        }
        using var logger = VibeWareDiagnostics.CreateLogger("RetroTerminal");
        Log.Logger = logger;
        Log.Information("Retro Terminal starting Version={Version}", typeof(App).Assembly.GetName().Version);
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Start(_ =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                new App { activationRequested = activation };
            });
        }
        finally { Log.Information("Retro Terminal stopped"); }
    }

    public App()
    {
        UnhandledException += (_, args) =>
            Log.Error("Unhandled UI error Type={Type} HResult={HResult}", args.Exception.GetType().Name, args.Exception.HResult);
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (window is not null)
        {
            window.RestoreWindow();
            return;
        }
        window = new MainWindow();
        var dispatcher = window.DispatcherQueue;
        if (activationRequested is not null)
            activationWait = ThreadPool.RegisterWaitForSingleObject(activationRequested,
                (_, _) => dispatcher.TryEnqueue(() => window?.RestoreWindow()), null, Timeout.Infinite, executeOnlyOnce: false);
        window.Closed += (_, _) =>
        {
            window = null;
            activationWait?.Unregister(null);
        };
        window.Activate();
    }
}
