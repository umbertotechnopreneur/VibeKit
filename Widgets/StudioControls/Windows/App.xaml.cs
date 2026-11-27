using Microsoft.UI.Xaml;
using System.ComponentModel;
using Tools.Widgets;

namespace VibeKit.StudioControls.Windows;

public partial class App : Application
{
    private Window? window;
    private WidgetTrayIcon? tray;
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
        if (UiSmoke.Enabled)
        {
            main.RunUiSmoke();
            return;
        }

        var menu = new WidgetTrayMenu(main, items =>
        {
            items.Add("Open Studio Controls", main.ShowFromTray);
            items.Add("About", main.ShowAbout);
            items.Separator();
            items.Add("Quit", main.Close);
        });
        try
        {
            tray = new WidgetTrayIcon(main, "Studio Controls", MainWindow.IconPath, menu, main.ShowFromTray);
            tray.Show();
            main.Closed += (_, _) => tray?.Dispose();
        }
        catch (Win32Exception error)
        {
            if (tray is { } icon) icon.Dispose();
            else menu.Dispose();
            tray = null;
            Diagnostics.Record("tray", error);
        }
    }
}
