using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static Tools.Widgets.EffectStudioLayout;

namespace Tools.Widgets;

internal sealed record DisplayTarget(nint Handle, string Device, string Label, (int X, int Y, int Width, int Height) Bounds)
{
    internal static DisplayTarget? Resolve(string device) => device.Length == 0 ? null :
        GetConnected().FirstOrDefault(display => display.Device == device);

    internal static IReadOnlyList<DisplayTarget> GetConnected()
    {
        var displays = new List<(int Number, DisplayTarget Target)>();
        EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info)) return true;
            const string displayPrefix = @"\\.\DISPLAY";
            int number = info.Device.StartsWith(displayPrefix, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(info.Device.AsSpan(displayPrefix.Length), out int index) ? index : 0;
            string name = number > 0 ? $"Display {number}" : info.Device;
            var bounds = info.Monitor;
            int width = bounds.Right - bounds.Left;
            int height = bounds.Bottom - bounds.Top;
            string resolution = $"{width} × {height}";
            string primary = (info.Flags & 1) != 0 ? " (primary)" : "";
            displays.Add((number, new DisplayTarget(monitor, info.Device, $"{name} · {resolution}{primary}",
                (bounds.Left, bounds.Top, width, height))));
            return true;
        }, 0);
        return displays.OrderBy(display => display.Number).ThenBy(display => display.Target.Device)
            .Select(display => display.Target).ToArray();
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

    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool MonitorCallback(nint monitor, nint dc, nint bounds, nint data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint dc, nint bounds, MonitorCallback callback, nint data);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}

internal sealed class DisplayTargetPicker : StackPanel
{
    private readonly ComboBox picker = new() { DisplayMemberPath = nameof(DisplayTarget.Label) };
    private readonly TextBlock hint = SettingsHint("");
    private readonly Func<string> readSelected;
    private bool refreshing;

    internal DisplayTargetPicker(Func<string> readSelected, Action<string> setSelected, bool compact = false)
    {
        this.readSelected = readSelected;
        Spacing = compact ? 4 : 8;
        picker.MinWidth = 0;
        picker.HorizontalAlignment = HorizontalAlignment.Stretch;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(picker, "Target display");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(picker, "SettingTargetDisplayPicker");
        Children.Add(compact ? picker : SettingsRow("Target display", picker));
        Children.Add(hint);
        picker.SelectionChanged += (_, _) =>
        {
            if (refreshing || picker.SelectedItem is not DisplayTarget display) return;
            setSelected(display.Device);
            hint.Text = "Applies on next start.";
        };
        Refresh();
    }

    internal void Refresh()
    {
        var choices = new List<DisplayTarget> { new(0, "", "Control window’s display", default) };
        choices.AddRange(DisplayTarget.GetConnected());
        var selected = choices.Find(display => display.Device == readSelected());
        refreshing = true;
        picker.ItemsSource = choices;
        picker.SelectedItem = selected ?? choices[0];
        refreshing = false;
        hint.Text = selected is null
            ? "The saved display is disconnected. The effect will use the control window’s display."
            : "Applies on next start.";
    }
}
