// SPDX-License-Identifier: MIT
import Clutter from 'gi://Clutter';
import Cogl from 'gi://Cogl';
import GLib from 'gi://GLib';
import Mtk from 'gi://Mtk';
import {MAX_PIXELS} from './guard.js';
import {planCapture} from './capture-plan.js';
import {paintHistory} from './shared/capture-pass.js';

// Preserve the existing compositor workload ceiling when more displays are selected.
const MAX_CAPTURE_FPS = 24;
const now = () => GLib.get_monotonic_time() / 1e6;

// The history actors stay above uiGroup, so the CRT color pass is applied once.
// Each selected monitor has two fixed textures, alternating as source/destination.
export class PhosphorPersistence {
    constructor({onError, onDelay, onOverlayAdded}) {
        this._onError = onError;
        this._onDelay = onDelay;
        this._onOverlayAdded = onOverlayAdded;
        this._timer = 0;
        this._slots = [];
        this._selection = '';
        this._captureCount = 0;
    }

    get running() { return this._timer !== 0; }
    get hasHistory() { return this._slots.some(slot => slot.history); }
    get captureCount() { return this._captureCount; }
    get captureInfo() { return this._slots.map(slot => slot.info); }

    configure(settings, monitors) {
        const selection = JSON.stringify(monitors.map(({id, x, y, width, height, scale}) => [id, x, y, width, height, scale]));
        const fps = Math.min(MAX_CAPTURE_FPS, settings.get_int('capture-fps'), settings.get_int('animation-fps'));
        const changedSelection = selection !== this._selection;
        const changedRate = fps !== this._fps;
        if (changedSelection) this.stop();
        this._selection = selection;
        this._monitors = monitors;
        this._fps = fps;
        this._duration = settings.get_double('persistence-duration');
        this._strength = settings.get_double('persistence-strength') * settings.get_double('intensity');
        this._enabled = settings.get_boolean('persistence-enabled') && this._strength > 0;
        if (!this._enabled) this.stop();
        else if (changedRate && this.running) this._startTimer();
    }

    start() {
        if (this.running || !this._enabled || !this._monitors.length) return;
        try {
            const crops = this._monitors.map(monitor => {
                const {x, y, width, height} = monitor;
                const rect = new Mtk.Rectangle({x, y, width, height});
                const [ok, nativeWidth, nativeHeight, nativeScale] = global.stage.get_capture_final_size(rect);
                if (!ok || nativeWidth <= 0 || nativeHeight <= 0 || !Number.isFinite(nativeScale) || nativeScale <= 0)
                    throw Error(`Cannot capture ${monitor.name} for afterglow`);
                return {monitor, rect, nativeWidth, nativeHeight, nativeScale};
            });
            const pixels = crops.reduce((total, crop) => total + crop.nativeWidth * crop.nativeHeight, 0);
            const context = global.stage.get_context().get_backend().get_cogl_context();
            for (const {monitor, rect, nativeWidth, nativeHeight, nativeScale} of crops) {
                const budget = Math.floor(MAX_PIXELS * nativeWidth * nativeHeight / pixels);
                const plan = planCapture(monitor.width, monitor.height, nativeScale, budget);
                const slot = {rect, info: {...plan, nativeWidth, nativeHeight, nativeScale}, buffers: [],
                    overlay: null, history: false, previous: now(), next: 0};
                this._slots.push(slot);
                for (let i = 0; i < 2; i++) {
                    const texture = Cogl.Texture2D.new_with_size(context, plan.width, plan.height);
                    texture.set_auto_mipmap(false);
                    const framebuffer = Cogl.Offscreen.new_with_texture(texture);
                    if (!framebuffer.allocate()) throw Error('Could not allocate the persistence framebuffer');
                    slot.buffers.push({texture, framebuffer, content: Clutter.TextureContent.new_from_texture(texture, null)});
                }
                const {x, y, width, height} = monitor;
                slot.overlay = new Clutter.Actor({name: 'retro-terminal-phosphor-history',
                    reactive: false, x, y, width, height, opacity: 0, clip_to_allocation: true});
                global.stage.add_child(slot.overlay);
            }
            this._onOverlayAdded();
            this._captureCount = 0;
            this._nextSlot = 0;
            this._startTimer();
        } catch (error) {
            this.stop();
            throw error;
        }
    }

    _startTimer() {
        if (this._timer) GLib.source_remove(this._timer);
        let previous = now();
        this._timer = GLib.timeout_add(GLib.PRIORITY_DEFAULT, Math.ceil(1000 / this._fps), () => {
            try {
                const time = now();
                const elapsed = time - previous;
                previous = time;
                if (!Number.isFinite(elapsed) || elapsed < 0) throw Error('The capture clock became invalid');
                if (elapsed > Math.max(0.25, 2 / this._fps)) {
                    this._timer = 0;
                    this._onDelay(Math.round(elapsed * 1000));
                    return GLib.SOURCE_REMOVE;
                }
                this._capture(this._slots[this._nextSlot], time);
                this._nextSlot = (this._nextSlot + 1) % this._slots.length;
                return GLib.SOURCE_CONTINUE;
            } catch (error) {
                this._timer = 0;
                this.stop();
                this._onError(error);
                return GLib.SOURCE_REMOVE;
            }
        });
    }

    _capture(slot, time) {
        const elapsed = time - slot.previous;
        slot.previous = time;
        const decay = Math.exp(-elapsed / Math.max(0.04, this._duration));
        slot.overlay.opacity = slot.history ? Math.round(255 * decay) : 0;
        const target = slot.buffers[slot.next];
        try {
            const stage = global.stage;
            paintHistory(stage, () => stage.paint_to_framebuffer(target.framebuffer, slot.rect, slot.info.scale,
                stage.get_color_state(), Clutter.PaintFlag.NO_CURSORS | Clutter.PaintFlag.CLEAR));
        } finally {
            slot.overlay.opacity = Math.round(255 * this._strength);
        }
        slot.overlay.set_content(target.content);
        slot.history = true;
        this._captureCount++;
        slot.next = 1 - slot.next;
    }

    stop() {
        if (this._timer) GLib.source_remove(this._timer);
        this._timer = 0;
        for (const {overlay} of this._slots) {
            overlay?.set_content(null);
            overlay?.destroy();
        }
        // Let Cogl retire textures after any pending paint nodes finish.
        this._slots = [];
    }
}
