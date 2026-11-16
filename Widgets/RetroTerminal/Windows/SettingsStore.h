// SPDX-License-Identifier: MIT
#pragma once
#include <windows.h>
#include <array>
#include <string>
#include "EffectSettings.h"

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
inline constexpr wchar_t SettingsPath[] = L"Software\\VibeWare\\RetroTerminal";
inline bool ReadSettings(EffectSettings& settings, const std::wstring& path = SettingsPath)
{
    DWORD version = 0, bytes = sizeof(version);
    if (RegGetValueW(HKEY_CURRENT_USER, path.c_str(), L"Version", RRF_RT_REG_DWORD, nullptr, &version, &bytes) != ERROR_SUCCESS || version != 2)
        return false;
    for (const auto& field : FloatControls) {
        float value = settings.*(field.field); bytes = sizeof(value);
        if (RegGetValueW(HKEY_CURRENT_USER, path.c_str(), field.key, RRF_RT_REG_BINARY, nullptr, &value, &bytes) == ERROR_SUCCESS && bytes == sizeof(value))
            settings.*(field.field) = value;
    }
    auto read = [&](const wchar_t* key, DWORD fallback) {
        DWORD value = fallback; bytes = sizeof(value);
        if (RegGetValueW(HKEY_CURRENT_USER, path.c_str(), key, RRF_RT_REG_DWORD, nullptr, &value, &bytes) != ERROR_SUCCESS) return fallback;
        return value;
    };
    settings.palette = int(std::min(read(L"Palette", 0), DWORD(3)));
    settings.mask = int(std::min(read(L"Mask", 0), DWORD(2)));
    settings.persistence = read(L"Persistence", 0) != 0;
    settings.randomGlitches = read(L"RandomGlitches", 0) != 0;
    settings.glitchTypes = read(L"GlitchTypes", 15) & 15;
    DWORD color = read(L"Color", RGB(51, 255, 107));
    settings.color = {GetRValue(color) / 255.f, GetGValue(color) / 255.f, GetBValue(color) / 255.f};
    settings.Clamp();
    return true;
}
inline bool WriteSettings(const EffectSettings& settings, const std::wstring& path = SettingsPath)
{
    bool success = true;
    auto write = [&](const wchar_t* key, DWORD value) {
        success &= RegSetKeyValueW(HKEY_CURRENT_USER, path.c_str(), key, REG_DWORD, &value, sizeof(value)) == ERROR_SUCCESS;
    };
    for (const auto& field : FloatControls) {
        const float value = settings.*(field.field);
        success &= RegSetKeyValueW(HKEY_CURRENT_USER, path.c_str(), field.key, REG_BINARY, &value, sizeof(value)) == ERROR_SUCCESS;
    }
    write(L"Palette", DWORD(settings.palette)); write(L"Mask", DWORD(settings.mask));
    write(L"Persistence", settings.persistence); write(L"RandomGlitches", settings.randomGlitches);
    write(L"GlitchTypes", settings.glitchTypes);
    write(L"Color", RGB(int(settings.color[0] * 255), int(settings.color[1] * 255), int(settings.color[2] * 255)));
    write(L"Version", 2);
    return success;
}
