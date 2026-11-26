using Tools.Widgets;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;

[SupportedOSPlatform("windows")]
internal static class RecoveryCoordinationChecks
{
    internal static void Run()
    {
        long now = 1000;
        bool healthy = true, desktop = true;
        int first = 0, second = 0, third = 0;
        string protocol = @"Local\VibeWare.RecoveryChecks." + Guid.NewGuid().ToString("N");
        DesktopEffectRecovery Create(string name, Action action) => new(name, action, protocol,
            () => now, () => healthy, () => desktop);
        using var a = Create("first", () => first++);
        using var b = Create("second", () => second++);
        using var c = Create("third", () => third++);
        void Pump() { a.Tick(); b.Tick(); c.Tick(); }
        void Advance(int seconds) { for (int i = 0; i < seconds; i++) { now += 1000; Pump(); } }
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }

        Pump();
        a.Tick(running: true); b.Tick(); c.Tick(); a.Tick(running: true);
        Check(a.AnyEnabled && !a.AllEnabled, "A mixed effect state was not reported.");
        b.Tick(running: true); c.Tick(running: true); a.Tick(running: true);
        Check(a.AnyEnabled && a.AllEnabled, "All running participants were not reported as enabled.");
        a.Tick(); b.Tick(); c.Tick();
        Check(a.Pause(true, 30) && b.Pause(true, 30), "Could not queue recoveries.");
        Advance(31);
        Check(first == 1 && second == 0, "The first participant did not receive the sole first grant.");
        Advance(31);
        Check(first == 1 && second == 1, "The next participant did not recover after stabilization.");

        Check(c.Pause(true, 30), "Could not queue a third recovery.");
        c.Cancel();
        Advance(35);
        Check(third == 0, "A manual cancellation was resumed.");

        Check(a.Pause(true, 30), "Could not queue the second attempt.");
        desktop = false;
        Advance(70);
        Check(first == 1, "An effect resumed while the desktop was unavailable.");
        desktop = true;
        healthy = false;
        Advance(35);
        Check(first == 1, "An effect resumed without healthy samples.");
        healthy = true;
        Advance(31);
        Check(first == 2, "The pending effect did not resume after stability returned.");
        Check(!a.Pause(true, 30), "The retry budget was not enforced.");

        for (int i = 0; i < 301; i++) { now += 1000; a.Tick(running: true); b.Tick(); c.Tick(); }
        Check(a.Pause(true, 30), "Five minutes of stable running did not reset the retry budget.");
        a.Cancel();

        Check(c.Pause(true, 30), "Could not queue failover recovery.");
        a.Dispose();
        Advance(31);
        Check(third == 1, "The next open participant did not take over after the leader exited.");
        CheckAcrossProcesses(null, false);
        uint enable = RegisterWindowMessage("VibeWare.DesktopEffects.EnableAll.v1");
        uint disable = RegisterWindowMessage("VibeWare.DesktopEffects.DisableAll.v1");
        Check(enable != 0 && disable != 0 && enable != disable && DesktopEffectCommands.IsEnableAll(enable) &&
            DesktopEffectCommands.IsDisableAll(disable) && !DesktopEffectCommands.IsEnableAll(disable),
            "Manual command identifiers did not match the public contract.");
        var oldSettings = JsonSerializer.Deserialize<VibeWare.Vignette.Preferences>(
            "{\"appearance\":{\"color\":\"#000000\",\"intensity\":0.39,\"radius\":0.43,\"softness\":0.43,\"aspect\":1},\"screens\":null,\"personal\":[]}",
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Check(oldSettings?.AutoRecover == true, "Existing Vignette settings lost the default recovery preference.");
        string? native = Environment.GetEnvironmentVariable("VIBEWARE_RECOVERY_NATIVE_WORKER");
        CheckCorruptState(native);
        if (!string.IsNullOrEmpty(native))
        {
            var commandCheck = new ProcessStartInfo(native) { UseShellExecute = false, CreateNoWindow = true };
            commandCheck.ArgumentList.Add("--commands");
            using (var commandWorker = Process.Start(commandCheck) ?? throw new Exception("Could not start native command check."))
            {
                commandWorker.WaitForExit();
                Check(commandWorker.ExitCode == 0, "Native manual command identifiers did not match.");
            }
            CheckAcrossProcesses(native, true);
            CheckAcrossProcesses(native, false);
        }
        Console.WriteLine("PASS coordinated recovery: ordering, cancellation, desktop and health gates, retry limit, failover");
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
    private static extern uint RegisterWindowMessage(string name);

    private static void CheckCorruptState(string? native)
    {
        string protocol = @"Local\VibeWare.RecoveryCorruptChecks." + Guid.NewGuid().ToString("N");
        using var mutex = new Mutex(false, protocol + ".Mutex");
        using var map = MemoryMappedFile.CreateOrOpen(protocol + ".State", 32768);
        using (var view = map.CreateViewAccessor())
        {
            byte[] data = Encoding.UTF8.GetBytes("{\"NextOrder\":1,\"NextGrant\":0,\"Leader\":\"\",\"Members\":[null]}");
            view.WriteArray(4, data, 0, data.Length);
            view.Write(0, data.Length);
        }
        using var recovery = new DesktopEffectRecovery("corrupt-state check", () => throw new Exception("Corrupt state granted a resume."),
            protocol, () => 1000, () => true, () => true);
        recovery.Tick();
        if (recovery.Available || recovery.Pause(true, 30)) throw new Exception("Managed participant accepted corrupt shared state.");
        if (native is null) return;
        var start = new ProcessStartInfo(native) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--corrupt"); start.ArgumentList.Add(protocol);
        using var worker = Process.Start(start) ?? throw new Exception("Could not start corrupt-state worker.");
        string? result = worker.StandardOutput.ReadLine();
        worker.WaitForExit();
        if (worker.ExitCode != 0 || result != "unavailable") throw new Exception("Native participant accepted corrupt shared state.");
    }

    internal static void Worker(string protocol)
    {
        long now = 1000;
        bool healthy = true, desktop = true;
        int resumed = 0;
        using var recovery = new DesktopEffectRecovery("worker", () => resumed++, protocol,
            () => now, () => healthy, () => desktop);
        string? line;
        while ((line = Console.ReadLine()) is not null)
        {
            var parts = line.Split('|');
            now = long.Parse(parts[1]);
            healthy = parts[2] == "1";
            desktop = parts[3] == "1";
            if (parts[0] == "pause") recovery.Pause(true, 30);
            else if (parts[0] == "cancel") recovery.Cancel();
            else if (parts[0] == "quit") break;
            recovery.Tick();
            Console.WriteLine($"{resumed}|{(recovery.Pending ? 1 : 0)}");
        }
    }

    private static void CheckAcrossProcesses(string? native, bool nativeLeads)
    {
        string protocol = @"Local\VibeWare.RecoveryProcessChecks." + Guid.NewGuid().ToString("N");
        Process StartWorker(bool useNative)
        {
            string executable = useNative ? native! : Environment.ProcessPath ?? throw new Exception("Process executable is unavailable.");
            var start = new ProcessStartInfo(executable)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            if (!useNative && Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            if (!useNative) start.ArgumentList.Add("--recovery-worker");
            start.ArgumentList.Add(protocol);
            return Process.Start(start) ?? throw new Exception("Could not start recovery worker.");
        }
        long now = 1000;
        static int Send(Process worker, string command, long time)
        {
            worker.StandardInput.WriteLine($"{command}|{time}|1|1");
            if (command == "quit") return 0;
            string? response = worker.StandardOutput.ReadLine();
            if (response is null) throw new Exception("Recovery worker exited: " + worker.StandardError.ReadToEnd());
            return int.Parse(response.Split('|')[0]);
        }
        using var a = StartWorker(native is not null && nativeLeads);
        Send(a, "tick", now);
        using var b = StartWorker(native is not null && !nativeLeads);
        Send(b, "tick", now);
        using var c = StartWorker(false);
        Send(c, "tick", now);
        Send(b, "pause", now);
        Send(c, "pause", now);
        if (!DesktopEffectRecovery.TryObserve(protocol, now, out var observed) || observed.Count != 3 ||
            observed.Count(x => x.IsLeader) != 1 || observed.Count(x => x.Pending) != 2)
            throw new Exception("The managed observer changed or misread participant state.");
        if (native is not null)
        {
            var observe = new ProcessStartInfo(native) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            observe.ArgumentList.Add("--observe"); observe.ArgumentList.Add(protocol); observe.ArgumentList.Add(now.ToString());
            using var observer = Process.Start(observe) ?? throw new Exception("Could not start native observer.");
            string? snapshot = observer.StandardOutput.ReadLine();
            observer.WaitForExit();
            if (observer.ExitCode != 0 || snapshot != "3|1|2") throw new Exception("The native observer changed or misread participant state.");
            if (!DesktopEffectRecovery.TryObserve(protocol, now, out observed) || observed.Count != 3)
                throw new Exception("An observer joined the election.");
        }
        int br = 0, cr = 0;
        for (int i = 0; i < 31; i++)
        {
            now += 1000;
            Send(a, "tick", now);
            br = Send(b, "tick", now);
            cr = Send(c, "tick", now);
        }
        if (br != 1 || cr != 0) throw new Exception("Cross-process arbitration did not issue one ordered grant.");
        Send(a, "quit", now);
        a.WaitForExit();
        for (int i = 0; i < 32; i++)
        {
            now += 1000;
            Send(b, "tick", now);
            cr = Send(c, "tick", now);
        }
        if (cr != 1) throw new Exception("Cross-process leader failover did not resume the remaining participant.");
        Send(b, "quit", now);
        Send(c, "quit", now);
        b.WaitForExit();
        c.WaitForExit();
        Console.WriteLine(native is null ? "PASS managed cross-process ordered grants and leader failover"
            : nativeLeads ? "PASS native leader with managed participants and failover"
            : "PASS managed leader with native participant and failover");
    }
}
