// SPDX-License-Identifier: MIT
import Adw from 'gi://Adw';
import Gtk from 'gi://Gtk?version=4.0';
import Gdk from 'gi://Gdk?version=4.0';
import Gio from 'gi://Gio';
import {NUMERIC, BUILTINS, readLibrary, readValues, applyValues, savePreset} from './settings-model.js';
import {createAbout} from './about.js';
import {createGpuJokes} from './gpu-jokes.js';

export function buildControls(window, settings) {
    window.set_title('Retro Terminal');
    window.set_default_size(640, 820);
    const page = new Adw.PreferencesPage({title: 'CRT desktop', icon_name: 'utilities-terminal-symbolic'});
    window.add(page);
    const about = createAbout(window);
    const aboutPage = new Adw.PreferencesPage({title: 'About', icon_name: 'help-about-symbolic'});
    const brand = new Adw.PreferencesGroup({title: 'VibeWare', description: 'Independent desktop utilities by Umberto Giacobbi.'});
    const aboutButton = new Gtk.Button({label: 'About Retro Terminal', valign: Gtk.Align.CENTER});
    aboutButton.connect('clicked', () => about.show());
    const brandRow = new Adw.ActionRow({title: 'Retro Terminal', subtitle: 'Modern graphics. Retro priorities.',
        activatable_widget: aboutButton});
    brandRow.add_prefix(new Gtk.Image({icon_name: 'utilities-terminal-symbolic', pixel_size: 32}));
    brandRow.add_suffix(aboutButton);
    brand.add(brandRow);
    aboutPage.add(brand);
    window.add(aboutPage);
    const signals = [];
    const controls = {};
    const group = (title, description) => {
        const result = new Adw.PreferencesGroup({title, description});
        page.add(result);
        return result;
    };
    const switchRow = (parent, key, title, subtitle = '') => {
        const row = new Adw.SwitchRow({title, subtitle});
        settings.bind(key, row, 'active', Gio.SettingsBindFlags.DEFAULT);
        parent.add(row);
        controls[key] = row;
        return row;
    };
    const spinRow = (parent, key, enabled = null) => {
        const [, title, low, high, step] = NUMERIC.find(p => p[0] === key);
        const row = new Adw.SpinRow({title, digits: step < 0.01 ? 3 : step < 1 ? 2 : 0,
            adjustment: new Gtk.Adjustment({lower: low, upper: high, step_increment: step, page_increment: step * 5})});
        settings.bind(key, row, 'value', Gio.SettingsBindFlags.DEFAULT);
        if (enabled) settings.bind(enabled, row, 'sensitive', Gio.SettingsBindFlags.GET);
        parent.add(row);
        controls[key] = row;
    };
    const power = group('', 'Super + Shift + F12 stops the effect. It starts off at each login.');
    const active = switchRow(power, 'active', 'CRT effect');
    const status = new Adw.ActionRow({title: settings.get_string('status'), use_markup: false});
    power.add(status);
    signals.push(settings.connect('changed::status', () => status.title = settings.get_string('status')));
    const jokes = createGpuJokes(settings);
    page.add(jokes.group);

    const library = group('Looks', 'Load, adjust and save with a new name. Worn terminal includes random glitches; Long phosphor enables afterglow. Changes apply immediately.');
    const look = new Adw.ComboRow({title: 'Saved look'});
    library.add(look);
    const actions = new Gtk.Box({spacing: 8, margin_top: 8});
    const applyPreset = new Gtk.Button({label: 'Load look'});
    const removePreset = new Gtk.Button({label: 'Delete saved look'});
    actions.append(applyPreset); actions.append(removePreset); library.add(actions);
    const saveName = new Gtk.Entry({placeholder_text: 'Name your look', max_length: 48, hexpand: true});
    const save = new Gtk.Button({label: 'Save new look'});
    const saveBox = new Gtk.Box({spacing: 8, margin_top: 8});
    saveBox.append(saveName); saveBox.append(save); library.add(saveBox);
    const libraryError = new Adw.ActionRow({use_markup: false, visible: false});
    library.add(libraryError);
    let saved = [];
    const report = message => { libraryError.title = message; libraryError.visible = Boolean(message); };
    const refresh = () => {
        try {
            saved = readLibrary(settings.get_string('saved-presets'));
            const selected = look.selected;
            look.model = Gtk.StringList.new([...BUILTINS.map(p => p.name), ...saved.map(p => `Saved: ${p.name}`)]);
            look.selected = selected < BUILTINS.length + saved.length ? selected : 0;
            removePreset.sensitive = look.selected >= BUILTINS.length;
            save.sensitive = true;
            report('');
        } catch (error) {
            saved = [];
            look.model = Gtk.StringList.new(BUILTINS.map(p => p.name));
            look.selected = 0;
            save.sensitive = removePreset.sensitive = false;
            report(`Cannot read saved looks: ${error.message}. Existing data has been preserved.`);
        }
    };
    look.connect('notify::selected', () => removePreset.sensitive = look.selected >= BUILTINS.length && saved.length > 0);
    applyPreset.connect('clicked', () => {
        const item = [...BUILTINS, ...saved][look.selected];
        if (item) applyValues(settings, item.values);
    });
    save.connect('clicked', () => {
        try {
            settings.set_string('saved-presets', savePreset(settings.get_string('saved-presets'), saveName.text, readValues(settings)));
            saveName.text = '';
            look.selected = BUILTINS.length + saved.length - 1;
        } catch (error) { report(error.message); }
    });
    removePreset.connect('clicked', () => {
        try {
            const items = readLibrary(settings.get_string('saved-presets'));
            const index = look.selected - BUILTINS.length;
            if (index < 0 || index >= items.length) return;
            items.splice(index, 1);
            settings.set_string('saved-presets', JSON.stringify(items));
        } catch (error) { report(error.message); }
    });
    signals.push(settings.connect('changed::saved-presets', refresh));
    refresh();

    const phosphor = group('Phosphor', 'Green and amber use one phosphor color. Arcade retains the source colors unless custom color is enabled.');
    const preset = new Adw.ComboRow({title: 'Palette', model: Gtk.StringList.new(['Green phosphor', 'Amber terminal', 'Arcade color'])});
    settings.bind('preset', preset, 'selected', Gio.SettingsBindFlags.DEFAULT);
    phosphor.add(preset);
    switchRow(phosphor, 'custom-color', 'Custom phosphor color');
    const color = new Gtk.ColorDialogButton({dialog: new Gtk.ColorDialog({title: 'Phosphor color', with_alpha: false}), valign: Gtk.Align.CENTER});
    const colorRow = new Adw.ActionRow({title: 'Phosphor color', activatable_widget: color});
    colorRow.add_suffix(color); phosphor.add(colorRow);
    settings.bind('custom-color', color, 'sensitive', Gio.SettingsBindFlags.GET);
    let syncingColor = false;
    const updateColor = () => {
        syncingColor = true;
        const rgba = new Gdk.RGBA();
        if (!rgba.parse(settings.get_string('phosphor-color'))) rgba.parse('#33ff6b');
        color.rgba = rgba;
        syncingColor = false;
    };
    color.connect('notify::rgba', () => {
        if (syncingColor) return;
        const c = color.rgba;
        const hex = [c.red, c.green, c.blue].map(v => Math.round(Math.max(0, Math.min(1, v)) * 255).toString(16).padStart(2, '0')).join('');
        settings.set_string('phosphor-color', `#${hex}`);
    });
    signals.push(settings.connect('changed::phosphor-color', updateColor));
    updateColor();
    for (const key of ['intensity', 'black-level', 'contrast']) spinRow(phosphor, key);
    const surface = group('Monitor surface', 'Thickness is limited to the selected scanline spacing. Pixel dimensions follow the render surface.');
    for (const key of ['scanlines', 'scanline-spacing', 'scanline-width', 'glow', 'glow-radius']) spinRow(surface, key);
    const mask = new Adw.ComboRow({title: 'Phosphor mask', model: Gtk.StringList.new(['None', 'RGB stripes', 'Phosphor dots'])});
    settings.bind('mask', mask, 'selected', Gio.SettingsBindFlags.DEFAULT);
    surface.add(mask); controls.mask = mask;
    for (const key of ['mask-strength', 'vignette', 'grain']) spinRow(surface, key);
    const persistence = group('Phosphor persistence', 'Retains previous frames on the GPU to create an afterglow. Uses additional GPU memory and redraws while enabled.');
    switchRow(persistence, 'persistence-enabled', 'Temporal afterglow');
    for (const key of ['persistence-duration', 'persistence-strength']) spinRow(persistence, key, 'persistence-enabled');

    const glitches = group('Signal glitches', 'Short, local disturbances. One at a time, with at least two seconds of rest after an episode. Try now also works with random glitches off.');
    const random = switchRow(glitches, 'glitches-enabled', 'Random glitches');
    for (const [key, title] of [['glitch-rgb', 'RGB misalignment'], ['glitch-tear', 'Horizontal tear'],
        ['glitch-noise', 'Line interference'], ['glitch-band', 'Soft scanning band']]) switchRow(glitches, key, title);
    for (const key of ['glitch-intensity', 'glitch-interval-min', 'glitch-interval-max', 'glitch-duration-min', 'glitch-duration-max']) spinRow(glitches, key);
    const tryGlitch = new Gtk.Button({label: 'Try a glitch now', margin_top: 8});
    settings.bind('active', tryGlitch, 'sensitive', Gio.SettingsBindFlags.GET);
    tryGlitch.connect('clicked', () => settings.set_uint('glitch-request', (settings.get_uint('glitch-request') + 1) >>> 0));
    glitches.add(tryGlitch);
    const glitchStatus = new Adw.ActionRow({title: settings.get_string('glitch-status'), use_markup: false});
    glitches.add(glitchStatus);
    signals.push(settings.connect('changed::glitch-status', () => glitchStatus.title = settings.get_string('glitch-status')));
    const stopGroup = group('', 'Stop cancels glitches and releases history. Lock, display changes and desktop delays also stop CRT; restart is always manual.');
    const stop = new Gtk.Button({label: 'Stop CRT', css_classes: ['destructive-action']});
    stop.connect('clicked', () => settings.set_boolean('active', false));
    stopGroup.add(stop);
    let disposed = false;
    const dispose = () => {
        if (disposed) return;
        disposed = true;
        jokes.dispose();
        about.dispose();
        for (const id of signals) settings.disconnect(id);
    };
    window.connect('close-request', () => { dispose(); return false; });
    window.connect('unrealize', dispose);
    return {active, preset, controls, color, random, tryGlitch, look, saveName, save, applyPreset, removePreset, stop,
        page, aboutPage, aboutButton, about, jokes};
}
