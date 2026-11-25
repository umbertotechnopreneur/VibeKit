/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; scoped WinUI desktop document WebView2 transport
 * Human guidance: keep window, workspace, menus and graph native while preserving the document renderers
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using MarkVibe.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace MarkVibe.Windows;

/// <summary>Hosts a trusted local renderer. All document and workspace authority stays with the native application.</summary>
internal sealed partial class DocumentWebView : IDisposable
{
    private const string Host = "markvibe-documents.invalid";
    private const string Origin = "https://" + Host;
    private const string EntryPage = Origin + "/index.html";
    private const string DiagramPage = Origin + "/js/diagram-frame.html";
    private const int MaximumMessageCharacters = 45 * 1048576;
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ActionTimeout = TimeSpan.FromMinutes(5);
    private readonly DesktopOptions _options;
    private readonly Func<Task<CoreWebView2Environment>> _getEnvironment;
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationToken _lifetimeToken;
    private readonly string _capability = Guid.NewGuid().ToString("N");
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _outbound = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _native = new(StringComparer.Ordinal);
    private readonly WebViewDiagnostics _diagnostics = new();
    private CoreWebView2Environment? _environment;
    private Task? _initialization;
    private WorkspaceException? _failure;
    private bool _disposed;

    /// <summary>Creates the visual container without starting WebView2 or reading disk assets.</summary>
    public DocumentWebView(DesktopOptions options, Func<Task<CoreWebView2Environment>> getEnvironment, CancellationToken lifetime)
    {
        _options = options;
        _getEnvironment = getEnvironment;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _lifetimeToken = _lifetime.Token;
        View.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        View.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        View.Children.Add(Control);
        InitializePaperMargin();
        AutomationProperties.SetName(Control, "Markdown document");
    }

    /// <summary>The native container for the reader and its private print controller.</summary>
    public Grid View { get; } = new();

    /// <summary>The visible control used to anchor native document context popups.</summary>
    public WebView2 Control { get; } = new() { DefaultBackgroundColor = Microsoft.UI.Colors.Transparent };

    /// <summary>Handles file and export requests delegated to the current native workspace.</summary>
    public Func<string, JsonElement, CancellationToken, Task<object?>>? HandleNativeRequest { get; set; }

    /// <summary>Reports visible renderer gestures and reading positions to the desktop shell.</summary>
    public event Action<JsonElement>? EventReceived;

    /// <summary>Whether this viewport has completed the renderer handshake.</summary>
    public bool IsReady => !_disposed && _failure == null && _initialization?.IsCompletedSuccessfully == true;

    /// <summary>Starts the trusted document renderer only when a command first needs it.</summary>
    public Task EnsureReadyAsync(CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        token.ThrowIfCancellationRequested();
        if (_failure != null)
        {
            return Task.FromException(_failure);
        }
        return (_initialization ??= InitializeAsync()).WaitAsync(token);
    }

    /// <summary>Displays a validated application snapshot in the requested document presentation.</summary>
    public Task RenderAsync(WorkspaceInfo workspace, MarkdownModel document, string view, Preferences preferences,
        string anchor, ReadingPosition? position, ShellLayout layout, bool dark, CancellationToken token = default, bool deferMedia = false)
    {
        var snapshot = view == "read" ? document with { Source = "" } : document;
        SetPaperMargin(view, layout.SourceRatio);
        return CallAsync("view.render", new { workspace, document = snapshot, view, preferences, anchor, position, layout, dark, deferMedia }, token);
    }

    /// <summary>Invokes a renderer command whose only result is successful completion.</summary>
    public async Task CallAsync(string operation, object? arguments = null, CancellationToken token = default)
    {
        _ = await CallAsync<JsonElement>(operation, arguments, token);
    }

    /// <summary>Returns a structured renderer result and cancels superseded renderer work promptly.</summary>
    public async Task<T> CallAsync<T>(string operation, object? arguments = null, CancellationToken token = default)
    {
        await EnsureReadyAsync(token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetimeToken);
        cancellation.Token.ThrowIfCancellationRequested();
        var id = Guid.NewGuid().ToString("N");
        var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _outbound.Add(id, reply);
        try
        {
            if (!Send(new { kind = "call", id, operation, arguments }))
            {
                throw new WorkspaceException("renderer-unavailable", "The document view is unavailable.");
            }
            var value = await reply.Task.WaitAsync(ActionTimeout, cancellation.Token);
            return value.Deserialize<T>(Wire.Options)!;
        }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException)
        {
            Send(new { kind = "cancel", id });
            throw;
        }
        finally
        {
            _outbound.Remove(id);
        }
    }

    /// <summary>Starts the window's private WebView environment, shared by reader, previews and printing.</summary>
    internal static async Task<CoreWebView2Environment> CreateEnvironmentAsync(DesktopOptions options)
    {
        var arguments = "--no-first-run";
        if (options.DebugPort != 0)
        {
            arguments += " --remote-debugging-address=127.0.0.1 --remote-debugging-port=" + options.DebugPort;
        }
        var userData = Path.Combine(options.DataDirectory, "WebView2.WinUI");
        return await CoreWebView2Environment.CreateWithOptionsAsync(null, userData,
            new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = arguments });
    }

    /// <summary>Loads only the bundled document page in a separate profile with explicit navigation and permissions.</summary>
    private async Task InitializeAsync()
    {
        if (!File.Exists(Path.Combine(_options.AssetsDirectory, "index.html")))
        {
            throw new IOException("The bundled document renderer is missing. Rebuild MarkVibe.");
        }
        _environment = await _getEnvironment();
        _lifetimeToken.ThrowIfCancellationRequested();
        await Control.EnsureCoreWebView2Async(_environment);
        _lifetimeToken.ThrowIfCancellationRequested();

        var core = Control.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping(Host, _options.AssetsDirectory, CoreWebView2HostResourceAccessKind.Deny);
        ConfigureCommonSettings(core);
        core.Settings.AreDevToolsEnabled = _options.SmokeTest;
        core.Settings.IsWebMessageEnabled = true;
        core.NavigationStarting += (_, args) => args.Cancel = args.Uri != EntryPage;
        core.FrameNavigationStarting += (_, args) => args.Cancel = args.Uri is not (DiagramPage or "about:blank");
        core.NewWindowRequested += OpenExternal;
        core.WebMessageReceived += Receive;
        core.NavigationCompleted += (_, args) =>
        {
            if (!args.IsSuccess && !_ready.Task.IsCompleted)
            {
                _ready.TrySetException(new WorkspaceException("renderer-unavailable", "The bundled document page could not be loaded."));
            }
        };
        core.ProcessFailed += BrowserFailed;
        _ = _diagnostics.AttachAsync(core);
        await core.AddScriptToExecuteOnDocumentCreatedAsync(
            "if (location.origin === '" + Origin + "' && window === window.top) " +
            "Object.defineProperty(window,'markvibeDesktop',{value:Object.freeze(" +
            Wire.Encode(new { version = 1, capability = _capability }) + ")});");
        _lifetimeToken.ThrowIfCancellationRequested();
        core.Navigate(EntryPage);
        await _ready.Task.WaitAsync(StartupTimeout, _lifetimeToken);
    }

    /// <summary>Disable native dialogs, host objects, downloads and permissions for embedded web documents.</summary>
    private static void ConfigureCommonSettings(CoreWebView2 core)
    {
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (_, args) => args.Cancel = true;
    }

    /// <summary>Completes pending calls if the WebView process becomes unavailable.</summary>
    private void BrowserFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs args)
    {
        if (args.ProcessFailedKind is not (CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited))
        {
            return;
        }
        var error = new WorkspaceException("renderer-unavailable", "The document renderer stopped. Restart MarkVibe to open a new renderer.");
        _failure = error;
        _ready.TrySetException(error);
        foreach (var request in _outbound.Values)
        {
            request.TrySetException(error);
        }
        foreach (var request in _native.Values.ToArray())
        {
            request.Cancel();
        }
    }

    /// <summary>Routes messages only from the validated top-level renderer and rejects oversized input.</summary>
    private async void Receive(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (_disposed || args.Source != EntryPage || sender.Source != EntryPage)
        {
            return;
        }
        var json = args.WebMessageAsJson;
        if (json.Length > MaximumMessageCharacters)
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json, new() { MaxDepth = 20 });
            var message = document.RootElement;
            if (message.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            switch (Text(message, "kind"))
            {
                case "ready":
                    _ready.TrySetResult();
                    break;
                case "response":
                    CompleteRendererCall(message);
                    break;
                case "event" when message.TryGetProperty("event", out var rendererEvent) && rendererEvent.ValueKind == JsonValueKind.Object:
                    if (Text(rendererEvent, "type") == "reader-material") { await ApplyPaperMaterialAsync(rendererEvent); }
                    else { EventReceived?.Invoke(rendererEvent.Clone()); }
                    break;
                default:
                    await RouteNativeRequestAsync(message);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // The native caller closed or replaced this document while its event was in flight.
        }
        catch (Exception error)
        {
            DesktopLog.Error("webview.messageFailure", error);
        }
    }

    /// <summary>Resolve a single renderer reply without reusing the WebView event's JSON lifetime.</summary>
    private void CompleteRendererCall(JsonElement message)
    {
        if (!_outbound.TryGetValue(Text(message, "id"), out var request))
        {
            return;
        }
        if (message.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True && message.TryGetProperty("value", out var value))
        {
            request.TrySetResult(value.Clone());
            return;
        }
        var code = Text(message, "code");
        request.TrySetException(new WorkspaceException(code.Length == 0 ? "renderer-failed" : code,
            BoundedMessage(Text(message, "message"), "The document operation failed.")));
    }

    /// <summary>Allow only the shared renderer's authenticated image and export calls through to native services.</summary>
    private async Task RouteNativeRequestAsync(JsonElement message)
    {
        var id = Text(message, "id");
        if (id.Length is 0 or > 80 || Text(message, "capability") != _capability ||
            !message.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out var protocol) || protocol != 1 ||
            !message.TryGetProperty("args", out var arguments) || arguments.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var operation = Text(message, "operation");
        if (operation == "cancel")
        {
            if (_native.TryGetValue(Text(arguments, "requestId"), out var pending))
            {
                pending.Cancel();
            }
            return;
        }

        if (_native.ContainsKey(id) || _native.Count >= 64)
        {
            Send(new { version = 1, id, ok = false, code = "native-busy", message = "Too many local requests. Wait and retry." });
            return;
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
        _native.Add(id, cancellation);
        try
        {
            var handler = HandleNativeRequest;
            if (handler == null || operation is not ("workspace.read" or "export.save" or "file.browser"))
            {
                throw new WorkspaceException("unsupported-operation", "The renderer cannot perform this native operation.");
            }
            var result = await handler(operation, arguments.Clone(), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            Send(new { version = 1, id, ok = true, value = result });
        }
        catch (Exception error)
        {
            if (error is not OperationCanceledException)
            {
                DesktopLog.Error("webview.nativeFailure", error);
            }
            var issue = error as WorkspaceException;
            var code = error is OperationCanceledException ? "cancelled" : issue?.Code ?? "native-failed";
            var detail = error is OperationCanceledException ? "Action cancelled." : issue?.Message;
            Send(new { version = 1, id, ok = false, code, message = BoundedMessage(detail, "The local document operation failed.") });
        }
        finally
        {
            _native.Remove(id);
        }
    }

    /// <summary>Open an approved external link from an explicit user gesture in its default Windows application.</summary>
    private static void OpenExternal(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        if (!args.IsUserInitiated || args.Uri.Length > 2048 || args.Uri.Any(char.IsControl) ||
            !Uri.TryCreate(args.Uri, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http" or "mailto") ||
            url.Scheme != "mailto" && (url.Host.Length == 0 || url.UserInfo.Length != 0))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            DesktopLog.Error("webview.openExternal", error);
        }
    }

    /// <summary>Read one string property without coercing invalid JSON input.</summary>
    private static string Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() ?? "" : "";

    /// <summary>Keep renderer error messages bounded and provide a user-facing fallback.</summary>
    private static string BoundedMessage(string? message, string fallback) => message is { Length: > 0 and <= 512 } ? message : fallback;

    /// <summary>Post a structured message only while the bundled document page still owns the controller.</summary>
    private bool Send<T>(T value)
    {
        if (_disposed || Control.CoreWebView2 is not { Source: EntryPage } core)
        {
            return false;
        }
        try
        {
            core.PostWebMessageAsJson(Wire.Encode(value));
            return true;
        }
        catch (COMException error)
        {
            DesktopLog.Error("webview.sendFailure", error);
            return false;
        }
    }

    /// <summary>Cancel in-flight bridge operations and release both WebView controllers.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _paperEdge.SizeChanged -= PaperEdgeSizeChanged;
        Control.SizeChanged -= PaperEdgeSizeChanged;
        _lifetime.Cancel();
        _print?.Close();
        Control.Close();
        _lifetime.Dispose();
    }
}
