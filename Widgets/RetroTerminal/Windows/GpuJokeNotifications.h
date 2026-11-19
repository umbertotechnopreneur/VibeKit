// SPDX-License-Identifier: MIT
#pragma once
#include "SettingsStore.h"
#include "TrayIcon.h"
#include <random>

class GpuJokeNotifications final
{
public:
    static constexpr UINT_PTR TimerId = 3;

    static bool ReadPreference()
    {
        DWORD value{}, bytes = sizeof(value);
        return RegGetValueW(HKEY_CURRENT_USER, SettingsPath, L"ShowGpuJokes",
            RRF_RT_REG_DWORD, nullptr, &value, &bytes) != ERROR_SUCCESS || value != 0;
    }

    static bool SavePreference(bool enabled)
    {
        const DWORD value = enabled ? 1 : 0;
        return RegSetKeyValueW(HKEY_CURRENT_USER, SettingsPath, L"ShowGpuJokes",
            REG_DWORD, &value, sizeof(value)) == ERROR_SUCCESS;
    }

    static const wchar_t* Caption(unsigned index) { return jokes[index % jokes.size()]; }

    void Start(HWND window)
    {
        std::minstd_rand random{static_cast<unsigned>(GetTickCount64() ^ GetCurrentProcessId())};
        Schedule(window, std::uniform_int_distribution<UINT>(120'000, 240'000)(random));
    }

    void Cancel(HWND window)
    {
        KillTimer(window, TimerId);
        due = 0;
    }

    void Tick(HWND window, TrayIcon& tray)
    {
        // A WM_TIMER already queued before KillTimer or SetTimer may still arrive.
        if (!due) return;
        const auto now = GetTickCount64();
        if (now < due) {
            Schedule(window, static_cast<UINT>(due - now));
            return;
        }
        Cancel(window);
        tray.Notify(L"Retro Terminal · That's a joke", Caption(0), true);
    }

private:
    static constexpr std::array<const wchar_t*, 5> jokes{
        L"Family time won. Gaming time lost. The GPU makes scanlines now.",
        L"Imagine a $1,000 GPU. Now ask it to look like 1985.",
        L"Built for gaming. Promoted to desktop decoration.",
        L"All this graphics power, just to bring back scanlines.",
        L"No time to game. Plenty of time to adjust the phosphor.",
    };
    ULONGLONG due{};

    void Schedule(HWND window, UINT delay)
    {
        KillTimer(window, TimerId);
        const auto deadline = GetTickCount64() + delay;
        due = SetTimer(window, TimerId, delay, nullptr) ? deadline : 0;
    }
};
