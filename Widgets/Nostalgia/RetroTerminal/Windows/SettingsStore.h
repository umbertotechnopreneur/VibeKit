// SPDX-License-Identifier: MIT
#pragma once
#include <windows.h>
#include <array>
#include <string>
#include "SettingsSnapshot.h"

inline constexpr wchar_t SettingsPath[] = L"Software\\VibeWare\\RetroTerminal";
enum class SnapshotRead { Invalid = -1, Missing = 0, Ok = 1 };

inline SnapshotRead ReadSettings(EffectSettings& settings, const std::wstring& path = SettingsPath)
{
    HKEY key{};
    const LSTATUS opened = RegOpenKeyExW(HKEY_CURRENT_USER, path.c_str(), 0, KEY_QUERY_VALUE, &key);
    if (opened == ERROR_FILE_NOT_FOUND || opened == ERROR_PATH_NOT_FOUND) return SnapshotRead::Missing;
    if (opened != ERROR_SUCCESS) return SnapshotRead::Invalid;

    SettingsSnapshot snapshot{};
    DWORD bytes = sizeof(snapshot);
    const LSTATUS status = RegGetValueW(key, nullptr, L"Snapshot", RRF_RT_REG_BINARY, nullptr, snapshot.data(), &bytes);
    if (status == ERROR_FILE_NOT_FOUND) {
        // The installer can create subkeys before the first launch.
        DWORD values{};
        const auto query = RegQueryInfoKeyW(key, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr,
            &values, nullptr, nullptr, nullptr, nullptr);
        RegCloseKey(key);
        return query == ERROR_SUCCESS && values == 0 ? SnapshotRead::Missing : SnapshotRead::Invalid;
    }
    RegCloseKey(key);
    if (status != ERROR_SUCCESS || bytes != sizeof(snapshot) || !DecodeSettings(snapshot, settings))
        return SnapshotRead::Invalid;
    return SnapshotRead::Ok;
}

inline bool WriteSettings(const EffectSettings& settings, const std::wstring& path = SettingsPath)
{
    EffectSettings existing;
    if (ReadSettings(existing, path) == SnapshotRead::Invalid) return false;
    const auto snapshot = EncodeSettings(settings);
    return RegSetKeyValueW(HKEY_CURRENT_USER, path.c_str(), L"Snapshot", REG_BINARY,
        snapshot.data(), DWORD(sizeof(snapshot))) == ERROR_SUCCESS;
}
