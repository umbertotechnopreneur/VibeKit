// SPDX-License-Identifier: MIT
import Clutter from 'gi://Clutter';
import GLib from 'gi://GLib';
import {getShellMonitors} from './monitor-selection.js';
import {shellPressure} from './shell-pressure.js';

// Each extension is packaged independently. The stage is the common owner.
const sharedKey = Symbol.for('biz.umbertogiacobbi.VibeWare.shellEffects.v1');

class ShellEffects {
    constructor(stage, layoutManager) {
        this.stage = stage;
        this.layoutManager = layoutManager;
        this.clients = new Set();
        this.frames = new Set();
        this.pressure = new Map();
        this.memorySource = 0;
        this.timeline = null;
        this.monitors = null;
        this.monitorSignal = layoutManager.connect('monitors-changed', () => {
            this.monitors = null;
            for (const client of [...this.clients]) {
                if (!this.clients.has(client)) continue;
                try { client(); }
                catch (error) { console.error('VibeWare display layout:', error); }
            }
        });
    }

    displays() {
        if (this.monitors === null)
            this.monitors = getShellMonitors(this.layoutManager.monitors, global.display);
        return this.monitors;
    }

    raisePostEffect() {
        const actor = this.postEffect;
        if (actor?.get_parent() === this.stage && this.stage.get_last_child() !== actor)
            this.stage.set_child_above_sibling(actor, null);
    }

    subscribeFrames(callback) {
        this.frames.add(callback);
        if (!this.timeline) {
            // Binding to the stage keeps the clock stable as waves migrate
            // between views. Timestamp and update ordering are shared.
            this.timeline = Clutter.Timeline.new_for_actor(this.stage, 1000);
            this.timeline.set_repeat_count(-1);
            this.frameSignal = this.timeline.connect('new-frame', () => {
                const time = GLib.get_monotonic_time() / 1e6;
                for (const frame of [...this.frames]) {
                    if (!this.frames.has(frame)) continue;
                    try { frame(time); }
                    catch (error) { console.error('VibeWare animation:', error); }
                }
            });
            this.timeline.start();
        }
        return () => {
            if (!this.frames.delete(callback) || this.frames.size) return;
            this.timeline.stop();
            this.timeline.disconnect(this.frameSignal);
            this.timeline.set_actor(null);
            this.timeline = null;
            this.frameSignal = 0;
        };
    }

    subscribePressure(stop) {
        this.pressure.set(stop, false);
        if (!this.memorySource) {
            this.memorySource = GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, 3, () => {
                const sample = shellPressure();
                for (const [callback, previouslyLow] of [...this.pressure]) {
                    if (!this.pressure.has(callback)) continue;
                    this.pressure.set(callback, Boolean(sample));
                    if (sample && previouslyLow) {
                        try { callback({atUtc: new Date().toISOString(), ...sample}); }
                        catch (error) { console.error('VibeWare memory pressure:', error); }
                    }
                }
                return this.memorySource ? GLib.SOURCE_CONTINUE : GLib.SOURCE_REMOVE;
            });
        }
        return () => {
            if (!this.pressure.delete(stop) || this.pressure.size) return;
            GLib.source_remove(this.memorySource);
            this.memorySource = 0;
        };
    }
}

export function acquireShellEffects(stage, layoutManager, onMonitorsChanged) {
    const shared = stage[sharedKey] ??= new ShellEffects(stage, layoutManager);
    const subscriptions = new Set();
    shared.clients.add(onMonitorsChanged);
    function track(unsubscribe) {
        subscriptions.add(unsubscribe);
        return () => { if (subscriptions.delete(unsubscribe)) unsubscribe(); };
    }
    return {
        displays: () => shared.displays(),
        subscribeFrames: callback => track(shared.subscribeFrames(callback)),
        subscribePressure: callback => track(shared.subscribePressure(callback)),
        registerPostEffect(actor) {
            shared.postEffect = actor;
            shared.raisePostEffect();
            return track(() => { if (shared.postEffect === actor) shared.postEffect = null; });
        },
        raisePostEffect: () => shared.raisePostEffect(),
        release() {
            for (const unsubscribe of subscriptions) unsubscribe();
            subscriptions.clear();
            if (!shared.clients.delete(onMonitorsChanged) || shared.clients.size) return;
            layoutManager.disconnect(shared.monitorSignal);
            delete stage[sharedKey];
        },
    };
}
