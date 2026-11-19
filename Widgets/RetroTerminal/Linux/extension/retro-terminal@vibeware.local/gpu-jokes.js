// SPDX-License-Identifier: MIT
import Adw from 'gi://Adw';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import {GPU_JOKES} from './gpu-joke-notifications.js';

export function createGpuJokes(settings) {
    const group = new Adw.PreferencesGroup({title: 'GPU excuses', description: "That's a joke."});
    const toggle = new Adw.SwitchRow({title: 'Show GPU jokes', subtitle: 'Captions and one notification at startup'});
    const row = new Adw.ActionRow({title: GPU_JOKES[0], title_lines: 0, use_markup: false});
    settings.bind('show-gpu-jokes', toggle, 'active', Gio.SettingsBindFlags.DEFAULT);
    settings.bind('show-gpu-jokes', row, 'visible', Gio.SettingsBindFlags.GET);
    group.add(toggle);
    group.add(row);
    let source = 0;
    let index = 0;
    let disposed = false;
    const stop = () => {
        if (source) GLib.source_remove(source);
        source = 0;
    };
    const sync = () => {
        if (disposed || !row.get_mapped() || !toggle.active) { stop(); return; }
        if (source) return;
        source = GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, 12, () => {
            index = (index + 1) % GPU_JOKES.length;
            row.title = GPU_JOKES[index];
            return GLib.SOURCE_CONTINUE;
        });
    };
    const handlers = [[row, row.connect('map', sync)], [row, row.connect('unmap', stop)],
        [toggle, toggle.connect('notify::active', sync)]];
    return {group, toggle, row, get running() { return source !== 0; }, dispose() {
        if (disposed) return;
        disposed = true;
        stop();
        for (const [object, signal] of handlers) object.disconnect(signal);
        Gio.Settings.unbind(toggle, 'active');
        Gio.Settings.unbind(row, 'visible');
    }};
}
