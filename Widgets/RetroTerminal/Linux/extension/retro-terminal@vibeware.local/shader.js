// SPDX-License-Identifier: MIT
// Kept separate so the exact production GLSL can be rendered and timed in a harness.
export const DECLARATIONS = `
uniform sampler2D desktopTexture;
uniform vec2 crtSize;
uniform vec4 crtStyle; // preset, intensity, scanline strength, glow amount
uniform vec2 crtFinish; // vignette, static grain
uniform vec4 crtPhosphor; // custom RGB, use custom color
uniform vec4 crtRaster; // scanline spacing, width, mask mode (off/stripes/dots), strength
uniform vec4 crtOptics; // glow radius, black level, contrast, reserved
uniform vec4 crtGlitch; // type (off/RGB/tear/noise/band), progress, strength, seed
`;

export const CODE = `
vec2 uv = cogl_tex_coord_in[0].xy;
vec2 size = max(crtSize, vec2(1.0));
vec2 texel = 1.0 / size;
// Preserve original UVs for a true bypass even while a glitch is in progress.
vec4 original = texture2D(desktopTexture, uv);
vec2 sampleUV = uv;
float pulse = sin(clamp(crtGlitch.y, 0.0, 1.0) * 3.14159265359);
pulse *= pulse * crtGlitch.z;
float bandCenter = 0.12 + fract(crtGlitch.w * 7.31) * 0.76;
float localBand = 1.0 - smoothstep(0.012, 0.055, abs(uv.y - bandCenter));
if (crtGlitch.x > 1.5 && crtGlitch.x < 2.5) {
    float direction = fract(crtGlitch.w * 0.61803398875) < 0.5 ? -1.0 : 1.0;
    sampleUV.x += direction * localBand * pulse * 12.0 * texel.x;
}
sampleUV = clamp(sampleUV, vec2(0.0), vec2(1.0));
vec3 signal = original.rgb;
if (crtGlitch.x > 1.5 && crtGlitch.x < 2.5)
    signal = texture2D(desktopTexture, sampleUV).rgb;
float radius = max(0.0, crtOptics.x);
if (crtStyle.w > 0.0 && radius > 0.0) {
    vec3 neighbors = texture2D(desktopTexture, clamp(sampleUV + vec2(texel.x * radius, 0.0), vec2(0.0), vec2(1.0))).rgb
                   + texture2D(desktopTexture, clamp(sampleUV - vec2(texel.x * radius, 0.0), vec2(0.0), vec2(1.0))).rgb
                   + texture2D(desktopTexture, clamp(sampleUV + vec2(0.0, texel.y * radius), vec2(0.0), vec2(1.0))).rgb
                   + texture2D(desktopTexture, clamp(sampleUV - vec2(0.0, texel.y * radius), vec2(0.0), vec2(1.0))).rgb;
    signal += neighbors * 0.25 * crtStyle.w * (1.0 - signal);
}
signal = clamp((signal - 0.5) * crtOptics.z + 0.5 + crtOptics.y, 0.0, 1.0);
float luminance = dot(signal, vec3(0.2126, 0.7152, 0.0722));
vec3 color = signal;
if (crtStyle.x < 1.5 || crtPhosphor.w > 0.5) {
    vec3 phosphor = crtPhosphor.w > 0.5 ? crtPhosphor.rgb :
        (crtStyle.x < 0.5 ? vec3(0.20, 1.0, 0.42) : vec3(1.0, 0.63, 0.18));
    color = phosphor * pow(max(luminance, 0.0), 0.85);
}
// Discrete physical rows prevent a half-pixel phase from cancelling scanlines.
float pitch = max(2.0, crtRaster.x);
float width = clamp(crtRaster.y, 0.25, pitch - 0.25);
float row = mod(floor(uv.y * size.y), pitch) + 0.5;
float line = 1.0 - smoothstep(width - 0.5, width + 0.5, row);
color *= 1.0 - crtStyle.z * line;
if (crtRaster.z > 0.5) {
    float maskStrength = clamp(crtRaster.w, 0.0, 0.6);
    vec2 pixel = floor(uv * size);
    float stripe = mod(pixel.x, 3.0);
    vec3 mask = vec3(1.0);
    if (crtRaster.z < 1.5) {
        if (crtStyle.x > 1.5 && crtPhosphor.w < 0.5) {
            mask = vec3(stripe < 1.0 ? 1.0 : 1.0 - maskStrength,
                        stripe >= 1.0 && stripe < 2.0 ? 1.0 : 1.0 - maskStrength,
                        stripe >= 2.0 ? 1.0 : 1.0 - maskStrength);
        } else {
            mask *= stripe < 2.0 ? 1.0 : 1.0 - maskStrength;
        }
    } else {
        vec2 dotPosition = mod(pixel, vec2(3.0)) - vec2(1.0);
        float phosphorDot = 1.0 - smoothstep(0.7, 1.5, length(dotPosition));
        mask *= 1.0 - maskStrength * (1.0 - phosphorDot);
    }
    color *= mask;
}
if (crtGlitch.x > 0.5 && crtGlitch.x < 1.5) {
    // A local transient chromatic offset, also visible over monochrome phosphor.
    vec2 split = vec2(texel.x * 8.0 * pulse * localBand, 0.0);
    float redSignal = dot(texture2D(desktopTexture, clamp(sampleUV + split, vec2(0.0), vec2(1.0))).rgb, vec3(0.2126, 0.7152, 0.0722));
    float blueSignal = dot(texture2D(desktopTexture, clamp(sampleUV - split, vec2(0.0), vec2(1.0))).rgb, vec3(0.2126, 0.7152, 0.0722));
    float centerSignal = dot(original.rgb, vec3(0.2126, 0.7152, 0.0722));
    color.r += (redSignal - centerSignal) * 0.75;
    color.b += (blueSignal - centerSignal) * 0.75;
}
float noise = fract(sin(dot(floor(uv * size), vec2(12.9898, 78.233))) * 43758.5453) - 0.5;
if (crtGlitch.x > 2.5 && crtGlitch.x < 3.5) {
    float noisyRow = step(0.72, fract(sin(floor(uv.y * size.y) * 3.17 + crtGlitch.w * 53.0) * 43758.5453));
    color += vec3(noise * localBand * noisyRow * pulse * 0.35);
}
if (crtGlitch.x > 3.5) {
    float movingCenter = mix(-0.1, 1.1, crtGlitch.y);
    float softBand = 1.0 - smoothstep(0.015, 0.12, abs(uv.y - movingCenter));
    color *= 1.0 - softBand * pulse * 0.18;
}
vec2 edge = uv * 2.0 - 1.0;
color *= 1.0 - crtFinish.x * smoothstep(0.25, 1.5, dot(edge, edge));
color += noise * crtFinish.y * original.a;
cogl_color_out = vec4(mix(original.rgb, clamp(color, 0.0, original.a), crtStyle.y), original.a) * cogl_color_in;
`;
