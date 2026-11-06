// SPDX-License-Identifier: GPL-3.0-or-later
import GObject from 'gi://GObject';
import Shell from 'gi://Shell';
import Cogl from 'gi://Cogl';

const DECLARATIONS = `
uniform sampler2D rippleTexture;
uniform vec2 rippleSize;
uniform vec2 rippleCenter;
uniform vec4 rippleShape; // strength, speed, wavelength, number of rings
uniform vec4 rippleFade;  // lifetime, damping, light, initial radius
uniform vec2 rippleExtra; // aspect, inverted
uniform float rippleAge;
`;

const CODE = `
vec2 uv = cogl_tex_coord_in[0].xy;
vec2 p = uv * rippleSize;
vec2 delta = p - rippleCenter;
float aspect = rippleExtra.x;
vec2 ellipse = vec2(delta.x, delta.y / aspect);
float r = length(ellipse);
float sigma = max(2.0, rippleShape.z * rippleShape.w / 6.0);
float q = r - rippleFade.w - rippleAge * rippleShape.y;
float envelope = exp(-q*q/(2.0*sigma*sigma));
float fade = pow(max(0.0,1.0-rippleAge/rippleFade.x),rippleFade.y)
           * min(1.0,rippleAge/0.09);
// Taper displacement to zero at the crop edges, so adjacent original pixels match.
float edge = min(min(p.x,p.y),min(rippleSize.x-p.x,rippleSize.y-p.y));
float border = smoothstep(4.0,40.0,edge);
float phase = q * 6.28318530718 / rippleShape.z;
float amount = rippleShape.x * fade * sin(phase) * envelope * border;
amount *= mix(1.0,-1.0,rippleExtra.y);
vec2 normal = vec2(ellipse.x,ellipse.y/aspect)/max(r,0.001);
vec2 displaced = clamp(uv+normal*amount/rippleSize,vec2(0.001),vec2(0.999));
vec4 color = texture2D(rippleTexture,displaced);
float light = 1.0+rippleFade.z*fade*cos(phase)*envelope*border;
cogl_color_out = vec4(color.rgb*light,color.a) * cogl_color_in;
`;

export const RippleEffect = GObject.registerClass({GTypeName: 'VibewareRippleEffect'},
class RippleEffect extends Shell.GLSLEffect {
    vfunc_build_pipeline() {
        this.add_glsl_snippet(Cogl.SnippetHook.FRAGMENT, DECLARATIONS, CODE, true);
    }

    _init(width, height, center, values) {
        super._init();
        this._ageLocation = this.get_uniform_location('rippleAge');
        this._set('rippleSize', [width, height]);
        this._set('rippleCenter', center);
        this.configure(values);
        this.setAge(0);
        // Samplers default to texture unit 0, matching the OffscreenEffect input.
    }

    _set(name, values) {
        this.set_uniform_float(this.get_uniform_location(name), values.length, values);
    }

    configure(p) {
        this._set('rippleShape', [p.strength, p.speed, p.wavelength, p.rings]);
        this._set('rippleFade', [p.lifetime, p.damping, p.light / 100, p.radius]);
        this._set('rippleExtra', [p.aspect, p.invert ? 1 : 0]);
    }

    setAge(age) {
        this.set_uniform_float(this._ageLocation, 1, [age]);
        this.queue_repaint();
    }
});
