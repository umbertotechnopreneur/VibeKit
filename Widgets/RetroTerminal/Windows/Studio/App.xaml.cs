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
    private bool startAtSignIn;
    internal static readonly WindowsSignIn SignIn = new("Retro Terminal");

    [STAThread] private static void Main()
    {
        var arguments = Environment.GetCommandLineArgs();
        if (Array.IndexOf(arguments, WindowsSignIn.RemoveArgument) >= 0)
        {
            SignIn.Remove();
            return;
        }
        bool atSignIn = Array.IndexOf(arguments, WindowsSignIn.StartArgument) >= 0;
        using var activation = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\VibeWare.RetroTerminal.Activate");
        using var singleton = new Mutex(false, @"Local\VibeWare.RetroTerminal", out bool created);
        if (!created)
        {
            if (!atSignIn) activation.Set();
            return;
        }
        using var logger = VibeWareDiagnostics.CreateLogger("RetroTerminal");
        DispatcherQueue? dispatcher = null;
        using var memoryWatchdog = new VibeWare.MemoryProtection.ProcessMemoryWatchdog(
            "Retro Terminal", VibeWareDiagnostics.GetLogDirectory("RetroTerminal"), 1_000_000_000,
            () => Volatile.Read(ref dispatcher)?.TryEnqueue(() =>
            {
                if (Current is App app && app.window is { } mainWindow) mainWindow.RequestMemoryShutdown();
                else Current.Exit();
            }), showWindowsNotice: true);
        Log.Logger = logger;
        Log.Information("Retro Terminal starting Version={Version}", typeof(App).Assembly.GetName().Version);
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Start(_ =>
            {
                var queue = DispatcherQueue.GetForCurrentThread();
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(queue));
                new App { activationRequested = activation, startAtSignIn = atSignIn };
                Volatile.Write(ref dispatcher, queue);
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
        if (!startAtSignIn || !window.HideToTray()) window.Activate();
    }
}
