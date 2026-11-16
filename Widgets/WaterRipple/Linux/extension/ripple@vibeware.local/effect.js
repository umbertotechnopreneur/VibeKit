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
uniform vec4 rippleFilters; // chromatic separation, blend, crest, secondary waves
uniform float rippleLiquid;
uniform float rippleGlass;
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
if (rippleGlass > 0.5) {
    // Broad, softly irregular rings. The distortion and highlights both derive
    // from the slope of the same virtual surface, rather than a flat ring.
    vec2 direction = vec2(ellipse.x, ellipse.y / aspect) / max(r, 0.001);
    float angle = atan(ellipse.y, ellipse.x + 0.00001);
    float spacing = clamp(rippleShape.z, 55.0, 110.0);
    float warped = r + 4.0 * sin(angle * 3.0 + rippleAge * 0.7)
                     + 2.0 * sin(angle * 5.0 - rippleAge * 0.5);
    float glassQ = warped - rippleFade.w - rippleAge * rippleShape.y;
    float glassSigma = spacing * max(2.4, rippleShape.w) / 3.0;
    float glassEnvelope = exp(-glassQ * glassQ / (2.0 * glassSigma * glassSigma));
    float glassPhase = glassQ * 6.28318530718 / spacing;
    float slope = sin(glassPhase) * glassEnvelope * fade * border;
    float strength = rippleShape.x * 1.7;
    vec2 shift = direction * slope * strength / rippleSize;
    shift *= mix(1.0, -1.0, rippleExtra.y);
    vec2 sampleUV = clamp(uv + shift, vec2(0.001), vec2(0.999));
    vec4 glassColor = texture2D(rippleTexture, sampleUV);
    float red = texture2D(rippleTexture, clamp(sampleUV + shift * 0.12, vec2(0.001), vec2(0.999))).r;
    float blue = texture2D(rippleTexture, clamp(sampleUV - shift * 0.12, vec2(0.001), vec2(0.999))).b;
    glassColor.rgb = vec3(red, glassColor.g, blue);
    float arc = pow(max(0.0, dot(direction, normalize(vec2(-0.6, -0.8)))), 3.0);
    float arc2 = pow(max(0.0, dot(direction, normalize(vec2(0.7, 0.7)))), 6.0);
    float rim = pow(max(cos(glassPhase), 0.0), 12.0) * glassEnvelope * fade * border;
    vec3 shine = vec3(0.72, 0.87, 1.0) * arc + vec3(0.55, 0.35, 0.8) * arc2 * 0.65;
    glassColor.rgb += (1.0 - glassColor.rgb) * shine * rim * 0.8;
    cogl_color_out = glassColor * cogl_color_in;
} else {
float phase = q * 6.28318530718 / rippleShape.z;
float shape = sin(phase) + rippleFilters.w * 0.35 * sin(phase * 3.0)
    + rippleLiquid * 0.65 * clamp(q / max(sigma * 3.0, 1.0), -1.0, 1.0);
float amount = rippleShape.x * fade * shape * envelope * border;
amount *= mix(1.0,-1.0,rippleExtra.y);
vec2 normal = vec2(ellipse.x,ellipse.y/aspect)/max(r,0.001);
vec2 uvShift = normal*amount/rippleSize;
vec2 displaced = clamp(uv+uvShift,vec2(0.001),vec2(0.999));
vec4 color = texture2D(rippleTexture,displaced);
if (rippleFilters.x > 0.0 && rippleFilters.y > 0.0) {
    float red = texture2D(rippleTexture,clamp(displaced+uvShift*rippleFilters.x,vec2(0.001),vec2(0.999))).r;
    float blue = texture2D(rippleTexture,clamp(displaced-uvShift*rippleFilters.x,vec2(0.001),vec2(0.999))).b;
    color.rgb = vec3(mix(color.r,red,rippleFilters.y),color.g,mix(color.b,blue,rippleFilters.y));
}
float crest = fade * fade * envelope * pow(max(cos(phase), 0.0), 4.0) * border;
color.rgb += (1.0 - color.rgb) * clamp(crest * rippleFilters.z * 0.5, 0.0, 1.0);
float light = 1.0+rippleFade.z*fade*cos(phase)*envelope*border;
cogl_color_out = vec4(color.rgb*light,color.a) * cogl_color_in;
}
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
        this._set('rippleFilters', [
            p['chromatic-enabled'] ? p['chromatic-separation'] / 100 : 0,
            p['chromatic-blend'] / 100,
            p['crest-enabled'] ? p['crest-intensity'] / 100 : 0,
            p['ripples-enabled'] ? p['ripples-intensity'] / 100 : 0,
        ]);
        this._set('rippleLiquid', [p['liquid-enabled'] ? p['liquid-intensity'] / 100 : 0]);
        this._set('rippleGlass', [p['liquid-glass-experimental'] ? 1 : 0]);
    }

    setAge(age) {
        this.set_uniform_float(this._ageLocation, 1, [age]);
        this.queue_repaint();
    }
});
