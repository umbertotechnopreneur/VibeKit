using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VibeKit.StudioControls.Core;

public interface IObsClient
{
    Task<JsonElement> CallAsync(string requestType, object? data = null, CancellationToken cancellationToken = default);
}

/// <summary>A transport owns exactly one websocket; implementations must honor cancellation.</summary>
public interface IObsTransport : IAsyncDisposable
{
    Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken);
    Task SendAsync(string message, CancellationToken cancellationToken);
    Task<string> ReceiveAsync(CancellationToken cancellationToken);
    Task CloseAsync(CancellationToken cancellationToken);
    void Abort();
}

public sealed class WebSocketObsTransport : IObsTransport
{
    private const int MaximumMessageBytes = 1024 * 1024;
    private readonly ClientWebSocket socket = new();

    public WebSocketObsTransport()
    {
        // This connection is local; system HTTP proxies must never receive the OBS password.
        socket.Options.Proxy = null;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
    }

    public Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken) => socket.ConnectAsync(endpoint, cancellationToken);

    public async Task SendAsync(string message, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(message);
        await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ReceiveAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[8192];
        using var message = new MemoryStream();
        ValueWebSocketReceiveResult part;
        do
        {
            part = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (part.MessageType != WebSocketMessageType.Text)
                throw new InvalidDataException("OBS closed the connection or sent a non-text message.");
            if (message.Length + part.Count > MaximumMessageBytes)
                throw new InvalidDataException("The OBS message exceeded the size limit.");
            message.Write(buffer, 0, part.Count);
        } while (!part.EndOfMessage);
        return new UTF8Encoding(false, true).GetString(message.GetBuffer(), 0, checked((int)message.Length));
    }

    public Task CloseAsync(CancellationToken cancellationToken) => socket.State is WebSocketState.Open or WebSocketState.CloseReceived
        ? socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Controller closed", cancellationToken)
        : Task.CompletedTask;

    public void Abort() => socket.Abort();
    public ValueTask DisposeAsync()
    {
        socket.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>OBS websocket v5 RPC with one absolute deadline, serialized requests and no replay.</summary>
public sealed class ObsClient : IObsClient, IAsyncDisposable
{
    private readonly ObsConnectionSettings settings;
    private readonly Func<IObsTransport> transportFactory;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly TimeSpan requestTimeout;
    private readonly Action<string>? reportCleanupFailure;
    private IObsTransport? transport;
    private bool identified;
    private volatile bool disposed;

    public ObsClient(ObsConnectionSettings settings, Func<IObsTransport>? transportFactory = null, TimeSpan? requestTimeout = null, Action<string>? reportCleanupFailure = null)
    {
        settings.Validate();
        this.settings = settings;
        this.reportCleanupFailure = reportCleanupFailure;
        this.transportFactory = transportFactory ?? (() => new WebSocketObsTransport());
        this.requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(3);
        if (this.requestTimeout <= TimeSpan.Zero || this.requestTimeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default) => ExecuteAsync<object?>(async token =>
    {
        await EnsureConnectedAsync(token).ConfigureAwait(false);
        return null;
    }, "Connect", cancellationToken);

    public Task<JsonElement> CallAsync(string requestType, object? data = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestType);
        return ExecuteAsync(async token =>
        {
            await EnsureConnectedAsync(token).ConfigureAwait(false);
            string requestId = Guid.NewGuid().ToString();
            string message = JsonSerializer.Serialize(new
            {
                op = 6,
                d = new { requestType, requestId, requestData = data ?? new { } }
            });
            // Mark before sending: even a partial send can execute a mutation remotely.
            bool requestSent = true;
            try
            {
                await transport!.SendAsync(message, token).ConfigureAwait(false);
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    using JsonDocument document = JsonDocument.Parse(await transport.ReceiveAsync(token).ConfigureAwait(false));
                    JsonElement root = document.RootElement;
                    if (root.GetProperty("op").GetInt32() != 7) continue;
                    JsonElement result = root.GetProperty("d");
                    if (result.GetProperty("requestId").GetString() != requestId) continue;
                    JsonElement status = result.GetProperty("requestStatus");
                    if (!status.GetProperty("result").GetBoolean())
                    {
                        int? code = status.TryGetProperty("code", out JsonElement value) ? value.GetInt32() : null;
                        // Do not copy an arbitrary server comment or payload into diagnostics.
                        throw new ObsRequestException(requestType, code);
                    }
                    if (!result.TryGetProperty("responseData", out JsonElement response))
                        return JsonSerializer.SerializeToElement(new { });
                    if (response.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid OBS response data.");
                    return response.Clone();
                }
            }
            catch (ObsRequestException) { throw; }
            catch (Exception exception) when (IsConnectionFailure(exception))
            {
                throw new ObsConnectionException($"OBS did not confirm {requestType}; its outcome may be uncertain.", requestSent, exception);
            }
        }, requestType, cancellationToken);
    }

    private async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, string operationName, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(requestTimeout);
        // A cancelled waiter must not abort the socket belonging to the active operation.
        await gate.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return await operation(deadline.Token).ConfigureAwait(false);
        }
        catch (ObsRequestException) { throw; }
        catch (Exception exception) when (IsConnectionFailure(exception))
        {
            await DiscardTransportAsync().ConfigureAwait(false);
            if (exception is ObsConnectionException) throw;
            throw new ObsConnectionException($"Could not complete OBS {operationName}.", false, exception);
        }
        finally { gate.Release(); }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (identified) return;
        transport = transportFactory();
        await transport.ConnectAsync(settings.Endpoint, cancellationToken).ConfigureAwait(false);
        using JsonDocument hello = JsonDocument.Parse(await transport.ReceiveAsync(cancellationToken).ConfigureAwait(false));
        if (hello.RootElement.GetProperty("op").GetInt32() != 0)
            throw new InvalidDataException("OBS did not send a websocket v5 greeting.");
        JsonElement greeting = hello.RootElement.GetProperty("d");
        var identify = new Dictionary<string, object> { ["rpcVersion"] = 1, ["eventSubscriptions"] = 0 };
        if (greeting.TryGetProperty("authentication", out JsonElement authentication))
        {
            string salt = authentication.GetProperty("salt").GetString() ?? throw new InvalidDataException("Missing OBS salt.");
            string challenge = authentication.GetProperty("challenge").GetString() ?? throw new InvalidDataException("Missing OBS challenge.");
            string secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(settings.Password + salt)));
            identify["authentication"] = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
        }
        await transport.SendAsync(JsonSerializer.Serialize(new { op = 1, d = identify }), cancellationToken).ConfigureAwait(false);
        using JsonDocument response = JsonDocument.Parse(await transport.ReceiveAsync(cancellationToken).ConfigureAwait(false));
        if (response.RootElement.GetProperty("op").GetInt32() != 2)
            throw new InvalidDataException("OBS did not accept authentication.");
        identified = true;
    }

    private static bool IsConnectionFailure(Exception exception) => exception is ObsConnectionException or
        WebSocketException or IOException or InvalidDataException or OperationCanceledException or JsonException or
        InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or
        DecoderFallbackException or TimeoutException;

    private async Task DiscardTransportAsync()
    {
        identified = false;
        IObsTransport? previous = transport;
        transport = null;
        if (previous is null) return;
        AbortTransport(previous);
        try { await previous.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) when (IsConnectionFailure(error))
        { reportCleanupFailure?.Invoke("obs.cleanup.dispose." + error.GetType().Name); }
    }

    private void AbortTransport(IObsTransport connection)
    {
        try { connection.Abort(); }
        catch (Exception error) when (IsConnectionFailure(error))
        { reportCleanupFailure?.Invoke("obs.cleanup.abort." + error.GetType().Name); }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(requestTimeout);
        await gate.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            if (transport is not null)
            {
                try { await transport.CloseAsync(deadline.Token).ConfigureAwait(false); }
                catch (Exception exception) when (IsConnectionFailure(exception)) { /* Close is best effort. */ }
                await DiscardTransportAsync().ConfigureAwait(false);
            }
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        if (transport is { } active) AbortTransport(active);
        await gate.WaitAsync().ConfigureAwait(false);
        try { await DiscardTransportAsync().ConfigureAwait(false); }
        finally { gate.Release(); }
    }
}
