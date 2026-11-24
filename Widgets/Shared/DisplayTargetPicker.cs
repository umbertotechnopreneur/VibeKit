using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using static Tools.Widgets.EffectStudioLayout;

namespace Tools.Widgets;

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
