// SPDX-License-Identifier: MIT
import GObject from 'gi://GObject';
import Clutter from 'gi://Clutter';
import Shell from 'gi://Shell';
import Cogl from 'gi://Cogl';
import {DECLARATIONS, CODE} from './shader.js';

export const MAX_MONITOR_REGIONS = 32;

export const CrtEffect = GObject.registerClass({GTypeName: 'VibewareRetroTerminalEffect'},
class CrtEffect extends Shell.GLSLEffect {
    vfunc_paint(node, context, flags) {
        // Mutter can cull windows to this paint's damage region before updating
        // the offscreen texture. A different view or capture needs its own paint.
        const framebuffer = context.get_framebuffer();
        const clip = context.get_redraw_clip();
        const sameClip = clip ? this._paintClip?.equal(clip) : !this._paintClip;
        if (framebuffer !== this._paintFramebuffer?.deref() || !sameClip) {
            this._paintFramebuffer = new WeakRef(framebuffer);
            this._paintClip = clip?.copy() ?? null;
            flags |= Clutter.EffectPaintFlags.ACTOR_DIRTY;
        }
        super.vfunc_paint(node, context, flags);
    }

    vfunc_build_pipeline() {
        // Use stage projection, not the effect texture UVs. Clutter adds padding
        // to that texture and individual stage views may have different scales.
        this.add_glsl_snippet(Cogl.SnippetHook.VERTEX, 'varying vec2 crtStagePosition;', `
            vec4 projected = cogl_modelview_projection_matrix * cogl_position_in;
            crtStagePosition = vec2(0.5, 0.5) + vec2(0.5, -0.5) * projected.xy / projected.w;
        `, false);
        this.add_glsl_snippet(Cogl.SnippetHook.FRAGMENT, `${DECLARATIONS}
            varying vec2 crtStagePosition;
            uniform float crtMonitorCount;
            uniform vec4 crtMonitors[${MAX_MONITOR_REGIONS}];
        `, `
            bool selected = false;
            for (int index = 0; index < ${MAX_MONITOR_REGIONS}; index++) {
                if (float(index) >= crtMonitorCount) break;
                vec4 bounds = crtMonitors[index];
                if (all(greaterThanEqual(crtStagePosition, bounds.xy)) && all(lessThan(crtStagePosition, bounds.zw))) {
                    selected = true;
                    break;
                }
            }
            if (!selected) {
                cogl_color_out = texture2D(desktopTexture, cogl_tex_coord_in[0].xy) * cogl_color_in;
                return;
            }
            ${CODE}
        `, true);
    }

    _set(name, values) {
        this.set_uniform_float(this.get_uniform_location(name), values.length, values);
    }

    setMonitors(monitors, width, height) {
        const regions = monitors.flatMap(monitor => [monitor.x / width, monitor.y / height,
            (monitor.x + monitor.width) / width, (monitor.y + monitor.height) / height]);
        this._set('crtMonitorCount', [monitors.length]);
        this.set_uniform_float(this.get_uniform_location('crtMonitors[0]'), 4, regions);
        this.get_actor()?.queue_redraw();
    }

    configure(settings, width, height) {
        this._set('crtSize', [width, height]);
        this._set('crtStyle', [settings.get_int('preset'), settings.get_double('intensity'),
            settings.get_double('scanlines'), settings.get_double('glow')]);
        this._set('crtFinish', [settings.get_double('vignette'), settings.get_double('grain')]);
        const hex = settings.get_string('phosphor-color');
        const rgb = /^#[0-9a-f]{6}$/i.test(hex)
            ? [1, 3, 5].map(index => parseInt(hex.slice(index, index + 2), 16) / 255)
            : [0.2, 1.0, 0.42];
        this._set('crtPhosphor', [...rgb, settings.get_boolean('custom-color') ? 1 : 0]);
        this._set('crtRaster', [settings.get_double('scanline-spacing'), settings.get_double('scanline-width'),
            settings.get_int('mask'), settings.get_double('mask-strength')]);
        this._set('crtOptics', [settings.get_double('glow-radius'), settings.get_double('black-level'),
            settings.get_double('contrast'), 0]);
        // Invalidate the scene cache so a settings change also reaches static windows
        // outside the latest damage region of the compositor.
        this.get_actor()?.queue_redraw();
    }

    setGlitch(type = 0, progress = 0, intensity = 0, seed = 0) {
        const values = [type, progress, intensity, seed];
        if (values.every((value, index) => value === (this._glitch?.[index] ?? 0))) return;
        this._glitch = values;
        this._set('crtGlitch', values);
        this.queue_repaint();
    }
});
