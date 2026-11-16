// SPDX-License-Identifier: MIT
import Adw from 'gi://Adw';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';

const JOKES = [
    'Family time won. Gaming time lost. The GPU makes scanlines now.',
    'Imagine a $1,000 GPU. Now ask it to look like 1985.',
    'Built for gaming. Promoted to desktop decoration.',
    'All this graphics power, just to bring back scanlines.',
    'No time to game. Plenty of time to adjust the phosphor.',
];

export function createGpuJokes(settings) {
    const group = new Adw.PreferencesGroup({title: 'GPU excuses', description: 'Just for fun · not telemetry'});
    const toggle = new Adw.SwitchRow({title: 'Rotate GPU jokes'});
    const row = new Adw.ActionRow({title: JOKES[0], title_lines: 0, use_markup: false});
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
            index = (index + 1) % JOKES.length;
            row.title = JOKES[index];
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
