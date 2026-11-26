// SPDX-License-Identifier: MIT
import Clutter from 'gi://Clutter';
import Cogl from 'gi://Cogl';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import GObject from 'gi://GObject';
import Meta from 'gi://Meta';
import Shell from 'gi://Shell';
import St from 'gi://St';
import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as PanelMenu from 'resource:///org/gnome/shell/ui/panelMenu.js';
import * as PopupMenu from 'resource:///org/gnome/shell/ui/popupMenu.js';
import {selectMonitors} from './shared/monitor-selection.js';
import {acquireShellEffects} from './shared/shell-effects.js';
import {registerEffectStop, stopAllEffects} from './shared/effect-stop.js';
import {isHistoryCapture} from './shared/capture-pass.js';
import {createPixels, TEXTURE_SIZE} from './gradient.js';
import {readAppearance} from './pipelines.js';

const Overlay = GObject.registerClass({GTypeName: 'VibewareVignetteOverlay'}, class extends Clutter.Actor {
    vfunc_paint(context) {
        if (!isHistoryCapture(global.stage))
            super.vfunc_paint(context);
    }
});

export default class Vignette extends Extension {
    enable() {
        this._settings = this.getSettings();
        this._group = new Overlay({name: 'vibeware-vignette-overlay', reactive: false, visible: false});
        global.stage.add_child(this._group);
        this._effects = acquireShellEffects(global.stage, Main.layoutManager, () => {
            if (!this._settings.get_boolean('active')) return;
            this._group.hide();
            this._queueUpdate();
        });
        this._releaseOverlay = this._effects.registerPostEffect(this._group);
        this._memoryWatch = null;
        this._signalRecords = [];
        this._pending = 0;
        this._textureKey = '';
        this._content = null;
        this._button = new PanelMenu.Button(0, 'Vignette');
        this._button.add_child(new St.Icon({
            icon_name: 'local.vibeware.Vignette',
            fallback_gicon: new Gio.FileIcon({file: this.dir.resolve_relative_path(
                'icons/hicolor/32x32/apps/local.vibeware.Vignette.png')}),
            style_class: 'system-status-icon',
        }));
        const {menu} = this._button;
        menu.addAction('Preferences', () => this.openPreferences());
        menu.addAction('Open', () => this.openPreferences());
        menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());
        this._toggle = new PopupMenu.PopupSwitchMenuItem('Effect', false);
        menu.addMenuItem(this._toggle);
        this._toggle.connect('toggled', (_item, active) => this._settings.set_boolean('active', active));
        menu.addAction('Stop', () => this._settings.set_boolean('active', false));
        menu.addAction('Disable all effects', () => stopAllEffects(global.stage));
        this._status = new PopupMenu.PopupMenuItem('Stopped', {reactive: false});
        menu.addMenuItem(this._status);
        menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());
        menu.addAction('Quit', () => {
            this._settings.set_boolean('active', false);
            this._settings.set_int64('quit-request', GLib.get_monotonic_time());
            Main.extensionManager.disableExtension(this.uuid);
        });
        menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());
        menu.addAction('Mini Utilities', () => Gio.AppInfo.launch_default_for_uri(
            'https://umbertogiacobbi.biz/mini-utilities/', global.create_app_launch_context(0, -1)));
        menu.addAction('About', () => {
            this._settings.set_int64('about-request', GLib.get_monotonic_time());
            this.openPreferences();
        });
        Main.panel.addToStatusArea(this.uuid, this._button);
        this._releaseStop = registerEffectStop(global.stage, () => {
            this._settings.set_boolean('active', false);
            this._clear('All effects stopped manually');
        });
        this._connect(this._settings, 'changed', (_settings, key) => {
            if (!['active', 'appearance', 'target-monitors'].includes(key)) return;
            if (this._settings.get_boolean('active')) this._queueUpdate();
            else if (key === 'active') this._clear('Stopped');
        });
        this._connect(Main.sessionMode, 'updated', () => {
            if (!this._sessionReady()) {
                this._settings.set_boolean('active', false);
                this._clear('Stopped because the desktop session changed');
            }
        });
        this._shortcut = Main.wm.addKeybinding('vignette-panic', this._settings,
            Meta.KeyBindingFlags.IGNORE_AUTOREPEAT, Shell.ActionMode.NORMAL | Shell.ActionMode.OVERVIEW |
                Shell.ActionMode.POPUP | Shell.ActionMode.SYSTEM_MODAL,
            () => this._settings.set_boolean('active', false));
        this._settings.set_boolean('panic-available', this._shortcut !== Meta.KeyBindingAction.NONE);
        this._settings.set_boolean('active', false);
        this._clear('Stopped');
    }

    _connect(object, signal, callback) {
        this._signalRecords.push([object, object.connect(signal, callback)]);
    }

    _sessionReady() {
        return !Main.sessionMode.isLocked &&
            (Main.sessionMode.currentMode === 'user' || Main.sessionMode.parentMode === 'user');
    }

    _queueUpdate() {
        if (this._pending) return;
        this._pending = GLib.timeout_add(GLib.PRIORITY_DEFAULT, 35, () => {
            this._pending = 0;
            try {
                this._update();
            } catch (error) {
                this._settings.set_boolean('active', false);
                this._clear(`Vignette stopped: ${error.message}`);
                console.error('Vignette:', error);
            }
            return GLib.SOURCE_REMOVE;
        });
    }

    _update() {
        if (!this._settings.get_boolean('active') || !this._sessionReady()) return;
        if (this._shortcut === Meta.KeyBindingAction.NONE)
            throw Error('The emergency shortcut is unavailable.');
        this._toggle.setToggleState(true);
        const monitors = selectMonitors(this._effects.displays(),
            this._settings.get_strv('target-monitors'));
        if (!monitors.length) {
            this._clear('Select a display for the effect', true);
            return;
        }
        const appearance = readAppearance(this._settings);
        if (appearance.intensity === 0) {
            this._clear('Active · intensity is zero', true);
            return;
        }
        if (!this._memoryWatch) {
            const pressure = this._effects.memoryPressure();
            if (pressure) {
                this._stopForMemoryPressure(pressure);
                return;
            }
            this._memoryWatch = this._effects.subscribePressure(event => this._stopForMemoryPressure(event));
        }
        const key = JSON.stringify(appearance);
        if (key !== this._textureKey) {
            const context = global.stage.get_context().get_backend().get_cogl_context();
            const texture = Cogl.Texture2D.new_from_data(context, TEXTURE_SIZE, TEXTURE_SIZE,
                Cogl.PixelFormat.RGBA_8888_PRE, TEXTURE_SIZE * 4, createPixels(appearance));
            this._content = Clutter.TextureContent.new_from_texture(texture, null);
            this._textureKey = key;
        }
        const byId = new Map(this._group.get_children().map(actor => [actor.name, actor]));
        for (const monitor of monitors) {
            let actor = byId.get(monitor.id);
            if (actor) {
                byId.delete(monitor.id);
            } else {
                actor = new Clutter.Actor({name: monitor.id, reactive: false,
                    content_gravity: Clutter.ContentGravity.RESIZE_FILL});
                actor.set_content_scaling_filters(Clutter.ScalingFilter.LINEAR, Clutter.ScalingFilter.LINEAR);
                this._group.add_child(actor);
            }
            if (actor.x !== monitor.x || actor.y !== monitor.y) actor.set_position(monitor.x, monitor.y);
            if (actor.width !== monitor.width || actor.height !== monitor.height) actor.set_size(monitor.width, monitor.height);
            if (actor.get_content() !== this._content) actor.set_content(this._content);
        }
        for (const actor of byId.values()) actor.destroy();
        this._effects.raisePostEffect();
        this._group.show();
        this._setStatus(`Active · ${monitors.length} ${monitors.length === 1 ? 'display' : 'displays'}`);
    }

    _stopForMemoryPressure(event) {
        console.warn(JSON.stringify({kind: 'guard.memory-pressure', app: 'Vignette', ...event}));
        this._settings.set_boolean('active', false);
        this._clear('Stopped because system memory is low. Enable the effect manually to resume.');
    }

    _setStatus(message) {
        this._status.label.text = message;
        this._settings.set_string('status', message);
    }

    _clear(message, armed = false) {
        this._memoryWatch?.();
        this._memoryWatch = null;
        if (this._pending) GLib.source_remove(this._pending);
        this._pending = 0;
        this._group.hide();
        for (const actor of this._group.get_children()) actor.destroy();
        this._content = null;
        this._textureKey = '';
        this._toggle.setToggleState(armed);
        this._setStatus(message);
    }

    disable() {
        this._releaseStop?.();
        this._releaseStop = null;
        this._clear('Stopped');
        this._releaseOverlay();
        this._effects.release();
        this._effects = this._releaseOverlay = null;
        for (const [object, signal] of this._signalRecords) object.disconnect(signal);
        this._signalRecords = [];
        Main.wm.removeKeybinding('vignette-panic');
        this._settings.set_boolean('panic-available', false);
        this._button.destroy();
        this._group.destroy();
        this._settings = this._button = this._group = null;
    }
}
