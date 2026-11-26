using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using static Tools.Widgets.EffectStudioLayout;

namespace Tools.Widgets;

public sealed class DisplayTargetPicker : StackPanel
{
    private readonly Func<IReadOnlyCollection<string>> readSelected;
    private readonly Action<string[]> setSelected;
    private readonly TextBlock caption = SettingsHint("");

    public DisplayTargetPicker(Func<IReadOnlyCollection<string>> readSelected,
        Action<string[]> setSelected)
    {
        this.readSelected = readSelected;
        this.setSelected = setSelected;
        Spacing = 8;
        Loaded += (_, _) => Refresh();
        Refresh();
    }

    public void Refresh()
    {
        var connected = DisplayTarget.GetConnected();
        var selectedIds = readSelected();
        var selected = DisplayTarget.GetSelected(connected, selectedIds);
        var rows = new List<FrameworkElement>();
        foreach (var display in connected)
        {
            var toggle = new ToggleSwitch { IsOn = selected.Contains(display) };
            var row = PreferenceRow(display.Label, display.Details, toggle);
            AutomationProperties.SetName(toggle, $"{display.Label}, {display.Details}");
            AutomationProperties.SetAutomationId(toggle, $"Monitor-{display.Device}");
            toggle.Toggled += (_, _) =>
            {
                var ids = new HashSet<string>(readSelected(), StringComparer.OrdinalIgnoreCase);
                if (toggle.IsOn) ids.UnionWith(display.Identifiers);
                else ids.ExceptWith(display.Identifiers);
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

    private void UpdateCaption(IReadOnlyList<DisplayTarget> connected, IReadOnlyCollection<string> selectedIds)
    {
        bool missing = selectedIds.Any(id => !connected.Any(display => display.Matches(id)));
        caption.Text = missing ? "A selected monitor is disconnected. Its selection is saved."
            : "Choose where the effect runs when Effect is on.";
    }
}
