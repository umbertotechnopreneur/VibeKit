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
    const DWORD version = 2;
    const DWORD amber = DWORD(Palette::Amber);
    assert(RegSetKeyValueW(HKEY_CURRENT_USER, path.c_str(), L"Version", REG_DWORD, &version, sizeof(version)) == ERROR_SUCCESS);
    assert(RegSetKeyValueW(HKEY_CURRENT_USER, path.c_str(), L"Palette", REG_DWORD, &amber, sizeof(amber)) == ERROR_SUCCESS);
    EffectSettings settings;
    assert(ReadSettings(settings, path) && settings.palette == Palette::Amber);
    settings.palette = Palette::Custom;
    settings.intensity = .31f;
    settings.color = {.12f, .34f, .56f};
    const auto expected = EncodeSettings(settings);
    assert(WriteSettings(settings, path));
    EffectSettings restored;
    assert(ReadSettings(restored, path) && EncodeSettings(restored) == expected);
    const DWORD invalidSnapshot = 0;
    assert(RegSetKeyValueW(HKEY_CURRENT_USER, path.c_str(), L"Snapshot", REG_BINARY,
        &invalidSnapshot, sizeof(invalidSnapshot)) == ERROR_SUCCESS);
    assert(!ReadSettings(restored, path)); // Do not fall back to the stale amber v2 value.
    assert(EncodeSettings(restored) == expected);
    std::cout << "PASS native registry migration, complete snapshot replacement and invalid snapshot preservation\n";
}
