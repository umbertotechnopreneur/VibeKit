// SPDX-License-Identifier: MIT
import Adw from 'gi://Adw';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Gtk from 'gi://Gtk';
import {createAbout} from './shared/ui/about.js';
import {createAccordion} from './shared/ui/preferences-layout.js';
import {SOUNDS} from './sounds.js';

export function buildPreferences(window, settings, quit = () => window.close()) {
    const page = new Adw.PreferencesPage({title: 'Effect', icon_name: 'video-display-symbolic'});
    const group = new Adw.PreferencesGroup({title: 'Magnetization'});
    const soundGroup = new Adw.PreferencesGroup({title: 'Sound'});
    const actionsGroup = new Adw.PreferencesGroup();
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
    const showNow = new Gtk.Button({label: 'Show now', valign: Gtk.Align.CENTER});
    const stop = new Gtk.Button({label: 'Stop', valign: Gtk.Align.CENTER});
    status.add_suffix(showNow);
    status.add_suffix(degauss);
    status.add_suffix(stop);
    for (const row of [active, minutes, patches]) group.add(row);
    for (const row of [sound, variant]) soundGroup.add(row);
    actionsGroup.add(status);
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
        showNow.sensitive = enabled && !settings.get_boolean('degaussing');
        stop.sensitive = enabled;
    };
    const signals = ['active', 'degaussing'].map(key => settings.connect(`changed::${key}`, syncButtons));
    window.connect('unrealize', () => signals.forEach(id => settings.disconnect(id)));
    degauss.connect('clicked', () => settings.set_int64('degauss-request', GLib.get_monotonic_time()));
    showNow.connect('clicked', () => settings.set_int64('show-now-request', GLib.get_monotonic_time()));
    stop.connect('clicked', () => settings.set_boolean('active', false));
    syncButtons();
    const section = createAccordion(page);
    const effect = section('Effect');
    const preferences = section('Settings');
    const diagnostics = section('Diagnostics');
    effect.add(group);
    preferences.add(soundGroup);
    diagnostics.add(actionsGroup);
    effect.row.expanded = true;
    const about = createAbout(window, import.meta.url, 'local.vibeware.CrtDegauss', 'MIT');
    const aboutRow = new Adw.ActionRow({title: 'About CRT Degauss', activatable: true});
    aboutRow.connect('activated', () => about.show());
    const identity = new Adw.PreferencesGroup();
    identity.add(aboutRow);
    section('About').add(identity);
    window.add(page);
    const onQuit = settings.connect('changed::quit-request', quit);
    settings.get_int64('quit-request');
    window.connect('unrealize', () => {
        settings.disconnect(onQuit);
        about.dispose();
    });
    return {about, showPreferences() { preferences.row.expanded = true; }};
}
