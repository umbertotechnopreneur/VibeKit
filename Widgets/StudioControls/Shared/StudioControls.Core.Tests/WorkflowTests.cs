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
        [new(WorkflowLibrary.NewId(), "Intro", 15), new(WorkflowLibrary.NewId(), "Detail", 20)]);
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
        player.StartFree(180);
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
            await (restored with { ActiveProjectId = restored.Projects[0].Id }).SaveAsync(path);
            try { await stale.SaveAsync(path); throw new Exception("Stale writer accepted."); }
            catch (IOException) { }
            byte[] before = await File.ReadAllBytesAsync(path);
            var invalid = restored with { Projects = [second with { Id = "../outside" }] };
            try { await invalid.SaveAsync(path); throw new Exception("Unsafe identifier accepted."); }
            catch (ArgumentException) { }
            byte[] after = await File.ReadAllBytesAsync(path);
            Assert(before.SequenceEqual(after));
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
}
