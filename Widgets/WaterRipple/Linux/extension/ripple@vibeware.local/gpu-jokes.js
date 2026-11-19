// SPDX-License-Identifier: GPL-3.0-or-later
import Adw from 'gi://Adw';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import {GPU_JOKES} from './gpu-joke-notifications.js';

// The preferences caption rotates only while the panel is visible.
export function createGpuJokes(settings) {
    const group = new Adw.PreferencesGroup({title: 'GPU excuses', description: "That's a joke."});
    const toggle = new Adw.SwitchRow({title: 'Show GPU jokes', subtitle: 'Captions and one notification at startup'});
    const row = new Adw.ActionRow({title: GPU_JOKES[0], use_markup: false, title_lines: 0});
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
            index = (index + 1) % GPU_JOKES.length;
            row.title = GPU_JOKES[index];
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
