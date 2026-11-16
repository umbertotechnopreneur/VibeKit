// SPDX-License-Identifier: GPL-3.0-or-later
import Adw from 'gi://Adw';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';

const JOKES = [
    'Family time won. Gaming time lost. The GPU makes ripples now.',
    'Imagine a $1,000 GPU. Now give it ripples.',
    'Built for gaming. Promoted to desktop decoration.',
    'No time for one more level. Always time for one more ripple.',
    'A very serious use of a gaming GPU.',
];

// This is UI copy, not telemetry. There is no background work when hidden.
export function createGpuJokes(settings) {
    const group = new Adw.PreferencesGroup({title: 'GPU excuses', description: 'Just for fun · not telemetry'});
    const toggle = new Adw.SwitchRow({title: 'Rotate GPU jokes'});
    const row = new Adw.ActionRow({title: JOKES[0], use_markup: false, title_lines: 0});
    settings.bind('show-gpu-jokes', toggle, 'active', Gio.SettingsBindFlags.DEFAULT);
    settings.bind('show-gpu-jokes', row, 'visible', Gio.SettingsBindFlags.GET);
    group.add(toggle);
    group.add(row);
    let index = 0;
    let timeout = 0;
    let disposed = false;
    const stop = () => {
        if (timeout) GLib.source_remove(timeout);
        timeout = 0;
    };
    const sync = () => {
        stop();
        if (disposed || !toggle.active || !row.get_mapped()) return;
        timeout = GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, 12, () => {
            if (disposed || !toggle.active || !row.get_mapped()) {
                timeout = 0;
                return GLib.SOURCE_REMOVE;
            }
            index = (index + 1) % JOKES.length;
            row.title = JOKES[index];
            return GLib.SOURCE_CONTINUE;
        });
    };
    const handlers = [
        [row, row.connect('map', sync)],
        [row, row.connect('unmap', stop)],
        [toggle, toggle.connect('notify::active', sync)],
    ];
    return {
        group, toggle, row,
        get running() { return timeout !== 0; },
        dispose() {
            if (disposed) return;
            disposed = true;
            stop();
            for (const [object, id] of handlers) object.disconnect(id);
        },
    };
}
