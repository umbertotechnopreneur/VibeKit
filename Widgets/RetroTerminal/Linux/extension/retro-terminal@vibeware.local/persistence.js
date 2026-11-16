// SPDX-License-Identifier: MIT
import Clutter from 'gi://Clutter';
import Cogl from 'gi://Cogl';
import GLib from 'gi://GLib';
import Mtk from 'gi://Mtk';
import {MAX_PIXELS} from './guard.js';
import {planCapture} from './capture-plan.js';

const INTERVAL_MS = 42; // Under 24 capture/composition passes per second.
const now = () => GLib.get_monotonic_time() / 1e6;

// Real temporal persistence: each GPU target contains the current stage mixed
// with its preceding history. Two fixed textures alternate as source/destination.
// The history actor is a stage sibling of uiGroup, so the CRT color transform is
// not repeatedly applied to old frames. No capture is sent to CPU memory or disk.
export class PhosphorPersistence {
    constructor({onError = () => {}} = {}) {
        this._onError = onError;
        this._timer = 0;
        this._overlay = null;
        this._buffers = [];
        this._history = false;
        this._width = this._height = 0;
        this._enabled = false;
        this._strength = 0;
        this._duration = 0.15;
    }

    get running() { return this._timer !== 0; }
    get hasHistory() { return this._history; }
    get captureCount() { return this._captureCount ?? 0; }
    get captureInfo() { return this._captureInfo ?? null; }

    configure(settings, width, height) {
        const changedSize = width !== this._width || height !== this._height;
        if (changedSize && this.running) this.stop();
        this._width = width;
        this._height = height;
        this._duration = settings.get_double('persistence-duration');
        this._strength = settings.get_double('persistence-strength') * settings.get_double('intensity');
        this._enabled = settings.get_boolean('persistence-enabled') && this._strength > 0;
        if (!this._enabled) this.stop();
    }

    start() {
        if (this.running || !this._enabled) return;
        try {
            this._rect = new Mtk.Rectangle({x: 0, y: 0, width: this._width, height: this._height});
            const [ok, nativeWidth, nativeHeight, nativeScale] = global.stage.get_capture_final_size(this._rect);
            if (!ok || nativeWidth <= 0 || nativeHeight <= 0)
                throw Error('The desktop cannot be captured for afterglow');
            const plan = planCapture(this._width, this._height, nativeScale, MAX_PIXELS);
            const {width, height, scale} = plan;
            this._scale = scale;
            this._captureInfo = Object.freeze({...plan, nativeWidth, nativeHeight, nativeScale});
            const context = global.stage.get_context().get_backend().get_cogl_context();
            for (let i = 0; i < 2; i++) {
                const texture = Cogl.Texture2D.new_with_size(context, width, height);
                texture.set_auto_mipmap(false);
                const framebuffer = Cogl.Offscreen.new_with_texture(texture);
                const buffer = {texture, framebuffer, content: null};
                this._buffers.push(buffer);
                if (!framebuffer.allocate())
                    throw Error('Could not allocate the persistence framebuffer');
                buffer.content = Clutter.TextureContent.new_from_texture(texture, null);
            }
            this._overlay = new Clutter.Actor({name: 'retro-terminal-phosphor-history',
                reactive: false, x: 0, y: 0, width: this._width, height: this._height,
                opacity: 0, clip_to_allocation: true});
            global.stage.add_child(this._overlay);
            this._history = false;
            this._next = 0;
            this._captureCount = 0;
            this._previous = now();
            this._timer = GLib.timeout_add(GLib.PRIORITY_DEFAULT, INTERVAL_MS, () => {
                try {
                    this._capture();
                    return GLib.SOURCE_CONTINUE;
                } catch (error) {
                    this._timer = 0;
                    this.stop();
                    this._onError(error);
                    return GLib.SOURCE_REMOVE;
                }
            });
        } catch (error) {
            this.stop();
            throw error;
        }
    }

    _capture() {
        const time = now();
        const elapsed = time - this._previous;
        this._previous = time;
        if (elapsed > 0.25 || elapsed < 0)
            throw Error('Persistence stopped after a delayed frame');
        // Duration denotes the e-folding time of the stored history. Visible
        // strength is independent of decay: restore it before returning to GNOME.
        const decay = Math.exp(-elapsed / Math.max(0.04, this._duration));
        this._overlay.opacity = this._history ? Math.round(255 * decay) : 0;
        const target = this._buffers[this._next];
        try {
            global.stage.paint_to_framebuffer(target.framebuffer, this._rect, this._scale,
                global.stage.get_color_state(), Clutter.PaintFlag.NO_CURSORS | Clutter.PaintFlag.CLEAR);
        } finally {
            this._overlay.opacity = Math.round(255 * this._strength);
        }
        this._overlay.set_content(target.content);
        this._history = true;
        this._captureCount++;
        this._next = 1 - this._next;
    }

    stop() {
        if (this._timer) GLib.source_remove(this._timer);
        this._timer = 0;
        if (this._overlay) {
            this._overlay.set_content(null);
            this._overlay.destroy();
            this._overlay = null;
        }
        // Drop ownership normally. Cogl paint nodes/journals may still reference
        // these resources until the compositor finishes its pending commands.
        // Explicit run_dispose() can invalidate those native references and is
        // unsafe for Cogl objects. Only two history targets exist while active;
        // after Stop their final retirement follows GNOME/GJS native ownership.
        this._buffers = [];
        this._history = false;
        this._rect = null;
        this._captureInfo = null;
    }
}
