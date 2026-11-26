// SPDX-License-Identifier: MIT
// Run with memory settings in the isolated GTK test harness.
import Adw from 'gi://Adw';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
const directory = Gio.File.new_for_path(ARGV[0]);
const {buildPreferences} = await import(directory.get_child('preferences-ui.js').get_uri());
Adw.init();
if (GLib.getenv('GSETTINGS_BACKEND') !== 'memory') throw Error('Memory settings required');
const source = Gio.SettingsSchemaSource.new_from_directory(directory.get_child('schemas').get_path(), Gio.SettingsSchemaSource.get_default(), false);
const settings = Gio.Settings.new_full(source.lookup('org.gnome.shell.extensions.vibeware-vignette', true), null, null);
const window = new Adw.PreferencesWindow();
const ui = buildPreferences(window, settings);
if (!ui.diagnosticShortcut.subtitle.includes('Unavailable')) throw Error('Unavailable shortcut must be visible');
settings.set_boolean('panic-available', true);
settings.set_string('status', 'Active · 2 displays');
if (!ui.diagnosticShortcut.subtitle.includes('F10') || ui.diagnosticStatus.subtitle !== 'Active · 2 displays')
    throw Error('Diagnostics did not follow actual settings');
ui.hexColor.text = '#12abef';
ui.hexColor.emit('activate');
if (settings.get_value('appearance').deepUnpack()[0] !== '#12ABEF') throw Error('Hex color did not update appearance');
ui.hexColor.text = '#bad';
ui.hexColor.emit('activate');
if (!ui.colorRow.subtitle.includes('#RRGGBB') || ui.hexColor.text !== '#12ABEF')
    throw Error('Invalid color did not preserve the previous appearance');
settings.set_boolean('active', true);
if (!ui.active.active) throw Error('Power control did not follow settings');
window.destroy();
print('PASS Vignette native preferences and live diagnostics');
