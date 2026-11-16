// SPDX-License-Identifier: MIT
Texture2D Desktop : register(t0);
Texture2D History : register(t1);
SamplerState LinearClamp : register(s0);
cbuffer Parameters : register(b0)
{
    float4 Surface; // width, height, mix, palette
    float4 Phosphor; // custom RGB, mask style
    float4 Scan; // strength, spacing, thickness, mask strength
    float4 Tone; // glow radius, glow strength, black level, contrast
    float4 Finish; // vignette, grain, history decay, history mix
    float4 Glitch; // type (-1 disabled), amount, vertical position, phase
};
struct Vertex { float4 Position : SV_POSITION; float2 UV : TEXCOORD0; };
Vertex VSMain(uint id : SV_VertexID)
{
    Vertex v;
    v.UV = float2((id << 1) & 2, id & 2);
    v.Position = float4(v.UV * float2(2, -2) + float2(-1, 1), 0, 1);
    return v;
}
float Hash(float2 p) { return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453); }
struct Pixel { float4 Display : SV_TARGET0; float4 Memory : SV_TARGET1; };
Pixel PSMain(Vertex v)
{
    float2 texel = 1.0 / Surface.xy;
    float3 original = Desktop.Sample(LinearClamp, v.UV).rgb;
    float2 uv = v.UV;
    float local = 1.0 - smoothstep(0.012, 0.035, abs(uv.y - Glitch.z));
    if (Glitch.x > 0.5 && Glitch.x < 1.5)
        uv.x += local * sin(Glitch.w * 6.2831853) * Glitch.y * 10.0 * texel.x;
    float3 center = Desktop.Sample(LinearClamp, uv).rgb;
    float2 radius = texel * Tone.x;
    float3 neighbors = Desktop.Sample(LinearClamp, uv + float2(radius.x, 0)).rgb
                     + Desktop.Sample(LinearClamp, uv - float2(radius.x, 0)).rgb
                     + Desktop.Sample(LinearClamp, uv + float2(0, radius.y)).rgb
                     + Desktop.Sample(LinearClamp, uv - float2(0, radius.y)).rgb;
    float3 color = saturate((center + neighbors * .25 * Tone.y * (1 - center) - .5) * Tone.w + .5 + Tone.z);
    if (Surface.w < 1.5 || Surface.w > 2.5) {
        float luma = dot(color, float3(.2126, .7152, .0722));
        float3 tint = Surface.w < .5 ? float3(.2, 1, .42)
                    : Surface.w < 1.5 ? float3(1, .63, .18) : Phosphor.rgb;
        color = tint * pow(max(luma, 0), .85);
    }
    if (Glitch.x > -0.5 && Glitch.x < 0.5 && Glitch.y > 0) {
        float2 split = float2(4.0 * Glitch.y * texel.x, 0);
        float3 right = Desktop.Sample(LinearClamp, uv + split).rgb;
        float3 left = Desktop.Sample(LinearClamp, uv - split).rgb;
        // RGB edges remain colored even on a monochrome phosphor preset.
        float centerLuma = dot(center, float3(.2126, .7152, .0722));
        color.r += (dot(right, float3(.2126, .7152, .0722)) - centerLuma) * .65;
        color.b += (dot(left, float3(.2126, .7152, .0722)) - centerLuma) * .65;
    }
    float row = fmod(floor(v.Position.y), Scan.y);
    color *= 1.0 - Scan.x * (1.0 - smoothstep(Scan.z - .25, Scan.z + .25, row));
    if (Phosphor.w > .5) {
        int column = (int)v.Position.x % 3;
        float3 mask = column == 0 ? float3(1, 0, 0) : column == 1 ? float3(0, 1, 0) : float3(0, 0, 1);
        if (Phosphor.w > 1.5) {
            float2 dotUV = frac(v.Position.xy / float2(3, 3)) - .5;
            mask *= 1.0 - smoothstep(.15, .48, length(dotUV));
        }
        color *= lerp(1.0, mask, Scan.w);
    }
    float2 edge = v.UV * 2 - 1;
    color *= 1.0 - Finish.x * smoothstep(.25, 1.5, dot(edge, edge));
    color += (Hash(floor(v.Position.xy)) - .5) * Finish.y;
    if (Glitch.x > 1.5 && Glitch.x < 2.5)
        color += (Hash(float2(floor(v.Position.x / 2), floor(v.Position.y) + floor(Glitch.w * 10))) - .5) * local * Glitch.y * .25;
    if (Glitch.x > 2.5) {
        float band = 1.0 - smoothstep(.01, .09, abs(v.UV.y - lerp(.05, .95, Glitch.w)));
        color *= 1.0 - band * Glitch.y * .18;
    }
    // Mix zero is an exact passthrough, including while glitches/persistence are enabled.
    float3 current = lerp(original, saturate(color), Surface.z);
    float3 memory = current;
    if (Finish.w > 0 && Finish.z > 0 && Surface.z > 0)
        memory = max(current, History.Sample(LinearClamp, v.UV).rgb * Finish.z);
    Pixel result;
    result.Memory = float4(memory, 1);
    result.Display = float4(lerp(current, memory, Finish.w * Surface.z), 1);
    return result;
}
