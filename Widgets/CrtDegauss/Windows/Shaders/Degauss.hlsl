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
    float4 Effect; // strength, second patch, pulse progress (-1 when idle), reserved
};

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
    float3 pixel;
    [branch] if (phase >= 0.0)
    {
        float envelope = smoothstep(0.0, 0.035, phase) * (1.0 - phase) * exp(-3.0 * phase);
        float swing = envelope * sin(phase * 72.0);
        float angle = 0.065 * swing;
        float aspect = Surface.x / Surface.y;
        float2 point = (local - 0.5) * float2(aspect, 1.0);
        float2 rotated = float2(cos(angle) * point.x - sin(angle) * point.y,
            sin(angle) * point.x + cos(angle) * point.y) / float2(aspect, 1.0) + 0.5;
        rotated.y += swing * (0.058 + 0.023 * sin(local.x * 6.2831853));
        float2 split = float2(0.007, 0.002) * swing;
        pixel = Desktop.Sample(LinearClamp, rotated).rgb;
        pixel.r = Desktop.Sample(LinearClamp, rotated + split).r;
        pixel.b = Desktop.Sample(LinearClamp, rotated - split).b;
    }
    else
        pixel = Desktop.Sample(LinearClamp, local).rgb;

    float2 first = (local - float2(0.045, 0.10)) / float2(0.30, 0.39);
    float2 second = (local - float2(0.97, 0.94)) / float2(0.29, 0.42);
    float magenta = (1.0 - smoothstep(0.03, 1.0, dot(first, first))) * Effect.x;
    float green = (1.0 - smoothstep(0.03, 1.0, dot(second, second))) * Effect.x * Effect.y;
    float3 color = pixel * (1.0 - 0.20 * (magenta + green)) +
        pixel.gbr * float3(0.30, 0.025, 0.17) * magenta +
        pixel.brg * float3(0.04, 0.35, 0.12) * green;
    return float4(saturate(color), 1.0);
}
