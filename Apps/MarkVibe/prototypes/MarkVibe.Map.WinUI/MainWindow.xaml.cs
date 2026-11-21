/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; workspace picker and reproducible native graph timing
 * Human guidance: keep the OpenSilver reader separate and compare the same map
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace MarkVibe.Map.WinUI;

/// <summary>Hosts the Win2D graph and its isolated benchmark entry point.</summary>
public sealed partial class MainWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string? _workspace;
    private readonly string? _reportPath;
    private bool _started;
    private bool _closed;

    /// <summary>Sets up the native window and accepts optional workspace and benchmark paths.</summary>
    public MainWindow(IReadOnlyList<string> arguments)
    {
        InitializeComponent();
        _workspace = ReadArgument(arguments, "--workspace");
        _reportPath = ReadArgument(arguments, "--benchmark");
        AppWindow.Resize(new SizeInt32(1250, 820));
        if (_reportPath != null && int.TryParse(ReadArgument(arguments, "--viewport-width"), out var width) &&
            int.TryParse(ReadArgument(arguments, "--viewport-height"), out var height) &&
            width is >= 200 and <= 1180 && height is >= 200 and <= 680)
        {
            // Measure the same drawable area as the desktop graph, including when its inspector is open.
            Surface.Width = width;
            Surface.Height = height;
            Surface.HorizontalAlignment = HorizontalAlignment.Left;
            Surface.VerticalAlignment = VerticalAlignment.Top;
        }
        Closed += OnClosed;
    }

    /// <summary>Opens the command-line workspace once the window can present a frame.</summary>
    private async void OnLoaded(object sender, RoutedEventArgs arguments)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        if (_workspace != null)
        {
            await OpenWorkspaceAsync(_workspace);
        }
        else if (_reportPath != null)
        {
            Status.Text = "Benchmark mode requires --workspace.";
        }
    }

    /// <summary>Opens a workspace from the Windows folder picker in the interactive prototype.</summary>
    private async void OpenFolder(object sender, RoutedEventArgs arguments)
    {
        OpenButton.IsEnabled = false;
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (!_closed && folder != null)
            {
                await OpenWorkspaceAsync(folder.Path);
            }
        }
        catch (Exception error)
        {
            if (!_closed)
            {
                Status.Text = error.Message;
            }
        }
        finally
        {
            if (!_closed)
            {
                OpenButton.IsEnabled = true;
            }
        }
    }

    /// <summary>Creates the map after indexing, timing the same post-catalog work as the OpenSilver map command.</summary>
    private async Task OpenWorkspaceAsync(string directory)
    {
        var cancellation = _lifetime.Token;
        OpenButton.IsEnabled = false;
        Status.Text = "Preparing the document catalog…";

        try
        {
            var prepared = await GraphWorkspace.PrepareAsync(directory, cancellation);
            if (_reportPath != null)
            {
                // Both renderers start from a visible window with a prepared catalog.
                await Task.Delay(750, cancellation);
            }

            var opening = Stopwatch.GetTimestamp();
            var workspace = await prepared.OpenAsync(cancellation);
            cancellation.ThrowIfCancellationRequested();

            Surface.SetWorkspace(workspace);
            FitButton.IsEnabled = workspace.Map.Nodes.Count > 0;
            var summary = $"{workspace.Map.Nodes.Count} documents · {workspace.Map.Edges.Count} links";
            Status.Text = string.IsNullOrEmpty(workspace.Warning) ? summary : summary + " · " + workspace.Warning;
            if (_reportPath != null)
            {
                var measurements = await GraphBenchmark.RunAsync(Surface, opening, cancellation);
                await WriteReportAsync(workspace, measurements, cancellation);
                Close();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Closing the window cancels any outstanding catalog or benchmark work.
        }
        catch (Exception error)
        {
            if (!_closed)
            {
                Status.Text = error.Message;
            }
            if (_reportPath != null && !cancellation.IsCancellationRequested)
            {
                await File.WriteAllTextAsync(_reportPath, JsonSerializer.Serialize(new { error = error.Message }), cancellation);
                Close();
            }
        }
        finally
        {
            if (!_closed)
            {
                OpenButton.IsEnabled = true;
            }
        }
    }

    /// <summary>Writes aggregate timings and fixture dimensions for a single run.</summary>
    private Task WriteReportAsync(GraphWorkspace workspace, GraphBenchmarkResult results, CancellationToken cancellation)
    {
        var report = new
        {
            renderer = "WinUI 3 / Win2D",
            nodes = workspace.Map.Nodes.Count,
            edges = workspace.Map.Edges.Count,
            viewport = new { width = Surface.ActualWidth, height = Surface.ActualHeight },
            results
        };
        return File.WriteAllTextAsync(_reportPath!, JsonSerializer.Serialize(report,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), cancellation);
    }

    /// <summary>Centers and scales the complete visible projection.</summary>
    private void FitGraph(object sender, RoutedEventArgs arguments)
    {
        Surface.Fit();
    }

    /// <summary>Cancels outstanding work and releases the Win2D resources.</summary>
    private void OnClosed(object sender, WindowEventArgs arguments)
    {
        _closed = true;
        _lifetime.Cancel();
        Surface.Dispose();
        _lifetime.Dispose();
    }

    /// <summary>Reads an explicit option without mixing it with the command-line executable name.</summary>
    private static string? ReadArgument(IReadOnlyList<string> arguments, string name)
    {
        for (var index = 0; index + 1 < arguments.Count; index++)
        {
            if (arguments[index].Equals(name, StringComparison.Ordinal))
            {
                return arguments[index + 1];
            }
        }
        return null;
    }
}
