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

// Keep the raster gentle and give each look a small amount of static grain.
inline constexpr NamedPreset BuiltInPresets[]{
    // Existing monitor presets.
    {L"Commodore 1702 Composite", StaticPreset({
        .intensity = 0.4f, .scanStrength = 0.105f, .scanSpacing = 5.0f, .scanThickness = 1.25f, .glow = 0.08f,
        .glowRadius = 1.75f, .vignette = 0.06f, .grain = 0.06f, .mask = PhosphorMask::Dots,
        .maskStrength = 0.065f})},
    {L"Commodore 1902 RGBI", StaticPreset({
        .intensity = 0.36f, .scanStrength = 0.085f, .scanSpacing = 3.0f, .scanThickness = 0.75f,
        .grain = 0.045f, .mask = PhosphorMask::Dots, .maskStrength = 0.075f, .contrast = 1.035f})},
    {L"Commodore PET Green", StaticPreset({
        .palette = Palette::Green, .intensity = 0.31f, .scanStrength = 0.09f, .scanSpacing = 4.0f,
        .scanThickness = 1.0f, .glow = 0.08f, .glowRadius = 1.25f, .vignette = 0.055f, .grain = 0.06f})},
    {L"Amiga 1084S RGB", StaticPreset({
        .intensity = 0.39f, .scanStrength = 0.095f, .scanSpacing = 4.0f, .scanThickness = 1.0f, .glow = 0.05f,
        .glowRadius = 1.25f, .grain = 0.05f, .mask = PhosphorMask::Dots, .maskStrength = 0.08f,
        .contrast = 1.025f})},
    {L"Amiga Workbench Blue", StaticPreset({
        .color = PhosphorColor(0xa4b5ff), .intensity = 0.39f, .scanStrength = 0.075f, .scanSpacing = 3.0f,
        .scanThickness = 1.0f, .glow = 0.05f, .grain = 0.05f, .blackLevel = 0.01f})},
    {L"Amiga 500 PAL", StaticPreset({
        .intensity = 0.41f, .scanStrength = 0.11f, .scanSpacing = 5.0f, .scanThickness = 1.25f, .glow = 0.08f,
        .glowRadius = 1.75f, .vignette = 0.045f, .grain = 0.075f, .contrast = 0.985f})},
    {L"TV Composite PAL", StaticPreset({
        .intensity = 0.44f, .scanStrength = 0.105f, .scanSpacing = 5.0f, .scanThickness = 1.25f,
        .glow = 0.095f, .glowRadius = 1.75f, .vignette = 0.08f, .grain = 0.085f, .mask = PhosphorMask::Dots,
        .maskStrength = 0.045f, .blackLevel = 0.01f})},
    {L"TV Black and White", StaticPreset({
        .color = PhosphorColor(0xedf2f7), .intensity = 0.46f, .scanStrength = 0.09f, .scanSpacing = 4.0f,
        .scanThickness = 1.0f, .glow = 0.075f, .glowRadius = 1.5f, .vignette = 0.075f, .grain = 0.075f,
        .contrast = 1.03f})},
    {L"TV RF Soft Focus", StaticPreset({
        .intensity = 0.43f, .scanStrength = 0.085f, .scanSpacing = 5.0f, .scanThickness = 1.25f, .glow = 0.13f,
        .glowRadius = 2.5f, .vignette = 0.1f, .grain = 0.1f, .blackLevel = 0.012f, .contrast = 0.97f})},
    {L"Trinitron Aperture Grille", StaticPreset({
        .intensity = 0.42f, .scanStrength = 0.08f, .scanSpacing = 4.0f, .scanThickness = 1.0f,
        .vignette = 0.025f, .grain = 0.055f, .mask = PhosphorMask::RgbStripes, .maskStrength = 0.095f})},
    {L"Trinitron Fine Pitch", StaticPreset({
        .intensity = 0.37f, .scanStrength = 0.075f, .scanSpacing = 2.0f, .scanThickness = 0.75f,
        .grain = 0.05f, .mask = PhosphorMask::RgbStripes, .maskStrength = 0.08f})},
    {L"Trinitron Broadcast RGB", StaticPreset({
        .intensity = 0.43f, .scanStrength = 0.09f, .scanSpacing = 5.0f, .scanThickness = 1.0f, .grain = 0.055f,
        .mask = PhosphorMask::RgbStripes, .maskStrength = 0.085f, .contrast = 1.025f})},

    // More Commodore and Amiga monitors.
    {L"Commodore 64 Luma", StaticPreset({
        .intensity = 0.38f, .scanStrength = 0.12f, .scanSpacing = 4.0f, .scanThickness = 1.0f, .glow = 0.095f,
        .glowRadius = 1.5f, .vignette = 0.045f, .grain = 0.075f, .mask = PhosphorMask::Dots,
        .maskStrength = 0.05f, .contrast = 0.98f})},
    {L"Commodore 128 Text", StaticPreset({
        .intensity = 0.34f, .scanStrength = 0.09f, .scanSpacing = 3.0f, .scanThickness = 0.75f,
        .grain = 0.055f, .mask = PhosphorMask::Dots, .maskStrength = 0.06f, .contrast = 1.04f})},
    {L"Commodore 1084 Composite", StaticPreset({
        .intensity = 0.42f, .scanStrength = 0.105f, .scanSpacing = 4.0f, .scanThickness = 1.0f, .glow = 0.1f,
        .glowRadius = 2.0f, .vignette = 0.045f, .grain = 0.07f, .mask = PhosphorMask::Dots,
        .maskStrength = 0.07f})},
    {L"Commodore Amber Phosphor", StaticPreset({
        .palette = Palette::Amber, .intensity = 0.32f, .scanStrength = 0.085f, .scanSpacing = 4.0f,
        .scanThickness = 1.0f, .glow = 0.075f, .glowRadius = 1.5f, .vignette = 0.045f, .grain = 0.065f})},
    {L"Amiga Workbench Grey", StaticPreset({
        .color = PhosphorColor(0xdddfe3), .intensity = 0.37f, .scanStrength = 0.065f, .scanSpacing = 3.0f,
        .scanThickness = 0.75f, .grain = 0.045f})},
    {L"Amiga 1200 RGB", StaticPreset({
        .intensity = 0.38f, .scanStrength = 0.085f, .scanSpacing = 3.0f, .scanThickness = 0.75f,
        .glow = 0.045f, .glowRadius = 1.25f, .grain = 0.05f, .mask = PhosphorMask::Dots, .maskStrength = 0.09f})},
    {L"Amiga 500 NTSC", StaticPreset({
        .intensity = 0.41f, .scanStrength = 0.095f, .scanSpacing = 4.0f, .scanThickness = 1.0f, .glow = 0.075f,
        .glowRadius = 1.5f, .vignette = 0.04f, .grain = 0.085f, .contrast = 0.99f})},
    {L"Amiga Genlock", StaticPreset({
        .intensity = 0.4f, .scanStrength = 0.07f, .scanSpacing = 5.0f, .scanThickness = 1.0f, .glow = 0.065f,
        .glowRadius = 1.75f, .vignette = 0.03f, .grain = 0.075f, .blackLevel = 0.008f, .contrast = 1.015f})},

    // More TV and Trinitron displays.
    {L"TV Composite NTSC", StaticPreset({
        .intensity = 0.43f, .scanStrength = 0.105f, .scanSpacing = 4.0f, .scanThickness = 1.0f, .glow = 0.085f,
        .glowRadius = 1.75f, .vignette = 0.07f, .grain = 0.09f, .mask = PhosphorMask::Dots,
        .maskStrength = 0.06f, .blackLevel = 0.008f})},
    {L"TV S-Video", StaticPreset({
        .intensity = 0.37f, .scanStrength = 0.09f, .scanSpacing = 4.0f, .scanThickness = 1.0f, .glow = 0.055f,
        .glowRadius = 1.25f, .vignette = 0.035f, .grain = 0.065f, .mask = PhosphorMask::Dots,
        .maskStrength = 0.065f})},
    {L"TV Portable Mono", StaticPreset({
        .color = PhosphorColor(0xdce7e4), .intensity = 0.46f, .scanStrength = 0.1f, .scanSpacing = 5.0f,
        .scanThickness = 1.25f, .glow = 0.09f, .glowRadius = 1.75f, .vignette = 0.085f, .grain = 0.09f,
        .blackLevel = 0.01f})},
    {L"TV Studio Monitor", StaticPreset({
        .intensity = 0.41f, .scanStrength = 0.075f, .scanSpacing = 4.0f, .scanThickness = 1.0f, .glow = 0.045f,
        .grain = 0.06f, .mask = PhosphorMask::Dots, .maskStrength = 0.07f, .contrast = 1.02f})},
    {L"Trinitron Desktop VGA", StaticPreset({
        .intensity = 0.36f, .scanStrength = 0.07f, .scanSpacing = 3.0f, .scanThickness = 0.75f, .grain = 0.05f,
        .mask = PhosphorMask::RgbStripes, .maskStrength = 0.11f, .contrast = 1.02f})},
    {L"Trinitron Studio PAL", StaticPreset({
        .intensity = 0.43f, .scanStrength = 0.095f, .scanSpacing = 5.0f, .scanThickness = 1.25f,
        .glow = 0.065f, .glowRadius = 1.5f, .grain = 0.075f, .mask = PhosphorMask::RgbStripes,
        .maskStrength = 0.075f})},
    {L"Trinitron Consumer RGB", StaticPreset({
        .intensity = 0.4f, .scanStrength = 0.095f, .scanSpacing = 4.0f, .scanThickness = 1.0f, .glow = 0.075f,
        .glowRadius = 1.5f, .vignette = 0.055f, .grain = 0.065f, .mask = PhosphorMask::RgbStripes,
        .maskStrength = 0.085f})},
    {L"Trinitron Soft Grille", StaticPreset({
        .intensity = 0.38f, .scanStrength = 0.065f, .scanSpacing = 5.0f, .scanThickness = 1.25f, .glow = 0.09f,
        .glowRadius = 1.75f, .grain = 0.07f, .mask = PhosphorMask::RgbStripes, .maskStrength = 0.07f,
        .contrast = 0.985f})},

    // Terminal phosphors.
    {L"Terminal IBM Green", StaticPreset({
        .palette = Palette::Green, .intensity = 0.34f, .scanStrength = 0.105f, .scanSpacing = 4.0f,
        .scanThickness = 1.0f, .glow = 0.07f, .glowRadius = 1.5f, .grain = 0.065f, .contrast = 1.02f})},
    {L"Terminal Amber Glow", StaticPreset({
        .palette = Palette::Amber, .intensity = 0.35f, .scanStrength = 0.085f, .scanSpacing = 5.0f,
        .scanThickness = 1.0f, .glow = 0.115f, .glowRadius = 1.75f, .vignette = 0.06f, .grain = 0.065f})},
    {L"Terminal Ice Blue", StaticPreset({
        .color = PhosphorColor(0xaccfff), .intensity = 0.4f, .scanStrength = 0.075f, .scanSpacing = 4.0f,
        .scanThickness = 1.0f, .glow = 0.055f, .glowRadius = 1.25f, .grain = 0.05f})},
    {L"Terminal Paper White", StaticPreset({
        .color = PhosphorColor(0xf5ead6), .intensity = 0.43f, .scanStrength = 0.065f, .scanSpacing = 3.0f,
        .scanThickness = 0.75f, .grain = 0.055f, .contrast = 1.025f})},
    {L"Terminal Mint Phosphor", StaticPreset({
        .color = PhosphorColor(0xa9ffcc), .intensity = 0.41f, .scanStrength = 0.08f, .scanSpacing = 4.0f,
        .scanThickness = 1.0f, .glow = 0.08f, .glowRadius = 1.5f, .grain = 0.06f})},
    {L"Terminal Violet Phosphor", StaticPreset({
        .color = PhosphorColor(0xd5bbff), .intensity = 0.42f, .scanStrength = 0.085f, .scanSpacing = 5.0f,
        .scanThickness = 1.0f, .glow = 0.075f, .glowRadius = 1.5f, .grain = 0.065f})},

    // Arcade and video signals.
    {L"Arcade Shadow Mask", StaticPreset({
        .intensity = 0.44f, .scanStrength = 0.095f, .scanSpacing = 4.0f, .scanThickness = 1.0f, .glow = 0.08f,
        .glowRadius = 1.5f, .vignette = 0.06f, .grain = 0.06f, .mask = PhosphorMask::Dots,
        .maskStrength = 0.11f})},
    {L"Arcade RGB Cabinet", StaticPreset({
        .intensity = 0.42f, .scanStrength = 0.09f, .scanSpacing = 5.0f, .scanThickness = 1.0f, .glow = 0.08f,
        .glowRadius = 1.5f, .vignette = 0.07f, .grain = 0.065f, .mask = PhosphorMask::RgbStripes,
        .maskStrength = 0.08f})},
    {L"Arcade Fine Raster", StaticPreset({
        .intensity = 0.34f, .scanStrength = 0.075f, .scanSpacing = 3.0f, .scanThickness = 0.75f,
        .glow = 0.055f, .grain = 0.045f, .mask = PhosphorMask::Dots, .maskStrength = 0.065f})},
    {L"Arcade Warm Glass", StaticPreset({
        .color = PhosphorColor(0xffe1b8), .intensity = 0.36f, .scanStrength = 0.085f, .scanSpacing = 4.0f,
        .scanThickness = 1.0f, .glow = 0.105f, .glowRadius = 1.75f, .vignette = 0.07f, .grain = 0.07f,
        .contrast = 0.985f})},
    {L"VHS Soft Signal", StaticPreset({
        .intensity = 0.45f, .scanStrength = 0.07f, .scanSpacing = 6.0f, .scanThickness = 1.25f, .glow = 0.12f,
        .glowRadius = 2.0f, .vignette = 0.055f, .grain = 0.1f, .blackLevel = 0.012f, .contrast = 0.965f})},
    {L"CRT Clean Signal", StaticPreset({
        .intensity = 0.33f, .scanStrength = 0.065f, .scanSpacing = 4.0f, .scanThickness = 0.75f,
        .grain = 0.055f})},
};
inline constexpr int BuiltInPresetCount = int(sizeof(BuiltInPresets) / sizeof(BuiltInPresets[0]));
