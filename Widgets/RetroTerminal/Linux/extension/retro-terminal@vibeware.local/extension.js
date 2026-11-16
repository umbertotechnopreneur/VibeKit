// SPDX-License-Identifier: MIT
import GLib from 'gi://GLib';
import Meta from 'gi://Meta';
import Shell from 'gi://Shell';
import St from 'gi://St';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as PanelMenu from 'resource:///org/gnome/shell/ui/panelMenu.js';
import * as PopupMenu from 'resource:///org/gnome/shell/ui/popupMenu.js';
import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';
import {CrtEffect} from './effect.js';
import {DelayGuard, surfaceAllowed} from './guard.js';
import {PhosphorPersistence} from './persistence.js';
import {GlitchSequence} from './glitches.js';
import {DEFAULTS, readValues} from './settings-model.js';

const now = () => GLib.get_monotonic_time() / 1e6;
export default class RetroTerminal extends Extension {
    enable() {
        this._settings = this.getSettings();
        this._signals = [];
        this._timer = 0;
        this._previewTimer = 0;
        this._effect = null;
        this._persistence = null;
        this._glitchTimer = 0;
        this._glitchWake = 0;
        this._glitchSequence = new GlitchSequence();
        this._panic = Meta.KeyBindingAction.NONE;
        this._settings.set_boolean('active', false);
        this._settings.set_string('status', 'Stopped');
        this._button = new PanelMenu.Button(0.0, 'Retro Terminal');
        this._button.add_child(new St.Icon({icon_name: 'utilities-terminal-symbolic', style_class: 'system-status-icon'}));
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
            }
        });
        this._button.menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());
        ['Green phosphor', 'Amber terminal', 'Arcade color'].forEach((name, index) => {
            this._button.menu.addAction(name, () => this._settings.set_int('preset', index));
        });
        this._button.menu.addAction('Preferences', () => this.openPreferences());
        this._button.menu.addAction('Try a glitch now', () => this._startGlitch(true));
        this._button.menu.addAction('Stop · Super+Shift+F12', () => this._stop('Stopped manually'));
        this._status = new PopupMenu.PopupMenuItem('Stopped', {reactive: false});
        this._button.menu.addMenuItem(this._status);
        Main.panel.addToStatusArea(this.uuid, this._button);
        this._panic = Main.wm.addKeybinding('retro-terminal-panic', this._settings, Meta.KeyBindingFlags.IGNORE_AUTOREPEAT,
            Shell.ActionMode.NORMAL | Shell.ActionMode.OVERVIEW | Shell.ActionMode.POPUP | Shell.ActionMode.SYSTEM_MODAL,
            () => this._stop('Stopped by keyboard shortcut'));
        this._connect(this._settings, 'changed', (_settings, key) => {
            if (key === 'status') this._status.label.text = this._settings.get_string('status');
            else if (key === 'active') this._sync();
            else if (key === 'glitch-request') this._startGlitch(true);
            else if (Object.hasOwn(DEFAULTS, key) && this._effect) {
                try {
                    this._configure();
                    if (key.startsWith('glitch') || key === 'intensity') this._configureGlitches();
                    if (!key.startsWith('glitch')) this._configurePersistence();
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
    }

    _connect(object, signal, callback) { this._signals.push([object, object.connect(signal, callback)]); }
    _userSession() {
        return !Main.sessionMode.isLocked &&
            (Main.sessionMode.currentMode === 'user' || Main.sessionMode.parentMode === 'user');
    }
    _dimensions() {
        const scale = Math.max(1, St.ThemeContext.get_for_stage(global.stage).scale_factor);
        return [global.stage.width, global.stage.height, scale];
    }
    _configure() {
        const [width, height, scale] = this._dimensions();
        this._effect.configure(this._settings, width * scale, height * scale);
    }
    _configurePersistence() {
        if (!this._persistence) return;
        const [width, height] = this._dimensions();
        this._persistence.configure(this._settings, width, height);
        if (this._settings.get_boolean('persistence-enabled') && this._settings.get_double('intensity') > 0 &&
            this._settings.get_double('persistence-strength') > 0) this._persistence.start();
        else this._persistence.stop();
        this._updateActiveStatus();
    }
    _updateActiveStatus() {
        if (!this._effect) return;
        const capture = this._persistence?.captureInfo;
        const history = capture ? `Afterglow ${capture.width}×${capture.height}${capture.reduced ? ' (scaled)' : ''} · ` : '';
        this._settings.set_string('status', `Active · ${history}Super+Shift+F12 stops CRT`);
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
        this._glitchTimer = GLib.timeout_add(GLib.PRIORITY_DEFAULT, 34, () => {
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
        return true;
    }
    _sync() {
        const active = this._settings.get_boolean('active');
        this._toggle.setToggleState(active);
        if (!active) {
            this._clear();
            this._settings.set_string('status', 'Stopped');
            return;
        }
        if (this._effect) return;
        if (this._panic === Meta.KeyBindingAction.NONE) {
            this._stop('Emergency shortcut unavailable');
            return;
        }
        if (!this._userSession()) {
            this._stop('Available only in the unlocked desktop');
            return;
        }
        if (!surfaceAllowed(...this._dimensions())) {
            this._stop('Desktop exceeds the 9 million pixel safety limit');
            return;
        }
        try {
            this._effect = new CrtEffect();
            this._configure();
            // The base CRT pass transforms uiGroup once, without clones or input grabs.
            // Optional temporal afterglow separately captures the composed stage on the GPU.
            Main.layoutManager.uiGroup.add_effect_with_name(this.uuid, this._effect);
            this._persistence = new PhosphorPersistence({onError: error => {
                console.error('Retro Terminal persistence:', error);
                this._stop(`Afterglow stopped: ${error.message}. Enable CRT again to retry.`);
            }});
            this._configurePersistence();
            if (!this._effect) return;
            this._configureGlitches();
            this._updateActiveStatus();
            const guard = new DelayGuard(now());
            this._timer = GLib.timeout_add(GLib.PRIORITY_DEFAULT, 250, () => {
                if (guard.sample(now())) {
                    this._timer = 0;
                    this._stop('Stopped after a desktop delay; restart manually');
                    Main.notify('Retro Terminal stopped', 'The desktop slowed down. The CRT effect remains off until you enable it again.');
                    return GLib.SOURCE_REMOVE;
                }
                return GLib.SOURCE_CONTINUE;
            });
        } catch (error) {
            console.error('Retro Terminal:', error);
            this._stop(`Could not start CRT: ${error.message}. Enable CRT again to retry.`);
        }
    }
    _clear() {
        if (this._previewTimer) GLib.source_remove(this._previewTimer);
        if (this._timer) GLib.source_remove(this._timer);
        this._previewTimer = this._timer = 0;
        this._cancelGlitchTimers();
        this._glitchSequence.cancel(now());
        this._persistence?.stop();
        this._persistence = null;
        this._settings.set_string('glitch-status', 'Stopped');
        if (this._effect) Main.layoutManager.uiGroup.remove_effect(this._effect);
        this._effect = null;
    }
    _stop(reason) {
        this._settings.set_boolean('active', false);
        this._clear();
        this._settings.set_string('status', reason);
    }
    disable() {
        this._stop('Extension disabled');
        for (const [object, id] of this._signals) object.disconnect(id);
        this._signals = [];
        if (this._panic !== Meta.KeyBindingAction.NONE) Main.wm.removeKeybinding('retro-terminal-panic');
        this._button?.destroy();
        this._button = this._toggle = this._status = this._settings = null;
    }
}
