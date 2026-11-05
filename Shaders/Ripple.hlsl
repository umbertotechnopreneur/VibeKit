Texture2D Desktop : register(t0);
SamplerState LinearClamp : register(s0);
cbuffer RippleConstants : register(b0)
{
    float4 Surface;
    float4 Timing;
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

float2 Displacement(float2 position, float4 wave)
{
    float2 delta = position - wave.xy * Surface.xy;
    float distance = length(delta);
    float age = max(0.0, wave.z);
    float front = age * Timing.x;
    float band = max(1.0, Surface.w);
    float offset = distance - front;
    // A single initialized return value also keeps FXC's loop-inlining analysis definite.
    float2 displacement = float2(0, 0);
    [branch] if (abs(offset) < band && wave.w >= 0.5)
    {
        float normalizedOffset = offset / band;
        float envelope = 0.5 + 0.5 * cos(3.14159265 * normalizedOffset);
        float fade = saturate(1.0 - age / max(0.01, Timing.y));
        float amount = Surface.z * fade * fade * envelope * sin(normalizedOffset * 6.28318531);
        displacement = delta / max(1.0, distance) * amount;
    }
    return displacement;
}

float4 PSMain(VertexOutput input) : SV_TARGET
{
    float2 position = input.UV * Surface.xy;
    float2 shift = float2(0, 0);
    [loop] for (int i = 0; i < min(8, (int)Timing.z); ++i)
        shift += Displacement(position, Waves[i]);
    float2 samplePosition = saturate(input.UV + shift / max(Surface.xy, float2(1, 1)));
    return float4(Desktop.Sample(LinearClamp, samplePosition).rgb, 1);
}
