// SPDX-License-Identifier: MIT
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import {SOUNDS} from './sounds.js';
import Meta from 'gi://Meta';
import Shell from 'gi://Shell';
import St from 'gi://St';
import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import {registerPanel} from './shared/panel-endpoint.js';
import * as PanelMenu from 'resource:///org/gnome/shell/ui/panelMenu.js';
import * as PopupMenu from 'resource:///org/gnome/shell/ui/popupMenu.js';
import {acquireShellEffects} from './shared/shell-effects.js';
import {registerEffectStop, stopAllEffects} from './shared/effect-stop.js';
import {MagnetizationCycle} from './cycle.js';
import {DegaussEffect} from './effect.js';

const MAX_DISPLAY_PIXELS = 3840 * 2160;
const MAX_STAGE_PIXELS = 2 * MAX_DISPLAY_PIXELS;
const PANIC_KEY = 'crt-degauss-panic';
const now = () => GLib.get_monotonic_time() / 1e6;

export default class CrtDegauss extends Extension {
    enable() {
        this._settings = this.getSettings();
        this._cycle = null;
        this._effect = null;
        this._wake = 0;
        this._frames = null;
        this._pressure = null;
        this._sound = null;
        this._lastStatus = null;
        this._settings.set_boolean('active', false);
        this._settings.set_boolean('degaussing', false);

        this._button = new PanelMenu.Button(0, 'CRT Degauss');
        this._button.add_child(new St.Icon({
            icon_name: 'local.vibeware.CrtDegauss',
            fallback_gicon: new Gio.FileIcon({file: this.dir.resolve_relative_path(
                'icons/hicolor/32x32/apps/local.vibeware.CrtDegauss.png')}),
            style_class: 'system-status-icon',
        }));
        const {menu} = this._button;
        menu.addAction('Preferences', () => this._panel.open('preferences'));
        menu.addAction('Open', () => this._panel.open('open'));
        menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());
        this._toggle = new PopupMenu.PopupSwitchMenuItem('Effect', false);
        this._toggle.connect('toggled', (_item, active) => this._settings.set_boolean('active', active));
        menu.addMenuItem(this._toggle);
        this._degaussItem = menu.addAction('Degauss', () => this._degauss());
        this._showNowItem = menu.addAction('Show magnetization now', () => this._showNow());
        menu.addAction('Stop', () => this._stop('Stopped'));
        menu.addAction('Disable all effects', () => stopAllEffects(global.stage));
        this._status = new PopupMenu.PopupMenuItem('Stopped', {reactive: false});
        menu.addMenuItem(this._status);
        menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());
        menu.addAction('Quit', () => {
            this._stop('Stopped');
            this._settings.set_int64('quit-request', GLib.get_monotonic_time());
            Main.extensionManager.disableExtension(this.uuid);
        });
        menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());
        menu.addAction('Mini Utilities', () => Gio.AppInfo.launch_default_for_uri(
            'https://umbertogiacobbi.biz/mini-utilities/', global.create_app_launch_context(0, -1)));
        menu.addAction('About', () => this._panel.open('about'));
        Main.panel.addToStatusArea(this.uuid, this._button);
        this._panel = registerPanel(this.dir, 'local.vibeware.CrtDegauss');

        this._shell = acquireShellEffects(global.stage, Main.layoutManager, () => {
            if (this._cycle) this._stop('Display changed. Enable the effect to resume.');
        });
        this._releaseStop = registerEffectStop(global.stage, () => this._stop('All effects stopped manually'));
        this._panic = Main.wm.addKeybinding(PANIC_KEY, this._settings, Meta.KeyBindingFlags.IGNORE_AUTOREPEAT,
            Shell.ActionMode.NORMAL | Shell.ActionMode.OVERVIEW | Shell.ActionMode.POPUP | Shell.ActionMode.SYSTEM_MODAL,
            () => this._stop('Emergency stop'));
        this._signals = [
            [this._settings, this._settings.connect('changed::active', () => {
                if (this._settings.get_boolean('active')) this._start();
                else if (this._cycle) this._stop('Stopped');
            })],
            [this._settings, this._settings.connect('changed::minutes', () => this._refresh())],
            [this._settings, this._settings.connect('changed::patches', () => this._refresh())],
            [this._settings, this._settings.connect('changed::sound', () => {
                if (!this._settings.get_boolean('sound')) this._cancelSound();
            })],
            [this._settings, this._settings.connect('changed::degauss-request', () => this._degauss())],
            [this._settings, this._settings.connect('changed::show-now-request', () => this._showNow())],
            [Main.sessionMode, Main.sessionMode.connect('updated', () => {
                if (this._cycle && !this._sessionReady()) this._stop('Stopped when the desktop locked');
            })],
        ];
        this._setStatus('Stopped');
        this._updateControls();
    }

    _sessionReady() {
        return !Main.sessionMode.isLocked &&
            (Main.sessionMode.currentMode === 'user' || Main.sessionMode.parentMode === 'user');
    }

    _start() {
        if (this._cycle) return;
        if (!this._sessionReady() || this._panic === Meta.KeyBindingAction.NONE) {
            this._stop('The unlocked desktop and emergency shortcut must be available.');
            return;
        }
        const monitor = Main.layoutManager.primaryMonitor;
        const actor = Main.layoutManager.uiGroup;
        const {width, height} = global.stage;
        const scale = Math.max(1, Math.ceil(actor.get_resource_scale()));
        const monitorScale = monitor ? global.display.get_monitor_scale(monitor.index) : 1;
        const bufferWidth = (width + 4) * scale;
        const bufferHeight = (height + 4) * scale;
        if (!monitor || width <= 0 || height <= 0 ||
            monitor.width * monitor.height * monitorScale ** 2 > MAX_DISPLAY_PIXELS ||
            bufferWidth * bufferHeight > MAX_STAGE_PIXELS || Math.max(bufferWidth, bufferHeight) > 16384) {
            this._stop('The primary display or desktop size is outside the supported limit.');
            return;
        }
        if (this._shell.memoryPressure()) {
            this._stop('System memory is low. Enable the effect when memory recovers.');
            return;
        }
        try {
            this._effect = new DegaussEffect();
            this._effect.setDisplay(monitor, width, height);
            this._newAppearance();
            this._effect.setFrame(0, this._settings.get_int('patches'), -1);
            global.compositor.disable_unredirect();
            this._unredirectInhibited = true;
            actor.add_effect_with_name(this.uuid, this._effect);
            this._pressure = this._shell.subscribePressure(() =>
                this._stop('System memory is low. Enable the effect when memory recovers.', true));
            this._cycle = new MagnetizationCycle();
            this._cycle.start(now());
            this._refresh();
            this._updateControls();
        } catch (error) {
            console.error('CRT Degauss:', error);
            this._stop('Could not apply the effect. Enable it to retry.', true);
        }
    }

    _refresh() {
        if (!this._cycle) return;
        try {
            this._render(now());
        } catch (error) {
            console.error('CRT Degauss:', error);
            this._stop('The compositor effect stopped. Enable it to retry.', true);
        }
    }

    _render(time) {
        const wasDegaussing = this._cycle.degaussing;
        const frame = this._cycle.sample(time, this._settings.get_int('minutes'));
        if (wasDegaussing && !this._cycle.degaussing) this._newAppearance();
        this._effect.setFrame(frame.amount, this._settings.get_int('patches'), frame.pulse);
        if (frame.pulse >= 0) {
            this._setStatus('Degaussing…');
            return;
        }
        if (this._frames) {
            this._frames();
            this._frames = null;
            this._cancelSound();
            this._settings.set_boolean('degaussing', false);
            this._updateControls();
        }
        if (frame.due) {
            if (this._wake) GLib.source_remove(this._wake);
            this._wake = 0;
            this._setStatus('Degauss due');
            if (frame.notify)
                Main.notify('CRT Degauss', 'Is it time for a degauss? Use Degauss in the top-bar menu.');
            return;
        }
        const remaining = Math.ceil(frame.remaining / 60);
        this._setStatus(`Degauss in ${remaining} min`);
        if (!this._wake) {
            this._wake = GLib.timeout_add(GLib.PRIORITY_DEFAULT, Math.ceil(Math.min(1000, frame.remaining * 1000)), () => {
                this._wake = 0;
                this._refresh();
                return GLib.SOURCE_REMOVE;
            });
        }
    }

    _degauss() {
        if (!this._cycle?.degauss(now(), this._settings.get_int('minutes'))) return;
        if (this._wake) GLib.source_remove(this._wake);
        this._wake = 0;
        this._settings.set_boolean('degaussing', true);
        this._updateControls();
        this._frames = this._shell.subscribeFrames(() => this._refresh());
        if (this._settings.get_boolean('sound')) {
            try {
                this._sound = new Gio.Cancellable();
                global.display.get_sound_player().play_from_file(
                    this.dir.resolve_relative_path(SOUNDS[this._settings.get_uint('sound-variant')][1]),
                    'CRT Degauss', this._sound);
            } catch (error) {
                this._cancelSound();
                console.debug('CRT Degauss sound:', error.message);
            }
        }
        this._refresh();
    }

    _newAppearance() {
        this._effect.setSeed(1 + Math.floor(Math.random() * 10000));
    }

    _showNow() {
        if (!this._cycle?.showNow(now(), this._settings.get_int('minutes'))) return;
        this._refresh();
    }

    _cancelSound() {
        this._sound?.cancel();
        this._sound = null;
    }

    _updateControls() {
        this._toggle.setToggleState(Boolean(this._cycle));
        this._degaussItem.setSensitive(Boolean(this._cycle) && !this._settings.get_boolean('degaussing'));
        this._showNowItem.setSensitive(Boolean(this._cycle) && !this._settings.get_boolean('degaussing'));
    }

    _setStatus(status) {
        if (this._lastStatus === status) return;
        this._lastStatus = status;
        this._status.label.text = status;
        this._settings.set_string('status', status);
    }

    _stop(status, notify = false) {
        this._cycle = null;
        this._cancelSound();
        if (this._wake) GLib.source_remove(this._wake);
        this._wake = 0;
        this._frames?.();
        this._frames = null;
        this._pressure?.();
        this._pressure = null;
        const actor = this._effect?.get_actor();
        if (actor) actor.remove_effect(this._effect);
        this._effect = null;
        if (this._unredirectInhibited) {
            global.compositor.enable_unredirect();
            this._unredirectInhibited = false;
        }
        this._settings.set_boolean('active', false);
        this._settings.set_boolean('degaussing', false);
        this._updateControls();
        this._setStatus(status);
        if (notify && this._sessionReady()) Main.notify('CRT Degauss stopped', status);
    }

    disable() {
        this._panel?.dispose();
        this._panel = null;
        this._stop('Stopped');
        for (const [object, id] of this._signals) object.disconnect(id);
        Main.wm.removeKeybinding(PANIC_KEY);
        this._releaseStop();
        this._shell.release();
        this._button.destroy();
        this._signals = this._releaseStop = this._shell = this._settings = this._button = null;
    }
}
