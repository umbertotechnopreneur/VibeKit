// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace VibeWare.MemoryProtection;

/// <summary>Stops the current app after sustained memory use in its live process tree.</summary>
internal sealed class ProcessMemoryWatchdog : IDisposable
{
    private const int PollMilliseconds = 2000;
    private const int GraceMilliseconds = 5000;
    private const long MaxLogBytes = 512 * 1024;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string _app;
    private readonly string _logDirectory;
    private readonly long _limitBytes;
    private readonly Action? _requestStop;
    private readonly bool _showWindowsNotice;
    private readonly bool _stopDescendants;
    private int _disposed;
    private int _triggered;

    /// <summary>Starts a monitor with a local log and an optional graceful shutdown callback.</summary>
    /// <remarks>The callback runs on the watchdog thread and should queue work before returning.</remarks>
    internal ProcessMemoryWatchdog(string app, string logDirectory, long limitBytes, Action? requestStop = null,
        bool showWindowsNotice = false, bool stopDescendants = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limitBytes);
        _app = app;
        _logDirectory = logDirectory;
        _limitBytes = limitBytes;
        _requestStop = requestStop;
        _showWindowsNotice = showWindowsNotice;
        _stopDescendants = stopDescendants;
        var thread = new Thread(Watch) { IsBackground = true, Name = app + " RAM watchdog" };
        thread.Start();
    }

    /// <summary>Reports whether this monitor requested a memory-protection shutdown.</summary>
    internal bool Triggered => Volatile.Read(ref _triggered) != 0;

    /// <summary>Cancels monitoring and any pending forced exit during normal shutdown.</summary>
    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

    /// <summary>Samples twice above the threshold before intervening.</summary>
    private void Watch()
    {
        bool previouslyOver = false;
        while (Volatile.Read(ref _disposed) == 0)
        {
            Thread.Sleep(PollMilliseconds);
            if (Volatile.Read(ref _disposed) != 0) return;
            List<ProcessMemory>? processes;
            try { processes = ReadProcessTree(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                previouslyOver = false;
                continue;
            }
            if (processes is null)
            {
                previouslyOver = false;
                continue;
            }
            long total = 0;
            foreach (var process in processes) total += process.Bytes ?? 0;
            bool over = total > _limitBytes;
            if (previouslyOver && over)
            {
                Stop(total, processes);
                return;
            }
            previouslyOver = over;
        }
    }

    /// <summary>Records the triggering sample and gives the app a bounded chance to close.</summary>
    private void Stop(long total, List<ProcessMemory> processes)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        Interlocked.Exchange(ref _triggered, 1);
        WriteLog(total, processes);
        if (Volatile.Read(ref _disposed) != 0) return;
        const string reason = "The app is closing because it exceeded its memory limit. See memory-watchdog logs in the local log folder.";
        try { Console.Error.WriteLine($"{_app}: {reason}"); }
        catch (IOException) { }
        if (_showWindowsNotice && OperatingSystem.IsWindows())
        {
            var notice = new Thread(() => MessageBox(IntPtr.Zero, reason, _app, 0x50030)) { IsBackground = true };
            notice.Start();
        }
        try { _requestStop?.Invoke(); }
        catch (Exception error) when (error is InvalidOperationException or OperationCanceledException) { }
        var timer = Stopwatch.StartNew();
        while (Volatile.Read(ref _disposed) == 0 && timer.ElapsedMilliseconds < GraceMilliseconds)
            Thread.Sleep(100);
        if (Volatile.Read(ref _disposed) != 0) return;
        if (_stopDescendants) StopDescendants(processes);
        Environment.Exit(137);
    }

    /// <summary>Signals observed descendants through OS handles after checking creation identity.</summary>
    private static void StopDescendants(List<ProcessMemory> processes)
    {
        for (int i = processes.Count - 1; i > 0; i--)
        {
            var entry = processes[i];
            if (entry.Started <= 0) continue;
            if (OperatingSystem.IsWindows())
            {
                using var process = OpenProcess(0x1001, false, entry.Pid);
                if (!process.IsInvalid && GetProcessTimes(process, out long started, out _, out _, out _) && started == entry.Started)
                    _ = TerminateProcess(process, 137);
            }
            else if (OperatingSystem.IsLinux())
            {
                try
                {
                    int fd = OpenPidFileDescriptor(entry.Pid, 0);
                    if (fd < 0) continue;
                    try
                    {
                        if (ReadLinuxProcess($"/proc/{entry.Pid}", entry.Pid)?.Started == entry.Started)
                            _ = SignalPidFileDescriptor(fd, 9, IntPtr.Zero, 0);
                    }
                    finally { _ = CloseFileDescriptor(fd); }
                }
                catch (EntryPointNotFoundException) { return; }
            }
        }
    }

    /// <summary>Persists the per-process snapshot without writing arguments or file paths.</summary>
    private void WriteLog(long total, List<ProcessMemory> processes)
    {
        try
        {
            Directory.CreateDirectory(_logDirectory);
            // A PID-specific file avoids cross-process rotation races during concurrent app instances.
            string path = Path.Combine(_logDirectory, $"memory-watchdog-{Environment.ProcessId}.jsonl");
            if (File.Exists(path) && new FileInfo(path).Length >= MaxLogBytes) File.Delete(path);
            string line = JsonSerializer.Serialize(new
            {
                atUtc = DateTimeOffset.UtcNow, kind = "guard.memory-limit", app = _app,
                rootPid = Environment.ProcessId, limitBytes = _limitBytes, bytes = total,
                metric = OperatingSystem.IsWindows() ? "privateBytes" : "rssBytes", processes
            }, Json);
            File.AppendAllText(path, line + "\n");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Returns a sample confined to the current process and its living descendants.</summary>
    private static List<ProcessMemory>? ReadProcessTree()
    {
        int rootPid = Environment.ProcessId;
        // Exclude a PID reused after enumeration began, even if its old entry had an owned parent.
        long snapshotStarted = OperatingSystem.IsWindows() ? DateTime.UtcNow.ToFileTimeUtc() : 0;
        TreeEntry? root;
        var children = new Dictionary<int, List<TreeEntry>>();
        if (OperatingSystem.IsWindows())
        {
            using var snapshot = CreateToolhelp32Snapshot(2, 0);
            if (snapshot.IsInvalid) return null;
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Name = string.Empty };
            if (!Process32First(snapshot, ref entry)) return null;
            root = null;
            do
            {
                var node = new TreeEntry((int)entry.Pid, (int)entry.ParentPid, entry.Name, 0, null);
                if (node.Pid == rootPid) root = node;
                else AddChild(children, node);
            }
            while (Process32Next(snapshot, ref entry));
        }
        else if (OperatingSystem.IsLinux())
        {
            root = null;
            foreach (string directory in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(directory), out int pid)) continue;
                var node = ReadLinuxProcess(directory, pid);
                if (node is null) continue;
                if (pid == rootPid) root = node;
                else AddChild(children, node);
            }
        }
        else return null;

        if (root is null) return null;
        var result = new List<ProcessMemory>();
        var pending = new Queue<(TreeEntry Entry, long ParentStarted)>();
        var visited = new HashSet<int>();
        pending.Enqueue((root, 0));
        while (pending.TryDequeue(out var item))
        {
            var entry = item.Entry;
            if (!visited.Add(entry.Pid)) continue;
            if (OperatingSystem.IsWindows())
            {
                entry = ReadWindowsProcess(entry);
                if (entry.Started > snapshotStarted) continue;
            }
            // Windows retains the numeric parent PID after the parent exits. Its replacement is newer.
            if (item.ParentStarted > 0 && entry.Started > 0 && entry.Started < item.ParentStarted) continue;
            result.Add(new ProcessMemory(entry.Pid, entry.ParentPid, entry.Name, entry.Bytes, entry.Started));
            if (children.TryGetValue(entry.Pid, out var descendants) && entry.Started > 0)
                foreach (var child in descendants) pending.Enqueue((child, entry.Started));
        }
        return result.Count > 0 && result[0].Bytes.HasValue ? result : null;
    }

    /// <summary>Indexes a process by its parent for linear-time subtree traversal.</summary>
    private static void AddChild(Dictionary<int, List<TreeEntry>> children, TreeEntry entry)
    {
        if (!children.TryGetValue(entry.ParentPid, out var entries))
            children[entry.ParentPid] = entries = [];
        entries.Add(entry);
    }

    /// <summary>Reads Linux RSS, creation time and the process name from a single proc record.</summary>
    private static TreeEntry? ReadLinuxProcess(string directory, int pid)
    {
        try
        {
            string stat = File.ReadAllText(Path.Combine(directory, "stat"));
            int open = stat.IndexOf('(');
            int close = stat.LastIndexOf(')');
            if (open < 0 || close <= open || close + 2 >= stat.Length) return null;
            string[] fields = stat[(close + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 22 || fields[0] == "Z"
                || !int.TryParse(fields[1], out int parentPid)
                || !long.TryParse(fields[19], out long started)
                || !long.TryParse(fields[21], out long pages)) return null;
            return new TreeEntry(pid, parentPid, stat[(open + 1)..close], started, Math.Max(0, pages) * Environment.SystemPageSize);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Samples private committed bytes through a read-only handle to the selected PID.</summary>
    private static TreeEntry ReadWindowsProcess(TreeEntry entry)
    {
        using var process = OpenProcess(0x1000, false, entry.Pid);
        if (process.IsInvalid || !GetProcessTimes(process, out long started, out long exited, out _, out _) || exited != 0)
            return entry;
        var counters = new ProcessCounters { Size = (uint)Marshal.SizeOf<ProcessCounters>() };
        return entry with
        {
            Started = started,
            Bytes = K32GetProcessMemoryInfo(process, ref counters, counters.Size) ? (long)counters.PrivateBytes : null
        };
    }

    private sealed record TreeEntry(int Pid, int ParentPid, string Name, long Started, long? Bytes);
    private sealed record ProcessMemory(int Pid, int ParentPid, string Name, long? Bytes,
        [property: JsonIgnore] long Started);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, Pid;
        public nuint DefaultHeapId;
        public uint Module, Threads, ParentPid;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessCounters
    {
        public uint Size, PageFaults;
        public nuint PeakWorkingSet, WorkingSet, PeakPagedPool, PagedPool, PeakNonPagedPool, NonPagedPool;
        public nuint Commit, PeakCommit, PrivateBytes;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, int processId);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern bool K32GetProcessMemoryInfo(SafeProcessHandle process, ref ProcessCounters counters, uint size);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
    [DllImport("user32.dll", EntryPoint = "MessageBoxW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr window, string text, string caption, uint type);
    [DllImport("libc", EntryPoint = "pidfd_open")]
    private static extern int OpenPidFileDescriptor(int pid, uint flags);
    [DllImport("libc", EntryPoint = "pidfd_send_signal")]
    private static extern int SignalPidFileDescriptor(int fd, int signal, IntPtr info, uint flags);
    [DllImport("libc", EntryPoint = "close")]
    private static extern int CloseFileDescriptor(int fd);
}
