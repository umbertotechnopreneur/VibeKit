// SPDX-License-Identifier: MIT
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using Serilog;
using Tools.Widgets;

namespace VibeWare.EffectsStudio;

public sealed partial class App : Application
{
    private Studio? window;
    private RegisteredWaitHandle? listener;
    private readonly EventWaitHandle activation;
    private readonly bool startup;

    private App(EventWaitHandle activation, bool startup)
    {
        this.activation = activation;
        this.startup = startup;
        InitializeComponent();
    }

    [STAThread]
    private static void Main(string[] args)
    {
        bool startup = args.Contains("--autostart");
        using var activation = new EventWaitHandle(false, EventResetMode.AutoReset, Studio.InstanceName + ".Activate");
        using var singleton = new Mutex(true, Studio.InstanceName, out bool first);
        if (!first)
        {
            if (!startup) activation.Set();
            return;
        }
        using var logger = VibeWareDiagnostics.CreateLogger(Studio.Identifier);
        Log.Logger = logger;
        Log.Information("{Product} starting Version={Version}", Studio.Product, typeof(App).Assembly.GetName().Version);
        try
        {
            SetCurrentProcessExplicitAppUserModelID("biz.umbertogiacobbi." + Studio.Identifier);
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Start(_ =>
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                new App(activation, startup);
            });
        }
        finally { singleton.ReleaseMutex(); }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var studio = new Studio(startup);
            window = studio;
            var dispatcher = studio.DispatcherQueue;
            listener = ThreadPool.RegisterWaitForSingleObject(activation,
                (_, _) => dispatcher.TryEnqueue(studio.Reveal), null, Timeout.Infinite, false);
            studio.Closed += (_, _) =>
            {
                listener?.Unregister(null);
                window = null;
                Exit();
            };
            if (!startup || !studio.HasTray) studio.Reveal();
        }
        catch (Exception error)
        {
            Log.Error("Studio initialization failed Type={Type} HResult={HResult}", error.GetType().Name, error.HResult);
            try { Native.WidgetStop(); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) {}
            MessageBoxW(0, error.Message, Studio.Product, 0x10);
            Exit();
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(nint owner, string text, string caption, uint flags);
}
