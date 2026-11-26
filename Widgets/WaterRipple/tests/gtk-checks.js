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
settings.set_string('status', 'Diagnostic test');
settings.set_boolean('panic-available', true);
settings.set_boolean('recovery-pending', true);
check(controls.diagnosticStatus.subtitle === 'Diagnostic test' && controls.diagnosticShortcut.subtitle.includes('available') &&
    controls.diagnosticRecovery.subtitle.includes('stable'), 'Diagnostics must follow live state');
settings.set_boolean('recovery-pending', false);
check(!controls.jokes.running, 'Hidden preferences must not schedule joke updates.');
controls.preset.selected = 1;
check(settings.get_double('strength') === 7, 'Pipeline selection did not reach settings.');
controls.pipelines.create('<Test & water>');
const originalRows = controls.pipelines.items.map(pipeline => controls.library.rowFor(pipeline.id));
settings.set_double('strength', 13);
check(controls.pipelines.items.every((pipeline, index) => controls.library.rowFor(pipeline.id) === originalRows[index]),
    'A live parameter edit recreated pipeline rows.');
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
check(controls.library.rowFor(selectedId) === originalRows.at(-1), 'Rename recreated the selected pipeline row.');
check(controls.library.rowFor(selectedId).title.includes('Renamed'), 'Stable row did not update its title.');
check(controls.preset.model.get_string(controls.preset.selected) === 'Renamed', 'Selector did not reflect rename.');
window.present();

let appWindow;
let next;
let shellState = 8; // ACTIVATING
let stateQueries = 0;
const shell = {
    call(_name, _path, _interface, method, _params, _type, _flags, _timeout, _cancellable, callback) {
        GLib.idle_add(GLib.PRIORITY_DEFAULT, () => {
            if (method === 'GetExtensionInfo') stateQueries++;
            const result = method === 'EnableExtension' ? new GLib.Variant('(b)', [true]) :
                new GLib.Variant('(a{sv})', [{state: new GLib.Variant('d', shellState)}]);
            callback(this, result);
            return GLib.SOURCE_REMOVE;
        });
    },
    call_finish(result) { return result; },
};
const steps = [
    [() => window.get_mapped(), 'Preferences window did not map.', () => {
        check(!controls.jokes.running, 'Collapsed settings must leave the ticker idle.');
        controls.settingsSection.expanded = true;
    }],
    [() => controls.jokes.running, 'Expanding Settings did not start the optional ticker.', () => {
        const savedSelection = controls.pipelines.selected.id;
        controls.jokes.toggle.active = false;
        check(!settings.get_boolean('show-gpu-jokes') && !controls.jokes.running && !controls.jokes.row.visible,
            'Disabling GPU jokes must hide copy and cancel the timer.');
        check(controls.pipelines.selected.id === savedSelection, 'GPU jokes changed the pipeline selection.');
        settings.set_boolean('show-gpu-jokes', true);
    }],
    [() => controls.jokes.running, 'The GPU jokes setting did not restart the mapped ticker.', () => {
        const about = controls.about.show();
        check(about.title === 'About Ripple' && about.has_css_class('widget-about'), 'About branding was not available in preferences.');
        check(controls.about.show() === about, 'Repeated About actions opened duplicate windows.');
        about.close();
        controls.about.show();
        window.close();
        check(!settings.get_boolean('active'), 'Closing preferences did not stop the effect.');
        check(!controls.jokes.running && controls.about.show() === undefined, 'Closing preferences did not dispose ticker and About.');
        const app = new Adw.Application({application_id: 'local.vibeware.RippleGtkChecks', flags: Gio.ApplicationFlags.NON_UNIQUE});
        app.register(null);
        appWindow = new Adw.ApplicationWindow({application: app});
        // The real controller intercepts close while offering Hide / Quit / Cancel.
        appWindow.connect('close-request', () => true);
        next = buildControls(appWindow, settings, app, true, false, shell);
        appWindow.set_content(next.page);
        check(next.pipelines.selected?.id === selectedId, 'Selected pipeline did not restore.');
        next.settingsSection.expanded = true;
        appWindow.present();
    }],
    [() => stateQueries > 0, 'Controller did not inspect Shell extension state.', () => {
        check(!next.active.sensitive && !settings.get_boolean('active'),
            'Controller started the effect before GNOME activated the extension.');
        next.stop.emit('clicked');
        shellState = 1; // ACTIVE
    }],
    [() => next.active.sensitive,
        'Controller did not finish connecting after GNOME confirmed activation.', () => {
            check(!settings.get_boolean('active'), 'Stop during startup must prevent delayed activation.');
            next.active.active = true;
            check(settings.get_boolean('active'), 'Manual activation should remain available after a stop.');
            next.stop.emit('clicked');
        }],
    [() => next.jokes.running, 'Application view did not start the optional ticker.', () => {
        appWindow.close();
        check(next.jokes.running, 'Intercepted close disposed a panel that remains open.');
        appWindow.hide();
        check(!next.jokes.running, 'Hiding the application did not pause the ticker.');
        appWindow.present();
    }],
    [() => next.jokes.running, 'Restoring the panel did not resume the ticker.', () => {
        next.aboutRow.emit('activated');
        check(next.about.show()?.title === 'About Ripple', 'Application About action failed.');
        appWindow.destroy();
        check(!next.jokes.running && next.about.show() === undefined, 'Direct application destruction left branding resources active.');
        print('PASS GTK preferences and application views: settings, pipelines, branded About, optional ticker and cleanup');
    }],
];

const loop = new GLib.MainLoop(null, false);
let failure;
const stepTimeout = 5_000_000;
let deadline = GLib.get_monotonic_time() + stepTimeout;
GLib.timeout_add(GLib.PRIORITY_DEFAULT, 50, () => {
    try {
        const [ready, message, action] = steps[0];
        if (!ready()) {
            check(GLib.get_monotonic_time() < deadline, message);
            return GLib.SOURCE_CONTINUE;
        }
        action();
        steps.shift();
        if (steps.length) {
            deadline = GLib.get_monotonic_time() + stepTimeout;
            return GLib.SOURCE_CONTINUE;
        }
    } catch (error) {
        failure = error;
    }
    loop.quit();
    return GLib.SOURCE_REMOVE;
});
loop.run();
if (failure) throw failure;
