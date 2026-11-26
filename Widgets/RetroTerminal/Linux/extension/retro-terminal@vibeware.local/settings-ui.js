// SPDX-License-Identifier: MIT
import Adw from 'gi://Adw';
import Gtk from 'gi://Gtk?version=4.0';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import {createControlBuilders, createPhosphorColor} from './settings-controls.js';
import {createAbout} from './about.js';
import {createAccordion, addThemeSetting} from './preferences-layout.js';
import {createGpuJokes} from './gpu-jokes.js';
import {loadPipelines} from './pipelines.js';
import {pipelineControls} from './pipeline-ui.js';
import {createMonitorGroup} from './shared/monitor-settings.js';

export function buildControls(window, settings) {
    window.set_title('Retro Terminal');
    window.set_default_size(760, 860);
    window.set_search_enabled(false);
    const page = new Adw.PreferencesPage();
    const {model: pipelines, path} = loadPipelines(settings);
    const power = new Adw.PreferencesGroup({description: 'The effect starts off at each login.'});
    page.add(power);
    const addSection = createAccordion(page);
    const effect = addSection('Effect');
    const pipelineSection = addSection('Pipelines');
    const settingsSection = addSection('Settings');
    const diagnostics = addSection('Diagnostics');
    const aboutSection = addSection('About');
    addThemeSetting(window, settings, settingsSection);
    const library = pipelineControls(window, pipelines, path, pipelineSection, () => {
        effect.row.expanded = true;
        effect.row.grab_focus();
    });
    const about = createAbout(window);
    window.add(page);
    const signals = [];
    const handleRequest = (key, action) => {
        const requested = settings.get_int64(key);
        if (!requested || !window.get_mapped()) return;
        settings.reset(key);
        const age = GLib.get_monotonic_time() - requested;
        if (age >= 0 && age < 10 * GLib.USEC_PER_SEC) action();
    };
    const showAbout = () => handleRequest('about-request', () => about.show());
    const showSettings = () => handleRequest('preferences-request', () => {
        settingsSection.row.expanded = true;
        settingsSection.row.grab_focus();
    });
    signals.push(settings.connect('changed::about-request', showAbout));
    signals.push(settings.connect('changed::preferences-request', showSettings));
    signals.push(settings.connect('changed::quit-request', () => window.close()));
    settings.get_int64('quit-request');
    const aboutMap = window.connect('map', () => { showSettings(); showAbout(); });
    const {controls, expanders, group, section, switchRow, spinRow} = createControlBuilders(effect, settings);

    const active = switchRow(power, 'active', 'CRT effect', 'Turn on CRT to preview changes on the desktop.');
    const status = new Adw.ActionRow({title: settings.get_string('status'), use_markup: false, title_lines: 0});
    const stop = new Gtk.Button({label: 'Stop', css_classes: ['destructive-action'], valign: Gtk.Align.CENTER});
    stop.connect('clicked', () => settings.set_boolean('active', false));
    status.add_suffix(stop); power.add(status);
    signals.push(settings.connect('changed::status', () => status.title = settings.get_string('status')));
    const syncStop = () => { stop.label = settings.get_boolean('recovery-pending') ? 'Cancel recovery' : 'Stop'; };
    signals.push(settings.connect('changed::recovery-pending', syncStop));
    syncStop();

    settingsSection.add(createMonitorGroup(settings));
    const protection = new Adw.PreferencesGroup({title: 'Performance protection'});
    settingsSection.add(protection);
    const limit = (key, title, subtitle, min, max) => {
        const row = new Adw.ActionRow({title, subtitle});
        const slider = Gtk.Scale.new_with_range(Gtk.Orientation.HORIZONTAL, min, max, 1);
        slider.set_size_request(120, -1);
        slider.valign = Gtk.Align.CENTER;
        slider.set_draw_value(false);
        const value = new Gtk.Label({width_chars: 7, xalign: 1});
        row.add_suffix(slider);
        row.add_suffix(value);
        const sync = () => {
            const fps = settings.get_int(key);
            slider.set_value(fps);
            value.label = `${fps} fps`;
        };
        signals.push(settings.connect(`changed::${key}`, sync));
        slider.connect('value-changed', () => {
            if (!settings.set_int(key, Math.round(slider.get_value()))) sync();
        });
        sync();
        protection.add(row);
        return slider;
    };
    const animationLimit = limit('animation-fps', 'Animation limit', 'Maximum update rate for animated CRT glitches.', 15, 120);
    const captureLimit = limit('capture-fps', 'Capture limit', 'Maximum rate for afterglow snapshots. Up to 24 fps shared by the selected displays.', 5, 60);
    const resume = switchRow(protection, 'auto-restart', 'Resume automatically', 'Resume after a temporary performance pause.');
    const recovery = Adw.SpinRow.new_with_range(30, 300, 1);
    recovery.title = 'Recovery';
    recovery.subtitle = 'Delay in seconds. Requires 30 seconds of stability. Up to 2 retries per manual start.';
    const syncRecovery = () => { recovery.value = settings.get_int('recovery-delay'); };
    signals.push(settings.connect('changed::recovery-delay', syncRecovery));
    recovery.connect('notify::value', () => {
        if (!settings.set_int('recovery-delay', Math.round(recovery.value))) syncRecovery();
    });
    settings.bind('auto-restart', recovery, 'sensitive', Gio.SettingsBindFlags.GET);
    syncRecovery();
    protection.add(recovery);

    const shortcutGroup = new Adw.PreferencesGroup();
    const shortcut = switchRow(shortcutGroup, 'panic-enabled', 'Emergency shortcut', 'Super + Shift + F12');
    const syncShortcut = () => {
        const key = 'Super + Shift + F12';
        shortcut.subtitle = !settings.get_boolean('panic-enabled') ? `${key} · Disabled` :
            settings.get_boolean('panic-available') ? key : `${key} · Unavailable`;
    };
    for (const key of ['panic-enabled', 'panic-available'])
        signals.push(settings.connect(`changed::${key}`, syncShortcut));
    syncShortcut();
    page.add(shortcutGroup);

    const diagnosticGroup = new Adw.PreferencesGroup({title: 'Live effect status',
        description: 'The compositor does not report GPU frame time here.'});
    const diagnosticStatus = new Adw.ActionRow({title: 'Effect', use_markup: false, subtitle_lines: 0});
    const diagnosticShortcut = new Adw.ActionRow({title: 'Emergency shortcut', use_markup: false});
    const diagnosticRecovery = new Adw.ActionRow({title: 'Recovery', use_markup: false});
    diagnosticGroup.add(diagnosticStatus);
    diagnosticGroup.add(diagnosticShortcut);
    diagnosticGroup.add(diagnosticRecovery);
    diagnostics.add(diagnosticGroup);
    const syncDiagnostics = () => {
        diagnosticStatus.subtitle = settings.get_string('status');
        diagnosticShortcut.subtitle = !settings.get_boolean('panic-enabled') ? 'Disabled' :
            settings.get_boolean('panic-available') ? 'Super + Shift + F12 is available.' : 'Shortcut unavailable.';
        diagnosticRecovery.subtitle = settings.get_boolean('recovery-pending') ? 'Waiting for stable conditions.' :
            settings.get_boolean('auto-restart') ? 'Enabled after a performance pause.' : 'Automatic recovery is off.';
    };
    for (const key of ['status', 'panic-enabled', 'panic-available', 'recovery-pending', 'auto-restart'])
        signals.push(settings.connect(`changed::${key}`, syncDiagnostics));
    syncDiagnostics();

    const selection = group('', 'Adjust a personal pipeline to save it automatically. Duplicate a built-in in Pipelines to keep your changes.');
    const pipeline = new Adw.ComboRow({
        title: 'Pipeline',
        enable_search: true,
        expression: Gtk.PropertyExpression.new(Gtk.StringObject.$gtype, null, 'string'),
    });
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
    const {color, signal: colorSignal} = createPhosphorColor(phosphor, settings);
    signals.push(colorSignal);
    for (const key of ['intensity', 'black-level', 'contrast']) spinRow(phosphor, key);

    const surface = section('surface', 'Monitor surface', 'Scanlines, glow, phosphor mask and texture', 'video-display-symbolic');
    for (const key of ['scanlines', 'scanline-spacing', 'scanline-width', 'glow', 'glow-radius']) spinRow(surface, key);
    const mask = new Adw.ComboRow({title: 'Phosphor mask', model: Gtk.StringList.new(['None', 'RGB stripes', 'Phosphor dots'])});
    settings.bind('mask', mask, 'selected', Gio.SettingsBindFlags.DEFAULT);
    surface.add_row(mask); controls.mask = mask;
    for (const key of ['mask-strength', 'vignette', 'grain']) spinRow(surface, key);
    const syncSurface = () => {
        const hasScanlines = settings.get_double('scanlines') > 0;
        controls['scanline-spacing'].sensitive = controls['scanline-width'].sensitive = hasScanlines;
        controls['glow-radius'].sensitive = settings.get_double('glow') > 0;
        controls['mask-strength'].sensitive = settings.get_int('mask') !== 0;
    };
    for (const key of ['scanlines', 'glow', 'mask'])
        signals.push(settings.connect(`changed::${key}`, syncSurface));
    syncSurface();

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
    brandRow.add_prefix(new Gtk.Image({icon_name: 'local.vibeware.RetroTerminal', pixel_size: 32}));
    brandRow.add_suffix(aboutButton); brand.add(brandRow); aboutSection.add(brand);
    const jokes = createGpuJokes(settings);
    aboutSection.add(jokes.group);
    let disposed = false;
    const dispose = () => {
        if (disposed) return;
        disposed = true;
        window.disconnect(aboutMap);
        for (const signal of signals) settings.disconnect(signal);
        unsubscribePipeline(); library.dispose(); pipelines.dispose();
        jokes.dispose(); about.dispose();
    };
    window.connect('close-request', () => { dispose(); return false; });
    window.connect('unrealize', dispose);
    return {active, status, diagnosticStatus, diagnosticShortcut, diagnosticRecovery, preset, controls, expanders, color, random, tryGlitch, stop,
        animationLimit, captureLimit, resume, recovery, shortcut,
        pipeline, pipelines, library, page, effectSection: effect.row, aboutSection: aboutSection.row, aboutButton, about, jokes};
}
