// SPDX-License-Identifier: MIT
import GObject from 'gi://GObject';
import Shell from 'gi://Shell';
import Cogl from 'gi://Cogl';
import {DECLARATIONS, CODE} from './shader.js';

export const CrtEffect = GObject.registerClass({GTypeName: 'VibewareRetroTerminalEffect'},
class CrtEffect extends Shell.GLSLEffect {
    vfunc_build_pipeline() {
        this.add_glsl_snippet(Cogl.SnippetHook.FRAGMENT, DECLARATIONS, CODE, true);
    }

    _set(name, values) {
        this.set_uniform_float(this.get_uniform_location(name), values.length, values);
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
