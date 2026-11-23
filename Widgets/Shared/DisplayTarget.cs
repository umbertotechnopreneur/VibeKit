using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using static Tools.Widgets.EffectStudioLayout;

namespace Tools.Widgets;

internal sealed record DisplayTarget(nint Handle, string Device, string Label, (int X, int Y, int Width, int Height) Bounds)
{
    internal string Id => Identifiers[0];
    internal string Details { get; init; } = "";
    internal string[] Identifiers { get; init; } = [Device];
    internal bool Matches(string id) =>
        StringComparer.OrdinalIgnoreCase.Equals(id, Device) || Identifiers.Contains(id, StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyList<DisplayTarget> GetSelected(nint owner, string legacyDevice, IReadOnlyCollection<string>? selectedIds) =>
        GetSelected(GetConnected(), owner, legacyDevice, selectedIds);

    internal static IReadOnlyList<DisplayTarget> GetSelected(IReadOnlyList<DisplayTarget> connected, nint owner,
        string legacyDevice, IReadOnlyCollection<string>? selectedIds)
    {
        if (selectedIds is not null)
            return connected.Where(display => selectedIds.Any(display.Matches)).ToArray();

        var preferred = connected.FirstOrDefault(display => display.Matches(legacyDevice));
        if (preferred is not null) return new[] { preferred };

        var nearest = MonitorFromWindow(owner, 2); // MONITOR_DEFAULTTONEAREST
        var current = connected.FirstOrDefault(display => display.Handle == nearest);
        return current is null ? Array.Empty<DisplayTarget>() : new[] { current };
    }

    internal static IReadOnlyList<DisplayTarget> GetConnected()
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

internal sealed class DisplayTargetPicker : StackPanel
{
    private readonly nint owner;
    private readonly Func<IReadOnlyCollection<string>?> readSelected;
    private readonly Action<string[]> setSelected;
    private readonly Func<string> readLegacyDevice;
    private readonly TextBlock caption = SettingsHint("");

    internal DisplayTargetPicker(nint owner, Func<IReadOnlyCollection<string>?> readSelected,
        Action<string[]> setSelected, Func<string> readLegacyDevice)
    {
        this.owner = owner;
        this.readSelected = readSelected;
        this.setSelected = setSelected;
        this.readLegacyDevice = readLegacyDevice;
        Spacing = 8;
        Loaded += (_, _) => Refresh();
        Refresh();
    }

    internal void Refresh()
    {
        var connected = DisplayTarget.GetConnected();
        var selectedIds = readSelected();
        var selected = DisplayTarget.GetSelected(connected, owner, readLegacyDevice(), selectedIds);
        var rows = new List<FrameworkElement>();
        foreach (var display in connected)
        {
            var toggle = new ToggleSwitch { IsOn = selected.Contains(display) };
            var row = PreferenceRow(display.Label, display.Details, toggle);
            AutomationProperties.SetName(toggle, $"{display.Label}, {display.Details}");
            AutomationProperties.SetAutomationId(toggle, $"Monitor-{display.Device}");
            toggle.Toggled += (_, _) =>
            {
                var effective = readSelected() ?? selected.SelectMany(target => target.Identifiers).ToArray();
                var ids = new HashSet<string>(effective, StringComparer.OrdinalIgnoreCase);
                if (toggle.IsOn) ids.UnionWith(display.Identifiers);
                else
                {
                    ids.ExceptWith(display.Identifiers);
                    ids.Remove(display.Device);
                }
                setSelected(ids.ToArray());
                UpdateCaption(connected, ids);
            };
            rows.Add(row);
        }
        Children.Clear();
        if (rows.Count == 0)
        {
            Children.Add(RowGroup("Monitors", PreferenceRow("No monitors found", "Connect a display to select it.", new TextBlock())));
            return;
        }
        Children.Add(RowGroup("Monitors", rows.ToArray()));
        Children.Add(caption);
        UpdateCaption(connected, selectedIds);
    }

    private void UpdateCaption(IReadOnlyList<DisplayTarget> connected, IReadOnlyCollection<string>? selectedIds)
    {
        bool missing = selectedIds?.Any(id => !connected.Any(display => display.Matches(id))) == true;
        caption.Text = missing ? "A selected monitor is disconnected. Its selection is saved."
            : "Choose where the effect runs when Effect is on.";
    }
}
