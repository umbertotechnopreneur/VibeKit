// SPDX-License-Identifier: GPL-3.0-or-later
import Adw from 'gi://Adw';
import Gtk from 'gi://Gtk?version=4.0';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';

export const UUID = 'ripple@vibeware.local';
export {SCHEMA} from './schema.js';
import {loadPipelines} from './pipelines.js';
import {pipelineControls} from './pipeline-ui.js';
import {createAbout} from './about.js';
import {createGpuJokes} from './gpu-jokes.js';

export function buildControls(window, settings, application = null, externalLease = false, startEffect = false, extensionConnection = Gio.DBus.session) {
    window.set_title('Ripple');
    window.set_default_size(760, 860);
    const {model: pipelines, path: libraryPath} = loadPipelines(settings);
    const page = new Adw.PreferencesPage({title: 'Effect', icon_name: 'preferences-desktop-effects-symbolic'});
    const about = createAbout(window);
    const aboutPage = new Adw.PreferencesPage({title: 'About', icon_name: 'help-about-symbolic'});
    const aboutGroup = new Adw.PreferencesGroup({title: 'VibeWare', description: 'Desktop toys by Umberto Giacobbi'});
    const aboutRow = new Adw.ActionRow({title: 'About Ripple', subtitle: 'Version, credits, license and project links', activatable: true});
    aboutRow.add_suffix(new Gtk.Image({icon_name: 'go-next-symbolic'}));
    aboutRow.connect('activated', () => about.show());
    aboutGroup.add(aboutRow);
    aboutPage.add(aboutGroup);
    let content = page;
    let stack = null;
    const library = pipelineControls(window, pipelines, libraryPath, () => {
        if (stack) stack.visible_child_name = 'effect';
        else window.set_visible_page(page);
    });
    if (!application) {
        window.set_search_enabled(false);
        window.add(page); window.add(library.page); window.add(aboutPage);
    } else {
        stack = new Adw.ViewStack({vexpand: true});
        stack.add_titled(page, 'effect', 'Effect').icon_name = 'preferences-desktop-effects-symbolic';
        stack.add_titled(library.page, 'pipelines', 'Pipelines').icon_name = 'view-list-symbolic';
        stack.add_titled(aboutPage, 'about', 'About').icon_name = 'help-about-symbolic';
        content = new Gtk.Box({orientation: Gtk.Orientation.VERTICAL});
        content.append(new Adw.ViewSwitcher({stack, policy: Adw.ViewSwitcherPolicy.WIDE, halign: Gtk.Align.CENTER}));
        content.append(stack);
    }
    const group = new Adw.PreferencesGroup();
    page.add(group);
    const jokes = createGpuJokes(settings);
    page.add(jokes.group);
    const active = new Adw.SwitchRow({title: 'Effect'});
    settings.bind('active', active, 'active', Gio.SettingsBindFlags.DEFAULT);
    group.add(active);
    const autoRestart = new Adw.SwitchRow({
        title: 'Auto restart',
        subtitle: 'Resume after a performance pause when the system is stable',
    });
    settings.bind('auto-restart', autoRestart, 'active', Gio.SettingsBindFlags.DEFAULT);
    group.add(autoRestart);
    const emergency = new Adw.SwitchRow({
        title: 'Emergency stop shortcut',
        subtitle: 'Super + Shift + Esc stops the effect',
    });
    settings.bind('panic-enabled', emergency, 'active', Gio.SettingsBindFlags.DEFAULT);
    group.add(emergency);
    const syncEmergency = () => {
        emergency.subtitle = !settings.get_boolean('panic-enabled')
            ? 'Shortcut disabled'
            : settings.get_boolean('panic-available')
                ? 'Super + Shift + Esc stops the effect'
                : 'Shortcut unavailable; GNOME may not have registered it';
    };
    const emergencyChanges = [
        settings.connect('changed::panic-enabled', syncEmergency),
        settings.connect('changed::panic-available', syncEmergency),
    ];
    syncEmergency();
    const status = new Adw.ActionRow({title: settings.get_string('status')});
    group.add(status);
    const target = new Adw.ComboRow({
        title: 'Apply to', model: Gtk.StringList.new(['Wallpaper only', 'Windows only', 'Wallpaper and windows']),
    });
    const targets = ['desktop', 'windows', 'both'];
    target.selected = Math.max(0, targets.indexOf(settings.get_string('target')));
    target.connect('notify::selected', () => settings.set_string('target', targets[target.selected]));
    group.add(target);
    const pipelineGroup = new Adw.PreferencesGroup({description: 'Changes apply immediately. Saved pipelines update automatically; duplicate a built-in pipeline to keep your changes.'});
    page.add(pipelineGroup);
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
        const section = new Adw.PreferencesGroup(); page.add(section);
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
    const limits = new Adw.PreferencesGroup({title: 'Limits', description: 'Animation follows the monitor frame clock, up to this FPS limit. 60 FPS is recommended for a 60 Hz display.'});
    page.add(limits);
    for (const [key, title, low, high, step] of [
        ['patch-size', 'Area · px', 256, 512, 64],
        ['max-waves', 'Simultaneous waves', 1, 2, 1],
        ['fps', 'FPS limit', 15, 120, 1],
    ]) {
        const row = new Adw.SpinRow({title, adjustment: new Gtk.Adjustment({
            lower: low, upper: high, step_increment: step, page_increment: step,
        })});
        settings.bind(key, row, 'value', Gio.SettingsBindFlags.DEFAULT);
        limits.add(row);
    }
    const foot = new Adw.PreferencesGroup({
        description: application
            ? 'Super + Shift + Esc stops the effect. You can leave Ripple in the top bar.'
            : 'Super + Shift + Esc stops the effect. Closing this panel also stops it.',
    });
    page.add(foot);
    const stop = new Gtk.Button({label: 'Stop', css_classes: ['destructive-action'], margin_top: 6});
    let enableWhenReady = application ? true : startEffect;
    stop.connect('clicked', () => {
        enableWhenReady = false;
        settings.set_boolean('recovery-pending', false);
        settings.set_boolean('active', false);
    });
    foot.add(stop);
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
        settings.set_boolean('recovery-pending', false);
        settings.set_boolean('active', false);
        settings.set_double('lease-until', 0);
        Gio.Settings.sync();
        if (timeout)
            GLib.source_remove(timeout);
        settings.disconnect(changed);
        for (const handler of emergencyChanges)
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
        const cancellable = new Gio.Cancellable();
        let retries = startEffect ? 3 : 0;
        function enableExtension() {
            extensionConnection.call('org.gnome.Shell.Extensions', '/org/gnome/Shell/Extensions',
                'org.gnome.Shell.Extensions', 'EnableExtension', new GLib.Variant('(s)', [UUID]),
                null, Gio.DBusCallFlags.NONE, 2000, cancellable, (connection, result) => {
                    if (closed)
                        return;

                    let ok = false;
                    let failure = 'Extension available after the next sign-in';
                    try {
                        [ok] = connection.call_finish(result).deep_unpack();
                    } catch {
                        failure = 'GNOME is unavailable';
                    }

                    if (ok) {
                        if (enableWhenReady) {
                            renewLease();
                            settings.set_boolean('active', true);
                        }
                        active.set_sensitive(true);
                    } else if (retries > 0) {
                        retries--;
                        GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, 1, () => {
                            if (!closed)
                                enableExtension();
                            return GLib.SOURCE_REMOVE;
                        });
                    } else {
                        settings.set_boolean('active', false);
                        settings.set_string('status', failure);
                    }
                });
        }
        enableExtension();
        window.connect('close-request', () => { cancellable.cancel(); return false; });
    }
    return {page: content, active, preset, stop, status, pipelines, library, about, aboutRow, aboutPage, jokes};
}
