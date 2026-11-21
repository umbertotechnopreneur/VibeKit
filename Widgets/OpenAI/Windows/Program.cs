using Microsoft.UI.Xaml;
using Serilog.Events;
using Tools.Widgets;

namespace OpenAIUsage.Windows;

public static class Program
{
    internal const string AppId = "OpenAIUsage";

    [STAThread]
    public static int Main(string[] args)
    {
        using var logger = VibeWareDiagnostics.CreateLogger(AppId);
        Diagnostics.UseRecorder((kind, bytes) =>
        {
            LogEventLevel level = kind.EndsWith(".failed", StringComparison.Ordinal) ||
                kind.EndsWith(".failure", StringComparison.Ordinal) || kind == "ui.unhandled"
                ? LogEventLevel.Warning : LogEventLevel.Information;
            logger.Write(level, "Event {Kind} Bytes={Bytes}", kind, bytes);
        }, VibeWareDiagnostics.GetLogDirectory(AppId));
        if (args.FirstOrDefault() == "--memory-guard") return WindowsMemoryGuard.Run(args);
        try
        {
            var userId = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value
                ?? throw new InvalidOperationException("Cannot identify the current user.");
            using var instance = new System.Threading.Mutex(true, $@"Global\OpenAIUsage-{userId}", out var createdNew);
            if (!createdNew) return 0;
            uint packageNameLength = 0;
            const int noPackage = 15700;
            if (GetCurrentPackageFullName(ref packageNameLength, 0) == noPackage)
                SetCurrentProcessExplicitAppUserModelID("biz.umbertogiacobbi.OpenAIUsage");
            logger.Information("Application started Version={Version}", typeof(Program).Assembly.GetName().Version);
            using var guard = WindowsMemoryGuard.Start();
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(initialization =>
            {
                SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
                _ = new App();
            });
            return 0;
        }
        catch (Exception ex)
        {
            Diagnostics.Record("startup.failed", ex.GetType().Name + ": " + ex.Message);
            WindowsMemoryGuard.ShowNotice("OpenAI Usage could not start with memory protection.\nDetails are in the local diagnostic logs.");
            return 1;
        }
    }
    [System.Runtime.InteropServices.DllImport("shell32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, nint packageFullName);
}
