// SPDX-License-Identifier: GPL-3.0-or-later
import Adw from 'gi://Adw';
import Gtk from 'gi://Gtk?version=4.0';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';

export const UUID = 'ripple@vibeware.local';
export {SCHEMA} from './schema.js';
import {loadPipelines} from './pipelines.js';
import {pipelineControls} from './pipeline-ui.js';
import {createAbout} from './shared/ui/about.js';
import {createAccordion, addThemeSetting} from './shared/ui/preferences-layout.js';
import {createGpuJokes} from './gpu-jokes.js';
import {createMonitorGroup} from './shared/monitor-settings.js';
import {AutostartPreference} from './autostart.js';

export function buildControls(window, settings, application = null, externalLease = false, startEffect = false,
    extensionConnection = Gio.DBus.session, enableOnStartup = Boolean(application) || startEffect) {
    window.set_title('Ripple');
    window.set_default_size(760, 860);
    const {model: pipelines, path: libraryPath} = loadPipelines(settings);
    const page = new Adw.PreferencesPage();
    const group = new Adw.PreferencesGroup();
    page.add(group);
    const addSection = createAccordion(page);
    const effect = addSection('Effect');
    const pipelineSection = addSection('Pipelines');
    const settingsSection = addSection('Settings');
    const diagnostics = addSection('Diagnostics');
    const aboutSection = addSection('About');
    addThemeSetting(window, settings, settingsSection, application);
    settingsSection.add(createMonitorGroup(settings));
    const startupGroup = new Adw.PreferencesGroup({title: 'Startup'});
    const startup = new Adw.SwitchRow({title: 'Start with GNOME'});
    const startupPreference = new AutostartPreference();
    let syncingStartup = false;
    const syncStartup = error => {
        syncingStartup = true;
        try {
            const state = startupPreference.read();
            startup.active = state.enabled;
            startup.sensitive = state.available;
            startup.subtitle = error || (state.available
                ? 'Start Ripple in the top bar when you sign in.'
                : 'The system startup entry is not installed.');
        } catch (problem) {
            startup.sensitive = false;
            startup.subtitle = problem.message;
        } finally { syncingStartup = false; }
    };
    startup.connect('notify::active', () => {
        if (syncingStartup) return;
        try { startupPreference.setEnabled(startup.active); syncStartup(); }
        catch (error) { syncStartup(`Could not update startup: ${error.message}`); }
    });
    syncStartup();
    startupGroup.add(startup);
    settingsSection.add(startupGroup);
    const about = createAbout(window, import.meta.url, 'local.vibeware.WaterRipple', 'GPL-3.0-or-later');
    const aboutGroup = new Adw.PreferencesGroup({title: 'VibeWare', description: 'Desktop toys by Umberto Giacobbi'});
    const aboutRow = new Adw.ActionRow({title: 'About Ripple', subtitle: 'Version, credits, license and project links', activatable: true});
    aboutRow.add_suffix(new Gtk.Image({icon_name: 'go-next-symbolic'}));
    aboutRow.connect('activated', () => about.show());
    aboutGroup.add(aboutRow);
    aboutSection.add(aboutGroup);
    const library = pipelineControls(window, pipelines, libraryPath, pipelineSection, () => {
        effect.row.expanded = true;
        effect.row.grab_focus();
    });
    if (!application) {
        window.set_search_enabled(false);
        window.add(page);
    }
    const jokes = createGpuJokes(settings);
    settingsSection.add(jokes.group);
    const active = new Adw.SwitchRow({title: 'Effect'});
    settings.bind('active', active, 'active', Gio.SettingsBindFlags.DEFAULT);
    group.add(active);
    const autoRestart = new Adw.SwitchRow({
        title: 'Resume automatically',
        subtitle: 'Resume after a performance pause when the system is stable',
    });
    settings.bind('auto-restart', autoRestart, 'active', Gio.SettingsBindFlags.DEFAULT);
    const footer = new Gtk.Box({
        spacing: 10, margin_start: 12, margin_end: 12, margin_top: 6, margin_bottom: 6,
    });
    footer.append(new Gtk.Image({icon_name: 'input-keyboard-symbolic'}));
    const shortcutText = new Gtk.Label({
        label: 'Emergency shortcut · Super + Shift + Esc', xalign: 0, wrap: true, hexpand: true,
    });
    const shortcutStatus = new Gtk.Label({xalign: 0, wrap: true, css_classes: ['dim-label']});
    const shortcut = new Gtk.Box({orientation: Gtk.Orientation.VERTICAL, hexpand: true});
    shortcut.append(shortcutText);
    shortcut.append(shortcutStatus);
    footer.append(shortcut);
    const emergency = new Gtk.Switch({valign: Gtk.Align.CENTER});
    shortcutText.set_mnemonic_widget(emergency);
    settings.bind('panic-enabled', emergency, 'active', Gio.SettingsBindFlags.DEFAULT);
    footer.append(emergency);
    const brand = new Gtk.Button({label: 'VibeWare', css_classes: ['flat'], valign: Gtk.Align.CENTER});
    brand.set_tooltip_text('About Ripple');
    brand.connect('clicked', () => about.show());
    footer.append(brand);
    if (!application) {
        const foot = new Adw.PreferencesGroup();
        foot.add(new Gtk.ListBoxRow({child: footer, activatable: false, selectable: false}));
        page.add(foot);
    }
    const syncEmergency = () => {
        const enabled = settings.get_boolean('panic-enabled');
        const available = settings.get_boolean('panic-available');
        const status = !enabled ? 'Shortcut disabled' : available ? 'Stops the effect' : 'Shortcut unavailable';
        emergency.set_tooltip_text(status);
        shortcutStatus.label = status;
        shortcutStatus.visible = enabled && !available;
    };
    const emergencyChanges = [
        settings.connect('changed::panic-enabled', syncEmergency),
        settings.connect('changed::panic-available', syncEmergency),
    ];
    syncEmergency();
    const status = new Adw.ActionRow({title: settings.get_string('status'), use_markup: false});
    group.add(status);
    const diagnosticGroup = new Adw.PreferencesGroup({title: 'Live effect status',
        description: 'The compositor does not report GPU frame time here.'});
    const diagnosticStatus = new Adw.ActionRow({title: 'Effect', use_markup: false});
    const diagnosticShortcut = new Adw.ActionRow({title: 'Emergency shortcut', use_markup: false});
    const diagnosticRecovery = new Adw.ActionRow({title: 'Recovery', use_markup: false});
    for (const row of [diagnosticStatus, diagnosticShortcut, diagnosticRecovery]) diagnosticGroup.add(row);
    diagnostics.add(diagnosticGroup);
    const syncDiagnostics = () => {
        diagnosticStatus.subtitle = settings.get_string('status');
        diagnosticShortcut.subtitle = !settings.get_boolean('panic-enabled') ? 'Disabled' :
            settings.get_boolean('panic-available') ? 'Super + Shift + Esc is available.' : 'Shortcut unavailable.';
        diagnosticRecovery.subtitle = settings.get_boolean('recovery-pending') ? 'Waiting for stable conditions.' :
            settings.get_boolean('auto-restart') ? 'Enabled after a performance pause.' : 'Automatic recovery is off.';
    };
    const diagnosticChanges = ['status', 'panic-enabled', 'panic-available', 'recovery-pending', 'auto-restart']
        .map(key => settings.connect(`changed::${key}`, syncDiagnostics));
    syncDiagnostics();
    const target = new Adw.ComboRow({
        title: 'Apply to', model: Gtk.StringList.new(['Wallpaper only', 'Windows only', 'Wallpaper and windows']),
    });
    const targets = ['desktop', 'windows', 'both'];
    target.selected = Math.max(0, targets.indexOf(settings.get_string('target')));
    target.connect('notify::selected', () => settings.set_string('target', targets[target.selected]));
    group.add(target);
    const pipelineGroup = new Adw.PreferencesGroup({description: 'Changes apply immediately. Saved pipelines update automatically; duplicate a built-in pipeline to keep your changes.'});
    effect.add(pipelineGroup);
    const preset = new Adw.ComboRow({title: 'Pipeline'});
    pipelineGroup.add(preset);
    let syncing = false;
    const pipelineError = new Adw.ActionRow({title: pipelines.error, use_markup: false});
    pipelineGroup.add(pipelineError);
    const refreshPipeline = () => {
        syncing = true;
        const names = ['Custom', ...pipelines.items.map(p => p.name)];
        if (!preset.model || preset.model.get_n_items() !== names.length || names.some((name, index) => preset.model.get_string(index) !== name))
            preset.model = Gtk.StringList.new(names);
        preset.selected = pipelines.selected ? pipelines.items.indexOf(pipelines.selected) + 1 : 0;
        pipelineError.title = pipelines.error;
        pipelineError.visible = Boolean(pipelines.error);
        syncing = false;
    };
    const unsubscribePipeline = pipelines.subscribe(refreshPipeline);
    preset.connect('notify::selected', () => {
        if (syncing) return;
        if (preset.selected === 0 && pipelines.selected) pipelines.detach();
        else if (preset.selected > 0 && pipelines.items[preset.selected - 1] !== pipelines.selected)
            pipelines.select(pipelines.items[preset.selected - 1]);
    });
    refreshPipeline();
    const parameters = new Map(pipelines.catalog.parameters.map(p => [p.key, p]));
    const filterUpdates = [];
    for (const spec of pipelines.catalog.groups) {
        const section = new Adw.PreferencesGroup(); effect.add(section);
        const expander = new Adw.ExpanderRow({title: spec.title, expanded: !spec.enabled}); section.add(expander);
        if (spec.enabled) {
            const row = new Adw.SwitchRow({title: 'Enable'});
            settings.bind(spec.enabled, row, 'active', Gio.SettingsBindFlags.DEFAULT); expander.add_row(row);
        }
        if (spec.presets) {
            const choice = new Adw.ComboRow({title: 'Preset', model: Gtk.StringList.new(['Custom', ...spec.presets.map(p => p.name)])});
            let updating = false;
            const syncFilter = () => {
                updating = true;
                choice.selected = pipelines.filterPresetIndex(spec);
                choice.subtitle = choice.selected === 0 ? 'Adjust the values below.' : spec.presets[choice.selected - 1].description;
                updating = false;
            };
            choice.connect('notify::selected', () => {
                if (!updating && choice.selected > 0 && choice.selected !== pipelines.filterPresetIndex(spec))
                    pipelines.applyFilterPreset(spec.presets[choice.selected - 1]);
            });
            settings.bind(spec.enabled, choice, 'sensitive', Gio.SettingsBindFlags.GET);
            filterUpdates.push(syncFilter); syncFilter(); expander.add_row(choice);
        }
        for (const key of spec.keys) {
            const p = parameters.get(key);
            let row;
            if (typeof p.default === 'boolean') {
                row = new Adw.SwitchRow({title: p.title});
                settings.bind(key, row, 'active', Gio.SettingsBindFlags.DEFAULT);
            } else {
                row = new Adw.SpinRow({title: p.title, digits: p.step < 0.1 ? 2 : p.step < 1 ? 1 : 0,
                    adjustment: new Gtk.Adjustment({lower: p.min, upper: p.max, step_increment: p.step, page_increment: p.step * 5})});
                settings.bind(key, row, 'value', Gio.SettingsBindFlags.DEFAULT);
            }
            if (spec.enabled) settings.bind(spec.enabled, row, 'sensitive', Gio.SettingsBindFlags.GET);
            expander.add_row(row);
        }
    }
    const pipelineChanged = settings.connect('changed', (_settings, key) => {
        pipelines.changed(key);
        if (parameters.has(key)) for (const update of filterUpdates) update();
    });
    const limits = new Adw.PreferencesGroup({
        title: 'Performance protection',
        description: 'Water Ripple uses GNOME’s compositor directly. Retro Terminal’s afterglow has a separate capture limit.',
    });
    settingsSection.add(limits);
    for (const [key, title, subtitle, low, high, step] of [
        ['fps', 'Animation limit', 'Maximum ripple updates per second, up to the monitor’s frame rate.', 15, 120, 1],
        ['patch-size', 'Ripple area · px', 'Maximum width or height of the area affected by a ripple.', 256, 512, 64],
        ['max-waves', 'Simultaneous waves', 'Maximum number of ripples across the selected monitors.', 1, 2, 1],
    ]) {
        const row = new Adw.SpinRow({title, subtitle, adjustment: new Gtk.Adjustment({
            lower: low, upper: high, step_increment: step, page_increment: step,
        })});
        settings.bind(key, row, 'value', Gio.SettingsBindFlags.DEFAULT);
        limits.add(row);
    }
    const recovery = new Adw.ActionRow({title: 'Recovery', subtitle: 'Resume only after the system becomes stable again.'});
    recovery.add_suffix(new Gtk.Label({label: 'At least 1 min delay · 30 s stable', wrap: true, xalign: 1}));
    limits.add(recovery);
    limits.add(autoRestart);
    const stop = new Gtk.Button({label: 'Stop', css_classes: ['destructive-action'], valign: Gtk.Align.CENTER});
    status.add_suffix(stop);
    let enableWhenReady = enableOnStartup;
    const cancelStartup = () => { enableWhenReady = false; };
    stop.connect('clicked', () => {
        cancelStartup();
        settings.set_boolean('recovery-pending', false);
        settings.set_boolean('active', false);
    });
    let closed = false;
    let timeout = 0;
    const renewLease = () => {
        settings.set_double('lease-until', GLib.get_monotonic_time() / 1e6 + 7.5);
        return GLib.SOURCE_CONTINUE;
    };
    if (!externalLease) {
        renewLease();
        timeout = GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, 1, renewLease);
    }
    const changed = settings.connect('changed::status', () => status.set_title(settings.get_string('status')));
    window.connect('close-request', () => {
        jokes.dispose();
        about.dispose();
        pipelines.dispose();
        closed = true;
        // GNOME can close its own settings window while the real controller still holds a lease.
        if (application) {
            settings.set_boolean('recovery-pending', false);
            settings.set_boolean('active', false);
            settings.set_double('lease-until', 0);
            Gio.Settings.sync();
        }
        if (timeout)
            GLib.source_remove(timeout);
        settings.disconnect(changed);
        for (const handler of emergencyChanges)
            settings.disconnect(handler);
        for (const handler of diagnosticChanges)
            settings.disconnect(handler);
        settings.disconnect(pipelineChanged);
        unsubscribePipeline(); library.dispose();
        return false;
    });
    // Direct destruction bypasses close-request. Hiding to the tray only unmaps.
    window.connect('unrealize', () => { jokes.dispose(); about.dispose(); });
    // Let GNOME load the installed extension through its supported API only.
    if (application && extensionConnection) {
        active.set_sensitive(false);
        status.set_title('Waiting for GNOME Shell…');
        const cancellable = new Gio.Cancellable();
        const busName = 'org.gnome.Shell.Extensions';
        const busPath = '/org/gnome/Shell/Extensions';
        const uuid = new GLib.Variant('(s)', [UUID]);
        let retries = startEffect ? 3 : 0;
        let timer = 0;
        let readyDeadline = 0;

        function schedule(delayMs, callback) {
            timer = GLib.timeout_add(GLib.PRIORITY_DEFAULT, delayMs, () => {
                timer = 0;
                callback();
                return GLib.SOURCE_REMOVE;
            });
        }

        function fail(message) {
            settings.set_boolean('active', false);
            settings.set_string('status', message);
            status.set_title(message);
        }

        function request(method, onReply) {
            extensionConnection.call(busName, busPath, busName, method, uuid,
                null, Gio.DBusCallFlags.NONE, 2000, cancellable, (connection, result) => {
                    if (closed) return;
                    let reply;
                    try {
                        [reply] = connection.call_finish(result).recursiveUnpack();
                    } catch (error) {
                        if (!cancellable.is_cancelled())
                            fail(`Could not contact GNOME Shell: ${error.message}`);
                        return;
                    }
                    onReply(reply);
                });
        }

        function waitForActive() {
            request('GetExtensionInfo', info => {
                // GNOME Shell 50's Extensions D-Bus API uses ACTIVE = 1,
                // ERROR = 3 and OUT_OF_DATE = 4.
                if (info.state === 1) {
                    status.set_title(settings.get_string('status'));
                    if (enableWhenReady) {
                        renewLease();
                        settings.set_boolean('active', true);
                    }
                    active.set_sensitive(true);
                } else if (info.state === 3 || info.state === 4) {
                    fail(info.error ? `Could not load Ripple: ${info.error}` :
                        'Ripple could not load in this GNOME session');
                } else if (GLib.get_monotonic_time() < readyDeadline) {
                    schedule(200, waitForActive);
                } else {
                    fail('GNOME did not activate the Ripple extension');
                }
            });
        }

        function enableExtension() {
            request('EnableExtension', accepted => {
                // An accepted request only changes the extension list. Shell may
                // still be loading the module and reset active during enable().
                if (accepted) {
                    readyDeadline = GLib.get_monotonic_time() + 5_000_000;
                    waitForActive();
                } else if (retries > 0) {
                    retries--;
                    schedule(1000, enableExtension);
                } else {
                    fail('Ripple extension unavailable in this GNOME session');
                }
            });
        }
        enableExtension();
        window.connect('close-request', () => {
            cancellable.cancel();
            if (timer) GLib.source_remove(timer);
            return false;
        });
    }
    return {page, footer, active, preset, stop, status, pipelines, library, about, aboutRow, aboutSection: aboutSection.row,
        settingsSection: settingsSection.row, jokes, cancelStartup, startup, diagnosticStatus, diagnosticShortcut, diagnosticRecovery};
}
