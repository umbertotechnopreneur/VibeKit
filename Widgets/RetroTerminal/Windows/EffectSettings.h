// SPDX-License-Identifier: MIT
#pragma once
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <random>

// Values are part of the shader/settings format. Keep explicit numeric IDs.
enum class Palette : int { Green = 0, Amber = 1, SourceColor = 2, Custom = 3 };
enum class PhosphorMask : int { Off = 0, RgbStripes = 1, Dots = 2 };
enum class GlitchType : int { None = -1, Rgb = 0, Tear = 1, Noise = 2, Band = 3 };
inline constexpr unsigned AllGlitchTypes = 0b1111;
inline constexpr double GlitchRestSeconds = 2.0;

struct EffectSettings
{
    Palette palette = Palette::Green;
    float intensity = .9f;
    std::array<float, 3> color{.20f, 1.f, .42f};
    float scanStrength = .22f;
    float scanSpacing = 3.f;
    float scanThickness = 1.f;
    float glow = .35f;
    float glowRadius = 2.f;
    float vignette = .25f;
    float grain = .015f;
    PhosphorMask mask = PhosphorMask::Off;
    float maskStrength = .16f;
    float blackLevel = 0.f;
    float contrast = 1.f;
    bool persistence = false;
    float persistenceMs = 120.f;
    float persistenceStrength = .35f;
    bool randomGlitches = false;
    unsigned glitchTypes = AllGlitchTypes;
    float intervalMin = 20.f; // Seconds; durations below use milliseconds.
    float intervalMax = 60.f;
    float durationMinMs = 100.f;
    float durationMaxMs = 250.f;
    float glitchIntensity = .25f;

    void Clamp();
};

// One descriptor table supplies UI, validation and the ordered snapshot format.
// Append fields for a new snapshot version; never reorder an existing format.
struct FloatControl
{
    const wchar_t* key;
    const wchar_t* title;
    float EffectSettings::* field;
    float low, high;
    int page;
};
inline constexpr FloatControl FloatControls[]{
    {L"Intensity", L"Effect mix", &EffectSettings::intensity, 0, 1, 0},
    {L"ScanStrength", L"Scanline strength", &EffectSettings::scanStrength, 0, .6f, 0},
    {L"ScanSpacing", L"Scanline spacing (px)", &EffectSettings::scanSpacing, 2, 12, 0},
    {L"ScanThickness", L"Scanline width (px)", &EffectSettings::scanThickness, .25f, 6, 0},
    {L"Glow", L"Phosphor glow", &EffectSettings::glow, 0, .8f, 0},
    {L"GlowRadius", L"Glow radius (px)", &EffectSettings::glowRadius, .5f, 8, 0},
    {L"Vignette", L"Darkened edges", &EffectSettings::vignette, 0, .6f, 0},
    {L"Grain", L"Static grain", &EffectSettings::grain, 0, .1f, 0},
    {L"MaskStrength", L"Phosphor mask strength", &EffectSettings::maskStrength, 0, .5f, 0},
    {L"BlackLevel", L"Black level", &EffectSettings::blackLevel, -.2f, .2f, 0},
    {L"Contrast", L"Contrast", &EffectSettings::contrast, .5f, 2, 0},
    {L"PersistenceMs", L"Afterglow half-life (ms)", &EffectSettings::persistenceMs, 40, 500, 0},
    {L"PersistenceStrength", L"Afterglow mix", &EffectSettings::persistenceStrength, 0, .6f, 0},
    {L"IntervalMin", L"Minimum interval (s)", &EffectSettings::intervalMin, 5, 300, 1},
    {L"IntervalMax", L"Maximum interval (s)", &EffectSettings::intervalMax, 5, 300, 1},
    {L"DurationMin", L"Minimum duration (ms)", &EffectSettings::durationMinMs, 80, 1200, 1},
    {L"DurationMax", L"Maximum duration (ms)", &EffectSettings::durationMaxMs, 80, 1200, 1},
    {L"GlitchIntensity", L"Glitch intensity", &EffectSettings::glitchIntensity, 0, 1, 1},
};
inline void EffectSettings::Clamp()
{
    const EffectSettings defaults;
    for (const auto& spec : FloatControls) {
        float& value = this->*(spec.field);
        const float fallback = defaults.*(spec.field);
        value = std::clamp(std::isfinite(value) ? value : fallback, spec.low, spec.high);
    }
    palette = Palette(std::clamp(int(palette), int(Palette::Green), int(Palette::Custom)));
    mask = PhosphorMask(std::clamp(int(mask), int(PhosphorMask::Off), int(PhosphorMask::Dots)));
    for (auto& channel : color)
        channel = std::clamp(std::isfinite(channel) ? channel : .5f, 0.f, 1.f);
    // Relational constraints are applied after every field has a finite value.
    scanThickness = std::min(scanThickness, scanSpacing - .25f);
    intervalMax = std::max(intervalMin, intervalMax);
    durationMaxMs = std::max(durationMinMs, durationMaxMs);
    glitchTypes &= AllGlitchTypes;
}

inline EffectSettings BuiltInPreset(int preset)
{
    EffectSettings s;
    if (preset == 0) { s.scanStrength = .12f; s.glow = .18f; s.grain = 0; }
    if (preset == 1) {
        s.palette = Palette::Amber; s.glow = .45f; s.grain = .025f;
        s.randomGlitches = true; s.glitchIntensity = .2f;
    }
    if (preset == 2) {
        s.palette = Palette::SourceColor; s.mask = PhosphorMask::RgbStripes; s.glow = .5f; s.scanStrength = .25f;
    }
    return s;
}

struct GlitchFrame {
    GlitchType type = GlitchType::None;
    float amount = 0;
    float position = .5f;
    float phase = 0;
};

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
        restUntil = now + GlitchRestSeconds;
        next = restUntil + Interval(settings);
        previous = {};
    }
    bool Trigger(double now, const EffectSettings& settings)
    {
        if (active || now < restUntil || !settings.glitchTypes || settings.intensity <= 0 || settings.glitchIntensity <= 0) return false;
        std::array<GlitchType, 4> choices{};
        int count = 0;
        for (int i = 0; i < 4; ++i) if (settings.glitchTypes & (1u << i)) choices[count++] = GlitchType(i);
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
            restUntil = now + GlitchRestSeconds;
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
    // All scheduler times are monotonic seconds.
    double next = 0;
    double restUntil = 0;
    double begin = 0;
    double duration = .1;
    bool active = false;
    GlitchType type = GlitchType::None;
    float position = .5f;
    GlitchFrame previous;
};
