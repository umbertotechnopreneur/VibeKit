// SPDX-License-Identifier: MIT
#pragma once
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <random>

struct EffectSettings
{
    int palette = 0; // green, amber, source color, custom phosphor
    float intensity = .9f;
    std::array<float, 3> color{.20f, 1.f, .42f};
    float scanStrength = .22f, scanSpacing = 3.f, scanThickness = 1.f;
    float glow = .35f, glowRadius = 2.f, vignette = .25f, grain = .015f;
    int mask = 0; // off, RGB stripes, phosphor dots
    float maskStrength = .16f, blackLevel = 0.f, contrast = 1.f;
    bool persistence = false;
    float persistenceMs = 120.f, persistenceStrength = .35f;
    bool randomGlitches = false;
    unsigned glitchTypes = 15; // RGB, tear, local noise, scan band
    float intervalMin = 20.f, intervalMax = 60.f;
    float durationMinMs = 100.f, durationMaxMs = 250.f, glitchIntensity = .25f;

    void Clamp()
    {
        auto clamp = [](float& value, float low, float high, float fallback) {
            value = std::clamp(std::isfinite(value) ? value : fallback, low, high);
        };
        palette = std::clamp(palette, 0, 3); mask = std::clamp(mask, 0, 2);
        clamp(intensity, 0, 1, .9f);
        for (auto& channel : color) clamp(channel, 0, 1, .5f);
        clamp(scanStrength, 0, .6f, .22f); clamp(scanSpacing, 2, 12, 3);
        clamp(scanThickness, .25f, std::min(6.f, scanSpacing - .25f), 1);
        clamp(glow, 0, .8f, .35f); clamp(glowRadius, .5f, 8, 2);
        clamp(vignette, 0, .6f, .25f); clamp(grain, 0, .1f, .015f);
        clamp(maskStrength, 0, .5f, .16f); clamp(blackLevel, -.2f, .2f, 0);
        clamp(contrast, .5f, 2.f, 1);
        clamp(persistenceMs, 40, 500, 120); clamp(persistenceStrength, 0, .6f, .35f);
        clamp(intervalMin, 5, 300, 20); clamp(intervalMax, intervalMin, 300, 60);
        clamp(durationMinMs, 80, 1200, 100); clamp(durationMaxMs, durationMinMs, 1200, 250);
        clamp(glitchIntensity, 0, 1, .25f);
        glitchTypes &= 15;
    }
};

inline EffectSettings BuiltInPreset(int preset)
{
    EffectSettings s;
    if (preset == 0) { s.scanStrength = .12f; s.glow = .18f; s.grain = 0; }
    if (preset == 1) {
        s.palette = 1; s.glow = .45f; s.grain = .025f;
        s.randomGlitches = true; s.glitchIntensity = .2f;
    }
    if (preset == 2) {
        s.palette = 2; s.mask = 1; s.glow = .5f; s.scanStrength = .25f;
    }
    return s;
}

struct GlitchFrame { int type = -1; float amount = 0, position = .5f, phase = 0; };

class GlitchSchedule
{
public:
    explicit GlitchSchedule(uint32_t seed = std::random_device{}()) : random(seed) {}
    void Reset(double now, const EffectSettings& settings)
    {
        active = false;
        next = now + Interval(settings);
        restUntil = now;
        previous = {};
    }
    void Cancel(double now, const EffectSettings& settings)
    {
        active = false;
        restUntil = now + 2.0;
        next = restUntil + Interval(settings);
        previous = {};
    }
    bool Trigger(double now, const EffectSettings& settings)
    {
        if (active || now < restUntil || !settings.glitchTypes || settings.intensity <= 0 || settings.glitchIntensity <= 0) return false;
        std::array<int, 4> choices{}; int count = 0;
        for (int i = 0; i < 4; ++i) if (settings.glitchTypes & (1u << i)) choices[count++] = i;
        if (!count) return false;
        type = choices[std::uniform_int_distribution<int>(0, count - 1)(random)];
        position = Range(.12f, .88f);
        begin = now;
        duration = Range(settings.durationMinMs, settings.durationMaxMs) / 1000.0;
        active = true;
        return true;
    }
    GlitchFrame Sample(double now, const EffectSettings& settings)
    {
        if (active && now >= begin + duration) {
            active = false;
            restUntil = now + 2.0;
            next = now + Interval(settings);
        }
        if (!active && settings.randomGlitches && now >= next) Trigger(now, settings);
        GlitchFrame frame;
        if (active) {
            frame.type = type;
            frame.phase = float(std::clamp((now - begin) / duration, 0.0, 1.0));
            frame.amount = settings.glitchIntensity * std::sin(frame.phase * 3.14159265f);
            frame.position = position;
        }
        return frame;
    }
    bool Changed(const GlitchFrame& frame)
    {
        bool changed = frame.type != previous.type || frame.phase != previous.phase || frame.amount != previous.amount;
        previous = frame;
        return changed;
    }
private:
    float Range(float low, float high) { return std::uniform_real_distribution<float>(low, high)(random); }
    double Interval(const EffectSettings& settings) { return Range(settings.intervalMin, settings.intervalMax); }
    std::mt19937 random;
    double next = 0, restUntil = 0, begin = 0, duration = .1;
    bool active = false;
    int type = -1;
    float position = .5f;
    GlitchFrame previous;
};
