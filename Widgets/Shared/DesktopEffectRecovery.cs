// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace Tools.Widgets;

public sealed record DesktopEffectParticipantStatus(string Application, bool Running, bool Pending, bool IsLeader);

// One participant per process. The first live participant arbitrates recovery for
// the session; all decisions and membership changes are serialized by the mutex.
[SupportedOSPlatform("windows")]
public sealed class DesktopEffectRecovery : IDisposable
{
    private const int Capacity = 32768;
    private const long StaleMilliseconds = 10000, StableMilliseconds = 30000;
    private readonly string id = Guid.NewGuid().ToString("N");
    private readonly string app;
    private readonly Action resume;
    private readonly long opened = ProcessOpenedMilliseconds();
    private readonly Func<long> clock;
    private readonly Func<bool>? healthyOverride, desktopOverride;
    private readonly Mutex? gate;
    private readonly MemoryMappedFile? map;
    private string request = "";
    private long due, lastSample, stableSince, runningSince;
    private ulong lastIdle, lastKernel, lastUser;
    private int attempts;
    private bool haveSample, disposed;

    public DesktopEffectRecovery(string app, Action resume) : this(app, resume, null, null, null, null) { }

    internal DesktopEffectRecovery(string app, Action resume, string? protocol,
        Func<long>? clock, Func<bool>? healthy, Func<bool>? desktop)
    {
        if (string.IsNullOrWhiteSpace(app) || app.Length > 80) throw new ArgumentException("An application name of at most 80 characters is required.", nameof(app));
        ArgumentNullException.ThrowIfNull(resume);
        this.app = app;
        this.resume = resume;
        this.clock = clock ?? (() => Environment.TickCount64);
        healthyOverride = healthy;
        desktopOverride = desktop;
        protocol ??= @"Local\VibeWare.DesktopEffects.Recovery.v1";
        try
        {
            gate = new Mutex(false, protocol + ".Mutex");
            map = MemoryMappedFile.CreateOrOpen(protocol + ".State", Capacity);
            Available = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            gate?.Dispose();
        }
    }

    public bool Pending => request.Length != 0;
    public bool Available { get; private set; }
    public bool AllEnabled { get; private set; }
    public bool AnyEnabled { get; private set; }

    /// <summary>Reads a snapshot without joining the election or changing shared state.</summary>
    public static bool TryObserve(out IReadOnlyList<DesktopEffectParticipantStatus> participants) =>
        TryObserve(@"Local\VibeWare.DesktopEffects.Recovery.v1", Environment.TickCount64, out participants);

    internal static bool TryObserve(string protocol, long now, out IReadOnlyList<DesktopEffectParticipantStatus> participants)
    {
        participants = Array.Empty<DesktopEffectParticipantStatus>();
        bool acquired = false;
        try
        {
            using var mutex = Mutex.OpenExisting(protocol + ".Mutex");
            try
            {
                try { acquired = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) return false;
                using var mapping = MemoryMappedFile.OpenExisting(protocol + ".State", MemoryMappedFileRights.Read);
                using var view = mapping.CreateViewAccessor(0, Capacity, MemoryMappedFileAccess.Read);
                var state = Read(view);
                if (state?.Members is null) return false;
                participants = state.Members.Where(x => now >= x.Seen && now - x.Seen <= StaleMilliseconds)
                    .OrderBy(x => x.Opened).ThenBy(x => x.Order)
                    .Select(x => new DesktopEffectParticipantStatus(x.App, x.Running, x.Request.Length != 0, x.Id == state.Leader))
                    .ToArray();
                return true;
            }
            finally { if (acquired) mutex.ReleaseMutex(); }
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or IOException or
            UnauthorizedAccessException or System.Security.SecurityException or JsonException or ArgumentException or InvalidOperationException)
        { return false; }
    }

    // Manual enable starts a new retry budget; manual stop always cancels it.
    public void Reset() { request = ""; attempts = 0; runningSince = 0; }
    public void Cancel() { request = ""; runningSince = 0; }

    public bool Pause(bool enabled, int delaySeconds)
    {
        if (!enabled || !Available || attempts >= 2) { request = ""; return false; }
        if (Pending) return true;
        request = Guid.NewGuid().ToString("N");
        due = clock() + Math.Clamp(delaySeconds, 30, 300) * 1000L * (attempts + 1);
        return true;
    }

    // Invoke on the owning UI thread. If the coordinator is unavailable or the
    // state is invalid, fail closed: no automatic resume is granted.
    public void Tick(bool canResume = true, bool running = false)
    {
        if (disposed || gate is null || map is null || !Available) return;
        long currentTime = clock();
        if (!running) runningSince = 0;
        else if (runningSince == 0) runningSince = currentTime;
        else if (currentTime >= runningSince && currentTime - runningSince >= 300000) attempts = 0;
        bool acquired = false, grant = false;
        try
        {
            try { acquired = gate.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return;
            using var view = map.CreateViewAccessor();
            var state = Read(view);
            if (state is null) { Available = AllEnabled = AnyEnabled = false; return; }
            long now = currentTime;
            state.Members.RemoveAll(x => now < x.Seen || now - x.Seen > StaleMilliseconds);
            var me = state.Members.FirstOrDefault(x => x.Id == id);
            if (me is null)
            {
                me = new Member { Id = id, App = app, Opened = opened, Order = ++state.NextOrder };
                state.Members.Add(me);
            }
            me.Seen = now;
            me.CanResume = canResume;
            me.Running = running;
            AllEnabled = state.Members.Count > 0 && state.Members.All(x => x.Running);
            AnyEnabled = state.Members.Any(x => x.Running);
            foreach (var member in state.Members)
                if (member.Grant.Length != 0 && now > member.GrantUntil) member.Grant = "";
            if (me.Request != request)
            {
                me.Request = request;
                me.Grant = "";
                me.Due = due;
            }
            var leader = state.Members.OrderBy(x => x.Opened).ThenBy(x => x.Order).First();
            if (state.Leader != leader.Id)
            {
                state.Leader = leader.Id;
                stableSince = lastSample = 0;
                haveSample = false;
            }
            if (leader.Id == id)
            {
                bool healthy = (desktopOverride?.Invoke() ?? DesktopAvailable()) && (healthyOverride?.Invoke() ?? IsHealthy(now));
                if (!healthy) stableSince = 0;
                else if (stableSince == 0) stableSince = now;
                if (stableSince != 0 && now - stableSince >= StableMilliseconds && now >= state.NextGrant)
                {
                    var candidate = state.Members.Where(x => x.CanResume && x.Request.Length != 0 && x.Grant != x.Request && now >= x.Due)
                        .OrderBy(x => x.Opened).ThenBy(x => x.Order).FirstOrDefault();
                    if (candidate is not null)
                    {
                        candidate.Grant = candidate.Request;
                        candidate.GrantUntil = now + 2000;
                        state.NextGrant = now + StableMilliseconds;
                        stableSince = 0;
                    }
                }
            }
            if (canResume && (desktopOverride?.Invoke() ?? DesktopAvailable()) && request.Length != 0 && me.Grant == request && now <= me.GrantUntil)
            {
                request = me.Request = me.Grant = "";
                attempts++;
                grant = true;
            }
            Write(view, state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        {
            Available = AllEnabled = AnyEnabled = false;
            grant = false;
        }
        finally { if (acquired) gate.ReleaseMutex(); }
        if (grant && !disposed) resume();
    }

    private bool IsHealthy(long now)
    {
        bool timely = lastSample != 0 && now >= lastSample && now - lastSample <= 2000;
        lastSample = now;
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!DesktopAvailable() || !GetSystemTimes(out ulong idle, out ulong kernel, out ulong user) || !GlobalMemoryStatusEx(ref memory))
        { haveSample = false; return false; }
        bool comparable = haveSample && idle >= lastIdle && kernel >= lastKernel && user >= lastUser;
        ulong total = comparable ? kernel - lastKernel + user - lastUser : 0;
        ulong idleDelta = comparable ? idle - lastIdle : 0;
        lastIdle = idle; lastKernel = kernel; lastUser = user; haveSample = true;
        return timely && total > 0 && idleDelta <= total && (double)idleDelta / total > .15 &&
            memory.TotalPhysical > 0 && memory.AvailablePhysical >= Math.Max(memory.TotalPhysical / 10, 512UL * 1024 * 1024);
    }

    private static long ProcessOpenedMilliseconds()
    {
        using var process = Process.GetCurrentProcess();
        return process.StartTime.ToUniversalTime().ToFileTimeUtc() / 10000;
    }

    private static bool DesktopAvailable()
    {
        if (!WTSQuerySessionInformationW(0, uint.MaxValue, 8, out nint state, out int bytes)) return false;
        try { if (state == 0 || bytes < sizeof(int) || Marshal.ReadInt32(state) != 0) return false; } // WTSActive
        finally { WTSFreeMemory(state); }
        nint desktop = OpenInputDesktop(0, false, 1);
        if (desktop == 0) return false;
        try
        {
            var name = new System.Text.StringBuilder(256);
            return GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _) &&
                string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(desktop); }
    }

    private static State? Read(MemoryMappedViewAccessor view)
    {
        int length = view.ReadInt32(0);
        if (length == 0) return new State();
        if (length < 0 || length > Capacity - 4) return null;
        byte[] data = new byte[length];
        view.ReadArray(4, data, 0, length);
        var state = JsonSerializer.Deserialize<State>(data);
        const long maxExactNumber = 9007199254740991;
        if (state?.Members is null || state.Leader is null || state.NextOrder < 0 || state.NextOrder > maxExactNumber ||
            state.NextGrant < 0 || state.NextGrant > maxExactNumber ||
            state.Members.Any(x => x is null || string.IsNullOrEmpty(x.Id) || x.Id.Length > 64 ||
                string.IsNullOrWhiteSpace(x.App) || x.App.Length > 80 || x.Request is null || x.Request.Length > 64 ||
                x.Grant is null || x.Grant.Length > 64 || x.Order <= 0 || x.Opened <= 0 || x.Opened > maxExactNumber ||
                x.Seen < 0 || x.Seen > maxExactNumber || x.Due < 0 || x.Due > maxExactNumber ||
                x.GrantUntil < 0 || x.GrantUntil > maxExactNumber ||
                (x.Grant.Length != 0 && x.Grant != x.Request)) ||
            state.Members.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != state.Members.Count ||
            state.Members.Select(x => x.Order).Distinct().Count() != state.Members.Count ||
            state.Members.Any(x => x.Order > state.NextOrder)) return null;
        return state;
    }

    private static void Write(MemoryMappedViewAccessor view, State state)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(state);
        if (data.Length > Capacity - 4) throw new InvalidOperationException("Recovery state is too large.");
        view.WriteArray(4, data, 0, data.Length);
        view.Write(0, data.Length);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (gate is null || map is null) return;
        bool acquired = false;
        try
        {
            try { acquired = gate.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return;
            using var view = map.CreateViewAccessor();
            var state = Read(view);
            if (state is null) return;
            state.Members.RemoveAll(x => x.Id == id);
            Write(view, state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException) { }
        finally
        {
            if (acquired) gate.ReleaseMutex();
            map.Dispose();
            gate.Dispose();
        }
    }

    private sealed class State
    {
        public long NextOrder { get; set; }
        public long NextGrant { get; set; }
        public string Leader { get; set; } = "";
        public List<Member> Members { get; set; } = new();
        [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
    }
    private sealed class Member
    {
        public string Id { get; set; } = "";
        public string App { get; set; } = "";
        public long Order { get; set; }
        public long Opened { get; set; }
        public long Seen { get; set; }
        public long Due { get; set; }
        public long GrantUntil { get; set; }
        public bool CanResume { get; set; }
        public bool Running { get; set; }
        public string Request { get; set; } = "";
        public string Grant { get; set; } = "";
        [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        internal uint Length, MemoryLoad;
        internal ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetUserObjectInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(nint handle, int index, System.Text.StringBuilder info, int length, out int needed);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseDesktop(nint desktop);
    [DllImport("wtsapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(nint server, uint session, int infoClass, out nint buffer, out int bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(nint buffer);
}
