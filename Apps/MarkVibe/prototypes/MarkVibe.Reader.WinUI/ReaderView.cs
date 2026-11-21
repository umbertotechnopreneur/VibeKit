/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; local WebView2 reader transport
 * Human guidance: keep document navigation and file access in WinUI and C#
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using System.Text.Json;
using MarkVibe.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.System;

namespace MarkVibe.Reader.WinUI;

/// <summary>Loads the bundled renderer and allows it to request only images from its current document.</summary>
internal sealed class ReaderView(string dataDirectory, bool dark, CancellationToken lifetime) : IDisposable
{
    private const string Host = "markvibe-reader.invalid";
    private const string Origin = "https://" + Host;
    private const string EntryPage = Origin + "/index.html";
    private const string DiagramPage = Origin + "/js/diagram-frame.html";
    private static readonly TimeSpan RendererTimeout = TimeSpan.FromSeconds(20);
    private readonly string _capability = Guid.NewGuid().ToString("N");
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, CancellationTokenSource> _assetReads = new(StringComparer.Ordinal);
    private readonly HashSet<string> _imagePaths = new(StringComparer.Ordinal);
    private TaskCompletionSource? _rendered;
    private Task? _initializing;
    private ReaderWorkspace? _workspace;
    private long _renderId;
    private string _currentPath = "";
    private bool _disposed;

    public WebView2 Control { get; } = new() { Visibility = Visibility.Collapsed };
    public bool IsReady => !_disposed && _initializing?.IsCompletedSuccessfully == true;
    public event Action<string, string>? Navigate;
    public event Action<string>? Status;

    /// <summary>Shares the first browser initialization across superseding document selections.</summary>
    public Task WaitUntilReadyAsync(CancellationToken token) => (_initializing ??= InitializeAsync()).WaitAsync(token);

    /// <summary>Displays and acknowledges one parsed document through the local renderer.</summary>
    public async Task ShowAsync(ReaderWorkspace workspace, WorkspaceEntry entry, MarkdownModel document, string anchor, CancellationToken token)
    {
        CancelAssets();
        _workspace = workspace;
        _currentPath = entry.Path;
        _imagePaths.Clear();
        _imagePaths.UnionWith(document.Elements.Where(element => element.Kind == "image" && element.AssetPath.Length > 0).Select(element => element.AssetPath));
        Control.Visibility = Visibility.Visible;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _rendered = completed;
        Send(new { kind = "render", renderId = ++_renderId, workspace = workspace.Info, document, anchor, dark });
        await completed.Task.WaitAsync(RendererTimeout, token);
    }

    /// <summary>Releases document resources and hides the renderer when the native shell has no document.</summary>
    public void Clear()
    {
        CancelAssets();
        _workspace = null;
        _imagePaths.Clear();
        _currentPath = "";
        ++_renderId;
        _rendered = null;
        Control.Visibility = Visibility.Collapsed;
        if (IsReady)
        {
            Send(new { kind = "clear" });
        }
    }

    /// <summary>Creates the isolated prototype profile and starts the source-built entry page.</summary>
    private async Task InitializeAsync()
    {
        var assets = Path.Combine(AppContext.BaseDirectory, "reader");
        if (!File.Exists(Path.Combine(assets, "index.html")))
        {
            throw new IOException("The bundled reader is missing. Rebuild the prototype.");
        }

        var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, Path.Combine(dataDirectory, "WebView2"), new CoreWebView2EnvironmentOptions());
        lifetime.ThrowIfCancellationRequested();
        // The empty control needs a layout slot before WinUI initializes its composition controller.
        Control.Visibility = Visibility.Visible;
        await Control.EnsureCoreWebView2Async(environment);
        lifetime.ThrowIfCancellationRequested();
        var core = Control.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping(Host, assets, CoreWebView2HostResourceAccessKind.Deny);
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.NavigationStarting += (_, args) => args.Cancel = args.Uri != EntryPage;
        core.FrameNavigationStarting += (_, args) => args.Cancel = args.Uri is not (DiagramPage or "about:blank");
        core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (_, args) => args.Cancel = true;
        core.WebMessageReceived += Receive;
        core.NewWindowRequested += OpenExternal;
        core.NavigationCompleted += (_, args) =>
        {
            if (!args.IsSuccess && !_ready.Task.IsCompleted)
            {
                _ready.TrySetException(new InvalidOperationException("The local reader page could not be loaded."));
            }
        };
        await core.AddScriptToExecuteOnDocumentCreatedAsync(
            "if (window === window.top) window.markvibeDesktop = " + Wire.Encode(new { version = 1, capability = _capability }) + ";");
        lifetime.ThrowIfCancellationRequested();
        core.Navigate(EntryPage);
        await _ready.Task.WaitAsync(RendererTimeout, lifetime);
    }

    /// <summary>Accepts bounded renderer messages from the currently loaded local entry page.</summary>
    private async void Receive(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (_disposed || args.Source != EntryPage || sender.Source != EntryPage || args.WebMessageAsJson.Length > 8192)
        {
            return;
        }

        try
        {
            using var message = JsonDocument.Parse(args.WebMessageAsJson, new() { MaxDepth = 6 });
            var value = message.RootElement;
            if (value.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var kind = Text(value, "kind");
            if (kind == "ready")
            {
                _ready.TrySetResult();
            }
            else if (kind.Length > 0)
            {
                HandleRenderEvent(kind, value);
            }
            else if (Text(value, "capability") == _capability &&
                value.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Number &&
                version.TryGetInt32(out var protocol) && protocol == 1)
            {
                await HandleAssetRequestAsync(value);
            }
        }
        catch (JsonException)
        {
            // A malformed message has no authority or active request to complete.
        }
    }

    /// <summary>Routes callbacks only for the document currently displayed by the native shell.</summary>
    private void HandleRenderEvent(string kind, JsonElement value)
    {
        if (_workspace == null || !value.TryGetProperty("renderId", out var id) || id.ValueKind != JsonValueKind.Number ||
            !id.TryGetInt64(out var renderId) || renderId != _renderId)
        {
            return;
        }

        switch (kind)
        {
            case "rendered":
                _rendered?.TrySetResult();
                break;
            case "error":
                _rendered?.TrySetException(new InvalidOperationException("The Markdown renderer could not show this document."));
                break;
            case "navigate":
                var path = Text(value, "path");
                var anchor = Text(value, "anchor");
                if (path.Length > 0 && anchor.Length <= 256)
                {
                    Navigate?.Invoke(path, anchor);
                }
                break;
            case "status":
                var message = Text(value, "message");
                if (message.Length is > 0 and <= 300)
                {
                    Status?.Invoke(message);
                }
                break;
        }
    }

    /// <summary>Services the shared image loader's versioned read and cancellation operations.</summary>
    private async Task HandleAssetRequestAsync(JsonElement value)
    {
        var id = Text(value, "id");
        var workspace = _workspace;
        if (id.Length is 0 or > 64 || workspace == null || !value.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var operation = Text(value, "operation");
        if (operation == "cancel")
        {
            if (_assetReads.TryGetValue(Text(args, "requestId"), out var pending))
            {
                pending.Cancel();
            }
            return;
        }

        var path = Text(args, "path");
        if (operation != "workspace.read" || Text(args, "workspaceId") != workspace.Info.Id || !_imagePaths.Contains(path))
        {
            Send(new { version = 1, id, ok = false, message = "This image is unavailable in the current document." });
            return;
        }
        if (_assetReads.Count >= 4 || _assetReads.ContainsKey(id))
        {
            Send(new { version = 1, id, ok = false, message = "The reader is busy loading images." });
            return;
        }

        using var read = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _assetReads.Add(id, read);
        try
        {
            var bytes = await workspace.ReadImageAsync(path, read.Token);
            if (!read.IsCancellationRequested)
            {
                Send(new { version = 1, id, ok = true, value = Convert.ToBase64String(bytes) });
            }
        }
        catch (OperationCanceledException) when (read.IsCancellationRequested)
        {
            // The reader already retired the request when navigating or closing.
        }
        catch (Exception error) when (error is WorkspaceException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (!read.IsCancellationRequested)
            {
                Send(new { version = 1, id, ok = false, message = "This local image could not be opened." });
            }
        }
        finally
        {
            _assetReads.Remove(id);
        }
    }

    /// <summary>Opens only validated, user-initiated external links in their registered Windows application.</summary>
    private async void OpenExternal(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        if (_disposed || !args.IsUserInitiated || _currentPath.Length == 0)
        {
            return;
        }

        var link = WorkspacePath.Resolve(_currentPath, args.Uri);
        if (link.Kind != LinkKind.External)
        {
            return;
        }
        try
        {
            if (await Launcher.LaunchUriAsync(new Uri(link.ExternalUrl)))
            {
                return;
            }
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // The system can reject a launch when no handler is available.
        }
        if (!_disposed)
        {
            Status?.Invoke("Windows could not open this external link.");
        }
    }

    /// <summary>Reads a string property without accepting JSON coercions.</summary>
    private static string Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() ?? "" : "";

    /// <summary>Sends a structured reply only to the bundled page.</summary>
    private void Send<T>(T value)
    {
        if (!_disposed && Control.CoreWebView2 is { Source: EntryPage } core)
        {
            core.PostWebMessageAsJson(Wire.Encode(value));
        }
    }

    /// <summary>Stops reads before the renderer receives another document or closes.</summary>
    private void CancelAssets()
    {
        foreach (var request in _assetReads.Values.ToArray())
        {
            request.Cancel();
        }
    }

    /// <summary>Releases the browser controller and outstanding native reads.</summary>
    public void Dispose()
    {
        _disposed = true;
        CancelAssets();
        Control.Close();
    }
}
