// SPDX-License-Identifier: MIT
#pragma once
#include "EffectSettings.h"

struct NamedPreset
{
    const wchar_t* name;
    EffectSettings settings;
};

struct StaticPresetSettings
{
    std::array<float, 3> color{};
    Palette palette = Palette::SourceColor;
    float intensity = .25f;
    float scanStrength = 0.f;
    float scanSpacing = 3.f;
    float scanThickness = 1.f;
    float glow = 0.f;
    float glowRadius = .75f;
    float vignette = 0.f;
    float grain = 0.f;
    PhosphorMask mask = PhosphorMask::Off;
    float maskStrength = 0.f;
    float blackLevel = 0.f;
    float contrast = 1.f;
};

constexpr std::array<float, 3> PhosphorColor(uint32_t rgb)
{
    return { float((rgb >> 16) & 255u) / 255.f, float((rgb >> 8) & 255u) / 255.f, float(rgb & 255u) / 255.f };
}

constexpr EffectSettings StaticPreset(StaticPresetSettings look)
{
    const bool customColor = look.color != std::array<float, 3>{};
    return {
        .palette = customColor ? Palette::Custom : look.palette,
        .intensity = look.intensity,
        .color = customColor ? look.color : EffectSettings{}.color,
        .scanStrength = look.scanStrength,
        .scanSpacing = look.scanSpacing,
        .scanThickness = look.scanThickness,
        .glow = look.glow,
        .glowRadius = look.glowRadius,
        .vignette = look.vignette,
        .grain = look.grain,
        .mask = look.mask,
        .maskStrength = look.maskStrength,
        .blackLevel = look.blackLevel,
        .contrast = look.contrast,
    };
}

inline constexpr NamedPreset BuiltInPresets[]{
    // Monitor-inspired looks use stronger optics and more of the processed signal.
    {L"Commodore 1702 Composite", StaticPreset({
        .intensity = 0.75f, .scanStrength = 0.42f, .scanSpacing = 5.0f, .scanThickness = 1.5f, .glow = 0.18f,
        .glowRadius = 2.0f, .vignette = 0.38f, .mask = PhosphorMask::Dots, .maskStrength = 0.18f,
        .contrast = 1.07f})},
    {L"Commodore 1902 RGBI", StaticPreset({
        .intensity = 0.7f, .scanStrength = 0.25f, .scanSpacing = 3.0f, .scanThickness = 0.75f, .vignette = 0.06f,
        .mask = PhosphorMask::Dots, .maskStrength = 0.23f, .blackLevel = -0.025f, .contrast = 1.22f})},
    {L"Commodore PET Green", StaticPreset({
        .palette = Palette::Green, .intensity = 0.66f, .scanStrength = 0.31f, .scanSpacing = 4.0f,
        .scanThickness = 1.25f, .glow = 0.15f, .glowRadius = 1.75f, .vignette = 0.27f, .contrast = 1.16f})},
    {L"Amiga 1084S RGB", StaticPreset({
        .intensity = 0.74f, .scanStrength = 0.4f, .scanSpacing = 4.0f, .scanThickness = 1.25f, .glow = 0.1f,
        .glowRadius = 1.5f, .vignette = 0.15f, .mask = PhosphorMask::Dots, .maskStrength = 0.24f,
        .contrast = 1.18f})},
    {L"Amiga Workbench Blue", StaticPreset({
        .color = PhosphorColor(0x889dff), .intensity = 0.58f, .scanStrength = 0.23f, .scanSpacing = 3.0f,
        .scanThickness = 1.0f, .glow = 0.07f, .glowRadius = 1.25f, .vignette = 0.08f, .blackLevel = 0.025f,
        .contrast = 1.06f})},
    {L"Amiga 500 PAL", StaticPreset({
        .intensity = 0.7f, .scanStrength = 0.43f, .scanSpacing = 6.0f, .scanThickness = 1.75f, .glow = 0.25f,
        .glowRadius = 2.5f, .vignette = 0.26f, .grain = 0.018f, .blackLevel = 0.015f, .contrast = 0.96f})},
    {L"TV Composite PAL", StaticPreset({
        .intensity = 0.82f, .scanStrength = 0.47f, .scanSpacing = 6.0f, .scanThickness = 2.0f, .glow = 0.28f,
        .glowRadius = 2.75f, .vignette = 0.48f, .grain = 0.03f, .mask = PhosphorMask::Dots, .maskStrength = 0.19f,
        .blackLevel = -0.025f, .contrast = 1.09f})},
    {L"TV Black and White", StaticPreset({
        .color = PhosphorColor(0xe5edf7), .intensity = 0.82f, .scanStrength = 0.27f, .scanSpacing = 4.0f,
        .scanThickness = 1.25f, .glow = 0.16f, .glowRadius = 2.0f, .vignette = 0.42f, .grain = 0.019f,
        .contrast = 1.24f})},
    {L"TV RF Soft Focus", StaticPreset({
        .intensity = 0.76f, .scanStrength = 0.32f, .scanSpacing = 5.0f, .scanThickness = 1.25f, .glow = 0.35f,
        .glowRadius = 3.25f, .vignette = 0.58f, .grain = 0.065f, .blackLevel = 0.035f, .contrast = 0.84f})},
    {L"Trinitron Aperture Grille", StaticPreset({
        .intensity = 0.8f, .scanStrength = 0.33f, .scanSpacing = 4.0f, .scanThickness = 1.25f, .vignette = 0.1f,
        .mask = PhosphorMask::RgbStripes, .maskStrength = 0.4f, .contrast = 1.12f})},
    {L"Trinitron Fine Pitch", StaticPreset({
        .intensity = 0.67f, .scanStrength = 0.18f, .scanSpacing = 2.0f, .scanThickness = 0.75f, .vignette = 0.03f,
        .mask = PhosphorMask::RgbStripes, .maskStrength = 0.29f, .blackLevel = -0.01f, .contrast = 1.1f})},
    {L"Trinitron Broadcast RGB", StaticPreset({
        .intensity = 0.74f, .scanStrength = 0.5f, .scanSpacing = 5.0f, .scanThickness = 1.75f, .vignette = 0.07f,
        .mask = PhosphorMask::RgbStripes, .maskStrength = 0.31f, .blackLevel = -0.035f, .contrast = 1.24f})},
};
inline constexpr int BuiltInPresetCount = int(sizeof(BuiltInPresets) / sizeof(BuiltInPresets[0]));
