// SPDX-License-Identifier: GPL-3.0-or-later
import Clutter from 'gi://Clutter';
import GLib from 'gi://GLib';
import Meta from 'gi://Meta';
import Shell from 'gi://Shell';
import St from 'gi://St';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';
import {RippleEffect} from './effect.js';

const NUMBERS = ['strength', 'speed', 'wavelength', 'rings', 'lifetime',
    'damping', 'light', 'radius', 'aspect', 'chromatic-separation', 'chromatic-blend',
    'crest-intensity', 'ripples-intensity', 'liquid-intensity'];
const SWITCHES = ['invert', 'chromatic-enabled', 'crest-enabled', 'ripples-enabled', 'liquid-enabled'];
const now = () => GLib.get_monotonic_time() / 1e6;

export default class RippleExtension extends Extension {
    enable() {
        this._settings = this.getSettings();
        this._patches = [];
        this._signals = [];
        this._timer = 0;
        this._pointerTimer = 0;
        this._lastPressed = false;
        this._lastClick = 0;
        this._previousFrame = 0;
        this._lagCount = 0;
        // A new login/extension reload always starts with refraction off.
        this._settings.set_boolean('active', false);
        this._settings.set_string('status', 'Ready');
        this._connect(this._settings, 'changed', (_settings, key) => {
            if (key === 'active') {
                if (!this._settings.get_boolean('active')) {
                    this._clear();
                    this._stopPointerPolling();
                } else {
                    this._startPointerPolling();
                }
                this._settings.set_string('status', this._settings.get_boolean('active') ? 'Active' : 'Stopped');
            }
            else if (['target', 'patch-size', 'fps'].includes(key))
                this._clear();
            else if (key === 'max-waves') {
                while (this._patches.length > this._settings.get_int('max-waves'))
                    this._removePatch(this._patches[0]);
            }
            else if (NUMBERS.includes(key) || SWITCHES.includes(key)) {
                const values = this._values();
                for (const patch of this._patches)
                    patch.effect.configure(values);
            }
        });
        this._connect(global.stage, 'captured-event', (_stage, event) => {
            if (event.type() === Clutter.EventType.BUTTON_PRESS && event.get_button() === 1) {
                const [x, y] = event.get_coords();
                this._onClick(x, y, event.get_source());
            }
            return Clutter.EVENT_PROPAGATE;
        });
        this._connect(Main.overview, 'showing', () => this._clear());
        this._connect(Main.layoutManager, 'monitors-changed', () => this._clear());
        this._connect(Main.sessionMode, 'updated', () => this._clear());
        Main.wm.addKeybinding('panic', this._settings,
            Meta.KeyBindingFlags.IGNORE_AUTOREPEAT,
            Shell.ActionMode.NORMAL | Shell.ActionMode.OVERVIEW,
            () => this._stop('Stopped by keyboard shortcut'));
        this._leaseTimer = GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, 1, () => {
            if (this._settings.get_boolean('active') && !this._leaseValid())
                this._stop('Effect stopped because the controller closed');
            return GLib.SOURCE_CONTINUE;
        });
    }

    _startPointerPolling() {
        if (this._pointerTimer)
            return;
        this._lastPressed = Boolean(global.get_pointer()[2] & Clutter.ModifierType.BUTTON1_MASK);
        // Wayland forwards application clicks before Clutter emits captured-event.
        // Read the cached pointer state only while active; never grab input.
        this._pointerTimer = GLib.timeout_add(GLib.PRIORITY_DEFAULT, 16, () => {
            const [x, y, modifiers] = global.get_pointer();
            const pressed = Boolean(modifiers & Clutter.ModifierType.BUTTON1_MASK);
            if (pressed && !this._lastPressed) {
                let source = global.stage.get_actor_at_pos(Clutter.PickMode.REACTIVE, x, y);
                outer: for (let actor = source; actor; actor = actor.get_parent()) {
                    for (const patch of this._patches) {
                        if (actor === patch.actor) {
                            source = patch.source;
                            break outer;
                        }
                    }
                }
                this._onClick(x, y, source);
            }
            this._lastPressed = pressed;
            return GLib.SOURCE_CONTINUE;
        });
    }

    _stopPointerPolling() {
        if (this._pointerTimer) {
            GLib.source_remove(this._pointerTimer);
            this._pointerTimer = 0;
        }
        this._lastPressed = false;
    }

    _connect(object, signal, handler) {
        const id = object.connect(signal, handler);
        this._signals.push([object, id]);
    }

    _leaseValid() {
        const remaining = this._settings.get_double('lease-until') - now();
        return remaining > 0 && remaining <= 8;
    }

    _values() {
        const values = {};
        for (const key of NUMBERS)
            values[key] = this._settings.get_double(key);
        for (const key of SWITCHES)
            values[key] = this._settings.get_boolean(key);
        return values;
    }

    _windowFromSource(source) {
        for (let actor = source; actor; actor = actor.get_parent()) {
            if (actor.meta_window)
                return actor;
        }
        return null;
    }

    _onClick(x, y, source) {
        if (!this._settings.get_boolean('active') || !this._leaseValid() ||
            Main.overview.visible || Main.sessionMode.isLocked)
            return;
        const time = now();
        if (time - this._lastClick < 0.18)
            return;
        const target = this._settings.get_string('target');
        const windowActor = this._windowFromSource(source);
        const metaWindow = windowActor?.meta_window;
        const desktopWindow = metaWindow && (metaWindow.get_window_type() === Meta.WindowType.DESKTOP ||
            (metaWindow.get_wm_class() ?? '').toLowerCase().includes('rastersoft.ding'));
        if (metaWindow && !desktopWindow) {
            if (target === 'desktop' || ![Meta.WindowType.NORMAL, Meta.WindowType.DIALOG,
                Meta.WindowType.MODAL_DIALOG].includes(metaWindow.get_window_type()) ||
                metaWindow.is_fullscreen() || !metaWindow.showing_on_its_workspace())
                return;
            this._lastClick = time;
            this._addPatch(windowActor, x, y, false);
            return;
        }
        if (target === 'windows')
            return;
        // Do not react to clicks on panels, menus, docks or the lock screen.
        let onBackground = Boolean(desktopWindow);
        for (let actor = source; actor && !onBackground; actor = actor.get_parent())
            onBackground = actor === Main.layoutManager._backgroundGroup;
        if (!onBackground)
            return;
        const background = Main.layoutManager._backgroundGroup.get_children().find(actor => {
            const [ax, ay] = actor.get_transformed_position();
            const [w, h] = actor.get_transformed_size();
            return x >= ax && y >= ay && x < ax + w && y < ay + h;
        });
        if (background) {
            this._lastClick = time;
            this._addPatch(background, x, y, true);
        }
    }

    _addPatch(source, stageX, stageY, desktop) {
        try {
            if (source.scale_x !== 1 || source.scale_y !== 1 || source.opacity !== 255)
                return;
            const [ok, localX, localY] = source.transform_stage_point(stageX, stageY);
            if (!ok)
                return;
            const parent = source.get_parent();
            if (!parent)
                return;
            const size = Math.min(512, this._settings.get_int('patch-size'));
            const width = Math.min(size, source.width);
            const height = Math.min(size, source.height);
            if (width < 16 || height < 16)
                return;
            const scale = Math.max(1, St.ThemeContext.get_for_stage(global.stage).scale_factor);
            if ((width + 8) * (height + 8) * scale * scale > 1048576) {
                this._settings.set_string('status', 'Area exceeds the safety limit');
                return;
            }
            const left = Math.max(0, Math.min(source.width - width, localX - width / 2));
            const top = Math.max(0, Math.min(source.height - height, localY - height / 2));
            const actor = new Clutter.Actor({
                width, height, x: source.x + left, y: source.y + top,
                reactive: false, clip_to_allocation: true,
            });
            actor.set_clip(0, 0, width, height);
            const clone = new Clutter.Clone({source, reactive: false, x: -left, y: -top});
            actor.add_child(clone);
            const effect = new RippleEffect(width, height, [localX - left, localY - top], this._values());
            actor.add_effect(effect);
            // Sibling of the source: cloning can never recurse into our own overlay.
            parent.insert_child_above(actor, source);
            const patch = {actor, effect, source, born: now(), sourceSignals: [], desktop};
            patch.sourceSignals.push(source.connect('destroy', () => this._removePatch(patch)));
            patch.sourceSignals.push(source.connect('notify::allocation', () => this._removePatch(patch)));
            this._patches.push(patch);
            while (this._patches.length > this._settings.get_int('max-waves'))
                this._removePatch(this._patches[0]);
            if (!this._timer) {
                this._previousFrame = now();
                const interval = Math.ceil(1000 / this._settings.get_int('fps'));
                this._timer = GLib.timeout_add(GLib.PRIORITY_DEFAULT, interval, () => this._tick());
            }
        } catch (error) {
            this._stop('Effect stopped. ' + error.message);
            console.error('Ripple:', error);
        }
    }

    _tick() {
        const time = now();
        const delay = time - this._previousFrame;
        this._previousFrame = time;
        if (!this._leaseValid() || !this._settings.get_boolean('active')) {
            this._timer = 0;
            this._clear();
            return GLib.SOURCE_REMOVE;
        }
        if (delay > 0.25) {
            this._timer = 0;
            this._stop('Effect stopped after a compositor delay');
            return GLib.SOURCE_REMOVE;
        }
        this._lagCount = delay > 0.09 ? this._lagCount + 1 : Math.max(0, this._lagCount - 1);
        if (this._lagCount >= 3) {
            this._timer = 0;
            this._stop('Effect stopped after repeated compositor delays');
            return GLib.SOURCE_REMOVE;
        }
        const lifetime = this._settings.get_double('lifetime');
        for (const patch of [...this._patches]) {
            const age = time - patch.born;
            if (age >= lifetime)
                this._removePatch(patch);
            else
                patch.effect.setAge(age);
        }
        if (!this._patches.length) {
            this._timer = 0;
            return GLib.SOURCE_REMOVE;
        }
        return GLib.SOURCE_CONTINUE;
    }

    _removePatch(patch) {
        const index = this._patches.indexOf(patch);
        if (index < 0)
            return;
        this._patches.splice(index, 1);
        for (const id of patch.sourceSignals)
            patch.source.disconnect(id);
        patch.actor.destroy();
    }

    _clear() {
        if (this._timer) {
            GLib.source_remove(this._timer);
            this._timer = 0;
        }
        for (const patch of [...this._patches])
            this._removePatch(patch);
        this._lagCount = 0;
    }

    _stop(message) {
        this._clear();
        this._settings.set_boolean('active', false);
        this._settings.set_string('status', message);
    }

    disable() {
        this._stopPointerPolling();
        this._clear();
        Main.wm.removeKeybinding('panic');
        if (this._leaseTimer)
            GLib.source_remove(this._leaseTimer);
        for (const [object, id] of this._signals)
            object.disconnect(id);
        this._signals = [];
        this._settings.set_boolean('active', false);
        this._settings = null;
    }
}
