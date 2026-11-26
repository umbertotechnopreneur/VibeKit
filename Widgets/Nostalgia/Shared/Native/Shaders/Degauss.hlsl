// SPDX-License-Identifier: MIT
Texture2D Desktop : register(t0);
SamplerState LinearClamp : register(s0);

cbuffer Parameters : register(b0)
{
    float4 Surface;
    float4 Phosphor;
    float4 Scan;
    float4 Tone;
    float4 Finish;
    float4 Glitch;
    float4 Target;
    float4 Effect; // strength, patch preset (1–6), pulse progress (-1 when idle), cycle seed
    float4 Vignette; // intensity, radius, softness, aspect
    float4 VignetteColor; // straight RGB, enabled
};

float patchRandom(float n) { return frac(sin(n * 12.9898) * 43758.5453); }
float3 patchTint(float n) {
    float3 v = abs(frac(n + float3(0.0, 0.6666667, 0.3333333)) * 6.0 - 3.0);
    return saturate(v - 1.0);
}
float3 patch(float2 uv, float seed, float side, float hue, float size) {
    float along = patchRandom(seed + 1.0);
    float2 center = side < 1.0 ? float2(along, 0.02) : side < 2.0 ? float2(0.98, along) :
        side < 3.0 ? float2(along, 0.98) : float2(0.02, along);
    float2 radius = float2(0.18 + 0.22 * patchRandom(seed + 2.0),
                           0.18 + 0.25 * patchRandom(seed + 3.0));
    float2 d = (uv - center) / (radius * size);
    float strength = 1.0 - smoothstep(0.03, 1.0, dot(d, d));
    return patchTint(hue) * strength;
}

float degaussAngle(float phase)
{
    phase = saturate(phase);
    float envelope = smoothstep(0.0, 0.04, phase) * (1.0 - smoothstep(0.2, 1.0, phase));
    return 0.5235988 * envelope * sin(phase * 25.1327412);
}

float2 rotateAroundCenter(float2 uv, float angle, float aspect)
{
    float2 position = (uv - 0.5) * float2(aspect, 1.0);
    float sine, cosine;
    sincos(angle, sine, cosine);
    return float2(cosine * position.x - sine * position.y,
        sine * position.x + cosine * position.y) / float2(aspect, 1.0) + 0.5;
}

float3 sampleComposedFrame(float2 uv)
{
    float3 pixel = Desktop.Sample(LinearClamp, uv).rgb;
    [branch] if (VignetteColor.a > 0.0) {
        // Match Vignette's 512px gradient and premultiplied 8-bit alpha.
        float2 gridPosition = floor(saturate(uv) * 511.0) / 511.0 * 2.0 - 1.0;
        float2 ellipse = gridPosition * float2(1.0 / Vignette.w, Vignette.w);
        float radius = sqrt(dot(ellipse, ellipse) * 0.5);
        float alpha = round(Vignette.x * smoothstep(Vignette.y, Vignette.y + Vignette.z, radius) * 255.0) / 255.0;
        pixel = pixel * (1.0 - alpha) + round(VignetteColor.rgb * alpha * 255.0) / 255.0;
    }
    return pixel;
}

struct Vertex
{
    float4 Position : SV_POSITION;
    float2 UV : TEXCOORD0;
};

Vertex VSMain(uint id : SV_VertexID)
{
    Vertex vertex;
    vertex.UV = float2((id << 1) & 2, id & 2);
    vertex.Position = float4(vertex.UV * float2(2, -2) + float2(-1, 1), 0, 1);
    return vertex;
}

float4 PSMain(Vertex vertex) : SV_TARGET
{
    float2 local = vertex.UV;
    float phase = Effect.z;
    float preset = Effect.y;
    float seed = Effect.w;
    bool similar = preset == 3.0 || preset == 6.0;
    bool subtle = preset == 4.0;
    bool intense = preset >= 5.0;
    float radius = subtle ? 0.85 : (intense ? 1.25 : 1.0);
    float intensity = Effect.x * (subtle ? 0.52 : (intense ? 1.55 : 1.0));
    float firstSide = floor(patchRandom(seed) * 4.0);
    float firstHue = patchRandom(seed + 4.0);
    float3 tint = patch(local, seed, firstSide, firstHue, radius);
    [branch] if (preset > 1.0)
    {
        float secondSeed = seed + 19.0;
        float secondSide = fmod(firstSide + 1.0 + floor(patchRandom(secondSeed) * 3.0), 4.0);
        float secondHue = similar ? firstHue + 0.055 : firstHue + 0.28 + 0.44 * patchRandom(secondSeed + 4.0);
        tint += patch(local, secondSeed, secondSide, secondHue, radius);
    }
    float coverage = min(1.0, max(max(tint.r, tint.g), tint.b) * intensity);
    // Between pulses, composite only the colored patches over the live desktop.
    // An opaque capture would hide capture-excluded overlays such as Retro Terminal.
    if (phase < 0.0)
    {
        float opacity = min(0.35, 0.35 * coverage);
        return float4(min(tint * (0.35 * intensity), opacity), opacity);
    }

    float aspect = Surface.x / Surface.y;
    float2 current = rotateAroundCenter(local, degaussAngle(phase), aspect);
    // Sample earlier rotation positions during the pulse to create a short motion trail.
    float3 pixel = sampleComposedFrame(current) * 0.40;
    pixel += sampleComposedFrame(rotateAroundCenter(local, degaussAngle(phase - 0.012), aspect)) * 0.25;
    pixel += sampleComposedFrame(rotateAroundCenter(local, degaussAngle(phase - 0.024), aspect)) * 0.18;
    pixel += sampleComposedFrame(rotateAroundCenter(local, degaussAngle(phase - 0.036), aspect)) * 0.11;
    pixel += sampleComposedFrame(rotateAroundCenter(local, degaussAngle(phase - 0.048), aspect)) * 0.06;
    float3 color = pixel * (1.0 - 0.35 * coverage) +
        dot(pixel, float3(0.299, 0.587, 0.114)) * tint * (0.55 * intensity);
    return float4(saturate(color), 1.0);
}
