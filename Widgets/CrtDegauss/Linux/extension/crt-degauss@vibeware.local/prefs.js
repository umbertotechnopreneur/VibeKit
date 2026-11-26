// SPDX-License-Identifier: MIT
import Adw from 'gi://Adw';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Gtk from 'gi://Gtk';
import {SOUNDS} from './sounds.js';
import {ExtensionPreferences} from 'resource:///org/gnome/Shell/Extensions/js/extensions/prefs.js';

export default class DegaussPreferences extends ExtensionPreferences {
    fillPreferencesWindow(window) {
        const settings = this.getSettings();
        const page = new Adw.PreferencesPage({title: 'Effect', icon_name: 'video-display-symbolic'});
        const group = new Adw.PreferencesGroup({title: 'Magnetization'});
        const active = new Adw.SwitchRow({title: 'Effect', subtitle: 'Primary display'});
        const minutes = Adw.SpinRow.new_with_range(1, 120, 1);
        minutes.title = 'Minutes before Degauss';
        minutes.subtitle = 'A reminder appears when this interval ends.';
        const patches = Adw.SpinRow.new_with_range(1, 2, 1);
        patches.title = 'Color patches';
        const sound = new Adw.SwitchRow({title: 'Degauss sound'});
        const variant = new Adw.ComboRow({
            title: 'Sound profile', model: Gtk.StringList.new(SOUNDS.map(([name]) => name)),
        });
        const status = new Adw.ActionRow({title: 'Status'});
        const degauss = new Gtk.Button({label: 'Degauss', valign: Gtk.Align.CENTER});
        const stop = new Gtk.Button({label: 'Stop', valign: Gtk.Align.CENTER});
        status.add_suffix(degauss);
        status.add_suffix(stop);
        for (const row of [active, minutes, patches, sound, variant, status]) group.add(row);
        settings.bind('active', active, 'active', Gio.SettingsBindFlags.DEFAULT);
        settings.bind('minutes', minutes, 'value', Gio.SettingsBindFlags.DEFAULT);
        settings.bind('patches', patches, 'value', Gio.SettingsBindFlags.DEFAULT);
        settings.bind('sound', sound, 'active', Gio.SettingsBindFlags.DEFAULT);
        settings.bind('sound-variant', variant, 'selected', Gio.SettingsBindFlags.DEFAULT);
        settings.bind('sound', variant, 'sensitive', Gio.SettingsBindFlags.GET);
        settings.bind('status', status, 'subtitle', Gio.SettingsBindFlags.GET);
        const syncButtons = () => {
            const enabled = settings.get_boolean('active');
            degauss.sensitive = enabled && !settings.get_boolean('degaussing');
            stop.sensitive = enabled;
        };
        const signals = ['active', 'degaussing'].map(key => settings.connect(`changed::${key}`, syncButtons));
        window.connect('unrealize', () => signals.forEach(id => settings.disconnect(id)));
        degauss.connect('clicked', () => settings.set_int64('degauss-request', GLib.get_monotonic_time()));
        stop.connect('clicked', () => settings.set_boolean('active', false));
        syncButtons();
        page.add(group);
        window.add(page);
    }
}
