/* VBWR B
 * Project: TermalRight VibeWare
 * Author: Umberto Giacobbi
 * SPDX-License-Identifier: MIT
 * Purpose: Captures process memory for a guarded Windows job at the limit event.
 * VBWR E */

using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ThermalrightVibeWare.Photino;

internal sealed record JobMemorySnapshot(
    DateTimeOffset SampledAtUtc, int RootPid, bool ProcessListComplete, IReadOnlyList<JobProcessMemory> Processes)
{
    private const int BasicProcessIdList = 3;
    private const int MoreData = 234;
    private const uint QueryLimitedInformation = 0x1000;
    private const int HeaderSize = 2 * sizeof(int);
    private const int MaxProcessIds = 4096;

    internal static JobMemorySnapshot Capture(SafeHandle job, int rootPid)
    {
        var sampledAt = DateTimeOffset.UtcNow;
        var (ids, complete) = ReadProcessIds(job);
        ids.Add(rootPid);
        var processes = ids.Select(pid => ReadProcess(job, pid))
            .OrderByDescending(process => process.PrivateBytes).ThenBy(process => process.Pid).ToArray();
        return new JobMemorySnapshot(sampledAt, rootPid, complete, processes);
    }

    private static (HashSet<int> Ids, bool Complete) ReadProcessIds(SafeHandle job)
    {
        int capacity = 32;
        while (true)
        {
            int size = HeaderSize + IntPtr.Size * capacity;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.WriteInt64(buffer, 0);
                bool success = QueryInformationJobObject(job, BasicProcessIdList, buffer, (uint)size, IntPtr.Zero);
                if (!success && Marshal.GetLastWin32Error() != MoreData) return ([], false);
                int assigned = Marshal.ReadInt32(buffer);
                int count = Marshal.ReadInt32(buffer, sizeof(int));
                if (assigned > capacity && capacity < MaxProcessIds)
                {
                    capacity = Math.Min(MaxProcessIds, Math.Max(assigned, capacity * 2));
                    continue;
                }
                var ids = new HashSet<int>();
                for (int i = 0; i < Math.Min(count, capacity); i++)
                    ids.Add(Marshal.ReadIntPtr(buffer, HeaderSize + IntPtr.Size * i).ToInt32());
                return (ids, success && count == assigned);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    private static JobProcessMemory ReadProcess(SafeHandle job, int pid)
    {
        using var handle = OpenProcess(QueryLimitedInformation, false, pid);
        // A process can exit after enumeration. Verify membership before reading a potentially reused PID.
        if (handle.IsInvalid || !IsProcessInJob(handle, job, out bool member) || !member)
            return new JobProcessMemory(pid, null, null, null, null);

        var counters = new ProcessMemoryCounters { Size = (uint)Marshal.SizeOf<ProcessMemoryCounters>() };
        bool measured = K32GetProcessMemoryInfo(handle, ref counters, counters.Size);
        var image = new StringBuilder(1024);
        uint imageLength = (uint)image.Capacity;
        string? name = QueryFullProcessImageName(handle, 0, image, ref imageLength)
            ? Path.GetFileName(image.ToString()) : null;
        return new JobProcessMemory(pid, name, measured ? counters.PrivateUsage.ToUInt64() : null,
            measured ? counters.WorkingSet.ToUInt64() : null, measured ? counters.PeakCommit.ToUInt64() : null);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint Size, PageFaultCount;
        public UIntPtr PeakWorkingSet, WorkingSet, PeakPagedPool, PagedPool, PeakNonPagedPool, NonPagedPool;
        public UIntPtr Commit, PeakCommit, PrivateUsage;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(SafeHandle job, int informationClass, IntPtr buffer, uint size, IntPtr returnedLength);

    [DllImport("kernel32.dll")]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool IsProcessInJob(SafeProcessHandle process, SafeHandle job, out bool member);

    [DllImport("kernel32.dll")]
    private static extern bool K32GetProcessMemoryInfo(SafeProcessHandle process, ref ProcessMemoryCounters counters, uint size);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder image, ref uint size);
}

internal sealed record JobProcessMemory(int Pid, string? Name, ulong? PrivateBytes, ulong? WorkingSetBytes, ulong? PeakCommitBytes);
