// Run with memory settings and an empty temporary XDG_CONFIG_HOME.
import Adw from 'gi://Adw';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Gtk from 'gi://Gtk?version=4.0';
import Gdk from 'gi://Gdk?version=4.0';
const directory = Gio.File.new_for_path(ARGV[0]);
const {buildControls} = await import(directory.get_child('settings-ui.js').get_uri());
const {BUILTINS, DEFAULTS, readValues} = await import(directory.get_child('settings-model.js').get_uri());
Adw.init();
const source = Gio.SettingsSchemaSource.new_from_directory(directory.get_child('schemas').get_path(), Gio.SettingsSchemaSource.get_default(), false);
const settings = Gio.Settings.new_full(source.lookup('org.gnome.shell.extensions.vibeware-retro-terminal', true), null, null);
const check = (value, message) => { if (!value) throw Error(message); };
check(GLib.getenv('GSETTINGS_BACKEND') === 'memory', 'GTK checks require memory settings');
settings.set_string('saved-presets', JSON.stringify([{name: 'Previous look', values: {...DEFAULTS, preset: 1}}]));
const legacy = settings.get_string('saved-presets');
const window = new Adw.PreferencesWindow();
const ui = buildControls(window, settings);
check(!ui.active.active, 'Must start disabled');
check(!ui.controls['mask-strength'].sensitive, 'Mask strength is inactive with no mask');
settings.set_double('scanlines', 0);
settings.set_double('glow', 0);
check(!ui.controls['scanline-spacing'].sensitive && !ui.controls['scanline-width'].sensitive &&
    !ui.controls['glow-radius'].sensitive, 'Fine controls are inactive when their effects are zero');
settings.reset('scanlines');
settings.reset('glow');
check(ui.controls['scanline-spacing'].sensitive && ui.controls['scanline-width'].sensitive &&
    ui.controls['glow-radius'].sensitive, 'Fine controls return when their effects are active');
check(ui.pipeline.enable_search && ui.pipeline.expression, 'The pipeline selector exposes native search');
for (const {name} of BUILTINS) {
    const index = ui.pipelines.items.findIndex(item => item.name === name);
    check(index >= 0 && ui.pipeline.model.get_string(index + 1) === name, `${name} is selectable`);
    ui.pipeline.selected = index + 1;
    const applied = readValues(settings);
    check(ui.pipelines.selected?.name === name &&
        Object.entries(ui.pipelines.selected.values).every(([key, value]) => applied[key] === value),
        `${name} applies all its monitor settings`);
    check(!ui.active.active, 'Choosing a look leaves CRT power under user control');
}
ui.pipeline.selected = 0;
for (const key of Object.keys(DEFAULTS)) settings.reset(key);
check(Object.values(ui.expanders).length === 4 && Object.values(ui.expanders).every(row => !row.expanded),
    'All four control sections start collapsed');
check(ui.pipelines.items.some(item => item.name === 'Previous look') && settings.get_string('saved-presets') === legacy,
    'The old saved look migrates without deleting the original');
check(ui.preset.selected === 0, 'Opening the library must not apply a migrated look');
check(!ui.jokes.running, 'Captions are idle on the Effect page');
ui.active.active = true;
check(settings.get_boolean('active'), 'Enable binding');
ui.preset.selected = 1;
check(settings.get_int('preset') === 1, 'Palette binding');
ui.controls.intensity.value = 0.42;
check(Math.abs(settings.get_double('intensity') - 0.42) < 0.001, 'Intensity binding');
for (const [key, value] of [
    ['scanline-spacing', 5], ['scanline-width', 1.5], ['glow-radius', 4],
    ['black-level', 0.08], ['contrast', 1.4], ['mask-strength', 0.3],
    ['persistence-duration', 0.3], ['persistence-strength', 0.4],
    ['glitch-intensity', 0.6], ['glitch-interval-min', 30], ['glitch-interval-max', 90],
    ['glitch-duration-min', 150], ['glitch-duration-max', 350],
]) {
    ui.controls[key].value = value;
    check(Math.abs(settings.get_double(key) - value) < 0.001, `${key} binding`);
}
ui.controls.mask.selected = 2;
check(settings.get_int('mask') === 2 && ui.controls['mask-strength'].sensitive, 'Mask selection enables strength');
ui.controls['custom-color'].active = true;
const color = new Gdk.RGBA();
color.parse('#12abef');
ui.color.rgba = color;
check(settings.get_string('phosphor-color') === '#12abef', 'Native color picker binding');
settings.set_string('phosphor-color', '#fedcba');
check(Math.abs(ui.color.rgba.red - 254 / 255) < 0.001, 'Color picker follows settings');
ui.controls['persistence-enabled'].active = true;
check(settings.get_boolean('persistence-enabled') && ui.controls['persistence-duration'].sensitive,
    'Persistence toggle and dependent controls');
ui.random.active = true;
check(settings.get_boolean('glitches-enabled'), 'Random glitch toggle binding');
for (const key of ['glitch-rgb', 'glitch-tear', 'glitch-noise', 'glitch-band']) {
    ui.controls[key].active = false;
    check(!settings.get_boolean(key), `${key} toggle binding`);
}
const request = settings.get_uint('glitch-request');
ui.tryGlitch.emit('clicked');
check(settings.get_uint('glitch-request') === request + 1, 'Try now command');

// Exercise the visible library actions, not just direct model calls.
ui.library.create.emit('clicked');
ui.library.name.text = '<Custom & cyan>';
ui.library.save.emit('clicked');
check(!ui.pipelines.error, ui.pipelines.error);
check(ui.pipelines.selected?.name === '<Custom & cyan>' && !ui.library.editor.visible, 'Create pipeline from the current controls');
const selectedId = ui.pipelines.selected.id;
check(ui.pipelines.selected.values['phosphor-color'] === '#fedcba', 'New pipeline stores the custom color');
for (const key of ['active', 'glitch-request', 'show-gpu-jokes'])
    check(!Object.hasOwn(ui.pipelines.selected.values, key), `Pipeline excludes ${key}`);
const selectedView = ui.library.rows.get(selectedId);
const renameButton = ui.library.actions.get(selectedId).rename;
const stableViews = new Map(ui.library.rows);
for (let value = 0.30; value <= 0.40; value += 0.01) ui.controls.intensity.value = value;
for (const [id, view] of stableViews)
    check(ui.library.rows.get(id) === view, 'Editing parameters must retain hidden pipeline rows');
check(ui.library.actions.get(selectedId).rename === renameButton, 'Editing retains the row actions');
renameButton.emit('clicked');
check(ui.library.name.text === '<Custom & cyan>', 'An existing action resolves the current immutable item');
ui.library.name.text = 'Personal cyan';
ui.library.save.emit('clicked');
check(ui.pipeline.model.get_string(ui.pipeline.selected) === 'Personal cyan', 'Selector follows rename');
check(ui.library.rows.get(selectedId) === selectedView && selectedView.row.title === 'Personal cyan',
    'Renaming updates the same native row');
ui.pipeline.selected = 1;
check(ui.pipelines.builtIn(ui.pipelines.selected) && !settings.get_boolean('glitches-enabled'), 'Pipeline selector applies the built-in immediately');
const original = ui.pipelines.selected;
check(!ui.library.actions.get(original.id).rename && !ui.library.actions.get(original.id).delete,
    'Built-in pipelines expose duplication, not rename or delete');
ui.library.actions.get(original.id).duplicate.emit('clicked');
const duplicateId = ui.pipelines.selected.id;
const duplicateView = ui.library.rows.get(duplicateId);
const deleteDuplicate = ui.library.actions.get(duplicateId).delete;
check(duplicateId !== original.id && !ui.pipelines.builtIn(ui.pipelines.selected), 'Duplicate creates an editable personal pipeline');
ui.controls.intensity.value = 0.35;
check(ui.pipelines.selected.values.intensity === 0.35 && original.values.intensity !== 0.35,
    'Editing a duplicated pipeline keeps its built-in unchanged');

window.present();
const loop = new GLib.MainLoop(null, false);
let failed = null;
let firstCaption;
let restoredWindow;
const steps = [
    [450, () => {
        check(ui.stop.get_mapped(), 'Stop stays accessible above the collapsed controls');
        ui.effectSection.expanded = true;
        check(!ui.controls.intensity.get_mapped(), 'Collapsed Phosphor hides its controls');
        ui.expanders.phosphor.expanded = true;
    }],
    [400, () => {
        check(ui.controls.intensity.get_mapped(), 'Opening Phosphor reveals its controls');
        ui.expanders.phosphor.expanded = false;
        check(ui.pipelines.flush(), ui.pipelines.error);
        check(ui.library.rows.get(duplicateId) === duplicateView, 'Autosave retains the native row');
        deleteDuplicate.emit('clicked');
        check(!ui.pipelines.error, 'An action retained across autosave resolves the latest item');
        check(!ui.pipelines.items.some(item => item.id === duplicateId), 'Delete removes only the chosen personal pipeline');
        ui.pipeline.selected = ui.pipelines.items.findIndex(item => item.id === selectedId) + 1;
        check(settings.get_string('phosphor-color') === '#fedcba', 'Selecting personal pipeline restores its color');
        ui.controls['scanline-spacing'].value = 6;
    }],
    [350, () => {
        check(ui.pipelines.selected.values['scanline-spacing'] === 6 && !ui.pipelines.error, 'Personal edits save after the quiet interval');
        ui.library.section.expanded = true;
    }],
    [350, () => {
        check(!ui.jokes.running, 'The Pipelines section does not run the joke timer');
        ui.stop.emit('clicked');
        check(!settings.get_boolean('active') && !ui.tryGlitch.sensitive, 'Stop cancels the effect and disables glitch preview');
        ui.aboutSection.expanded = true;
    }],
    [400, () => {
        check(ui.jokes.running, 'Captions run only on the expanded About section');
        firstCaption = ui.jokes.row.title;
    }],
    [13500, () => {
        check(ui.jokes.row.title !== firstCaption, 'Captions advance at the normal 12 second interval');
        ui.jokes.toggle.active = false;
        check(!settings.get_boolean('show-gpu-jokes') && !ui.jokes.running && !ui.jokes.row.visible,
            'The caption switch hides the row and removes its timer');
        ui.jokes.toggle.active = true;
        window.hide();
        check(!ui.jokes.running, 'Hidden preferences have no caption timer');
        window.present();
    }],
    [250, () => {
        settings.set_int64('about-request', GLib.get_monotonic_time() - 20 * GLib.USEC_PER_SEC);
        const windows = Gtk.Window.get_toplevels();
        const findAbout = () => {
            for (let i = 0; i < windows.get_n_items(); i++) {
                const candidate = windows.get_item(i);
                if (candidate.get_title() === 'About Retro Terminal' && candidate.get_visible()) return candidate;
            }
            return null;
        };
        check(findAbout() === null, 'Stale tray requests do not open About');
        settings.set_int64('about-request', GLib.get_monotonic_time());
        const about = findAbout();
        check(about && settings.get_int64('about-request') === 0, 'A tray request opens branded About exactly once');
        about.close();
        check(ui.about.show() !== about, 'About reopens after closing');
        window.destroy();
        check(!ui.jokes.running && ui.about.show() === null, 'Destroying preferences disposes About and ticker');
        settings.set_int64('about-request', GLib.get_monotonic_time());
        restoredWindow = new Adw.PreferencesWindow();
        const restored = buildControls(restoredWindow, settings);
        check(restored.pipelines.selected?.id === selectedId && settings.get_double('scanline-spacing') === 6,
            'A new panel restores pipeline selection and values');
        restoredWindow.present();
    }],
    [100, () => {
        check(settings.get_int64('about-request') === 0, 'A request also opens About when preferences were closed');
        restoredWindow.close();
        check(settings.get_string('saved-presets') === legacy, 'Legacy data remains preserved after pipeline edits');
        print('PASS GTK expanders, native controls, pipeline create/duplicate/rename/autosave/delete/restore, Stop, About and visible-only captions');
        loop.quit();
    }],
];
const next = () => {
    const [delay, action] = steps.shift();
    GLib.timeout_add(GLib.PRIORITY_DEFAULT, delay, () => {
        try { action(); if (steps.length) next(); }
        catch (error) { failed = error; window.destroy(); restoredWindow?.destroy(); loop.quit(); }
        return GLib.SOURCE_REMOVE;
    });
};
next();
loop.run();
if (failed) throw failed;
