using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;

namespace VibeKit.StudioControls.Windows;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.SequenceEqual(new[] { "--verify" })) return WindowsVerification.Run();
        bool smokeRequested = args.SequenceEqual(new[] { "--ui-smoke" });
        try
        {
            if (smokeRequested) UiSmoke.Prepare();
            Diagnostics.Initialize();
            string user = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value
                ?? throw new InvalidOperationException("The current Windows user could not be identified.");
            string instanceName = @"Local\VibeKit.StudioControls." + user + (smokeRequested ? ".smoke." + Guid.NewGuid().ToString("N") : "");
            using var instance = new Mutex(true, instanceName, out bool firstInstance);
            if (!firstInstance) return 0;
            SetCurrentProcessExplicitAppUserModelID("biz.umbertogiacobbi.StudioControls");
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(initialization =>
            {
                SynchronizationContext.SetSynchronizationContext(
                    new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
                _ = new App();
            });
            return !smokeRequested || UiSmoke.Passed ? 0 : 1;
        }
        catch (Exception error)
        {
            Diagnostics.Record("startup", error);
            if (smokeRequested) UiSmoke.Fail(error);
            else MessageBox(0, "Studio Controls could not start. Diagnostic logs are in " + Diagnostics.DirectoryPath,
                    "Studio Controls", 0x10);
            return 1;
        }
        finally { if (smokeRequested) UiSmoke.Cleanup(); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint owner, string text, string caption, uint type);
}
