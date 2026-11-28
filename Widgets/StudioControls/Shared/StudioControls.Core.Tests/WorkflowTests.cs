using System.Text.Json;
using VibeKit.StudioControls.Core;

internal static class WorkflowTests
{
    private sealed class Clock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public void Advance(double seconds) => ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }

    private static Workflow Flow => new(WorkflowLibrary.NewId(), "Demo", 180,
        [new(WorkflowLibrary.NewId(), "Intro", 15), new(WorkflowLibrary.NewId(), "Detail", 20, Transition: "fade")]);
    private static void Assert(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Workflow assertion failed.");
    }

    public static Task Playback()
    {
        var time = new Clock();
        var player = new WorkflowPlayer(time);
        Workflow flow = Flow;
        player.Start(flow, live: true);
        long first = player.Ticket;
        time.Advance(100);
        player.Tick();
        Assert(player.State == WorkflowState.Switching && player.TotalRemainingSeconds == 35);
        player.Confirm(first);
        time.Advance(2.6);
        player.Pause();
        Assert(player.RemainingSeconds == 13);
        time.Advance(60);
        player.Tick();
        player.Resume();
        time.Advance(12.5);
        player.Tick();
        Assert(player.Current?.Scene == "Detail" && player.State == WorkflowState.Switching);
        player.Confirm(first);
        Assert(player.State == WorkflowState.Switching);
        long second = player.Ticket;
        player.Stop("Privacy");
        player.Confirm(second);
        player.Fail(second, "late error");
        Assert(player.State == WorkflowState.Stopped && player.Reason == "Privacy");
        player.Start(flow, live: false);
        player.Next();
        time.Advance(20);
        player.Tick();
        Assert(player.State == WorkflowState.Finished && player.Current?.Scene == "Detail" && player.TotalRemainingSeconds == 0);
        player.Start(flow, live: false);
        time.Advance(19.5);
        player.Tick();
        Assert(player.Current?.Scene == "Detail" && player.RemainingSeconds == 16);
        player.Start(flow, live: true);
        player.ExtendCurrent(20);
        Assert(player.TotalRemainingSeconds == 35);
        player.Confirm(player.Ticket);
        time.Advance(5.25);
        long extendedScene = player.Ticket;
        player.ExtendCurrent(10);
        player.ExtendCurrent(20);
        Assert(player.State == WorkflowState.Running && player.Ticket == extendedScene &&
               player.Current?.Seconds == 15 && player.RemainingSeconds == 40 && player.TotalRemainingSeconds == 60);
        time.Advance(39.5);
        player.Tick();
        Assert(player.Ticket == extendedScene && player.RemainingSeconds == 1);
        time.Advance(0.25);
        player.Tick();
        Assert(player.State == WorkflowState.Switching && player.Current?.Scene == "Detail" && player.RemainingSeconds == 20);
        player.StartFree(180);
        player.ExtendCurrent(10);
        time.Advance(185);
        player.Tick();
        Assert(player.State == WorkflowState.Running && player.RemainingSeconds == -5);
        return Task.CompletedTask;
    }

    public static async Task Persistence()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        string path = Path.Combine(directory, "workflows.json");
        try
        {
            var library = WorkflowLibrary.Load(path, "Legacy");
            var second = new WorkflowProject(WorkflowLibrary.NewId(), "Another project", [Flow, Flow]);
            library = library with { Projects = [.. library.Projects, second], ActiveProjectId = second.Id };
            await library.SaveAsync(path);
            var stale = WorkflowLibrary.Load(path, "");
            var restored = WorkflowLibrary.Load(path, "");
            Assert(restored.ActiveProjectId == second.Id && restored.Projects[1].Workflows.Length == 2);
            Assert(restored.Projects[1].Workflows[0].Steps[1].Transition == "fade");
            await (restored with { ActiveProjectId = restored.Projects[0].Id }).SaveAsync(path);
            try { await stale.SaveAsync(path); throw new Exception("Stale writer accepted."); }
            catch (IOException) { }
            byte[] before = await File.ReadAllBytesAsync(path);
            var invalid = restored with { Projects = [second with { Id = "../outside" }] };
            try { await invalid.SaveAsync(path); throw new Exception("Unsafe identifier accepted."); }
            catch (ArgumentException) { }
            byte[] after = await File.ReadAllBytesAsync(path);
            Assert(before.SequenceEqual(after));
            var legacy = System.Text.Json.Nodes.JsonNode.Parse(before)!;
            var step = legacy["projects"]![1]!["workflows"]![0]!["steps"]![0]!.AsObject();
            step.Remove("transition");
            await File.WriteAllTextAsync(path, legacy.ToJsonString());
            Assert(WorkflowLibrary.Load(path, "").Projects[1].Workflows[0].Steps[0].Transition == "default");
            step["transition"] = "Unknown";
            await File.WriteAllTextAsync(path, legacy.ToJsonString());
            try { _ = WorkflowLibrary.Load(path, ""); throw new Exception("Unknown transition accepted."); }
            catch (InvalidDataException) { }
            await File.WriteAllTextAsync(path, "{}");
            try { _ = WorkflowLibrary.Load(path, "Legacy"); throw new Exception("Corrupt workflow accepted."); }
            catch (InvalidDataException) { }
            Assert(await File.ReadAllTextAsync(path) == "{}");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    public static async Task ComparisonPreflight()
    {
        var fake = new FakeObs();
        var service = new StudioService(fake);
        var data = new { scenes = new[] { new { sceneName = "Comparison A-B" } }, currentProgramSceneName = fake.Scene };
        fake.Handle = (type, _) => type switch
        {
            "GetSceneCollectionList" => JsonSerializer.SerializeToElement(new { currentSceneCollectionName = "Synthetic" }),
            "GetSceneList" => JsonSerializer.SerializeToElement(data),
            _ => (JsonElement?)null
        };
        object Item(int x, int left, int right) => new
        {
            sourceName = StudioCatalog.Default.ScreenSource,
            sceneItemTransform = new { sourceWidth = 1921, positionX = x, cropLeft = left, cropRight = right }
        };
        fake.Items = JsonSerializer.SerializeToElement(new { sceneItems = new[] { Item(1, 960, 0), Item(0, 0, 961) } });
        Assert((await service.GetWorkflowScenesAsync(default, ["Comparison A-B"])).Contains("Comparison A-B"));
        fake.Items = JsonSerializer.SerializeToElement(new { sceneItems = new[] { Item(1, 960, 0), Item(0, 0, 0) } });
        try { await service.SetWorkflowSceneAsync("Comparison A-B", null); throw new Exception("Invalid crop accepted."); }
        catch (InvalidOperationException error) when (error.Message.Contains("calibrate")) { }
        Assert(fake.Requests.All(request => !request.Type.StartsWith("Set")));
    }

    public static async Task ObsGuards()
    {
        var fake = new FakeObs();
        string collection = "Synthetic";
        fake.Handle = (type, data) =>
        {
            if (type == "GetSceneCollectionList") return JsonSerializer.SerializeToElement(new { currentSceneCollectionName = collection });
            if (type == "GetSceneList") return JsonSerializer.SerializeToElement(new
                { currentProgramSceneName = fake.Scene, scenes = new[] { new { sceneName = "Intro" }, new { sceneName = "Detail" } } });
            if (type == "SetCurrentProgramScene") fake.Scene = data.GetProperty("sceneName").GetString()!;
            return null;
        };
        var service = new StudioService(fake);
        Assert((await service.GetWorkflowScenesAsync()).SetEquals(["Intro", "Detail"]));
        await service.SetWorkflowSceneAsync("Intro", null);
        fake.Scene = "Privacy";
        async Task ExpectStop(string expected)
        {
            int mutations = fake.Requests.Count(request => request.Type.StartsWith("Set"));
            try { await service.SetWorkflowSceneAsync("Detail", expected); throw new Exception("Unexpected scene change."); }
            catch (InvalidOperationException) { }
            Assert(fake.Requests.Count(request => request.Type.StartsWith("Set")) == mutations);
        }
        await ExpectStop("Intro");
        await service.GetWorkflowScenesAsync();
        collection = "Other synthetic collection";
        await ExpectStop("Privacy");
        await service.GetWorkflowScenesAsync();
        fake.ConnectionGeneration++;
        await ExpectStop("Privacy");
        fake.Recording = true;
        await service.GetWorkflowScenesAsync();
        fake.Recording = false;
        await ExpectStop("Privacy");
        Assert(fake.Requests.All(request => !request.Type.StartsWith("Start") && !request.Type.StartsWith("Resume")));
    }

    public static async Task Transitions()
    {
        foreach (string scenario in new[] { "restore", "existing", "cancel", "reject", "manual", "disconnect", "unavailable" })
        {
            var fake = new FakeObs { Scene = "Intro" };
            string? overridden = scenario == "existing" ? "Dissolvenza" : null;
            using var stop = new CancellationTokenSource();
            fake.Handle = (type, data) =>
            {
                switch (type)
                {
                    case "GetSceneCollectionList": return JsonSerializer.SerializeToElement(new { currentSceneCollectionName = "Synthetic" });
                    case "GetSceneList":
                        return JsonSerializer.SerializeToElement(new { currentProgramSceneName = fake.Scene,
                            scenes = new[] { new { sceneName = "Intro", sceneUuid = "first" }, new { sceneName = "Detail", sceneUuid = "second" } } });
                    case "GetSceneTransitionList":
                        return JsonSerializer.SerializeToElement(new { transitions = new[] { new { transitionName = "Dissolvenza", transitionKind = "fade_transition" } } });
                    case "GetSceneSceneTransitionOverride":
                        Assert(data.GetProperty("sceneUuid").GetString() == "second");
                        return JsonSerializer.SerializeToElement(new { transitionName = overridden, transitionDuration = 750 });
                    case "SetSceneSceneTransitionOverride":
                        Assert(data.GetProperty("sceneUuid").GetString() == "second" && !data.TryGetProperty("transitionDuration", out _));
                        overridden = data.GetProperty("transitionName").GetString();
                        if (scenario == "cancel") stop.Cancel();
                        break;
                    case "SetCurrentProgramScene":
                        Assert(data.GetProperty("sceneUuid").GetString() == "second" && overridden == "Dissolvenza");
                        if (scenario == "reject") throw new ObsRequestException(type, 600);
                        fake.Scene = "Detail";
                        if (scenario == "manual") overridden = "Operator choice";
                        if (scenario == "disconnect")
                        {
                            fake.ConnectionGeneration++;
                            throw new ObsConnectionException("Synthetic lost reply", requestSent: true);
                        }
                        break;
                }
                return null;
            };
            var service = new StudioService(fake);
            try
            {
                await service.GetWorkflowScenesAsync(default, ["Intro", "Detail"], [scenario == "unavailable" ? "cut" : "fade"]);
                await service.SetWorkflowSceneAsync("Detail", "Intro", stop.Token, "fade");
                Assert(scenario is "restore" or "existing" or "manual");
            }
            catch (WorkflowTransitionRecoveryException) when (scenario == "disconnect") { }
            catch (ObsRequestException) when (scenario == "reject") { }
            catch (OperationCanceledException) when (scenario == "cancel") { }
            catch (InvalidOperationException error) when (scenario == "unavailable" && error.Message.Contains("unavailable")) { }
            Assert(fake.Requests.Count(x => x.Type == "SetCurrentProgramScene") == (scenario is "cancel" or "unavailable" ? 0 : 1));
            Assert(overridden == (scenario switch { "existing" or "disconnect" => "Dissolvenza", "manual" => "Operator choice", _ => null }));
            if (scenario is "unavailable" or "existing") Assert(fake.Requests.All(x => x.Type != "SetSceneSceneTransitionOverride"));
        }
    }
}
