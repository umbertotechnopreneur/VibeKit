// SPDX-License-Identifier: MIT
import Adw from 'gi://Adw';
import Gtk from 'gi://Gtk?version=4.0';
import Gdk from 'gi://Gdk?version=4.0';
import Gio from 'gi://Gio';
import {NUMERIC} from './settings-model.js';
import {createAbout} from './about.js';
import {createGpuJokes} from './gpu-jokes.js';
import {loadPipelines} from './pipelines.js';
import {pipelineControls} from './pipeline-ui.js';

export function buildControls(window, settings) {
    window.set_title('Retro Terminal');
    window.set_default_size(760, 860);
    window.set_search_enabled(false);
    const page = new Adw.PreferencesPage({title: 'Effect', icon_name: 'utilities-terminal-symbolic'});
    const {model: pipelines, path} = loadPipelines(settings);
    const library = pipelineControls(window, pipelines, path, () => window.set_visible_page(page));
    const about = createAbout(window);
    const aboutPage = new Adw.PreferencesPage({title: 'About', icon_name: 'help-about-symbolic'});
    window.add(page); window.add(library.page); window.add(aboutPage);
    const signals = [];
    const controls = {};
    const expanders = {};
    const add = (parent, row) => parent instanceof Adw.ExpanderRow ? parent.add_row(row) : parent.add(row);
    const group = (title = '', description = '') => {
        const result = new Adw.PreferencesGroup({title, description});
        page.add(result);
        return result;
    };
    const section = (key, title, subtitle, icon) => {
        const row = new Adw.ExpanderRow({title, subtitle, expanded: false, use_markup: false});
        row.add_prefix(new Gtk.Image({icon_name: icon}));
        group().add(row);
        expanders[key] = row;
        return row;
    };
    const switchRow = (parent, key, title, subtitle = '') => {
        const row = new Adw.SwitchRow({title, subtitle});
        settings.bind(key, row, 'active', Gio.SettingsBindFlags.DEFAULT);
        add(parent, row); controls[key] = row;
        return row;
    };
    const spinRow = (parent, key, enabled = null) => {
        const [, title, low, high, step] = NUMERIC.find(spec => spec[0] === key);
        const row = new Adw.SpinRow({title, digits: step < 0.01 ? 3 : step < 1 ? 2 : 0,
            adjustment: new Gtk.Adjustment({lower: low, upper: high, step_increment: step, page_increment: step * 5})});
        settings.bind(key, row, 'value', Gio.SettingsBindFlags.DEFAULT);
        if (enabled) settings.bind(enabled, row, 'sensitive', Gio.SettingsBindFlags.GET);
        add(parent, row); controls[key] = row;
    };

    const power = group('', 'Super + Shift + F12 stops CRT. The effect starts off at each login.');
    const active = switchRow(power, 'active', 'CRT effect');
    const status = new Adw.ActionRow({title: settings.get_string('status'), use_markup: false, title_lines: 0});
    const stop = new Gtk.Button({label: 'Stop', css_classes: ['destructive-action'], valign: Gtk.Align.CENTER});
    stop.connect('clicked', () => settings.set_boolean('active', false));
    status.add_suffix(stop); power.add(status);
    signals.push(settings.connect('changed::status', () => status.title = settings.get_string('status')));

    const selection = group('', 'Adjust a personal pipeline to save it automatically. Duplicate a built-in in Pipelines to keep your changes.');
    const pipeline = new Adw.ComboRow({title: 'Pipeline'});
    selection.add(pipeline);
    const pipelineError = new Adw.ActionRow({use_markup: false, title_lines: 0, visible: false});
    selection.add(pipelineError);
    let updatingPipeline = false;
    const updatePipeline = () => {
        updatingPipeline = true;
        const labels = ['Custom', ...pipelines.items.map(item => item.name)];
        if (!pipeline.model || pipeline.model.get_n_items() !== labels.length ||
            labels.some((label, i) => pipeline.model.get_string(i) !== label)) pipeline.model = Gtk.StringList.new(labels);
        pipeline.selected = pipelines.selected ? pipelines.items.indexOf(pipelines.selected) + 1 : 0;
        pipelineError.title = pipelines.error || '';
        pipelineError.visible = Boolean(pipelines.error);
        updatingPipeline = false;
    };
    const unsubscribePipeline = pipelines.subscribe(updatePipeline);
    pipeline.connect('notify::selected', () => {
        if (updatingPipeline) return;
        if (pipeline.selected === 0) pipelines.detach();
        else {
            const chosen = pipelines.items[pipeline.selected - 1];
            if (chosen && chosen !== pipelines.selected) pipelines.select(chosen);
        }
    });
    updatePipeline();
    signals.push(settings.connect('changed', (_settings, key) => pipelines.changed(key)));

    const phosphor = section('phosphor', 'Phosphor & color', 'Palette, custom color and picture balance', 'color-select-symbolic');
    const preset = new Adw.ComboRow({title: 'Palette', model: Gtk.StringList.new(['Green phosphor', 'Amber terminal', 'Arcade color'])});
    settings.bind('preset', preset, 'selected', Gio.SettingsBindFlags.DEFAULT);
    phosphor.add_row(preset);
    switchRow(phosphor, 'custom-color', 'Custom phosphor color', 'Override the selected palette with your own color');
    const color = new Gtk.ColorDialogButton({dialog: new Gtk.ColorDialog({title: 'Phosphor color', with_alpha: false}), valign: Gtk.Align.CENTER});
    const colorRow = new Adw.ActionRow({title: 'Phosphor color', activatable_widget: color});
    colorRow.add_suffix(color); phosphor.add_row(colorRow);
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
        const hex = [c.red, c.green, c.blue].map(value => Math.round(Math.max(0, Math.min(1, value)) * 255).toString(16).padStart(2, '0')).join('');
        settings.set_string('phosphor-color', `#${hex}`);
    });
    signals.push(settings.connect('changed::phosphor-color', updateColor));
    updateColor();
    for (const key of ['intensity', 'black-level', 'contrast']) spinRow(phosphor, key);

    const surface = section('surface', 'Monitor surface', 'Scanlines, glow, phosphor mask and texture', 'video-display-symbolic');
    for (const key of ['scanlines', 'scanline-spacing', 'scanline-width', 'glow', 'glow-radius']) spinRow(surface, key);
    const mask = new Adw.ComboRow({title: 'Phosphor mask', model: Gtk.StringList.new(['None', 'RGB stripes', 'Phosphor dots'])});
    settings.bind('mask', mask, 'selected', Gio.SettingsBindFlags.DEFAULT);
    surface.add_row(mask); controls.mask = mask;
    for (const key of ['mask-strength', 'vignette', 'grain']) spinRow(surface, key);

    const persistence = section('persistence', 'Phosphor persistence', 'A fading trail behind moving content', 'view-reveal-symbolic');
    switchRow(persistence, 'persistence-enabled', 'Temporal afterglow',
        'Uses extra GPU memory. Trail resolution adapts to keep large desktops within the capture limit.');
    for (const key of ['persistence-duration', 'persistence-strength']) spinRow(persistence, key, 'persistence-enabled');

    const glitches = section('glitches', 'Signal glitches', 'Brief interference, by chance or on demand', 'media-playlist-shuffle-symbolic');
    const random = switchRow(glitches, 'glitches-enabled', 'Random glitches', 'One episode at a time, with at least two seconds of rest');
    for (const [key, title] of [['glitch-rgb', 'RGB misalignment'], ['glitch-tear', 'Horizontal tear'],
        ['glitch-noise', 'Line interference'], ['glitch-band', 'Soft scanning band']]) switchRow(glitches, key, title);
    for (const key of ['glitch-intensity', 'glitch-interval-min', 'glitch-interval-max', 'glitch-duration-min', 'glitch-duration-max']) spinRow(glitches, key);
    const tryGlitch = new Gtk.Button({label: 'Try now', valign: Gtk.Align.CENTER});
    settings.bind('active', tryGlitch, 'sensitive', Gio.SettingsBindFlags.GET);
    tryGlitch.connect('clicked', () => settings.set_uint('glitch-request', (settings.get_uint('glitch-request') + 1) >>> 0));
    const glitchStatus = new Adw.ActionRow({title: 'Preview a glitch', subtitle: settings.get_string('glitch-status'),
        use_markup: false, activatable_widget: tryGlitch});
    glitchStatus.add_suffix(tryGlitch); glitches.add_row(glitchStatus);
    signals.push(settings.connect('changed::glitch-status', () => glitchStatus.subtitle = settings.get_string('glitch-status')));

    const brand = new Adw.PreferencesGroup({title: 'VibeWare', description: 'Independent desktop utilities by Umberto Giacobbi.'});
    const aboutButton = new Gtk.Button({label: 'About Retro Terminal', valign: Gtk.Align.CENTER});
    aboutButton.connect('clicked', () => about.show());
    const brandRow = new Adw.ActionRow({title: 'Retro Terminal', subtitle: 'Modern graphics. Retro priorities.', activatable_widget: aboutButton});
    brandRow.add_prefix(new Gtk.Image({icon_name: 'utilities-terminal-symbolic', pixel_size: 32}));
    brandRow.add_suffix(aboutButton); brand.add(brandRow); aboutPage.add(brand);
    const jokes = createGpuJokes(settings);
    aboutPage.add(jokes.group);
    const behavior = new Adw.PreferencesGroup({description: 'Stop cancels glitches and releases history. Lock, display changes and desktop delays also stop CRT. Restart is always manual.'});
    aboutPage.add(behavior);
    let disposed = false;
    const dispose = () => {
        if (disposed) return;
        disposed = true;
        for (const signal of signals) settings.disconnect(signal);
        unsubscribePipeline(); library.dispose(); pipelines.dispose();
        jokes.dispose(); about.dispose();
    };
    window.connect('close-request', () => { dispose(); return false; });
    window.connect('unrealize', dispose);
    return {active, status, preset, controls, expanders, color, random, tryGlitch, stop,
        pipeline, pipelines, library, page, aboutPage, aboutButton, about, jokes};
}
