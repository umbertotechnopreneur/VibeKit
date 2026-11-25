/* VBWR B
 * Project: MarkVibe | Creator: Umberto Giacobbi
 * Created with AI: OpenAI Codex; synthetic native OpenAI diagnostic transport checks
 * Human guidance: verify a real test request and its credential handling without contacting the service
 * Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
 * VBWR E */
using System.Net;
using System.Text;
using System.Text.Json;
using MarkVibe.Core;
using MarkVibe.Windows;

/// <summary>Exercises the native diagnostic transport with synthetic keys, replies and persistence.</summary>
internal static class OpenAiProbeChecks
{
    private const string CandidateKey = "sk-synthetic-candidate";
    private const string SavedKey = "sk-synthetic-saved";
    private const string RemoteDetail = "synthetic remote error mentioning " + CandidateKey;
    private const string ReplyText = "The synthetic connection works.";
    private const string Completed = """
        {"object":"response","status":"completed","output":[
            {"type":"message","role":"assistant","status":"completed",
             "content":[{"type":"output_text","text":"The synthetic connection works."}]}
        ]}
        """;

    /// <summary>Verifies one bounded POST and confirms only a completed reply commits a candidate.</summary>
    public static async Task SuccessAsync()
    {
        var store = new TestStore();
        var requests = 0;
        using var client = Fake(async (request, token) =>
        {
            requests++;
            Equal(HttpMethod.Post, request.Method);
            Equal("https://api.openai.com/v1/responses", request.RequestUri?.AbsoluteUri);
            Equal("Bearer", request.Headers.Authorization?.Scheme);
            Equal(CandidateKey, request.Headers.Authorization?.Parameter);
            Equal("application/json", request.Content?.Headers.ContentType?.MediaType);
            Equal(SavedKey, store.Key);
            Equal(0, store.Writes);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var payload = body.RootElement;
            Equal(4, payload.EnumerateObject().Count());
            Equal("gpt-4.1-mini", payload.GetProperty("model").GetString());
            Equal(OpenAiKeyRules.DiagnosticPrompt, payload.GetProperty("input").GetString());
            Equal(32, payload.GetProperty("max_output_tokens").GetInt32());
            Equal(false, payload.GetProperty("store").GetBoolean());
            return JsonReply(HttpStatusCode.OK, Completed);
        });
        Equal(ReplyText, await Probe(client, "  " + CandidateKey + "\r\n", store));
        Equal(1, requests);
        Equal(0, store.Reads);
        Equal(1, store.Writes);
        Equal(CandidateKey, store.Key);

        var savedStore = new TestStore();
        var savedRequests = 0;
        using var savedClient = Fake((request, _) =>
        {
            savedRequests++;
            Equal(SavedKey, request.Headers.Authorization?.Parameter);
            return Task.FromResult(JsonReply(HttpStatusCode.OK, Completed));
        });
        Equal(ReplyText, await Probe(savedClient, null, savedStore));
        Equal(ReplyText, await Probe(savedClient, "", savedStore));
        Equal(2, savedRequests);
        Equal(2, savedStore.Reads);
        Equal(0, savedStore.Writes);
    }

    /// <summary>Checks safe HTTP classification and incomplete or unsafe output without changing the stored key.</summary>
    public static async Task RepliesAsync()
    {
        var cases = new (HttpStatusCode Status, string Body, string Code)[]
        {
            (HttpStatusCode.Unauthorized, RemoteDetail, "openai-key-rejected"),
            (HttpStatusCode.Forbidden, RemoteDetail, "openai-access-denied"),
            (HttpStatusCode.NotFound, RemoteDetail, "openai-model-unavailable"),
            (HttpStatusCode.TooManyRequests, Error("insufficient_quota"), "openai-quota"),
            (HttpStatusCode.TooManyRequests, Error("rate_limit_exceeded"), "openai-rate-limit"),
            (HttpStatusCode.TooManyRequests, RemoteDetail, "openai-rate-limit"),
            (HttpStatusCode.InternalServerError, RemoteDetail, "openai-request-failed"),
            (HttpStatusCode.OK, RemoteDetail, "openai-response-invalid"),
            (HttpStatusCode.OK, Completed.Replace(ReplyText, "   ", StringComparison.Ordinal), "openai-response-invalid"),
            (HttpStatusCode.OK, Completed.Replace(ReplyText, CandidateKey, StringComparison.Ordinal), "openai-response-invalid"),
            (HttpStatusCode.OK, Completed.Replace(ReplyText, "\\ud800", StringComparison.Ordinal), "openai-response-invalid"),
            (HttpStatusCode.OK, Completed.Replace(ReplyText, new string('x', 1025), StringComparison.Ordinal), "openai-response-invalid"),
            (HttpStatusCode.OK, Completed.Replace(ReplyText, new string('x', 64 * 1024), StringComparison.Ordinal), "openai-response-invalid"),
            (HttpStatusCode.OK, Completed.Replace("completed", "incomplete", StringComparison.Ordinal), "openai-response-invalid"),
            (HttpStatusCode.OK, Completed.Replace("assistant", "user", StringComparison.Ordinal), "openai-response-invalid"),
            (HttpStatusCode.OK, Completed.Replace("output_text", "refusal", StringComparison.Ordinal), "openai-response-invalid")
        };
        foreach (var (status, body, code) in cases)
        {
            var requests = 0;
            using var client = Fake((_, _) =>
            {
                requests++;
                return Task.FromResult(JsonReply(status, body));
            });
            var store = new TestStore();
            await AssertFailureAsync(Probe(client, CandidateKey, store), code);
            Equal(1, requests);
            Equal(0, store.Writes);
            Equal(SavedKey, store.Key);
        }
    }

    /// <summary>Checks local validation and network failures without retrying or saving.</summary>
    public static async Task FailuresAsync()
    {
        using var noTraffic = Fake((_, _) => throw new InvalidOperationException("Unexpected HTTP call."));
        var store = new TestStore();
        await AssertFailureAsync(Probe(noTraffic, "   ", store), "openai-key-required");
        await AssertFailureAsync(Probe(noTraffic, "key\r\nheader", store), "openai-key-format");
        Equal(0, store.Reads);

        var emptyStore = new TestStore { Key = null };
        await AssertFailureAsync(Probe(noTraffic, "", emptyStore), "openai-key-required");
        Equal(1, emptyStore.Reads);

        var errors = new (Exception Error, string Code)[]
        {
            (new HttpRequestException(RemoteDetail), "openai-network"),
            (new TaskCanceledException(RemoteDetail), "openai-timeout")
        };
        foreach (var (error, code) in errors)
        {
            var requests = 0;
            using var client = Fake((_, _) =>
            {
                requests++;
                return Task.FromException<HttpResponseMessage>(error);
            });
            await AssertFailureAsync(Probe(client, CandidateKey, store), code);
            Equal(1, requests);
            Equal(0, store.Writes);
            Equal(SavedKey, store.Key);
        }

        using var canReply = Fake((_, _) => Task.FromResult(JsonReply(HttpStatusCode.OK, Completed)));
        await AssertFailureAsync(OpenAiKeySettings.ProbeAsync(canReply, CandidateKey, OpenAiKeyRules.DiagnosticPrompt,
            store.Read, _ => throw new UnauthorizedAccessException(RemoteDetail), default), "openai-storage");
        Equal(SavedKey, store.Key);
    }

    /// <summary>Checks a reply racing cancellation cannot replace the existing Windows user key.</summary>
    public static async Task CancellationAsync()
    {
        using var cancellation = new CancellationTokenSource();
        var requests = 0;
        using var client = Fake((_, _) =>
        {
            requests++;
            cancellation.Cancel();
            return Task.FromResult(JsonReply(HttpStatusCode.OK, Completed));
        });
        var store = new TestStore();
        try
        {
            await Probe(client, CandidateKey, store, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Equal(1, requests);
            Equal(0, store.Writes);
            Equal(SavedKey, store.Key);
            return;
        }
        throw new InvalidOperationException("Expected cancellation.");
    }

    /// <summary>Creates a synthetic application/json response without involving the network.</summary>
    private static HttpResponseMessage JsonReply(HttpStatusCode status, string text) =>
        new(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    /// <summary>Builds a provider error with details the UI must never display.</summary>
    private static string Error(string code) =>
        JsonSerializer.Serialize(new { error = new { code, message = RemoteDetail } });

    /// <summary>Creates an HTTP client whose only transport is the supplied delegate.</summary>
    private static HttpClient Fake(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new StubHandler(send));

    /// <summary>Calls the same overload as Windows with an isolated credential store.</summary>
    private static Task<string> Probe(HttpClient client, string? candidate, TestStore store, CancellationToken token = default) =>
        OpenAiKeySettings.ProbeAsync(client, candidate, OpenAiKeyRules.DiagnosticPrompt, store.Read, store.Save, token);

    /// <summary>Requires a safe, typed diagnostic error without provider details or credentials.</summary>
    private static async Task AssertFailureAsync(Task<string> request, string expectedCode)
    {
        try
        {
            await request;
        }
        catch (WorkspaceException error)
        {
            Equal(expectedCode, error.Code);
            Equal(false, error.Message.Contains(CandidateKey, StringComparison.Ordinal));
            Equal(false, error.Message.Contains(RemoteDetail, StringComparison.Ordinal));
            Equal<Exception?>(null, error.InnerException);
            return;
        }
        throw new InvalidOperationException("Expected diagnostic error " + expectedCode);
    }

    /// <summary>Checks values from synthetic fixtures.</summary>
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}; received {actual}.");
        }
    }

    /// <summary>Captures requests using a local delegate and never opens a network connection.</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        /// <summary>Passes the outgoing request and cancellation token to the synthetic transport.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    /// <summary>Captures credential reads and writes in memory without touching the user environment.</summary>
    private sealed class TestStore
    {
        public string? Key { get; set; } = SavedKey;
        public int Reads { get; private set; }
        public int Writes { get; private set; }

        /// <summary>Records reading the current synthetic key.</summary>
        public string? Read()
        {
            Reads++;
            return Key;
        }

        /// <summary>Records replacing the current synthetic key after validation.</summary>
        public void Save(string value)
        {
            Writes++;
            Key = value;
        }
    }
}
