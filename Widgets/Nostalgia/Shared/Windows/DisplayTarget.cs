using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Tools.Widgets;

public sealed record DisplayTarget(nint Handle, string Device, string Label, (int X, int Y, int Width, int Height) Bounds)
{
    public string Id => Identifiers[0];
    public string Details { get; init; } = "";
    public string[] Identifiers { get; init; } = [Device];
    public bool Matches(string id) =>
        Identifiers.Contains(id, StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns only connected displays whose identifiers were explicitly selected.</summary>
    public static IReadOnlyList<DisplayTarget> GetSelected(IReadOnlyList<DisplayTarget> connected,
        IReadOnlyCollection<string> selectedIds) =>
        connected.Where(display => selectedIds.Any(display.Matches)).ToArray();

    /// <summary>Identifies the display hosting the studio, used to initialize a new monitor selection.</summary>
    public static DisplayTarget? GetStudioDisplay(IReadOnlyList<DisplayTarget> connected, nint owner)
    {
        var nearest = MonitorFromWindow(owner, 2); // MONITOR_DEFAULTTONEAREST
        return connected.FirstOrDefault(display => display.Handle == nearest);
    }

    public static IReadOnlyList<DisplayTarget> GetConnected()
    {
        var identities = GetMonitorIdentities();
        var displays = new List<(int Number, DisplayTarget Target)>();
        EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info)) return true;

            const string displayPrefix = @"\\.\DISPLAY";
            int number = info.Device.StartsWith(displayPrefix, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(info.Device.AsSpan(displayPrefix.Length), out int index) ? index : 0;
            string displayName = number > 0 ? $"Display {number}" : info.Device;
            var bounds = info.Monitor;
            int width = bounds.Right - bounds.Left;
            int height = bounds.Bottom - bounds.Top;
            string details = $"{width} × {height}";
            if ((info.Flags & 1) != 0) details += " · Primary";
            string label = displayName;
            string[] ids = [info.Device];
            if (identities.TryGetValue(info.Device, out var monitors))
            {
                var names = monitors.Select(identity => identity.Name).Where(name => name.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (names.Length > 0)
                {
                    label = string.Join(" / ", names);
                    details = $"{displayName} · {details}";
                }
                if (monitors.Count > 1) details += " · Mirrored";
                var monitorIds = monitors.Select(identity => identity.DevicePath).Where(path => path.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
                if (monitorIds.Length > 0) ids = monitorIds;
            }
            displays.Add((number, new DisplayTarget(monitor, info.Device, label, (bounds.Left, bounds.Top, width, height))
            {
                Details = details,
                Identifiers = ids
            }));
            return true;
        }, 0);
        return displays.OrderBy(display => display.Number).ThenBy(display => display.Target.Device)
            .Select(display => display.Target).ToArray();
    }

    private sealed record MonitorIdentity(string Name, string DevicePath);

    private static Dictionary<string, List<MonitorIdentity>> GetMonitorIdentities()
    {
        const uint activePathsOnly = 2;
        const int insufficientBuffer = 122;
        var monitors = new Dictionary<string, List<MonitorIdentity>>(StringComparer.OrdinalIgnoreCase);
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(activePathsOnly, out int pathCount, out int modeCount) != 0 ||
                pathCount == 0) break;

            var paths = new DisplayPath[pathCount];
            var modes = new DisplayMode[modeCount];
            int result = QueryDisplayConfig(activePathsOnly, ref pathCount, paths, ref modeCount, modes, 0);
            if (result == insufficientBuffer) continue;
            if (result != 0) break;

            foreach (var path in paths.AsSpan(0, pathCount))
            {
                var source = new SourceName { Header = new DeviceInfo(1, Marshal.SizeOf<SourceName>(),
                    path.Source.Adapter, path.Source.Id) };
                var target = new TargetName { Header = new DeviceInfo(2, Marshal.SizeOf<TargetName>(),
                    path.Target.Adapter, path.Target.Id) };
                if (GetSourceName(ref source) != 0 || GetTargetName(ref target) != 0 ||
                    string.IsNullOrEmpty(source.Device)) continue;

                if (!monitors.TryGetValue(source.Device, out var group))
                    monitors.Add(source.Device, group = []);
                group.Add(new MonitorIdentity(target.Name?.Trim() ?? "", target.DevicePath ?? ""));
            }
            break;
        }
        return monitors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { internal int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        internal int Size;
        internal Rect Monitor, Work;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Device;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterId { internal uint Low; internal int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathSource
    {
        internal AdapterId Adapter;
        internal uint Id, ModeIndex, Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathTarget
    {
        internal AdapterId Adapter;
        internal uint Id, ModeIndex, Technology, Rotation, Scaling, RefreshNumerator, RefreshDenominator, Ordering;
        internal int Available;
        internal uint Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayPath
    {
        internal PathSource Source;
        internal PathTarget Target;
        internal uint Flags;
    }

    // QueryDisplayConfig fills this union, but monitor naming does not use its modes.
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct DisplayMode { }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo(int type, int size, AdapterId adapter, uint id)
    {
        internal int Type = type, Size = size;
        internal AdapterId Adapter = adapter;
        internal uint Id = id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceName
    {
        internal DeviceInfo Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Device;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TargetName
    {
        internal DeviceInfo Header;
        internal uint Flags, Technology;
        internal ushort Manufacturer, Product;
        internal uint Connector;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string? Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string? DevicePath;
    }

    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool MonitorCallback(nint monitor, nint dc, nint bounds, nint data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint dc, nint bounds, MonitorCallback callback, nint data);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out int pathCount, out int modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref int pathCount, [Out] DisplayPath[] paths,
        ref int modeCount, [Out] DisplayMode[] modes, nint topology);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetSourceName(ref SourceName name);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetTargetName(ref TargetName name);
}
