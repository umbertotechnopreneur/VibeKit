// SPDX-License-Identifier: MIT
#pragma once
#include "../../../Shared/DesktopEffectRecovery.h"
#include <memory>

#define WIDGET_API extern "C" __declspec(dllexport)
using NotifyUser = void (*)(const wchar_t* message);

// The WinUI dispatcher owns the engine, including all timers and exported calls.
struct WidgetEngine
{
    WidgetEngine(HWND owner, const wchar_t* product, NotifyUser notify)
        : window(owner), recovery(product), notifyUser(notify) {}
    virtual ~WidgetEngine() = default;
    virtual bool Initialize(bool startup) = 0;
    virtual bool Active() const = 0;
    virtual void SetEnabled(bool enable) = 0;
    virtual void OnMessage(UINT message, WPARAM wp, LPARAM lp) = 0;
    void Status(const std::wstring& message) { if (statusText != message) statusText = message; }
    void Notify(const wchar_t* message) { notifyUser(message); }

    HWND window;
    vibeware::effects::Recovery recovery;
    NotifyUser notifyUser;
    std::wstring statusText = L"Stopped";
};

std::unique_ptr<WidgetEngine> CreateEngine(HWND window, NotifyUser notify);
WidgetEngine& CurrentEngine();
