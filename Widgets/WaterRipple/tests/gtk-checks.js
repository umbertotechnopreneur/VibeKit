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
check(!controls.jokes.running, 'Hidden preferences must not schedule joke updates.');
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
    check(controls.jokes.running, 'Visible preferences did not start the optional ticker.');
    const savedSelection = controls.pipelines.selected.id;
    controls.jokes.toggle.active = false;
    check(!settings.get_boolean('show-gpu-jokes') && !controls.jokes.running && !controls.jokes.row.visible,
        'Disabling GPU jokes must hide copy and cancel the timer.');
    check(controls.pipelines.selected.id === savedSelection, 'GPU jokes changed the pipeline selection.');
    settings.set_boolean('show-gpu-jokes', true);
    check(controls.jokes.running, 'The GPU jokes setting did not restart the mapped ticker.');
    const about = controls.about.show();
    check(about.title === 'About Ripple' && about.has_css_class('widget-about'), 'About branding was not available in preferences.');
    check(controls.about.show() === about, 'Repeated About actions opened duplicate windows.');
    about.close();
    controls.about.show();
    window.close();
    check(!settings.get_boolean('active'), 'Closing preferences did not stop the effect.');
    check(!controls.jokes.running && controls.about.show() === undefined, 'Closing preferences did not dispose ticker and About.');
    const appWindow = new Adw.ApplicationWindow();
    // The real controller intercepts close while offering Hide / Quit / Cancel.
    appWindow.connect('close-request', () => true);
    const next = buildControls(appWindow, settings, {}, true, false, null);
    appWindow.set_content(next.page);
    check(next.pipelines.selected?.id === selectedId, 'Selected pipeline did not restore.');
    appWindow.present();
    GLib.timeout_add(GLib.PRIORITY_DEFAULT, 250, () => {
        check(next.jokes.running, 'Application view did not start the optional ticker.');
        appWindow.close();
        check(next.jokes.running, 'Intercepted close disposed a panel that remains open.');
        appWindow.hide();
        check(!next.jokes.running, 'Hiding the application did not pause the ticker.');
        appWindow.present();
        GLib.timeout_add(GLib.PRIORITY_DEFAULT, 100, () => {
            check(next.jokes.running, 'Restoring the panel did not resume the ticker.');
            next.aboutRow.emit('activated');
            check(next.about.show()?.title === 'About Ripple', 'Application About action failed.');
            appWindow.destroy();
            check(!next.jokes.running && next.about.show() === undefined, 'Direct application destruction left branding resources active.');
            print('PASS GTK preferences and application views: settings, pipelines, branded About, optional ticker and cleanup');
            loop.quit();
            return GLib.SOURCE_REMOVE;
        });
        return GLib.SOURCE_REMOVE;
    });
    return GLib.SOURCE_REMOVE;
});
loop.run();
