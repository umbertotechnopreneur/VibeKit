// UmbertoCiccobi.biz — Apple II / green monitor
// A stylized tribute, not a hardware-accurate emulator.
// CRT effect adapted from Hammster/windows-terminal-shaders, crt.hlsl.
// Source: https://github.com/Hammster/windows-terminal-shaders
// Local reference: revision 2f56929 plus local modifications.
// See licenses/ for CC0 and Microsoft Terminal MIT notices. Ghostty supplies
// iChannel0, iResolution and iTime. UI scale is expressed in physical pixels.
// Edit these options, or use scripts/ghostty-shaders.ps1. 1 = on, 0 = off.
#define ENABLE_CURVE 0
#define ENABLE_OVERSCAN 0
#define ENABLE_BLOOM 0
#define ENABLE_BLUR 0
#define ENABLE_GRAYSCALE 0
#define ENABLE_BLACKLEVEL 0
#define ENABLE_REFRESHLINE 1
#define ENABLE_SCANLINES 1
#define ENABLE_TINT 0
#define ENABLE_GRAIN 1
#define CURVE_INTENSITY 0.3
#define OVERSCAN_PERCENTAGE 0.02
#define BLOOM_OFFSET 0.0015
#define BLOOM_STRENGTH 0.8
#define BLUR_MULTIPLIER 1.05
#define BLUR_STRENGTH 0.2
#define BLUR_OFFSET 0.003
// Grayscale modes: 0 intensity, 1 gleam, 2 luminance, 3 luma.
#define GRAYSCALE_MODE 2
#define BLACKLEVEL 0.05
// Tint RGB: Apple IIc green by default; enable grayscale and tint for phosphor.
#define TINT_R 0.25
#define TINT_G 1.0
#define TINT_B 0.28
#define GRAIN_INTENSITY 0.004
#define SCANLINE_STRENGTH 0.16
#define SCANLINE_PIXEL_SIZE 2.0
// Map source luminance to a retro two-color display (0 = original colors).
#define PALETTE_MIX 1.0
#define PAPER_R 0.005
#define PAPER_G 0.025
#define PAPER_B 0.008
// Simulated glow; not temporal phosphor persistence.
#define GLOW_STRENGTH 0.3

float permuteCrt(float x) { return 289.0 * fract((x * (34.0 * x + 1.0)) / 289.0); }
vec3 sampleCrt(vec2 uv) { return texture(iChannel0, clamp(uv, vec2(0.0), vec2(1.0))).rgb; }

void mainImage(out vec4 fragColor, in vec2 fragCoord) {
    vec2 uv = fragCoord / iResolution.xy;
#if ENABLE_CURVE
    uv -= 0.5;
    uv *= (4.2 + dot(uv, uv) * CURVE_INTENSITY) * 0.25;
    uv += 0.5;
#endif
    vec2 screenUv = uv;
#if ENABLE_OVERSCAN
    uv = (uv - 0.5) / (1.0 - OVERSCAN_PERCENTAGE) + 0.5;
#endif
    // Out-of-screen pixels use opaque black instead of Windows' Background uniform.
    if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0)))) {
        fragColor = vec4(0.0, 0.0, 0.0, 1.0);
        return;
    }
    vec4 color = texture(iChannel0, uv);
    float luminance = dot(color.rgb, vec3(0.2126, 0.7152, 0.0722));
    vec3 glow = (sampleCrt(uv + vec2(1.5 / iResolution.x, 0.0))
               + sampleCrt(uv - vec2(1.5 / iResolution.x, 0.0))
               + sampleCrt(uv + vec2(0.0, 1.5 / iResolution.y))
               + sampleCrt(uv - vec2(0.0, 1.5 / iResolution.y))) * 0.25;
    luminance = clamp(luminance + dot(glow, vec3(0.2126, 0.7152, 0.0722)) * GLOW_STRENGTH, 0.0, 1.0);
    color.rgb = mix(color.rgb, mix(vec3(PAPER_R, PAPER_G, PAPER_B), vec3(TINT_R, TINT_G, TINT_B), luminance), PALETTE_MIX);
#if ENABLE_BLOOM
    color.rgb = clamp(color.rgb + (color.rgb - sampleCrt(uv + vec2(-BLOOM_OFFSET, 0.0))) * BLOOM_STRENGTH, 0.0, 1.0);
#endif
#if ENABLE_BLUR
    const float weights[9] = float[9](0.0, 0.092, 0.081, 0.071, 0.061, 0.051, 0.041, 0.031, 0.021);
    vec3 sum = sampleCrt(uv) * 0.204;
    for (int i = 1; i < 9; i++) {
        float d = float(i) * BLUR_OFFSET;
        sum += (sampleCrt(uv + vec2(d, 0.0)) + sampleCrt(uv - vec2(d, 0.0))
              + sampleCrt(uv + vec2(0.0, d)) + sampleCrt(uv - vec2(0.0, d))) * weights[i];
    }
    vec3 blurred = sum * 0.5 * BLUR_MULTIPLIER;
    color.rgb = clamp(color.rgb + (blurred - color.rgb) * BLUR_STRENGTH, 0.0, 1.0);
#endif
#if ENABLE_GRAYSCALE
    float gray;
#if GRAYSCALE_MODE == 0
    gray = dot(color.rgb, vec3(1.0 / 3.0));
#elif GRAYSCALE_MODE == 1 && !ENABLE_BLOOM
    gray = pow(dot(pow(max(color.rgb, 0.0), vec3(1.0 / 2.2)), vec3(1.0 / 3.0)), 2.2);
#elif GRAYSCALE_MODE == 3 && !ENABLE_BLOOM
    gray = pow(dot(pow(max(color.rgb, 0.0), vec3(1.0 / 2.2)), vec3(0.2126, 0.7152, 0.0722)), 2.2);
#else
    gray = dot(color.rgb, vec3(0.2989, 0.5866, 0.1145));
#endif
    color.rgb = vec3(clamp(gray, 0.0, 1.0));
#endif
#if ENABLE_BLACKLEVEL
    color.rgb = max(color.rgb, vec3(BLACKLEVEL));
#endif
#if ENABLE_REFRESHLINE
    // Ghostty's coordinates have their origin at the bottom left.
    float line = mod(iTime / 5.0, 1.5) - 0.5;
    float y = 1.0 - screenUv.y;
    if (y > line && y - 0.03 < line) color.rgb += vec3((line - y) * 2.0);
#endif
#if ENABLE_SCANLINES
    color.rgb *= 1.0 - mod(floor(fragCoord.y / SCANLINE_PIXEL_SIZE), 2.0) * SCANLINE_STRENGTH;
#endif
#if ENABLE_TINT
    color.rgb *= vec3(TINT_R, TINT_G, TINT_B);
#endif
#if ENABLE_GRAIN
    vec3 m = vec3(screenUv, mod(iTime, 5.0) / 5.0) + 1.0;
    float state = permuteCrt(permuteCrt(m.x) + m.y) + m.z;
    float q = 0.95 * fract(permuteCrt(state) / 41.0) + 0.025 - 0.5;
    float r2 = q * q;
    float grain = q * (1.365020122861334 + (-0.5303572634357367 * r2 + 0.151015505647689) / (r2 * r2 - 0.7607324991323768 * r2 + 0.132089632343748));
    color.rgb += GRAIN_INTENSITY * grain;
#endif
    fragColor = vec4(clamp(color.rgb, 0.0, 1.0), color.a);
}
