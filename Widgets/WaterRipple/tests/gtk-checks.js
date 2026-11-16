// Run against a packaged extension with GSETTINGS_BACKEND=memory and a temporary XDG_CONFIG_HOME.
import Adw from 'gi://Adw';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';

const directory = Gio.File.new_for_path(ARGV[0]);
const {buildControls} = await import(directory.get_child('settings-ui.js').get_uri());
const {loadSchema} = await import(directory.get_child('schema.js').get_uri());
Adw.init();
function check(condition, message) { if (!condition) throw Error(message); }
const settings = Gio.Settings.new_full(loadSchema(directory.get_child('schema.js').get_uri()), null, null);
const window = new Adw.PreferencesWindow();
const controls = buildControls(window, settings, null, true);
check(!controls.glass.active, 'Experimental Liquid Glass must default to off.');
controls.glass.active = true;
check(settings.get_boolean('liquid-glass-experimental'), 'Liquid Glass switch did not update settings.');
settings.set_boolean('liquid-glass-experimental', false);
check(!controls.glass.active, 'Liquid Glass switch did not follow settings.');
controls.preset.selected = 1;
check(settings.get_double('strength') === 7, 'Pipeline selection did not reach settings.');
controls.pipelines.create('<Test & water>');
settings.set_double('strength', 13);
check(controls.pipelines.selected.values.strength === 13, 'UI settings did not update the selected pipeline.');
check(!controls.pipelines.error, controls.pipelines.error);
const saved = controls.pipelines.selected;
controls.preset.selected = 0;
settings.set_double('strength', 14);
check(!controls.pipelines.selected && saved.values.strength === 13, 'Custom selection still edited the saved pipeline.');
controls.pipelines.select(saved);
const filter = controls.pipelines.catalog.groups[1];
controls.pipelines.applyFilterPreset(filter.presets[2]);
check(settings.get_double('chromatic-separation') === 45 && controls.pipelines.filterPresetIndex(filter) === 3, 'Shared filter preset failed.');
const selectedId = controls.pipelines.selected.id;
controls.pipelines.rename(controls.pipelines.selected, 'Renamed');
check(controls.preset.model.get_string(controls.preset.selected) === 'Renamed', 'Selector did not reflect rename.');
window.present();
const loop = new GLib.MainLoop(null, false);
GLib.timeout_add(GLib.PRIORITY_DEFAULT, 250, () => {
    window.close();
    check(!settings.get_boolean('active'), 'Closing preferences did not stop the effect.');
    const appWindow = new Adw.ApplicationWindow();
    const next = buildControls(appWindow, settings, {}, true, false, null);
    appWindow.set_content(next.page);
    check(next.pipelines.selected?.id === selectedId, 'Selected pipeline did not restore.');
    appWindow.present();
    GLib.timeout_add(GLib.PRIORITY_DEFAULT, 250, () => {
        appWindow.close();
        print('PASS GTK preferences and application views: live settings, CRUD, selection restore and cleanup');
        loop.quit();
        return GLib.SOURCE_REMOVE;
    });
    return GLib.SOURCE_REMOVE;
});
loop.run();
