/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; native reader application entry point
 * Human guidance: keep browser initialization off the application startup path
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using Microsoft.UI.Xaml;

namespace MarkVibe.Reader.WinUI;

/// <summary>Keeps the standalone reader's native window alive.</summary>
public partial class App : Application
{
    private MainWindow? _window;

    /// <summary>Loads WinUI application resources.</summary>
    public App() => InitializeComponent();

    /// <summary>Shows the native shell before initializing any web content.</summary>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
