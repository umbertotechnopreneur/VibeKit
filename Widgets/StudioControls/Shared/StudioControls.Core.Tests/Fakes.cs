using System.Text.Json;
using System.Threading.Channels;
using VibeKit.StudioControls.Core;

internal sealed class FakeTransport : IObsTransport
{
    private readonly Channel<string> incoming = Channel.CreateUnbounded<string>();
    public List<JsonElement> Sent { get; } = [];
    public int Aborts { get; private set; }
    public int Connects { get; private set; }
    public bool Authentication { get; set; }
    public bool FailCleanup { get; set; }
    public int Disposals { get; private set; }
    public string? HelloOverride { get; set; }
    public Func<JsonElement, CancellationToken, Task>? OnRequest { get; set; }

    public Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Connects++;
        Push(HelloOverride ?? (Authentication
            ? "{\"op\":0,\"d\":{\"rpcVersion\":1,\"authentication\":{\"salt\":\"salt-value\",\"challenge\":\"challenge-value\"}}}"
            : "{\"op\":0,\"d\":{\"rpcVersion\":1}}"));
        return Task.CompletedTask;
    }

    public async Task SendAsync(string message, CancellationToken cancellationToken)
    {
        JsonElement json = JsonSerializer.Deserialize<JsonElement>(message);
        Sent.Add(json);
        if (json.GetProperty("op").GetInt32() == 1) Push("{\"op\":2,\"d\":{\"negotiatedRpcVersion\":1}}");
        else if (OnRequest is not null) await OnRequest(json, cancellationToken);
        else Reply(json, new { });
    }

    public Task<string> ReceiveAsync(CancellationToken cancellationToken) => incoming.Reader.ReadAsync(cancellationToken).AsTask();
    public void Push(string message) => incoming.Writer.TryWrite(message);
    public void Reply(JsonElement request, object response, bool success = true, int code = 100) => Push(JsonSerializer.Serialize(new
    {
        op = 7,
        d = new
        {
            requestId = request.GetProperty("d").GetProperty("requestId").GetString(),
            requestStatus = new { result = success, code }, responseData = response
        }
    }));
    public Task CloseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public void Abort()
    {
        Aborts++;
        incoming.Writer.TryComplete(new IOException("Fake transport closed."));
        if (FailCleanup) throw new IOException("Synthetic abort failure");
    }
    public ValueTask DisposeAsync()
    {
        Disposals++;
        return FailCleanup ? ValueTask.FromException(new IOException("Synthetic dispose failure")) : ValueTask.CompletedTask;
    }
}

internal sealed class FakeObs : IObsClient
{
    public long ConnectionGeneration { get; set; } = 1;
    public List<(string Type, JsonElement Data)> Requests { get; } = [];
    public Func<string, JsonElement, JsonElement?>? Handle { get; set; }
    public string Scene { get; set; } = "Software Demo";
    public bool Recording { get; set; }
    public JsonElement Items { get; set; } = JsonSerializer.SerializeToElement(new { sceneItems = new object[] { ScreenItem() } });

    public Task<JsonElement> CallAsync(string requestType, object? data = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        JsonElement json = JsonSerializer.SerializeToElement(data ?? new { });
        Requests.Add((requestType, json));
        JsonElement? custom = Handle?.Invoke(requestType, json);
        if (custom is not null) return Task.FromResult(custom.Value);
        object response = requestType switch
        {
            "GetRecordStatus" => new { outputActive = Recording, outputPaused = false, outputTimecode = "00:00:12" },
            "GetCurrentProgramScene" => new { currentProgramSceneName = Scene },
            "GetInputMute" => new { inputMuted = false },
            "GetSourceFilterList" => new { filters = new object[]
            {
                new { filterName = "Background blur", filterEnabled = true, filterSettings = new { blur_background = 4 } },
                new { filterName = "Vignette intensity", filterEnabled = true, filterSettings = new { opacity = .25, lab_enabled = true, lab_strength = 25 } }
            } },
            "GetSceneItemList" => Items,
            "GetSceneList" => new { scenes = new[] { new { sceneName = Scene } } },
            "StopRecord" => new { outputPath = "synthetic-recording.mkv" },
            _ => new { }
        };
        return Task.FromResult(JsonSerializer.SerializeToElement(response));
    }

    public static object ScreenItem(long id = 7, string source = "ASUS ProArt", double position = 0) => new
    {
        sceneItemId = id, sourceName = source,
        sceneItemTransform = new
        {
            sourceWidth = 1920, sourceHeight = 1080, boundsType = "OBS_BOUNDS_SCALE_INNER",
            cropLeft = 0, cropRight = 0, cropTop = 0, cropBottom = 0, positionX = position, positionY = 20
        }
    };
}
