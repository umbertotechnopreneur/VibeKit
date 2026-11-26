Texture2D Desktop : register(t0);
Texture2D<float> WindowCoverage : register(t1);
SamplerState LinearClamp : register(s0);
cbuffer RippleConstants : register(b0)
{
    float4 Surface;
    float4 Timing;
    float4 Chromatic;
    float4 Filters;
    float4 Waves[8];
};
struct VertexOutput
{
    float4 Position : SV_POSITION;
    float2 UV : TEXCOORD0;
};

VertexOutput VSMain(uint vertexId : SV_VertexID)
{
    VertexOutput output;
    output.UV = float2((vertexId << 1) & 2, vertexId & 2);
    output.Position = float4(output.UV * float2(2, -2) + float2(-1, 1), 0, 1);
    return output;
}

float2 Displacement(float2 position, float4 wave, out float highlight)
{
    float2 delta = position - wave.xy * Surface.xy;
    float distance = length(delta);
    float age = max(0.0, wave.z);
    float front = age * Timing.x;
    float band = max(1.0, Surface.w);
    float offset = distance - front;
    // A single initialized return value also keeps FXC's loop-inlining analysis definite.
    float2 displacement = float2(0, 0);
    highlight = 0;
    [branch] if (abs(offset) < band && wave.w >= 0.5)
    {
        float normalizedOffset = offset / band;
        float envelope = 0.5 + 0.5 * cos(3.14159265 * normalizedOffset);
        float fade = saturate(1.0 - age / max(0.01, Timing.y));
        float phase = normalizedOffset * 6.28318531;
        float shape = sin(phase) + Filters.y * 0.35 * sin(phase * 3.0)
            + Filters.z * 0.65 * normalizedOffset;
        float amount = Surface.z * fade * fade * envelope * shape;
        highlight = fade * fade * envelope * pow(saturate(cos(phase)), 4.0);
        displacement = delta / max(1.0, distance) * amount;
    }
    return displacement;
}

float4 PSMain(VertexOutput input) : SV_TARGET
{
    [branch] if (Timing.w != 0)
    {
        bool overWindow = WindowCoverage.Load(int3(int2(input.Position.xy), 0)) > 0.5;
        if (overWindow != (Timing.w == 1))
            return float4(Desktop.Sample(LinearClamp, input.UV).rgb, 1);
    }
    float2 position = input.UV * Surface.xy;
    float2 shift = float2(0, 0);
    float highlight = 0;
    [loop] for (int i = 0; i < min(8, (int)Timing.z); ++i)
    {
        float waveHighlight;
        shift += Displacement(position, Waves[i], waveHighlight);
        highlight += waveHighlight;
    }
    float2 uvShift = shift / max(Surface.xy, float2(1, 1));
    float2 samplePosition = saturate(input.UV + uvShift);
    float3 center = Desktop.Sample(LinearClamp, samplePosition).rgb;
    [branch] if (Chromatic.x > 0 && Chromatic.y > 0)
    {
        float red = Desktop.Sample(LinearClamp, saturate(samplePosition + uvShift * Chromatic.x)).r;
        float blue = Desktop.Sample(LinearClamp, saturate(samplePosition - uvShift * Chromatic.x)).b;
        center = float3(lerp(center.r, red, Chromatic.y), center.g, lerp(center.b, blue, Chromatic.y));
    }
    center += (1.0 - center) * saturate(highlight * Filters.x * 0.5);
    return float4(center, 1);
}
