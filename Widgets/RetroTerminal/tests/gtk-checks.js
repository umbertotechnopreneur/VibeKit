// Run with memory settings and a temporary configuration directory.
import Adw from 'gi://Adw';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Gdk from 'gi://Gdk?version=4.0';
const directory = Gio.File.new_for_path(ARGV[0]);
const {buildControls} = await import(directory.get_child('settings-ui.js').get_uri());
Adw.init();
const source = Gio.SettingsSchemaSource.new_from_directory(directory.get_child('schemas').get_path(), Gio.SettingsSchemaSource.get_default(), false);
const settings = Gio.Settings.new_full(source.lookup('org.gnome.shell.extensions.vibeware-retro-terminal', true), null, null);
const check = (value, message) => { if (!value) throw Error(message); };
const window = new Adw.PreferencesWindow();
const ui = buildControls(window, settings);
check(!ui.active.active, 'Must start disabled');
ui.active.active = true;
check(settings.get_boolean('active'), 'Enable binding');
ui.preset.selected = 1;
check(settings.get_int('preset') === 1, 'Preset binding');
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
check(settings.get_int('mask') === 2, 'Mask selector binding');
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
ui.saveName.text = 'Custom cyan';
ui.save.emit('clicked');
const library = JSON.parse(settings.get_string('saved-presets'));
check(library.length === 1 && library[0].name === 'Custom cyan', 'Save personal look');
check(library[0].values['phosphor-color'] === '#fedcba' && library[0].values['scanline-spacing'] === 5,
    'Saved look includes color and raster settings');
check(!Object.hasOwn(library[0].values, 'active') && !Object.hasOwn(library[0].values, 'glitch-request') &&
    !Object.hasOwn(library[0].values, 'show-gpu-jokes'), 'Saved look must exclude live commands and interface preferences');
ui.look.selected = 0;
ui.applyPreset.emit('clicked');
check(!settings.get_boolean('glitches-enabled') && !settings.get_boolean('persistence-enabled'), 'Clean look applies');
ui.look.selected = 4;
ui.applyPreset.emit('clicked');
check(settings.get_boolean('persistence-enabled') && settings.get_string('phosphor-color') === '#fedcba',
    'Saved look restores custom settings');
ui.removePreset.emit('clicked');
check(JSON.parse(settings.get_string('saved-presets')).length === 0, 'Delete personal look');
settings.set_string('saved-presets', '{broken');
check(!ui.save.sensitive && settings.get_string('saved-presets') === '{broken', 'Corrupt library remains preserved');
settings.set_string('saved-presets', '[]');
ui.stop.emit('clicked');
check(!settings.get_boolean('active') && !ui.active.active, 'Stop binding');
check(!ui.tryGlitch.sensitive, 'Try now unavailable while CRT stopped');
window.present();
const loop = new GLib.MainLoop(null, false);
let failed = false;
const after = (delay, callback) => GLib.timeout_add(GLib.PRIORITY_DEFAULT, delay, () => {
    try { callback(); }
    catch (error) { failed = true; logError(error); window.destroy(); loop.quit(); }
    return GLib.SOURCE_REMOVE;
});
after(300, () => {
    check(ui.jokes.running, 'Visible page starts the joke timer');
    const first = ui.jokes.row.title;
    after(13500, () => {
        check(ui.jokes.row.title !== first, 'Caption advances at the normal 12 second interval');
        ui.jokes.toggle.active = false;
        check(!settings.get_boolean('show-gpu-jokes') && !ui.jokes.running && !ui.jokes.row.visible,
            'Disabling captions hides the row and removes the timer');
        ui.jokes.toggle.active = true;
        check(ui.jokes.running, 'Re-enabling visible captions resumes the timer');
        window.hide();
        check(!ui.jokes.running, 'Hidden preferences have no caption timer');
        window.present();
        window.set_visible_page(ui.aboutPage);
        after(400, () => {
            check(!ui.jokes.running, 'About page leaves captions unmapped and stopped');
            ui.aboutButton.emit('clicked');
            const about = ui.about.show();
            check(about.get_title() === 'About Retro Terminal' && about.get_visible(), 'Branded About opens');
            about.close();
            check(ui.about.show() !== about, 'About can reopen after closing');
            window.destroy();
            check(!ui.jokes.running && ui.about.show() === null, 'Destroying preferences disposes About and ticker');
            ui.jokes.dispose(); ui.about.dispose();
            print('PASS native GTK controls, saved looks, stop, About open/reopen/parent destruction, visible-only 12s jokes and cleanup');
            loop.quit();
        });
    });
});
loop.run();
if (failed) throw Error('Native GTK checks failed');
