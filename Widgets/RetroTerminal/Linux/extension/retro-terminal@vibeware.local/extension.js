// SPDX-License-Identifier: MIT
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import Meta from 'gi://Meta';
import Shell from 'gi://Shell';
import St from 'gi://St';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as PanelMenu from 'resource:///org/gnome/shell/ui/panelMenu.js';
import * as PopupMenu from 'resource:///org/gnome/shell/ui/popupMenu.js';
import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';
import {CrtEffect, MAX_MONITOR_REGIONS} from './effect.js';
import {DelayGuard, MAX_DESKTOP_PIXELS, MAX_STAGE_PIXELS, surfaceAllowed} from './guard.js';
import {shellPressure, watchMemory} from './ram-guard.js';
import {PhosphorPersistence} from './persistence.js';
import {GlitchSequence} from './glitches.js';
import {DEFAULTS, readValues} from './settings-model.js';
import {scheduleStartupGpuJoke} from './gpu-joke-notifications.js';
import {getShellMonitors, selectMonitors} from './shared/monitor-selection.js';
import {MAX_RECOVERY_ATTEMPTS, RecoveryWindow, parseResources} from './recovery.js';

const now = () => GLib.get_monotonic_time() / 1e6;
export default class RetroTerminal extends Extension {
    enable() {
        this._settings = this.getSettings();
        this._startRamWatch();
        this._signals = [];
        this._timer = 0;
        this._previewTimer = 0;
        this._settingsUpdate = 0;
        this._effect = null;
        this._unredirectInhibited = false;
        this._persistence = null;
        this._glitchTimer = 0;
        this._glitchWake = 0;
        this._glitchSequence = new GlitchSequence();
        this._panic = Meta.KeyBindingAction.NONE;
        this._selectedMonitors = [];
        this._recovery = null;
        this._recoveryTimer = 0;
        this._recoveryAttempts = 0;
        this._settings.set_boolean('recovery-pending', false);
        this._settings.set_boolean('panic-available', false);
        this._settings.set_boolean('active', false);
        this._settings.set_string('status', 'Stopped');
        this._button = new PanelMenu.Button(0.0, 'Retro Terminal');
        this._button.add_child(new St.Icon({icon_name: 'utilities-terminal-symbolic', style_class: 'system-status-icon'}));
        this._button.menu.addAction('Preferences', () => this.openPreferences());
        this._button.menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());
        this._toggle = new PopupMenu.PopupSwitchMenuItem('CRT desktop', false);
        this._toggle.connect('toggled', (_item, state) => this._settings.set_boolean('active', state));
        this._button.menu.addMenuItem(this._toggle);
        this._button.menu.addAction('Try for 15 seconds', () => {
            if (this._settings.get_boolean('active')) return;
            this._settings.set_boolean('active', true);
            if (this._effect) {
                this._previewTimer = GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, 15, () => {
                    this._previewTimer = 0;
                    this._stop('Preview finished');
                    return GLib.SOURCE_REMOVE;
                });
            } else if (this._settings.get_boolean('active')) this._stop('Select a display before starting the preview');
        });
        const palette = new PopupMenu.PopupSubMenuMenuItem('Palette');
        ['Green phosphor', 'Amber terminal', 'Arcade color'].forEach((name, index) => {
            palette.menu.addAction(name, () => this._selectPalette(index));
        });
        this._button.menu.addMenuItem(palette);
        this._button.menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());
        this._button.menu.addAction('Try a glitch now', () => this._startGlitch(true));
        this._button.menu.addAction('Stop', () => this._stop('Stopped manually'));
        this._status = new PopupMenu.PopupMenuItem('Stopped', {reactive: false});
        this._button.menu.addMenuItem(this._status);
        this._button.menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());
        this._button.menu.addAction('Mini Utilities', () => {
            Gio.AppInfo.launch_default_for_uri('https://umbertogiacobbi.biz/mini-utilities/', global.create_app_launch_context(0, -1));
        });
        this._button.menu.addAction('About', () => {
            this._settings.set_int64('about-request', GLib.get_monotonic_time());
            this.openPreferences();
        });
        Main.panel.addToStatusArea(this.uuid, this._button);
        this._syncPanic();
        this._connect(this._settings, 'changed', (_settings, key) => {
            if (key === 'status') this._status.label.text = this._settings.get_string('status');
            else if (key === 'active') {
                if (this._settings.get_boolean('active')) this._recoveryAttempts = 0;
                this._sync();
            }
            else if (key === 'target-monitors') this._sync();
            else if (key === 'panic-enabled') this._syncPanic();
            else if (key === 'auto-restart' && !this._settings.get_boolean('auto-restart') && this._recovery)
                this._stop('Automatic recovery cancelled');
            else if (key === 'recovery-delay' && this._recovery)
                this._recovery.window = new RecoveryWindow(this._recovery.started, this._settings.get_int('recovery-delay'), this._recoveryAttempts);
            else if (key === 'animation-fps' || key === 'capture-fps') {
                try {
                    if (key === 'animation-fps' && this._glitchTimer) this._startGlitchFrames();
                    if (this._effect) this._configurePersistence();
                } catch (error) {
                    console.error('Retro Terminal:', error);
                    this._stop(`Could not set the frame limit: ${error.message}. Enable CRT again to retry.`);
                }
            }
            else if (key === 'glitch-request') this._startGlitch(true);
            else if (Object.hasOwn(DEFAULTS, key) && this._effect) {
                try {
                    const glitch = key.startsWith('glitch');
                    const persistence = key.startsWith('persistence-');
                    if (glitch || key === 'intensity') this._configureGlitches();
                    if (persistence || key === 'intensity') this._configurePersistence();
                    if (!glitch && !persistence && this._effect) this._queueConfigure();
                } catch (error) {
                    console.error('Retro Terminal:', error);
                    this._stop(`Could not apply settings: ${error.message}. Enable CRT again to retry.`);
                }
            }
        });
        this._connect(Main.layoutManager, 'monitors-changed', () => this._stop('Display changed; enable again to resume'));
        this._connect(St.ThemeContext.get_for_stage(global.stage), 'notify::scale-factor',
            () => this._stop('Display scale changed; enable again to resume'));
        this._connect(Main.sessionMode, 'updated', () => {
            if (!this._userSession())
                this._stop('Session changed; enable again to resume');
        });
        this._cancelGpuJokes = scheduleStartupGpuJoke(this._settings, joke => {
            if (this._userSession()) Main.notify("Retro Terminal · That's a joke", joke);
        });
    }

    _connect(object, signal, callback) { this._signals.push([object, object.connect(signal, callback)]); }
    _syncPanic() {
        if (this._panic !== Meta.KeyBindingAction.NONE) Main.wm.removeKeybinding('retro-terminal-panic');
        this._panic = Meta.KeyBindingAction.NONE;
        if (this._settings.get_boolean('panic-enabled')) {
            this._panic = Main.wm.addKeybinding('retro-terminal-panic', this._settings, Meta.KeyBindingFlags.IGNORE_AUTOREPEAT,
                Shell.ActionMode.NORMAL | Shell.ActionMode.OVERVIEW | Shell.ActionMode.POPUP | Shell.ActionMode.SYSTEM_MODAL,
                () => this._stop('Stopped by keyboard shortcut'));
        }
        const available = this._panic !== Meta.KeyBindingAction.NONE;
        this._settings.set_boolean('panic-available', available);
        if (this._settings.get_boolean('active') && this._settings.get_boolean('panic-enabled') && !available)
            this._stop('Emergency shortcut unavailable');
        else this._updateActiveStatus();
    }

    _monitors() {
        return selectMonitors(getShellMonitors(Main.layoutManager.monitors, global.display), this._settings.get_strv('target-monitors'));
    }

    _userSession() {
        return !Main.sessionMode.isLocked &&
            (Main.sessionMode.currentMode === 'user' || Main.sessionMode.parentMode === 'user');
    }
    _dimensions() {
        const scale = Math.max(1, St.ThemeContext.get_for_stage(global.stage).scale_factor);
        return [global.stage.width, global.stage.height, scale];
    }
    _selectPalette(index) {
        this._settings.set_int('preset', index);
        this._settings.set_boolean('custom-color', false);
    }
    _queueConfigure() {
        if (this._settingsUpdate) return;
        this._settingsUpdate = GLib.idle_add(GLib.PRIORITY_HIGH_IDLE, () => {
            this._settingsUpdate = 0;
            if (!this._effect) return GLib.SOURCE_REMOVE;
            try {
                this._configure();
            } catch (error) {
                console.error('Retro Terminal:', error);
                this._stop(`Could not apply settings: ${error.message}. Enable CRT again to retry.`);
            }
            return GLib.SOURCE_REMOVE;
        });
    }
    _configure() {
        const [width, height, scale] = this._dimensions();
        this._effect.configure(this._settings, width * scale, height * scale);
    }
    _configurePersistence() {
        if (!this._persistence) return;
        this._persistence.configure(this._settings, this._selectedMonitors);
        if (this._settings.get_boolean('persistence-enabled') && this._settings.get_double('intensity') > 0 &&
            this._settings.get_double('persistence-strength') > 0) this._persistence.start();
        else this._persistence.stop();
        this._updateActiveStatus();
    }
    _updateActiveStatus() {
        if (!this._effect) return;
        const captures = this._persistence?.captureInfo ?? [];
        const history = captures.length ? ` · Afterglow ${captures.map(capture => `${capture.width}×${capture.height}${capture.reduced ? ' (scaled)' : ''}`).join(', ')}` : '';
        const count = this._selectedMonitors.length;
        this._settings.set_string('status', `Active · ${count} ${count === 1 ? 'display' : 'displays'}${history}`);
    }
    _configureGlitches() {
        this._cancelGlitchTimers();
        const values = readValues(this._settings);
        if (values.intensity === 0) values['glitch-intensity'] = 0;
        this._glitchSequence.configure(values, now());
        this._effect?.setGlitch(0, 0, 0, 0);
        this._armGlitch();
    }
    _cancelGlitchTimers() {
        if (this._glitchTimer) GLib.source_remove(this._glitchTimer);
        if (this._glitchWake) GLib.source_remove(this._glitchWake);
        this._glitchTimer = this._glitchWake = 0;
    }
    _armGlitch() {
        if (!this._effect) return;
        const due = this._glitchSequence.next;
        this._settings.set_string('glitch-status', Number.isFinite(due) ? 'Waiting for the next random glitch' : 'Idle · Try now is available');
        if (!Number.isFinite(due)) return;
        this._glitchWake = GLib.timeout_add(GLib.PRIORITY_DEFAULT, Math.max(1, Math.ceil((due - now()) * 1000)), () => {
            this._glitchWake = 0;
            if (!this._startGlitch(false)) this._armGlitch();
            return GLib.SOURCE_REMOVE;
        });
    }
    _startGlitch(manual) {
        if (!this._effect) {
            this._settings.set_string('glitch-status', 'Enable CRT before trying a glitch');
            return false;
        }
        if (!this._glitchSequence.start(now(), manual)) {
            if (manual) this._settings.set_string('glitch-status', 'Choose a glitch type and intensity, or wait for the current episode and rest period');
            return false;
        }
        if (this._glitchWake) GLib.source_remove(this._glitchWake);
        this._glitchWake = 0;
        const labels = ['', 'RGB misalignment', 'Horizontal tear', 'Line interference', 'Soft scanning band'];
        this._settings.set_string('glitch-status', labels[this._glitchSequence.event.type]);
        this._startGlitchFrames();
        return true;
    }

    _startGlitchFrames() {
        if (this._glitchTimer) GLib.source_remove(this._glitchTimer);
        const interval = Math.ceil(1000 / this._settings.get_int('animation-fps'));
        this._glitchTimer = GLib.timeout_add(GLib.PRIORITY_DEFAULT, interval, () => {
            try {
                const frame = this._glitchSequence.frame(now());
                if (!frame || !this._effect) {
                    this._glitchTimer = 0;
                    this._effect?.setGlitch(0, 0, 0, 0);
                    this._armGlitch();
                    return GLib.SOURCE_REMOVE;
                }
                this._effect.setGlitch(frame.type, frame.progress, frame.intensity, frame.seed);
                return GLib.SOURCE_CONTINUE;
            } catch (error) {
                this._glitchTimer = 0;
                console.error('Retro Terminal glitch:', error);
                this._stop('Glitch rendering failed; restart manually');
                return GLib.SOURCE_REMOVE;
            }
        });
    }
    _sync() {
        const active = this._settings.get_boolean('active');
        this._toggle.setToggleState(active);
        if (!active) {
            this._cancelRecovery();
            this._clear();
            this._settings.set_string('status', 'Stopped');
            return;
        }
        if (this._recovery) return;
        if (this._settings.get_boolean('panic-enabled') && this._panic === Meta.KeyBindingAction.NONE) {
            this._stop('Emergency shortcut unavailable');
            return;
        }
        if (!this._userSession()) {
            this._stop('Available only in the unlocked desktop');
            return;
        }
        try {
            const monitors = this._monitors();
            if (!monitors.length) {
                if (this._previewTimer) this._stop('Preview stopped because no displays are selected');
                else {
                    this._clear();
                    this._settings.set_string('status', 'Select a display to show the effect');
                }
                return;
            }
            const [width, height, scale] = this._dimensions();
            const actorScale = Math.max(scale, Math.ceil(Main.layoutManager.uiGroup.get_resource_scale()));
            if (monitors.length > MAX_MONITOR_REGIONS || !surfaceAllowed(width, height, scale, MAX_DESKTOP_PIXELS) ||
                !surfaceAllowed(width + 4, height + 4, actorScale, MAX_STAGE_PIXELS, 16384)) {
                this._stop('Desktop layout exceeds the CRT framebuffer safety limit');
                return;
            }
            this._selectedMonitors = monitors;
            if (this._effect) {
                this._effect.setMonitors(monitors, width, height);
                this._configurePersistence();
                return;
            }
            // Fullscreen surfaces must pass through the compositor while CRT is active.
            global.compositor.disable_unredirect();
            this._unredirectInhibited = true;
            this._effect = new CrtEffect();
            this._configure();
            this._effect.setMonitors(monitors, width, height);
            // The base CRT pass transforms uiGroup once, without clones or input grabs.
            // Optional temporal afterglow separately captures the composed stage on the GPU.
            Main.layoutManager.uiGroup.add_effect_with_name(this.uuid, this._effect);
            this._persistence = new PhosphorPersistence({
                onError: error => {
                    console.error('Retro Terminal persistence:', error);
                    this._stop(`Afterglow stopped: ${error.message}. Enable CRT again to retry.`);
                },
                onDelay: delay => this._pauseAfterDelay(delay),
            });
            this._configurePersistence();
            if (!this._effect) return;
            this._configureGlitches();
            this._updateActiveStatus();
            const guard = new DelayGuard(now());
            this._timer = GLib.timeout_add(GLib.PRIORITY_DEFAULT, 250, () => {
                if (guard.sample(now())) {
                    this._timer = 0;
                    if (!Number.isFinite(guard.lastDelay) || guard.lastDelay < 0) this._stop('The desktop clock became invalid');
                    else this._pauseAfterDelay(Math.round(guard.lastDelay * 1000));
                    return GLib.SOURCE_REMOVE;
                }
                return GLib.SOURCE_CONTINUE;
            });
        } catch (error) {
            console.error('Retro Terminal:', error);
            this._stop(`Could not start CRT: ${error.message}. Enable CRT again to retry.`);
        }
    }

    _cancelRecovery() {
        if (this._recoveryTimer) GLib.source_remove(this._recoveryTimer);
        this._recoveryTimer = 0;
        this._recovery = null;
        this._settings.set_boolean('recovery-pending', false);
    }

    _sampleResources() {
        try {
            const [cpuOk, cpu] = GLib.file_get_contents('/proc/stat');
            const [memoryOk, memory] = GLib.file_get_contents('/proc/meminfo');
            if (!cpuOk || !memoryOk) return null;
            const decoder = new TextDecoder();
            return parseResources(decoder.decode(cpu), decoder.decode(memory));
        } catch {
            return null;
        }
    }

    _pauseAfterDelay(delay) {
        const status = `Paused after a desktop delay (${delay} ms)`;
        if (this._previewTimer || !this._settings.get_boolean('auto-restart') || this._recoveryAttempts >= MAX_RECOVERY_ATTEMPTS) {
            this._stop(`${status}. Turn on CRT to resume.`);
            Main.notify('Retro Terminal stopped', 'The desktop slowed down. Turn on CRT when ready.');
            return;
        }
        this._clear();
        const started = now();
        this._recovery = {started, window: new RecoveryWindow(started, this._settings.get_int('recovery-delay'), this._recoveryAttempts)};
        this._settings.set_boolean('recovery-pending', true);
        this._settings.set_string('status', `${status}. Waiting for system stability.`);
        Main.notify('Retro Terminal paused', 'The desktop slowed down. CRT will resume after the system stabilizes.');
        this._recoveryTimer = GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, 5, () => {
            if (!this._userSession()) {
                this._recoveryTimer = 0;
                this._stop('Session changed; enable again to resume');
                return GLib.SOURCE_REMOVE;
            }
            if (!this._recovery.window.ready(now(), this._sampleResources())) return GLib.SOURCE_CONTINUE;
            this._recoveryTimer = 0;
            this._cancelRecovery();
            this._recoveryAttempts++;
            this._sync();
            return GLib.SOURCE_REMOVE;
        });
    }

    _clear() {
        if (this._previewTimer) GLib.source_remove(this._previewTimer);
        if (this._timer) GLib.source_remove(this._timer);
        if (this._settingsUpdate) GLib.source_remove(this._settingsUpdate);
        this._previewTimer = this._timer = this._settingsUpdate = 0;
        this._cancelGlitchTimers();
        this._glitchSequence.cancel(now());
        this._persistence?.stop();
        this._persistence = null;
        this._selectedMonitors = [];
        this._settings.set_string('glitch-status', 'Stopped');
        if (this._effect) Main.layoutManager.uiGroup.remove_effect(this._effect);
        this._effect = null;
        if (this._unredirectInhibited) {
            global.compositor.enable_unredirect();
            this._unredirectInhibited = false;
        }
    }
    _stop(reason) {
        if (this._settings.get_boolean('active')) this._settings.set_boolean('active', false);
        else {
            this._cancelRecovery();
            this._clear();
        }
        this._settings.set_string('status', reason);
    }
    _startRamWatch() {
        this._ramTimer = watchMemory(
            () => this._effect ? shellPressure() : null,
            event => {
                this._ramTimer = 0;
                console.warn(JSON.stringify({kind: 'guard.memory-pressure', app: 'RetroTerminal', ...event}));
                this._stop('Stopped because system memory is low. Enable the effect manually to resume.');
                Main.notify('Retro Terminal stopped', 'System memory is low. You can enable the effect again after memory recovers.');
                this._startRamWatch();
            });
    }

    disable() {
        if (this._ramTimer) GLib.source_remove(this._ramTimer);
        this._ramTimer = 0;
        this._cancelGpuJokes?.();
        this._cancelGpuJokes = null;
        this._stop('Extension disabled');
        for (const [object, id] of this._signals) object.disconnect(id);
        this._signals = [];
        if (this._panic !== Meta.KeyBindingAction.NONE) Main.wm.removeKeybinding('retro-terminal-panic');
        this._settings.set_boolean('panic-available', false);
        this._button?.destroy();
        this._button = this._toggle = this._status = this._settings = null;
    }
}
