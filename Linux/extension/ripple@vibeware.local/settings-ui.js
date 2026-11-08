// SPDX-License-Identifier: GPL-3.0-or-later
import Adw from 'gi://Adw';
import Gtk from 'gi://Gtk?version=4.0';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';

export const UUID = 'ripple@vibeware.local';
export {SCHEMA} from './schema.js';
const SPECS = [
    ['strength', 'Refraction', 0, 28, 1, 0],
    ['speed', 'Speed', 50, 300, 5, 0],
    ['wavelength', 'Ring spacing', 14, 160, 2, 0],
    ['rings', 'Rings', 1, 6, 1, 0],
    ['lifetime', 'Duration', 0.6, 6, 0.1, 1],
    ['damping', 'Damping', 0.3, 3, 0.1, 1],
    ['light', 'Highlights', 0, 15, 1, 0],
    ['radius', 'Initial radius', 0, 100, 2, 0],
    ['aspect', 'Shape', 0.6, 1.6, 0.05, 2],
];
const PRESETS = {
    'Calm water': [7, 190, 104, 1, 3.2, 1.2, 1, 8, 1],
    'Water drop': [16, 240, 100, 1, 2.4, 0.8, 3, 0, 1],
    'Elastic glass': [23, 110, 130, 2, 4.2, 1.4, 0, 8, 1],
    'Fine ripples': [7, 160, 32, 4, 3.8, 0.9, 1, 8, 1],
    'Original': [21, 175, 30.65, 4.9, 3.2, 1.25, 5.5, 0, 1],
};

export function buildControls(window, settings, application = null, externalLease = false, startEffect = false) {
    window.set_title('Ripple');
    window.set_default_size(420, 690);
    const page = new Adw.PreferencesPage();
    if (!application) {
        window.set_search_enabled(false);
        window.add(page);
    }
    const group = new Adw.PreferencesGroup();
    page.add(group);
    const active = new Adw.SwitchRow({title: 'Effect'});
    settings.bind('active', active, 'active', Gio.SettingsBindFlags.DEFAULT);
    group.add(active);
    const status = new Adw.ActionRow({title: settings.get_string('status')});
    group.add(status);
    const target = new Adw.ComboRow({
        title: 'Apply to', model: Gtk.StringList.new(['Wallpaper only', 'Windows only', 'Wallpaper and windows']),
    });
    const targets = ['desktop', 'windows', 'both'];
    target.selected = Math.max(0, targets.indexOf(settings.get_string('target')));
    target.connect('notify::selected', () => settings.set_string('target', targets[target.selected]));
    group.add(target);
    const preset = new Adw.ComboRow({title: 'Preset', model: Gtk.StringList.new([...Object.keys(PRESETS), 'Custom'])});
    group.add(preset);
    let applying = false;
    const match = Object.values(PRESETS).findIndex(values => values.every((value, i) => Math.abs(value - settings.get_double(SPECS[i][0])) < 0.001));
    preset.selected = match >= 0 ? match : 5;
    preset.connect('notify::selected', () => {
        const values = Object.values(PRESETS)[preset.selected];
        if (!values)
            return;
        applying = true;
        values.forEach((value, i) => settings.set_double(SPECS[i][0], value));
        settings.set_boolean('invert', false);
        applying = false;
    });
    const parameters = new Adw.PreferencesGroup();
    page.add(parameters);
    for (const [key, title, low, high, step, digits] of SPECS) {
        const row = new Adw.SpinRow({title, digits, adjustment: new Gtk.Adjustment({
            lower: low, upper: high, step_increment: step, page_increment: step * 5,
        })});
        settings.bind(key, row, 'value', Gio.SettingsBindFlags.DEFAULT);
        parameters.add(row);
        row.connect('notify::value', () => {
            if (!applying)
                preset.selected = 5;
        });
    }
    const invert = new Adw.SwitchRow({title: 'Invert refraction'});
    settings.bind('invert', invert, 'active', Gio.SettingsBindFlags.DEFAULT);
    parameters.add(invert);
    const limits = new Adw.PreferencesGroup({title: 'Limits'});
    page.add(limits);
    for (const [key, title, low, high, step] of [
        ['patch-size', 'Area · px', 256, 512, 64],
        ['max-waves', 'Simultaneous waves', 1, 2, 1],
        ['fps', 'FPS limit', 15, 30, 1],
    ]) {
        const row = new Adw.SpinRow({title, adjustment: new Gtk.Adjustment({
            lower: low, upper: high, step_increment: step, page_increment: step,
        })});
        settings.bind(key, row, 'value', Gio.SettingsBindFlags.DEFAULT);
        limits.add(row);
    }
    const foot = new Adw.PreferencesGroup({
        description: application
            ? 'Super + Shift + Esc stops the effect. You can leave Ripple in the top bar.'
            : 'Super + Shift + Esc stops the effect. Closing this panel also stops it.',
    });
    page.add(foot);
    const stop = new Gtk.Button({label: 'Stop', css_classes: ['destructive-action'], margin_top: 6});
    let enableWhenReady = startEffect;
    stop.connect('clicked', () => {
        enableWhenReady = false;
        settings.set_boolean('active', false);
    });
    foot.add(stop);
    let closed = false;
    let timeout = 0;
    const renewLease = () => {
        settings.set_double('lease-until', GLib.get_monotonic_time() / 1e6 + 7.5);
        return GLib.SOURCE_CONTINUE;
    };
    if (!externalLease) {
        renewLease();
        timeout = GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, 1, renewLease);
    }
    const changed = settings.connect('changed::status', () => status.set_title(settings.get_string('status')));
    window.connect('close-request', () => {
        closed = true;
        settings.set_boolean('active', false);
        settings.set_double('lease-until', 0);
        Gio.Settings.sync();
        if (timeout)
            GLib.source_remove(timeout);
        settings.disconnect(changed);
        return false;
    });
    // Let GNOME load the installed extension through its supported API only.
    if (application) {
        active.set_sensitive(false);
        const cancellable = new Gio.Cancellable();
        let retries = startEffect ? 3 : 0;
        function enableExtension() {
            Gio.DBus.session.call('org.gnome.Shell.Extensions', '/org/gnome/Shell/Extensions',
                'org.gnome.Shell.Extensions', 'EnableExtension', new GLib.Variant('(s)', [UUID]),
                null, Gio.DBusCallFlags.NONE, 2000, cancellable, (connection, result) => {
                    if (closed)
                        return;

                    let ok = false;
                    let failure = 'Extension available after the next sign-in';
                    try {
                        [ok] = connection.call_finish(result).deep_unpack();
                    } catch {
                        failure = 'GNOME is unavailable';
                    }

                    if (ok) {
                        if (enableWhenReady) {
                            renewLease();
                            settings.set_boolean('active', true);
                        }
                        active.set_sensitive(true);
                    } else if (retries > 0) {
                        retries--;
                        GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, 1, () => {
                            if (!closed)
                                enableExtension();
                            return GLib.SOURCE_REMOVE;
                        });
                    } else {
                        settings.set_boolean('active', false);
                        settings.set_string('status', failure);
                    }
                });
        }
        enableExtension();
        window.connect('close-request', () => { cancellable.cancel(); return false; });
    }
    return {page, active, preset, stop, status};
}
