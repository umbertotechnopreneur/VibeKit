/* VBWR B
 *
 * Project: MarkVibe
 * Repository: https://github.com/umbertotechnopreneur/MarkVibe
 * Creator: Umberto Giacobbi | https://umbertogiacobbi.biz
 *
 * VibeWare: Human intent, AI execution, and plenty of tokens
 * Manifesto: https://umbertogiacobbi.biz/vibeware/manifesto
 *
 * Created with AI: OpenAI Codex; OpenAI key settings contract; 2026-10-11
 * Human guidance: Umberto Giacobbi; validate keys and choose storage by host
 * Evidence: AI-DEVELOPMENT-DISCLOSURE.md; .github/tasks/archive.md
 *
 * Copyright (c) 2026 Umberto Giacobbi
 * License: MIT (see LICENSE)
 *
 * VBWR E */
using System.Text;
using System.Text.Json;

namespace MarkVibe.Core;

/// <summary>Describes credential availability without returning the saved API key.</summary>
public sealed record OpenAiKeyStatus(bool Configured, bool StorageAvailable, string Storage);

/// <summary>Shared input and response rules for the OpenAI authentication check.</summary>
public static class OpenAiKeyRules
{
    public const string DiagnosticPrompt = "Reply with a single short sentence confirming this connection works.";
    public const string DiagnosticModel = "gpt-4.1-mini";
    public const int DiagnosticMaxOutputTokens = 32;
    public const int DiagnosticMaxResponseBytes = 64 * 1024;
    private const int DiagnosticMaxReplyCharacters = 1024;

    /// <summary>Builds a small, stateless prompt with no tools, workspace context or previous response.</summary>
    public static object DiagnosticPayload(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 512)
        {
            throw new ArgumentException("The diagnostic prompt must contain between 1 and 512 characters.", nameof(prompt));
        }
        return new { model = DiagnosticModel, input = prompt, max_output_tokens = DiagnosticMaxOutputTokens, store = false };
    }

    /// <summary>Extracts bounded plain assistant output from a completed Responses API reply.</summary>
    public static string DiagnosticReply(JsonElement response)
    {
        if (!HasString(response, "object", "response") || !HasString(response, "status", "completed")
            || !response.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            throw InvalidDiagnosticReply();
        }

        var reply = new StringBuilder();
        foreach (var message in output.EnumerateArray())
        {
            if (!HasString(message, "type", "message") || !HasString(message, "role", "assistant")
                || !HasString(message, "status", "completed")
                || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            foreach (var part in content.EnumerateArray())
            {
                if (HasString(part, "type", "refusal"))
                {
                    throw InvalidDiagnosticReply();
                }
                if (HasString(part, "type", "output_text")
                    && part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    string value;
                    try
                    {
                        value = text.GetString()!;
                    }
                    catch (InvalidOperationException)
                    {
                        // String decoding can fail even after the JSON structure has parsed.
                        throw InvalidDiagnosticReply();
                    }
                    if (value.Length > DiagnosticMaxReplyCharacters - reply.Length)
                    {
                        throw InvalidDiagnosticReply();
                    }
                    reply.Append(value);
                }
            }
        }
        var result = reply.ToString().Trim();
        return result.Length > 0 ? result : throw InvalidDiagnosticReply();
    }

    /// <summary>Recognizes the one provider error needed to distinguish 429 quota from transient throttling.</summary>
    public static bool IsQuotaError(JsonElement response) =>
        response.ValueKind == JsonValueKind.Object && response.TryGetProperty("error", out var error)
        && (HasString(error, "code", "insufficient_quota") || HasString(error, "type", "insufficient_quota"));

    /// <summary>Reports an incomplete, malformed, empty or oversized reply without showing provider data.</summary>
    public static WorkspaceException InvalidDiagnosticReply() =>
        new("openai-response-invalid", "OpenAI returned an unexpected validation response. The key was not saved.");

    /// <summary>Matches a JSON string member without assuming a provider-supplied object shape.</summary>
    private static bool HasString(JsonElement value, string property, string expected) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var member)
        && member.ValueKind == JsonValueKind.String && member.ValueEquals(expected);

    /// <summary>Trims pasted whitespace and rejects empty or unsafe authorization-header values.</summary>
    public static string Normalize(string? apiKey)
    {
        var normalizedKey = apiKey?.Trim() ?? "";
        if (normalizedKey.Length == 0)
        {
            throw new WorkspaceException("openai-key-required", "Enter an OpenAI API key first.");
        }

        if (normalizedKey.Length > 4096 || normalizedKey.Any(character => character < '!' || character > '~'))
        {
            throw new WorkspaceException("openai-key-format", "The API key contains unsupported characters or is too long.");
        }

        return normalizedKey;
    }

    /// <summary>Accepts the authenticated models-list schema, including an empty permitted list.</summary>
    public static bool IsModelsResponse(JsonElement response)
    {
        return response.ValueKind == JsonValueKind.Object
            && response.TryGetProperty("object", out var responseKind)
            && responseKind.ValueKind == JsonValueKind.String
            && responseKind.GetString() == "list"
            && response.TryGetProperty("data", out var models)
            && models.ValueKind == JsonValueKind.Array;
    }

    /// <summary>Maps HTTP failures to fixed messages that never contain credentials or remote error bodies.</summary>
    public static WorkspaceException RequestFailure(int statusCode, bool quotaExhausted = false)
    {
        return statusCode switch
        {
            401 => new("openai-key-rejected", "OpenAI rejected this API key. Check it and try again."),
            403 => new("openai-access-denied", "OpenAI denied access. Check this key's permissions and your account restrictions."),
            404 => new("openai-model-unavailable", "This key cannot access the OpenAI test model. Check the project's model permissions."),
            429 when quotaExhausted => new("openai-quota", "OpenAI API quota is exhausted. Check billing and project limits."),
            429 => new("openai-rate-limit", "OpenAI limited the request. Check your account limits and try again later."),
            _ => new("openai-request-failed", "OpenAI could not complete the validation request. Try again later.")
        };
    }
}
