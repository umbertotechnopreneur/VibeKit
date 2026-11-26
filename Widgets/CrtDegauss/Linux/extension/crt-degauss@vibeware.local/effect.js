// SPDX-License-Identifier: MIT
import Clutter from 'gi://Clutter';
import Cogl from 'gi://Cogl';
import GObject from 'gi://GObject';
import Shell from 'gi://Shell';

export const DegaussEffect = GObject.registerClass({GTypeName: 'VibewareDegaussEffect'},
class extends Shell.GLSLEffect {
    vfunc_paint(node, context, flags) {
        const framebuffer = context.get_framebuffer();
        const clip = context.get_redraw_clip();
        const sameClip = clip ? this._clip?.equal(clip) : !this._clip;
        if (framebuffer !== this._framebuffer?.deref() || !sameClip) {
            this._framebuffer = new WeakRef(framebuffer);
            this._clip = clip?.copy() ?? null;
            flags |= Clutter.EffectPaintFlags.ACTOR_DIRTY;
        }
        super.vfunc_paint(node, context, flags);
    }

    vfunc_build_pipeline() {
        this.add_glsl_snippet(Cogl.SnippetHook.VERTEX, 'varying vec2 screenPosition;', `
            vec4 projected = cogl_modelview_projection_matrix * cogl_position_in;
            screenPosition = vec2(0.5) + vec2(0.5, -0.5) * projected.xy / projected.w;
        `, false);
        this.add_glsl_snippet(Cogl.SnippetHook.FRAGMENT, `
            varying vec2 screenPosition;
            uniform sampler2D desktopTexture;
            uniform vec4 displayBounds;
            uniform vec2 displayAspect;
            uniform vec3 effectFrame;
        `, `
            vec2 uv = cogl_tex_coord_in[0].xy;
            vec2 local = (screenPosition - displayBounds.xy) / displayBounds.zw;
            if (any(lessThan(local, vec2(0.0))) || any(greaterThanEqual(local, vec2(1.0)))) {
                cogl_color_out = texture2D(desktopTexture, uv) * cogl_color_in;
                return;
            }

            vec2 textureScale = displayBounds.zw;
            float phase = effectFrame.z;
            vec4 pixel;
            if (phase >= 0.0) {
                float envelope = smoothstep(0.0, 0.035, phase) * (1.0 - phase) * exp(-3.0 * phase);
                float swing = envelope * sin(phase * 72.0);
                float angle = 0.065 * swing;
                float aspect = displayAspect.x / displayAspect.y;
                vec2 point = (local - 0.5) * vec2(aspect, 1.0);
                vec2 rotated = vec2(cos(angle) * point.x - sin(angle) * point.y,
                    sin(angle) * point.x + cos(angle) * point.y) / vec2(aspect, 1.0) + 0.5;
                rotated.y += swing * (0.058 + 0.023 * sin(local.x * 6.2831853));
                vec2 split = vec2(0.007, 0.002) * swing;
                vec2 minimum = 0.5 / displayAspect;
                vec2 maximum = 1.0 - minimum;
                pixel = texture2D(desktopTexture, uv + (clamp(rotated, minimum, maximum) - local) * textureScale);
                pixel.r = texture2D(desktopTexture, uv + (clamp(rotated + split, minimum, maximum) - local) * textureScale).r;
                pixel.b = texture2D(desktopTexture, uv + (clamp(rotated - split, minimum, maximum) - local) * textureScale).b;
            } else {
                pixel = texture2D(desktopTexture, uv);
            }

            vec2 first = (local - vec2(0.045, 0.10)) / vec2(0.30, 0.39);
            vec2 second = (local - vec2(0.97, 0.94)) / vec2(0.29, 0.42);
            float magenta = (1.0 - smoothstep(0.03, 1.0, dot(first, first))) * effectFrame.x;
            float green = (1.0 - smoothstep(0.03, 1.0, dot(second, second))) * effectFrame.x * effectFrame.y;
            vec3 color = pixel.rgb * (1.0 - 0.20 * (magenta + green)) +
                pixel.gbr * vec3(0.30, 0.025, 0.17) * magenta +
                pixel.brg * vec3(0.04, 0.35, 0.12) * green;
            cogl_color_out = vec4(min(color, vec3(pixel.a)), pixel.a) * cogl_color_in;
        `, true);
    }

    setDisplay(monitor, stageWidth, stageHeight) {
        this._set('displayBounds', [monitor.x / stageWidth, monitor.y / stageHeight,
            monitor.width / stageWidth, monitor.height / stageHeight]);
        this._set('displayAspect', [monitor.width, monitor.height]);
    }

    setFrame(amount, patches, pulse) {
        this._set('effectFrame', [amount, patches === 2 ? 1 : 0, pulse]);
        // The changed transform can sample beyond the compositor's last damage region.
        this.get_actor()?.queue_redraw();
    }

    _set(name, values) {
        this.set_uniform_float(this.get_uniform_location(name), values.length, values);
    }
});
