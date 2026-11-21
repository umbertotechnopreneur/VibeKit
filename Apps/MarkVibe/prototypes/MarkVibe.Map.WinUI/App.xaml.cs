/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; WinUI graph prototype entry point
 * Human guidance: scope the native experiment to the graph
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using Microsoft.UI.Xaml;

namespace MarkVibe.Map.WinUI;

/// <summary>Launches the standalone Windows graph preview and keeps its only window alive.</summary>
public partial class App : Application
{
    private MainWindow? _mainWindow;

    /// <summary>Loads the native control resources shared by the preview window.</summary>
    public App()
    {
        InitializeComponent();
    }

    /// <summary>Passes explicit workspace and benchmark arguments to the window.</summary>
    protected override void OnLaunched(LaunchActivatedEventArgs arguments)
    {
        _mainWindow = new MainWindow(Environment.GetCommandLineArgs().Skip(1).ToArray());
        _mainWindow.Activate();
    }
}
