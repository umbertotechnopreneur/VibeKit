// SPDX-License-Identifier: MIT
import GLib from 'gi://GLib';

const decoder = new TextDecoder();

function proc(path) {
    return decoder.decode(GLib.file_get_contents(path)[1]);
}

export function processMemory(id = 'self') {
    try {
        const stat = proc(`/proc/${id}/stat`);
        const start = stat.indexOf('(');
        const end = stat.lastIndexOf(')');
        const status = proc(`/proc/${id}/status`);
        const rss = /^VmRSS:\s+(\d+)\s+kB$/m.exec(status);
        if (start < 0 || end <= start || !rss) return null;
        return {
            pid: Number(stat.slice(0, start).trim()),
            parentPid: Number(stat.slice(end + 2).split(' ')[1]),
            name: stat.slice(start + 1, end),
            rssBytes: Number(rss[1]) * 1024,
        };
    } catch {
        return null;
    }
}

// GNOME Shell owns the process for all extensions. System pressure can stop this effect only.
export function shellPressure() {
    try {
        const info = proc('/proc/meminfo');
        const total = Number(/^MemTotal:\s+(\d+)\s+kB$/m.exec(info)?.[1]) * 1024;
        const availableBytes = Number(/^MemAvailable:\s+(\d+)\s+kB$/m.exec(info)?.[1]) * 1024;
        const limitBytes = Math.min(2 * 1024 * 1024 * 1024, Math.max(512 * 1024 * 1024, Math.ceil(total * 0.05)));
        return availableBytes < limitBytes ? {
            scope: 'shared-gnome-shell',
            metric: 'systemAvailableBytes',
            limitBytes,
            availableBytes,
            processes: [processMemory()].filter(Boolean),
        } : null;
    } catch {
        return null;
    }
}

// Require two low samples. Removing the source cancels monitoring when the component closes.
export function watchMemory(sample, stop) {
    let previous = false;
    return GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, 3, () => {
        const event = sample();
        if (event && previous) {
            stop({atUtc: new Date().toISOString(), ...event});
            return GLib.SOURCE_REMOVE;
        }
        previous = Boolean(event);
        return GLib.SOURCE_CONTINUE;
    });
}
