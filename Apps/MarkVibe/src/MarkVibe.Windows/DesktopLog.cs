/* VBWR B
 * Project: MarkVibe
 * Repository: https://github.com/umbertotechnopreneur/MarkVibe
 * Creator: Umberto Giacobbi | https://umbertogiacobbi.biz
 * VibeWare: Human intent, AI execution, and plenty of tokens
 * Manifesto: https://umbertogiacobbi.biz/vibeware/manifesto
 * Modified with AI: OpenAI Codex; bounded local startup diagnostics; 2026-10-11
 * Human guidance: Umberto Giacobbi; diagnose startup and provide persistent logs
 * Evidence: AI-DEVELOPMENT-DISCLOSURE.md; .github/tasks/archive.md
 * Copyright (c) 2026 Umberto Giacobbi
 * License: MIT (see LICENSE)
 * VBWR E */
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;
using Tools.Widgets;

namespace MarkVibe.Windows;

/// <summary>Writes bounded local diagnostics without document text, arguments, paths or credentials.</summary>
internal static class DesktopLog
{
    private const string SupportAppId = "MarkVibe";
    private static Task<Logger>? _startup;
    private static string? _directory;

    /// <summary>Starts seven-day file diagnostics beneath the selected application data directory.</summary>
    internal static void Initialize(string dataDirectory)
    {
        var directory = Path.Combine(dataDirectory, "logs");
        _directory = directory;
        _startup = Task.Run(() =>
        {
            var logger = VibeWareDiagnostics.CreateLogger(SupportAppId, out _, directory);
            logger.Information("{Event:l} {Details:l}", "application.start", JsonSerializer.Serialize(new
            {
                version = typeof(App).Assembly.GetName().Version?.ToString(),
                runtime = Environment.Version.ToString(),
                architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString()
            }));
            return logger;
        });
    }

    /// <summary>Records a safe, bounded diagnostic event in the shared format.</summary>
    internal static void Write(string level, string eventName, object? details = null)
    {
        var severity = level switch
        {
            "error" => LogEventLevel.Error,
            "warning" => LogEventLevel.Warning,
            _ => LogEventLevel.Information
        };
        _startup?.GetAwaiter().GetResult().Write(severity, "{Event:l} {Details:l}", eventName,
            details is null ? "" : JsonSerializer.Serialize(details));
    }

    /// <summary>Archives local diagnostics and opens an email compose window for the user.</summary>
    internal static string EmailSupport()
    {
        var logs = _directory ?? throw new InvalidOperationException("Diagnostics are not available.");
        var archive = VibeWareDiagnostics.CreateSupportArchive(SupportAppId, logs,
            Path.Combine(Path.GetDirectoryName(logs)!, "diagnostics"), DateTimeOffset.UtcNow);
        var result = VibeWareDiagnostics.OpenEmailDraft("MarkVibe", archive);
        if (result != SupportEmailResult.Completed)
        {
            try { VibeWareDiagnostics.RevealArchive(archive); }
            catch (System.ComponentModel.Win32Exception) { }
        }
        return result switch
        {
            SupportEmailResult.Completed => $"Diagnostics ZIP saved to {archive}",
            SupportEmailResult.ManualAttachment => $"Attach this ZIP to the draft: {archive}",
            _ => $"No compatible email app was found. Diagnostics ZIP saved to {archive}"
        };
    }

    /// <summary>Flushes pending diagnostics when the Windows host exits.</summary>
    internal static void Shutdown()
    {
        Interlocked.Exchange(ref _startup, null)?.GetAwaiter().GetResult().Dispose();
    }

    /// <summary>Records exception types and method names, deliberately excluding messages and source file paths.</summary>
    internal static void Error(string eventName, Exception error)
    {
        var failures = new List<object>();
        for (Exception? current = error; current != null && failures.Count < 5; current = current.InnerException)
        {
            var methods = new StackTrace(current, false).GetFrames()
                .Take(16)
                .Select(frame => frame.GetMethod())
                .Select(method => method?.DeclaringType?.FullName + "." + method?.Name)
                .ToArray();
            failures.Add(new { type = current.GetType().FullName, hresult = current.HResult, methods });
        }
        Write("error", eventName, failures);
    }

    /// <summary>Extracts diagnostic identifiers from runtime errors instead of persisting arbitrary console content.</summary>
    internal static void WebError(string eventName, string text)
    {
        // Console payloads can contain documents or API failures. Only known error
        // identifiers and a missing XAML resource name cross the logging boundary.
        var boundedText = text[..Math.Min(text.Length, 32768)];
        var types = Regex.Matches(boundedText, @"\b(?:[A-Za-z_][A-Za-z0-9_]*\.)*[A-Za-z_][A-Za-z0-9_]*(?:Exception|Error)\b")
            .Select(match => match.Value)
            .Distinct()
            .Take(8)
            .ToArray();
        var missingResource = Regex.Match(boundedText, @"Cannot find resource named '([A-Za-z_][A-Za-z0-9_.-]{0,79})'\.");
        Write("error", eventName, new
        {
            types,
            resource = missingResource.Success ? missingResource.Groups[1].Value : null
        });
    }
}
