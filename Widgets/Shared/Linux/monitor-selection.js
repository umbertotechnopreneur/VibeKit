// SPDX-License-Identifier: MIT
export const MONITOR_SELECTION_KEY = 'target-monitors';
const ALL_MONITORS = '*';

function describeMonitor(geometry, outputs) {
    const ids = [...new Set(outputs.map(output => output.id).filter(Boolean))].sort();
    const names = [...new Set(outputs.map(output => output.name || output.id).filter(Boolean))];
    if (ids.length === 0)
        ids.push(`layout:${geometry.x},${geometry.y}`);
    return {
        ...geometry,
        id: ids[0],
        ids,
        name: names.join(' / ') || `Display ${geometry.index + 1}`,
    };
}

// Use Mutter directly in GNOME Shell. A synchronous call to Shell's own
// DisplayConfig bus service would prevent it from servicing the request.
export function getShellMonitors(layoutMonitors, display, manager = global.backend.get_monitor_manager()) {
    const outputs = new Map();
    for (const monitor of manager.get_monitors() ?? []) {
        const id = monitor.get_connector();
        if (!id)
            continue;
        const index = manager.get_monitor_for_connector(id);
        if (index < 0)
            continue;
        if (!outputs.has(index))
            outputs.set(index, []);
        outputs.get(index).push({id, name: monitor.get_display_name()});
    }
    return layoutMonitors.map(({index, x, y, width, height}) => describeMonitor({
        index, x, y, width, height, scale: display.get_monitor_scale(index),
    }, outputs.get(index) ?? []));
}

// The preferences process cannot access Shell's Meta objects. The D-Bus
// logical monitor records group the same connectors as the Shell API.
export function parseDisplayState([, monitors, logicalMonitors]) {
    const outputs = new Map(monitors.map(([[id], , properties]) => [id, {
        id,
        name: properties['display-name'],
    }]));
    return logicalMonitors.map(([x, y, scale, , primary, connectors], index) =>
        describeMonitor({index, x, y, scale, primary},
            connectors.map(([id]) => outputs.get(id) ?? {id})));
}

export function selectMonitors(monitors, selectedIds) {
    if (selectedIds.includes(ALL_MONITORS))
        return monitors;
    const selected = new Set(selectedIds);
    return monitors.filter(monitor => monitor.ids.some(id => selected.has(id)));
}

export function updateMonitorSelection(monitors, selectedIds, monitor, enabled) {
    const selected = new Set(selectedIds.includes(ALL_MONITORS)
        ? monitors.flatMap(display => display.ids)
        : selectedIds);
    for (const id of monitor.ids) {
        if (enabled)
            selected.add(id);
        else
            selected.delete(id);
    }
    return [...selected].sort();
}
