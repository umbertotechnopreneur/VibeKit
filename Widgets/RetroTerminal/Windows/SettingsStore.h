// SPDX-License-Identifier: MIT
#pragma once
#include <windows.h>
#include <array>
#include <string>
#include "SettingsSnapshot.h"

inline constexpr wchar_t SettingsPath[] = L"Software\\VibeWare\\RetroTerminal";
inline bool ReadLegacySettings(EffectSettings& settings, const std::wstring& path = SettingsPath)
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
    settings.palette = Palette(std::min(read(L"Palette", 0), DWORD(Palette::Custom)));
    settings.mask = PhosphorMask(std::min(read(L"Mask", 0), DWORD(PhosphorMask::Dots)));
    settings.persistence = read(L"Persistence", 0) != 0;
    settings.randomGlitches = read(L"RandomGlitches", 0) != 0;
    settings.glitchTypes = read(L"GlitchTypes", 15) & 15;
    DWORD color = read(L"Color", RGB(51, 255, 107));
    settings.color = {GetRValue(color) / 255.f, GetGValue(color) / 255.f, GetBValue(color) / 255.f};
    settings.Clamp();
    return true;
}
inline bool ReadSettings(EffectSettings& settings, const std::wstring& path = SettingsPath)
{
    SettingsSnapshot snapshot{};
    DWORD bytes = DWORD(sizeof(snapshot));
    const LSTATUS status = RegGetValueW(HKEY_CURRENT_USER, path.c_str(), L"Snapshot", RRF_RT_REG_BINARY,
        nullptr, snapshot.data(), &bytes);
    if (status == ERROR_FILE_NOT_FOUND) return ReadLegacySettings(settings, path);
    // A present but invalid newer snapshot must not silently load stale v2 keys.
    return status == ERROR_SUCCESS && bytes == sizeof(snapshot) && DecodeSettings(snapshot, settings);
}

inline bool WriteSettings(const EffectSettings& settings, const std::wstring& path = SettingsPath)
{
    const auto snapshot = EncodeSettings(settings);
    // One registry operation replaces the whole value, so a failed field write
    // cannot leave a mixture of old/new settings. This does not promise disk
    // durability across power loss; old v2 keys are retained only for migration.
    return RegSetKeyValueW(HKEY_CURRENT_USER, path.c_str(), L"Snapshot", REG_BINARY,
        snapshot.data(), DWORD(sizeof(snapshot))) == ERROR_SUCCESS;
}
