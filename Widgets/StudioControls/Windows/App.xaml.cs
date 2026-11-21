using Microsoft.UI.Xaml;

namespace VibeKit.StudioControls.Windows;

public partial class App : Application
{
    private Window? window;
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            Diagnostics.Record("ui.unhandled", args.Exception);
            if (UiSmoke.Enabled)
            {
                UiSmoke.Fail(args.Exception);
                args.Handled = true;
                Exit();
            }
        };
    }
    internal void RequestMemoryShutdown()
    {
        if (window is { } mainWindow) mainWindow.Close();
        else Exit();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var main = new MainWindow();
        window = main;
        window.Activate();
        if (UiSmoke.Enabled) main.RunUiSmoke();
    }
}
