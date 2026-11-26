// SPDX-License-Identifier: MIT
#pragma once
#include "SettingsStore.h"
#include <string>

// State and persistence are independent of the native presentation controls.
// The owning UI thread supplies monotonic time to the glitch scheduler.
struct ControllerState
{
    EffectSettings settings;
    GlitchSchedule glitches;

    SnapshotRead Load() { return ReadSettings(settings); }
    bool Save() const { return WriteSettings(settings); }
    void Change(double now)
    {
        settings.Clamp();
        glitches.Cancel(now, settings);
    }
    SnapshotRead LoadPersonalPreset(int slot)
    {
        return ReadSettings(settings, std::wstring(SettingsPath) + L"\\Preset" + std::to_wstring(slot));
    }
    bool SavePersonalPreset(int slot) const
    {
        return WriteSettings(settings, std::wstring(SettingsPath) + L"\\Preset" + std::to_wstring(slot));
    }
};
