// SPDX-License-Identifier: MIT
#pragma once
#include "EffectSettings.h"
#include <bit>
#include <iterator>
#include <span>

// The registry snapshot stores fixed-width words independently of native struct layout.
inline constexpr uint32_t SnapshotMagic = 0x43525433;
inline constexpr uint32_t SnapshotVersion = 3;
inline constexpr size_t SnapshotWordCount = 3 + std::size(FloatControls) + 3 + 5;
using SettingsSnapshot = std::array<uint32_t, SnapshotWordCount>;

inline SettingsSnapshot EncodeSettings(EffectSettings settings)
{
    settings.Clamp();
    SettingsSnapshot words{};
    words[0] = SnapshotMagic;
    words[1] = SnapshotVersion;
    words[2] = uint32_t(words.size());
    size_t index = 3;
    for (const auto& spec : FloatControls)
        words[index++] = std::bit_cast<uint32_t>(settings.*(spec.field));
    for (float channel : settings.color)
        words[index++] = std::bit_cast<uint32_t>(channel);
    words[index++] = uint32_t(settings.palette);
    words[index++] = uint32_t(settings.mask);
    words[index++] = settings.persistence;
    words[index++] = settings.randomGlitches;
    words[index] = settings.glitchTypes;
    return words;
}

inline bool DecodeSettings(std::span<const uint32_t> words, EffectSettings& settings)
{
    if (words.size() != SnapshotWordCount || words[0] != SnapshotMagic ||
        words[1] != SnapshotVersion || words[2] != SnapshotWordCount)
        return false;
    // Decode into a temporary: a truncated or invalid snapshot never partially
    // changes the caller's current settings.
    EffectSettings decoded;
    size_t index = 3;
    for (const auto& spec : FloatControls) {
        const float value = std::bit_cast<float>(words[index++]);
        if (!std::isfinite(value) || value < spec.low || value > spec.high) return false;
        decoded.*(spec.field) = value;
    }
    for (float& channel : decoded.color) {
        channel = std::bit_cast<float>(words[index++]);
        if (!std::isfinite(channel) || channel < 0 || channel > 1) return false;
    }
    const uint32_t palette = words[index++];
    const uint32_t mask = words[index++];
    const uint32_t persistence = words[index++];
    const uint32_t randomGlitches = words[index++];
    const uint32_t glitchTypes = words[index];
    if (palette > uint32_t(Palette::Custom) || mask > uint32_t(PhosphorMask::Dots) ||
        persistence > 1 || randomGlitches > 1 || (glitchTypes & ~AllGlitchTypes))
        return false;
    decoded.palette = Palette(palette);
    decoded.mask = PhosphorMask(mask);
    decoded.persistence = persistence != 0;
    decoded.randomGlitches = randomGlitches != 0;
    decoded.glitchTypes = glitchTypes;
    if (decoded.scanThickness > decoded.scanSpacing - .25f || decoded.intervalMin > decoded.intervalMax ||
        decoded.durationMinMs > decoded.durationMaxMs) return false;
    settings = decoded;
    return true;
}
