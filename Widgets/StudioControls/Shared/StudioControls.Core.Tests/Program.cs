using System.Diagnostics;
using System.Text.Json;
using VibeKit.StudioControls.Core;

var tests = new (string Name, Func<Task> Run)[]
{
    ("shared scene catalog", Tests.Catalog),
    ("loopback connection and password redaction", Tests.ConnectionSettings),
    ("OBS authentication challenge", Tests.Authentication),
    ("typed request rejection preserves connection", Tests.RequestRejection),
    ("partial mutation is never replayed", Tests.NoReplay),
    ("malformed handshake closes connection", Tests.InvalidHandshake),
    ("unrelated events cannot extend deadline", Tests.AbsoluteDeadline),
    ("cancelled queued call preserves active connection", Tests.QueueCancellation),
    ("disconnect reconnects with a new transport", Tests.Reconnect),
    ("screen crop preserves viewport", Tests.Crop),
    ("invalid and unavailable framing rejected", Tests.InvalidCrop),
    ("atomic settings roundtrip and password exclusion", Tests.SettingsRoundtrip),
    ("corrupt and unknown settings preserved", Tests.InvalidSettings),
    ("cancelled settings save preserves previous file", Tests.CancelledSettings),
    ("project validation before writing", Tests.ProjectValidation),
    ("scene preparation validates and preserves files", Tests.ScenePreparation),
    ("scene preparation rejects traversal and missing assets", Tests.SceneTraversal),
    ("optional request failure isolates capabilities", Tests.OptionalCapability),
    ("optional transport failure stops reconnect cascade", Tests.OptionalTransport),
    ("confirmed recording survives refresh failure", Tests.ConfirmedRecording),
    ("optional cue failure cannot fail recording", Tests.CueFailure),
    ("saved recording path is action-local", Tests.SavedPath),
    ("stale scene and source reject delayed framing", Tests.StaleFraming),
    ("scene checked again before crop write", Tests.SceneRace),
    ("screen action writes only four crop fields", Tests.ScreenAction),
    ("vignette off preserves intensity", Tests.VignetteOff),
    ("comparison validates exactly two screen items", Tests.Comparison),
    ("missing rendered assets cause no OBS mutation", Tests.MissingCards),
    ("partial project apply reports local success honestly", Tests.PartialProject),
    ("project apply updates cards branding and sound", Tests.ApplyProject)
};

int failures = 0;
var elapsed = Stopwatch.StartNew();
foreach ((string name, Func<Task> run) in tests)
{
    try
    {
        await run().WaitAsync(TimeSpan.FromSeconds(8));
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {exception.GetType().Name}: {exception.Message}");
    }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed in {elapsed.Elapsed.TotalSeconds:F2}s; no devices or live OBS used.");
return failures == 0 ? 0 : 1;

internal static class Tests
{
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static void Check(bool condition, string message = "Assertion failed")
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T exception) { return exception; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }
    private static Task Sync(Action action) { action(); return Task.CompletedTask; }
    private static string TemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "studio-core-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
    private static JsonElement Transform => Json(FakeObs.ScreenItem()).GetProperty("sceneItemTransform");
    private static ObsClient Client(FakeTransport transport, int timeout = 500) => new(new(), () => transport, TimeSpan.FromMilliseconds(timeout));

    public static Task Catalog() => Sync(() =>
    {
        StudioCatalog catalog = StudioCatalog.Default;
        Check(catalog.Scenes.Length == 12 && catalog.CardSources.Length == 6 && catalog.Vignettes.Length == 3);
        Check(catalog.Scenes.Select(scene => scene.Name).Distinct().Count() == 12);
        Check(catalog.CanvasWidth == 2560 && catalog.OutroCameraBounds.Width == 1464);
    });

    public static async Task ConnectionSettings()
    {
        Check(new ObsConnectionSettings("::1").Endpoint.IsLoopback);
        Check(!new ObsConnectionSettings(Password: "private-test-password").ToString().Contains("private-test-password"));
        await Throws<ArgumentException>(() => Sync(() => new ObsConnectionSettings("192.0.2.1").Validate()));
        await Throws<ArgumentOutOfRangeException>(() => Sync(() => new ObsConnectionSettings(Port: 0).Validate()));
    }

    public static async Task Authentication()
    {
        var transport = new FakeTransport { Authentication = true };
        await using var client = new ObsClient(new(Password: "correct horse"), () => transport);
        await client.ConnectAsync();
        JsonElement identify = transport.Sent.Single().GetProperty("d");
        Check(identify.GetProperty("authentication").GetString() == "VYDgQQGHdoeke/SJYAVX2gumVCFoGtZEm0c2NxEWwjc=");
        Check(identify.GetProperty("eventSubscriptions").GetInt32() == 0);
        Check(!transport.Sent[0].GetRawText().Contains("correct horse"));
    }

    public static async Task RequestRejection()
    {
        var transport = new FakeTransport();
        int calls = 0;
        transport.OnRequest = (request, _) => { transport.Reply(request, new { value = 42 }, ++calls > 1, 600); return Task.CompletedTask; };
        await using var client = Client(transport);
        ObsRequestException failure = await Throws<ObsRequestException>(() => client.CallAsync("MissingFilter"));
        Check(failure.Code == 600);
        Check((await client.CallAsync("GetVersion")).GetProperty("value").GetInt32() == 42);
        Check(transport.Connects == 1 && transport.Aborts == 0);
    }

    public static async Task NoReplay()
    {
        var transport = new FakeTransport { OnRequest = (_, _) => throw new IOException("partial send") };
        await using var client = Client(transport);
        ObsConnectionException exception = await Throws<ObsConnectionException>(() => client.CallAsync("StartRecord"));
        Check(exception.RequestSent && transport.Sent.Count == 2 && transport.Aborts == 1);
    }

    public static async Task InvalidHandshake()
    {
        var transport = new FakeTransport { HelloOverride = "{\"op\":7,\"d\":{}}" };
        await using var client = Client(transport);
        ObsConnectionException exception = await Throws<ObsConnectionException>(() => client.ConnectAsync());
        Check(!exception.RequestSent && transport.Aborts == 1 && transport.Sent.Count == 0);
    }

    public static async Task AbsoluteDeadline()
    {
        var transport = new FakeTransport();
        transport.OnRequest = async (_, token) =>
        {
            for (int index = 0; index < 8; index++)
            {
                transport.Push("{\"op\":5,\"d\":{}}");
                await Task.Delay(10, token);
            }
        };
        await using var client = Client(transport, 140);
        var elapsed = Stopwatch.StartNew();
        ObsConnectionException exception = await Throws<ObsConnectionException>(() => client.CallAsync("SetInputMute"));
        Check(exception.RequestSent && elapsed.Elapsed < TimeSpan.FromSeconds(1));
        Check(transport.Aborts == 1);
    }

    public static async Task QueueCancellation()
    {
        var transport = new FakeTransport();
        var sent = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.OnRequest = (request, _) => { sent.SetResult(request); return Task.CompletedTask; };
        await using var client = Client(transport, 2000);
        Task<JsonElement> active = client.CallAsync("First");
        JsonElement first = await sent.Task;
        using var cancel = new CancellationTokenSource(30);
        await Throws<OperationCanceledException>(() => client.CallAsync("Queued", cancellationToken: cancel.Token));
        Check(transport.Aborts == 0 && transport.Sent.Count == 2);
        transport.Reply(first, new { ok = true });
        Check((await active).GetProperty("ok").GetBoolean());
    }

    public static async Task Reconnect()
    {
        var transports = new List<FakeTransport>();
        await using var client = new ObsClient(new(), () => { var transport = new FakeTransport(); transports.Add(transport); return transport; });
        await client.ConnectAsync();
        await client.DisconnectAsync();
        await client.CallAsync("GetVersion");
        Check(transports.Count == 2 && transports[0].Aborts == 1 && transports[1].Connects == 1);
    }

    public static Task Crop() => Sync(() =>
    {
        IReadOnlyDictionary<string, int> crop = ScreenFraming.CalculateCrop(Transform, 2, 0, 0);
        Check(crop.Count == 4 && crop["cropLeft"] == 480 && crop["cropRight"] == 480 && crop["cropTop"] == 270);
        crop = ScreenFraming.CalculateCrop(Transform, 99, 200, -200);
        Check(crop["cropLeft"] == 1440 && crop["cropRight"] == 0 && crop["cropTop"] == 0 && crop["cropBottom"] == 810);
        Check(ScreenFraming.CalculateCrop(Transform, 1, 99, 99).Values.All(value => value == 0));
    });

    public static async Task InvalidCrop()
    {
        await Throws<ArgumentException>(() => Sync(() => ScreenFraming.CalculateCrop(Transform, double.NaN, 0, 0)));
        await Throws<ArgumentException>(() => Sync(() => ScreenFraming.CalculateCrop(Json(new { sourceWidth = 0, sourceHeight = 1080 }), 2, 0, 0)));
        Check(ScreenFraming.Read(Json(new { sourceWidth = 1920, sourceHeight = 1080, boundsType = "OBS_BOUNDS_NONE" })) is null);
    }

    public static async Task SettingsRoundtrip()
    {
        string directory = TemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            await SettingsStore.SaveAsync(path, new ProjectSettings { Name = "Synthetic project", VibeWare = true });
            ProjectSettings saved = SettingsStore.Load<ProjectSettings>(path);
            Check(saved.Name == "Synthetic project" && saved.VibeWare);
            await SettingsStore.SaveAsync(path, new ObsConnectionSettings(Password: "never-persist-me"));
            Check(!File.ReadAllText(path).Contains("never-persist-me") && !File.ReadAllText(path).Contains("password"));
            Check(Directory.GetFiles(directory).Length == 1);
        }
        finally { Directory.Delete(directory, true); }
    }

    public static async Task InvalidSettings()
    {
        string directory = TemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            foreach (string malformed in new[] { "{broken", "{\"unexpected\":42}", "{\"name\":null}" })
            {
                File.WriteAllText(path, malformed);
                await Throws<InvalidDataException>(() => Sync(() => SettingsStore.Load<ProjectSettings>(path)));
                Check(File.ReadAllText(path) == malformed);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    public static async Task CancelledSettings()
    {
        string directory = TemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            await SettingsStore.SaveAsync(path, new ProjectSettings { Name = "Previous" });
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Throws<OperationCanceledException>(() => SettingsStore.SaveAsync(path, new ProjectSettings { Name = "Next" }, cancel.Token));
            Check(SettingsStore.Load<ProjectSettings>(path).Name == "Previous" && Directory.GetFiles(directory).Length == 1);
        }
        finally { Directory.Delete(directory, true); }
    }

    public static async Task ProjectValidation()
    {
        await Throws<ArgumentException>(() => Sync(() => new ProjectSettings { Links = ["a", "b", "c", "d"] }.Validate()));
        await Throws<ArgumentOutOfRangeException>(() => Sync(() => new ProjectSettings { SoundVolume = double.PositiveInfinity }.Validate()));
        await Throws<ArgumentException>(() => Sync(() => new ScreenPreset(2, double.NaN).Validate()));
    }

    public static async Task ScenePreparation()
    {
        string directory = TemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory, "card.png"), "synthetic asset");
            string template = Path.Combine(directory, "template.json"), output = Path.Combine(directory, "import.json");
            const string input = "{\"sources\":[{\"file\":\"${ASSET_ROOT}/card.png\",\"device_id\":\"\"}]}";
            File.WriteAllText(template, input);
            Check(ScenePackage.Prepare(template, directory, output) == output);
            JsonElement result = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(output));
            Check(result.GetProperty("sources")[0].GetProperty("file").GetString() == Path.Combine(directory, "card.png"));
            await Throws<IOException>(() => Sync(() => ScenePackage.Prepare(template, directory, output)));
            Check(File.ReadAllText(template) == input);
        }
        finally { Directory.Delete(directory, true); }
    }

    public static async Task SceneTraversal()
    {
        string directory = TemporaryDirectory();
        try
        {
            string template = Path.Combine(directory, "template.json"), output = Path.Combine(directory, "import.json");
            foreach (string path in new[] { "${ASSET_ROOT}/../escape.png", "${ASSET_ROOT}/missing.png", "bad/${ASSET_ROOT}/card.png" })
            {
                File.WriteAllText(template, Json(new { file = path }).GetRawText());
                await Throws<InvalidDataException>(() => Sync(() => ScenePackage.Prepare(template, directory, output)));
                Check(!File.Exists(output));
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    public static async Task OptionalCapability()
    {
        var obs = new FakeObs { Handle = (type, data) => type == "GetSourceFilterList" && data.GetProperty("sourceName").GetString() == "Logitech Brio" ? throw new ObsRequestException(type, 600) : null };
        StudioStatus status = await new StudioService(obs).GetStatusAsync();
        Check(!status.BlurAvailable && status.AudioAvailable.Values.All(value => value));
        Check(status.Vignettes.Count == 3 && status.Screen is not null && status.ConnectionAvailable && status.Warnings.Count == 1);
    }

    public static async Task OptionalTransport()
    {
        var obs = new FakeObs { Handle = (type, _) => type == "GetSourceFilterList" ? throw new ObsConnectionException("synthetic disconnect") : null };
        StudioStatus status = await new StudioService(obs).GetStatusAsync();
        Check(!status.ConnectionAvailable && status.Warnings.Count == 1 && obs.Requests.Count == 3);
    }

    public static async Task ConfirmedRecording()
    {
        var obs = new FakeObs { Handle = (type, _) => type == "GetRecordStatus" ? throw new ObsConnectionException("synthetic disconnect") : null };
        StudioStatus status = await new StudioService(obs).SetRecordingAsync(true);
        Check(status.ActionSucceeded && status.IsRecording && !status.IsFresh && !status.ConnectionAvailable);
        Check(obs.Requests.Count(item => item.Type == "StartRecord") == 1);
    }

    public static async Task CueFailure()
    {
        var obs = new FakeObs { Scene = "Intro", Recording = true, Handle = (type, _) => type == "TriggerMediaInputAction" ? throw new ObsRequestException(type, 600) : null };
        StudioStatus status = await new StudioService(obs).SetRecordingAsync(true);
        Check(status.ActionSucceeded && status.IsRecording && status.Warnings.Any(warning => warning.Contains("intro sound")));
    }

    public static async Task SavedPath()
    {
        var obs = new FakeObs();
        var service = new StudioService(obs);
        Check((await service.SetRecordingAsync(false)).OutputPath == "synthetic-recording.mkv");
        obs.Handle = (type, _) => type == "GetRecordStatus" ? throw new ObsConnectionException("synthetic disconnect") : null;
        StudioStatus status = await service.SetAudioMutedAsync("Mic/Aux", true);
        Check(status.OutputPath is null && status.AudioMutes["Mic/Aux"] && status.ActionSucceeded);
    }

    public static async Task StaleFraming()
    {
        var obs = new FakeObs();
        var service = new StudioService(obs);
        Check(!await service.SetScreenFramingAsync("Detail", 7, 2, 0, 0));
        obs.Items = Json(new { sceneItems = new[] { FakeObs.ScreenItem(source: "Sony") } });
        Check(!await service.SetScreenFramingAsync("Software Demo", 7, 2, 0, 0));
        Check(obs.Requests.All(item => item.Type != "SetSceneItemTransform"));
    }

    public static async Task SceneRace()
    {
        var obs = new FakeObs(); int reads = 0;
        obs.Handle = (type, _) => type == "GetCurrentProgramScene" ? Json(new { currentProgramSceneName = ++reads == 1 ? "Software Demo" : "Privacy" }) : null;
        Check(!await new StudioService(obs).SetScreenFramingAsync("Software Demo", 7, 2, 0, 0));
        Check(obs.Requests.All(item => item.Type != "SetSceneItemTransform"));
    }

    public static async Task ScreenAction()
    {
        var obs = new FakeObs();
        Check(await new StudioService(obs).SetScreenFramingAsync("Software Demo", 7, 2, 0, 0));
        JsonElement crop = obs.Requests.Single(item => item.Type == "SetSceneItemTransform").Data.GetProperty("sceneItemTransform");
        Check(crop.EnumerateObject().Count() == 4 && crop.GetProperty("cropLeft").GetInt32() == 480);
    }

    public static async Task VignetteOff()
    {
        var obs = new FakeObs();
        await new StudioService(obs).SetVignetteAsync("hardware", false, 63);
        JsonElement request = obs.Requests.First().Data;
        JsonElement settings = request.GetProperty("filterSettings");
        Check(request.GetProperty("sourceName").GetString() == "Hardware vignette");
        Check(settings.GetProperty("opacity").GetDouble() == 0 && settings.GetProperty("lab_strength").GetDouble() == 63 && !settings.GetProperty("lab_enabled").GetBoolean());
    }

    public static async Task Comparison()
    {
        var obs = new FakeObs(); var service = new StudioService(obs);
        await Throws<ObsRequestException>(() => service.SetSceneAsync("Comparison A-B"));
        Check(obs.Requests.All(item => !item.Type.StartsWith("Set")));
        obs.Requests.Clear();
        obs.Items = Json(new { sceneItems = new[] { FakeObs.ScreenItem(8, position: 1320), FakeObs.ScreenItem(7, position: 80) } });
        await service.SetSceneAsync("Comparison A-B");
        var crops = obs.Requests.Where(item => item.Type == "SetSceneItemTransform").ToArray();
        Check(crops.Length == 2 && crops[0].Data.GetProperty("sceneItemId").GetInt64() == 7);
        Check(crops[0].Data.GetProperty("sceneItemTransform").GetProperty("cropRight").GetInt32() == 960);
        Check(crops[1].Data.GetProperty("sceneItemTransform").GetProperty("cropLeft").GetInt32() == 960);
    }

    public static async Task MissingCards()
    {
        string directory = TemporaryDirectory();
        try
        {
            var obs = new FakeObs();
            await Throws<FileNotFoundException>(() => new StudioService(obs).ApplyProjectSettingsAsync(new(), directory));
            Check(obs.Requests.Count == 0);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void CreateCards(string directory)
    {
        foreach (CardSourceDefinition card in StudioCatalog.Default.CardSources) File.WriteAllText(Path.Combine(directory, card.FileName), "synthetic image");
    }

    public static async Task PartialProject()
    {
        string directory = TemporaryDirectory();
        try
        {
            CreateCards(directory); int written = 0;
            var obs = new FakeObs { Handle = (type, _) => type == "SetInputSettings" && ++written == 2 ? throw new ObsRequestException(type, 600) : null };
            ProjectApplyException failure = await Throws<ProjectApplyException>(() => new StudioService(obs).ApplyProjectSettingsAsync(new(), directory));
            Check(failure.SavedLocally && written == 2 && Directory.GetFiles(directory).Length == 6);
        }
        finally { Directory.Delete(directory, true); }
    }

    public static async Task ApplyProject()
    {
        string directory = TemporaryDirectory();
        try
        {
            CreateCards(directory);
            File.WriteAllText(Path.Combine(directory, "Lab-intro.wav"), "synthetic audio");
            File.WriteAllText(Path.Combine(directory, "VibeWare-logo.png"), "synthetic logo");
            var obs = new FakeObs { Items = Json(new { sceneItems = new[] { new { sceneItemId = 3, sourceName = "VibeWare branding" } } }) };
            StudioStatus status = await new StudioService(obs).ApplyProjectSettingsAsync(new() { VibeWare = true, SoundEnabled = false, SoundVolume = 18 }, directory);
            Check(status.ActionSucceeded && obs.Requests.Count(item => item.Type == "SetInputSettings") == 8);
            Check(obs.Requests.Single(item => item.Type == "SetSceneItemEnabled").Data.GetProperty("sceneItemEnabled").GetBoolean());
            Check(obs.Requests.Single(item => item.Type == "SetInputMute").Data.GetProperty("inputMuted").GetBoolean());
            Check(obs.Requests.Single(item => item.Type == "SetInputVolume").Data.GetProperty("inputVolumeMul").GetDouble() == .18);
        }
        finally { Directory.Delete(directory, true); }
    }
}
