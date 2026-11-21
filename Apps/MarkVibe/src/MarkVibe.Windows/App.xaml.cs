/* VBWR B
 *
 * Project: MarkVibe
 * Repository: https://github.com/umbertotechnopreneur/MarkVibe
 * Creator: Umberto Giacobbi | https://umbertogiacobbi.biz
 *
 * VibeWare: Human intent, AI execution, and plenty of tokens
 * Manifesto: https://umbertogiacobbi.biz/vibeware/manifesto
 *
 * Modified with AI: OpenAI Codex; readability revision; 2026-10-11
 * Human guidance: Umberto Giacobbi; repository readability rules and MIT licensing
 * Evidence: AI-DEVELOPMENT-DISCLOSURE.md; .github/tasks/archive.md
 *
 * Copyright (c) 2026 Umberto Giacobbi
 * License: MIT (see LICENSE)
 *
 * VBWR E */
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MarkVibe.Windows;
/// <summary>Starts the desktop workspace or the installer's optional default-app setup.</summary>
public partial class App : Application
{
    private Window? _window;
    private VibeWare.MemoryProtection.ProcessMemoryWatchdog? _memoryWatchdog;

    /// <summary>Loads WinUI resources before creating the desktop shell.</summary>
    public App() => InitializeComponent();

    /// <summary>Parses startup options and opens the desktop window, reporting initialization failures.</summary>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var options = DesktopOptions.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray());
        DesktopLog.Initialize(options.DataDirectory);
        UnhandledException += (_, failure) => DesktopLog.Error("application.dispatcherFailure", failure.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, failure) =>
        {
            if (failure.ExceptionObject is Exception error)
            {
                DesktopLog.Error("application.unhandledFailure", error);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, failure) => DesktopLog.Error("application.unobservedTask", failure.Exception);
        try
        {
            if (options.ConfigureFileAssociations)
            {
                var root = new Grid();
                var setup = new Window { Title = "MarkVibe", Content = root };
                root.Loaded += async (_, _) =>
                {
                    try { await MarkdownFileAssociations.OfferAsync(options, root.XamlRoot); }
                    finally { setup.Close(); }
                };
                _window = setup;
            }
            else
            {
                var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                _memoryWatchdog = new VibeWare.MemoryProtection.ProcessMemoryWatchdog(
                    "MarkVibe", Path.Combine(options.DataDirectory, "logs"), 2_000_000_000,
                    () => dispatcher.TryEnqueue(() => _window?.Close()), showWindowsNotice: true, stopDescendants: true);
                _window = new DesktopWindow(options);
            }
            _window.Closed += OnClosed;
            _window.Activate();
        }
        catch (Exception error)
        {
            _memoryWatchdog?.Dispose();
            DesktopLog.Error("application.startupFailure", error);
            throw;
        }
    }

    /// <summary>Records normal shutdown without changing the application's exit status.</summary>
    private void OnClosed(object sender, WindowEventArgs args)
    {
        _memoryWatchdog?.Dispose();
        DesktopLog.Write("info", "application.exit");
        DesktopLog.Shutdown();
    }
}
