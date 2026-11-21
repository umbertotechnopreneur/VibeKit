/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; isolated WebView2 PDF export from the WinUI host
 * Human guidance: preserve self-contained PDF appearance and native destination selection
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using System.Text;
using MarkVibe.Core;
using MarkVibe.Native;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Streams;

namespace MarkVibe.Windows;

internal sealed partial class DocumentWebView
{
    private const int MaximumExportBytes = 32 * 1048576;
    private const string PrintPolicy = "default-src 'none'; img-src data:; style-src 'unsafe-inline'; font-src data:; " +
        "script-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'";
    private readonly SemaphoreSlim _printing = new(1, 1);
    private WebView2? _print;
    private Task? _printInitialization;
    private string? _printAddress;

    /// <summary>Generate a PDF from sanitized standalone HTML and write the destination atomically.</summary>
    public async Task PrintPdfAsync(string html, string destination, CancellationToken token = default)
    {
        if (Encoding.UTF8.GetByteCount(html) > MaximumExportBytes)
        {
            throw new WorkspaceException("invalid-export", "The prepared document is too large to export.");
        }

        await EnsureReadyAsync(token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetimeToken);
        var cancel = cancellation.Token;
        await _printing.WaitAsync(cancel);
        try
        {
            await (_printInitialization ??= InitializePrintAsync()).WaitAsync(cancel);
            using var page = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(page))
            {
                writer.WriteBytes(Encoding.UTF8.GetBytes(html));
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            page.Seek(0);
            cancel.ThrowIfCancellationRequested();
            await PrintPageAsync(page, destination, cancel);
        }
        finally
        {
            _printing.Release();
        }
    }

    /// <summary>Create the offscreen renderer on first use with page script and web messaging disabled.</summary>
    private async Task InitializePrintAsync()
    {
        _print = new WebView2
        {
            Width = 1,
            Height = 1,
            Opacity = 0,
            IsHitTestVisible = false,
            IsTabStop = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        View.Children.Insert(0, _print);
        await _print.EnsureCoreWebView2Async(_environment!);
        _lifetimeToken.ThrowIfCancellationRequested();
        var core = _print.CoreWebView2;
        ConfigureCommonSettings(core);
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.IsScriptEnabled = false;
        core.NavigationStarting += (_, args) => args.Cancel = args.Uri != _printAddress;
        core.FrameNavigationStarting += (_, args) => args.Cancel = true;
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.AddWebResourceRequestedFilter(Origin + "/print/*", CoreWebView2WebResourceContext.Document);
    }

    /// <summary>Supply only the active HTML page to the print renderer and wait for finished image and font layout.</summary>
    private async Task PrintPageAsync(IRandomAccessStream page, string destination, CancellationToken token)
    {
        var core = _print!.CoreWebView2;
        var environment = _environment!;
        var address = Origin + "/print/" + Guid.NewGuid().ToString("N") + ".html";
        _printAddress = address;
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong? navigationId = null;

        /// <summary>Associate navigation completion with the active export rather than a cancelled predecessor.</summary>
        void Starting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
        {
            if (args.Uri == address)
            {
                navigationId = args.NavigationId;
            }
        }

        /// <summary>Return the prepared UTF-8 page from trusted memory with a script-free content policy.</summary>
        void Respond(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
        {
            if (args.Request.Uri == address)
            {
                args.Response = environment.CreateWebResourceResponse(page, 200, "OK",
                    "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nContent-Security-Policy: " + PrintPolicy);
            }
        }

        /// <summary>Signal whether the print viewport's only permitted navigation completed.</summary>
        void Completed(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (args.NavigationId != navigationId)
            {
                return;
            }
            if (args.IsSuccess)
            {
                loaded.TrySetResult();
            }
            else
            {
                loaded.TrySetException(new IOException("The PDF document could not be loaded."));
            }
        }

        core.WebResourceRequested += Respond;
        core.NavigationStarting += Starting;
        core.NavigationCompleted += Completed;
        try
        {
            core.Navigate(address);
            await loaded.Task.WaitAsync(StartupTimeout, token);
            await WaitUntilPrintableAsync(core, token);
            var settings = environment.CreatePrintSettings();
            settings.ShouldPrintBackgrounds = true;
            settings.ShouldPrintHeaderAndFooter = false;
            settings.PageWidth = 8.2677;
            settings.PageHeight = 11.6929;
            settings.MarginTop = settings.MarginBottom = settings.MarginLeft = settings.MarginRight = 0;
            using var pdf = await core.PrintToPdfStreamAsync(settings);
            if (pdf == null)
            {
                throw new IOException("WebView2 did not return a PDF.");
            }
            token.ThrowIfCancellationRequested();
            using var stream = pdf.AsStreamForRead();
            await AtomicFile.WriteAsync(destination, stream, token);
        }
        finally
        {
            core.WebResourceRequested -= Respond;
            core.NavigationStarting -= Starting;
            core.NavigationCompleted -= Completed;
            _printAddress = null;
            if (!loaded.Task.IsCompleted && !_disposed)
            {
                core.Stop();
            }
        }
    }

    /// <summary>Give embedded PDF images and font layout a bounded interval to finish.</summary>
    private static async Task WaitUntilPrintableAsync(CoreWebView2 core, CancellationToken token)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            token.ThrowIfCancellationRequested();
            const string printable = "document.fonts.status==='loaded' && Array.from(document.images).every(image=>image.complete)";
            if (await core.ExecuteScriptAsync(printable) == "true")
            {
                return;
            }
            await Task.Delay(100, token);
        }
        throw new IOException("The PDF images or fonts did not finish loading.");
    }
}
