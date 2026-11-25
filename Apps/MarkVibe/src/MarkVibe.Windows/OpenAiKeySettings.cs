/* VBWR B
 *
 * Project: MarkVibe
 * Repository: https://github.com/umbertotechnopreneur/MarkVibe
 * Creator: Umberto Giacobbi | https://umbertogiacobbi.biz
 *
 * VibeWare: Human intent, AI execution, and plenty of tokens
 * Manifesto: https://umbertogiacobbi.biz/vibeware/manifesto
 *
 * Created with AI: OpenAI Codex; native OpenAI key validation and storage; 2026-10-11
 * Human guidance: Umberto Giacobbi; save validated WebView keys as user environment variables
 * Evidence: AI-DEVELOPMENT-DISCLOSURE.md; .github/tasks/archive.md
 *
 * Copyright (c) 2026 Umberto Giacobbi
 * License: MIT (see LICENSE)
 *
 * VBWR E */
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MarkVibe.Core;

namespace MarkVibe.Windows;

/// <summary>Tests desktop credentials with a bounded OpenAI reply and persists them in the Windows user environment.</summary>
internal static class OpenAiKeySettings
{
    private const string EnvironmentVariableName = "OPENAI_API_KEY";
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    /// <summary>Reports whether a user environment key exists without exposing its value to the WebView.</summary>
    public static OpenAiKeyStatus Status()
    {
        try
        {
            return new(!string.IsNullOrWhiteSpace(ReadKey()), true, "environment");
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return new(false, false, "environment");
        }
    }

    /// <summary>Validates and saves an explicitly supplied candidate through the diagnostic Responses request.</summary>
    public static async Task<OpenAiKeyStatus> ValidateAndSaveAsync(string apiKey, CancellationToken cancellationToken)
    {
        var candidate = OpenAiKeyRules.Normalize(apiKey);
        await ProbeAsync(candidate, OpenAiKeyRules.DiagnosticPrompt, cancellationToken);
        return new(true, true, "environment");
    }

    /// <summary>Returns one real assistant reply. A successful check with a saved key performs no write.</summary>
    public static Task<string> ProbeAsync(string? candidate, string prompt, CancellationToken cancellationToken) =>
        ProbeAsync(Client, candidate, prompt, ReadKey, SaveKey, cancellationToken);

    /// <summary>Runs the same diagnostic using an injected transport and credential store for synthetic checks.</summary>
    internal static async Task<string> ProbeAsync(HttpClient client, string? candidate, string prompt,
        Func<string?> readKey, Action<string> saveKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var shouldSave = !string.IsNullOrEmpty(candidate);
        string apiKey;
        try
        {
            apiKey = OpenAiKeyRules.Normalize(shouldSave ? candidate : readKey());
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException)
        {
            throw new WorkspaceException("openai-storage", "The OpenAI key could not be read. Open Settings and validate it again.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(Wire.Encode(OpenAiKeyRules.DiagnosticPayload(prompt)), Encoding.UTF8, "application/json");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        string reply;
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
            {
                var quotaExhausted = response.StatusCode == HttpStatusCode.TooManyRequests
                    && await IsQuotaExhaustedAsync(response.Content, deadline.Token);
                throw OpenAiKeyRules.RequestFailure((int)response.StatusCode, quotaExhausted);
            }
            using var document = await ReadJsonAsync(response.Content, deadline.Token)
                ?? throw OpenAiKeyRules.InvalidDiagnosticReply();
            reply = OpenAiKeyRules.DiagnosticReply(document.RootElement);
            if (reply.Contains(apiKey, StringComparison.Ordinal))
            {
                throw OpenAiKeyRules.InvalidDiagnosticReply();
            }
            deadline.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WorkspaceException("openai-timeout", "OpenAI validation timed out. Check your connection and try again.");
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new WorkspaceException("openai-network", "OpenAI could not be reached. Check your connection and try again.");
        }
        catch (JsonException)
        {
            throw OpenAiKeyRules.InvalidDiagnosticReply();
        }

        // A dialog closing during validation must not turn a delayed reply into a save.
        cancellationToken.ThrowIfCancellationRequested();
        if (shouldSave)
        {
            try
            {
                saveKey(apiKey);
            }
            catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or ArgumentException)
            {
                throw new WorkspaceException("openai-storage", "OpenAI accepted the key, but it could not be saved to the user environment.");
            }
        }
        return reply;
    }

    /// <summary>Reads one optional error classification. Malformed or oversized errors stay generic.</summary>
    private static async Task<bool> IsQuotaExhaustedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await ReadJsonAsync(content, cancellationToken);
            return document != null && OpenAiKeyRules.IsQuotaError(document.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Reads a bounded body regardless of whether the HTTP peer declared a length.</summary>
    private static async Task<JsonDocument?> ReadJsonAsync(HttpContent content, CancellationToken cancellationToken)
    {
        const int limit = OpenAiKeyRules.DiagnosticMaxResponseBytes;
        if (content.Headers.ContentLength > limit)
        {
            return null;
        }
        using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[limit + 1];
        var length = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken);
        return length <= limit ? JsonDocument.Parse(buffer.AsMemory(0, length), new() { MaxDepth = 24 }) : null;
    }

    /// <summary>Reads the user-scoped key without exposing it to the web renderer.</summary>
    private static string? ReadKey() => Environment.GetEnvironmentVariable(EnvironmentVariableName, EnvironmentVariableTarget.User);

    /// <summary>Replaces the credential in the Windows user environment without copying it to the process environment.</summary>
    private static void SaveKey(string key) => Environment.SetEnvironmentVariable(EnvironmentVariableName, key, EnvironmentVariableTarget.User);
}
