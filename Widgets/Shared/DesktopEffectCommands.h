// SPDX-License-Identifier: MIT
#pragma once
#include <windows.h>

namespace vibeware::effects
{
    // Commands are explicit user actions. Receiving apps decide whether they can
    // safely start, and DisableAll must cancel any pending automatic resume.
    struct Commands final
    {
        static UINT EnableMessage() { static const UINT id = RegisterWindowMessageW(L"VibeWare.DesktopEffects.EnableAll.v1"); return id; }
        static UINT DisableMessage() { static const UINT id = RegisterWindowMessageW(L"VibeWare.DesktopEffects.DisableAll.v1"); return id; }
        static bool IsEnableAll(UINT message) { const auto id = EnableMessage(); return id && id == message; }
        static bool IsDisableAll(UINT message) { const auto id = DisableMessage(); return id && id == message; }
        static bool EnableAll() { const auto id = EnableMessage(); return id && PostMessageW(HWND_BROADCAST, id, 0, 0); }
        static bool DisableAll() { const auto id = DisableMessage(); return id && PostMessageW(HWND_BROADCAST, id, 0, 0); }
    };
}
