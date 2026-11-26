// SPDX-License-Identifier: MIT
#include "../SettingsStore.h"
#include <cassert>
#include <iostream>

int main()
{
    const std::wstring path = L"Software\\VibeWare\\RetroTerminalTests\\" +
        std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64());
    struct Cleanup {
        const std::wstring& path;
        ~Cleanup() { RegDeleteTreeW(HKEY_CURRENT_USER, path.c_str()); }
    } cleanup{path};
    EffectSettings settings;
    assert(ReadSettings(settings, path) == SnapshotRead::Missing);
    const DWORD marker = 1;
    assert(RegSetKeyValueW(HKEY_CURRENT_USER, (path + L"\\Installer").c_str(), L"Installed",
        REG_DWORD, &marker, sizeof(marker)) == ERROR_SUCCESS);
    assert(ReadSettings(settings, path) == SnapshotRead::Missing);
    const auto preset = path + L"\\Preset1";
    assert(ReadSettings(settings, preset) == SnapshotRead::Missing);
    assert(RegSetKeyValueW(HKEY_CURRENT_USER, preset.c_str(), L"Version",
        REG_DWORD, &marker, sizeof(marker)) == ERROR_SUCCESS);
    assert(ReadSettings(settings, preset) == SnapshotRead::Invalid);
    assert(!WriteSettings(settings, preset));
    assert(ReadSettings(settings, preset) == SnapshotRead::Invalid);
    settings.palette = Palette::Custom;
    settings.intensity = .31f;
    settings.color = {.12f, .34f, .56f};
    const auto expected = EncodeSettings(settings);
    assert(WriteSettings(settings, path));
    EffectSettings restored;
    assert(ReadSettings(restored, path) == SnapshotRead::Ok && EncodeSettings(restored) == expected);
    const DWORD invalidSnapshot = 0;
    assert(RegSetKeyValueW(HKEY_CURRENT_USER, path.c_str(), L"Snapshot", REG_BINARY,
        &invalidSnapshot, sizeof(invalidSnapshot)) == ERROR_SUCCESS);
    assert(ReadSettings(restored, path) == SnapshotRead::Invalid);
    assert(EncodeSettings(restored) == expected);
    assert(!WriteSettings(settings, path));
    DWORD preserved = 1, bytes = sizeof(preserved);
    assert(RegGetValueW(HKEY_CURRENT_USER, path.c_str(), L"Snapshot", RRF_RT_REG_BINARY,
        nullptr, &preserved, &bytes) == ERROR_SUCCESS && bytes == sizeof(preserved) && preserved == invalidSnapshot);
    std::cout << "PASS native registry first-run settings, current snapshots and invalid data preservation\n";
}
