// SPDX-License-Identifier: MIT
#include "Engine.h"
#include "../../../Shared/DesktopEffectCommands.h"
#include <commctrl.h>

namespace {
constexpr UINT_PTR Subclass = 0x56574546, RecoveryTimer = 0x56574547;
std::unique_ptr<WidgetEngine> engine;
std::wstring initializationError;

LRESULT CALLBACK WindowMessage(HWND window, UINT message, WPARAM wp, LPARAM lp, UINT_PTR, DWORD_PTR)
{
    try {
        if (vibeware::effects::Commands::IsDisableAll(message)) {
            engine->recovery.Cancel();
            engine->SetEnabled(false);
        } else if (vibeware::effects::Commands::IsEnableAll(message)) {
            engine->recovery.Reset();
            if (!engine->Active()) engine->SetEnabled(true);
        } else if (message == WM_TIMER && wp == RecoveryTimer) {
            engine->recovery.Tick(true, engine->Active());
        } else {
            engine->OnMessage(message, wp, lp);
        }
    } catch (...) {
        engine->SetEnabled(false);
        engine->Status(L"The effect stopped after a Windows error. Enable it to retry.");
    }
    return DefSubclassProc(window, message, wp, lp);
}
}

WidgetEngine& CurrentEngine() { return *engine; }

WIDGET_API void WidgetStop()
{
    if (!engine) return;
    KillTimer(engine->window, RecoveryTimer);
    RemoveWindowSubclass(engine->window, WindowMessage, Subclass);
    engine.reset();
}

WIDGET_API int WidgetStart(HWND owner, int startup, NotifyUser notify)
{
    try {
        initializationError = L"Could not initialize the effect.";
        engine = CreateEngine(owner, notify);
        if (engine->Initialize(startup != 0) &&
            SetWindowSubclass(owner, WindowMessage, Subclass, 0) &&
            SetTimer(owner, RecoveryTimer, 1000, nullptr)) {
            engine->recovery.Tick(true, engine->Active());
            return 1;
        }
        if (engine->statusText != L"Stopped") initializationError = engine->statusText;
    } catch (const winrt::hresult_error& error) {
        initializationError = error.message();
    } catch (...) {}
    WidgetStop();
    return 0;
}

WIDGET_API const wchar_t* WidgetStatus()
{
    return engine ? engine->statusText.c_str() : initializationError.c_str();
}

struct Flags { int active, allEnabled, mixed; };
WIDGET_API void WidgetRead(Flags* flags)
{
    const auto& recovery = engine->recovery;
    *flags = {engine->Active(), recovery.AllEnabled(),
        !recovery.Available() || (recovery.AnyEnabled() && !recovery.AllEnabled())};
}

WIDGET_API void WidgetEnable(int enabled)
{
    if (enabled) engine->recovery.Reset(); else engine->recovery.Cancel();
    engine->SetEnabled(enabled != 0);
}
