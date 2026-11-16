// Subtle static CRT-style scanlines and edge shading for Ghostty.
// Ghostty supplies iChannel0 and iResolution and calls mainImage.
void mainImage(out vec4 fragColor, in vec2 fragCoord) {
    vec2 uv = fragCoord / iResolution.xy;
    vec4 source = texture(iChannel0, uv);

    // A four-pixel period keeps the effect visible without hiding the text.
    float scanline = 1.0 - 0.08 * (0.5 + 0.5 * cos(fragCoord.y * 1.5707963));
    vec2 centered = uv * 2.0 - 1.0;
    float vignette = 1.0 - 0.10 * clamp(dot(centered, centered) * 0.5, 0.0, 1.0);

    fragColor = vec4(source.rgb * scanline * vignette, source.a);
}
