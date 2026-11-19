// SPDX-License-Identifier: MIT
// Native lifecycle bridge for the WinUI studio. All calls and the message loop
// must run on the same UI thread. The window owns safety notifications and timers.
#include "Renderer.h"
#include "ControllerState.h"
#include "PerformanceRecovery.h"
#include "GpuJokeNotifications.h"
#include <windows.h>
#include <wtsapi32.h>
#include <commctrl.h>
#include <winrt/base.h>
#include <chrono>
#include <memory>
#include <string>
#include <algorithm>
#include <iterator>

namespace {
using Clock = std::chrono::steady_clock;
constexpr wchar_t EngineClass[] = L"VibeWareRetroEngineMessage";
constexpr wchar_t OverlayClass2[] = L"VibeWareRetroEngineOverlay";
constexpr UINT_PTR RenderTimer = 1, RecoveryTimer = 2;

class HealthSampler
{
public:
    bool Healthy()
    {
        FILETIME idleTime{}, kernelTime{}, userTime{};
        MEMORYSTATUSEX memory{sizeof(memory)};
        if (!GetSystemTimes(&idleTime, &kernelTime, &userTime) || !GlobalMemoryStatusEx(&memory)) {
            haveSample = false;
            return false;
        }
        const auto idle = Convert(idleTime);
        const auto kernel = Convert(kernelTime);
        const auto user = Convert(userTime);
        const bool comparable = haveSample && idle >= lastIdle && kernel >= lastKernel && user >= lastUser;
        const auto totalDelta = comparable ? (kernel - lastKernel) + (user - lastUser) : 0;
        const auto idleDelta = comparable ? idle - lastIdle : 0;
        lastIdle = idle;
        lastKernel = kernel;
        lastUser = user;
        haveSample = true;
        return totalDelta > 0 && idleDelta <= totalDelta && double(idleDelta) / double(totalDelta) > .15
            && memory.ullAvailPhys >= std::max(memory.ullTotalPhys / 10, 512ULL * 1024 * 1024);
    }

private:
    static ULONGLONG Convert(FILETIME time)
    {
        return (ULONGLONG(time.dwHighDateTime) << 32) | time.dwLowDateTime;
    }
    ULONGLONG lastIdle{}, lastKernel{}, lastUser{};
    bool haveSample{};
};

struct Engine {
    HWND window{}, owner{}, overlay{};
    HINSTANCE instance{};
    ControllerState model;
    Renderer renderer;
    TrayIcon tray;
    GpuJokeNotifications gpuJokes;
    PerformanceRecovery recovery;
    HealthSampler health;
    HMONITOR monitor{};
    bool confirmingResume{};
    bool running{}, hotkey{}, dirty{};
    int slowFrames{}, frames{};
    float cpuTotal{};
    Clock::time_point previousTick{}, statsSince{};
    std::wstring status = L"Stopped. Your desktop is unchanged.";
    double Time() const { return std::chrono::duration<double>(Clock::now().time_since_epoch()).count(); }
    void Change() { model.Change(Time()); dirty = true; }
    void Stop(const std::wstring& reason) {
        recovery.Cancel();
        confirmingResume = false;
        KillTimer(window, RecoveryTimer);
        running=false; model.glitches.Cancel(Time(),model.settings); KillTimer(window, RenderTimer);
        if(overlay) ShowWindow(overlay,SW_HIDE);
        renderer.Stop(); if(overlay) { DestroyWindow(overlay); overlay=nullptr; }
        status=reason;
    }
    std::wstring ShaderPath() {
        std::wstring path(32768,L'\0'); DWORD n=GetModuleFileNameW(nullptr,path.data(),DWORD(path.size()));
        if(!n || n>=path.size()) winrt::throw_last_error(); path.resize(n);
        return path.substr(0,path.find_last_of(L"\\/"))+L"\\Shaders\\Retro.hlsl";
    }
    void Start(bool automatic = false) {
        if(running || !hotkey) return;
        if (!automatic) recovery.Reset();
        KillTimer(window, RecoveryTimer);
        confirmingResume = automatic;
        try {
            if(!IsWindow(owner)) throw winrt::hresult_error(E_FAIL,L"The control window is unavailable.");
            if (!automatic) monitor = MonitorFromWindow(owner, MONITOR_DEFAULTTONEAREST);
            MONITORINFO info{sizeof(info)}; if(!GetMonitorInfoW(monitor,&info)) winrt::throw_last_error();
            overlay=CreateWindowExW(WS_EX_LAYERED|WS_EX_TRANSPARENT|WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE|WS_EX_TOPMOST,
                OverlayClass2,L"Retro Terminal screen effect",WS_POPUP,info.rcMonitor.left,info.rcMonitor.top,
                info.rcMonitor.right-info.rcMonitor.left,info.rcMonitor.bottom-info.rcMonitor.top,nullptr,nullptr,instance,nullptr);
            if(!overlay) winrt::throw_last_error();
            if(!SetLayeredWindowAttributes(overlay,0,0,LWA_ALPHA)) winrt::throw_last_error();
            if(!SetWindowDisplayAffinity(overlay,WDA_EXCLUDEFROMCAPTURE)) throw winrt::hresult_error(HRESULT_FROM_WIN32(GetLastError()),L"Capture exclusion unavailable.");
            ShowWindow(overlay,SW_SHOWNOACTIVATE); renderer.Start(overlay,monitor,ShaderPath());
            previousTick=statsSince=Clock::now(); slowFrames=frames=0; cpuTotal=0; dirty=true; model.glitches.Reset(Time(),model.settings);
            if(!SetTimer(window,RenderTimer,34,nullptr)) winrt::throw_last_error(); running=true; status=L"Waiting for a screen frame…";
        } catch(const winrt::hresult_error& e) { Stop(L"Stopped: "+std::wstring(e.message().c_str())); }
        catch(...) { Stop(L"Stopped: the effect could not start. No automatic restart."); }
        if (automatic && !running) tray.Notify(L"CRT restart failed", status.c_str());
    }
    void PauseForPerformance(const wchar_t* reason) {
        Stop(reason);
        health = {};
        if (recovery.Pause(Clock::now()) && SetTimer(window, RecoveryTimer, 1000, nullptr)) {
            status += L" Waiting for system stability.";
        } else {
            recovery.Cancel();
            status += L" Turn CRT on when ready.";
        }
        tray.Notify(L"CRT paused for performance", status.c_str());
    }
    void TickRecovery() {
        if (!recovery.Pending() || !recovery.Ready(Clock::now(), health.Healthy())) return;
        recovery.Resume();
        Start(true);
    }
    void SetRecovery(bool enabled, int seconds) {
        if (recovery.Configure(enabled, seconds)) {
            KillTimer(window, RecoveryTimer);
            status = L"Automatic restart cancelled. CRT is off.";
        }
    }
    void Tick() {
        if(!running) return;
        auto start=Clock::now();
        if(!IsWindow(owner)) { Stop(L"Stopped because the control window closed."); return; }
        if(start-previousTick>std::chrono::milliseconds(250)) { PauseForPerformance(L"The desktop stopped responding."); return; }
        previousTick=start;
        try {
            auto glitch=model.glitches.Sample(Time(),model.settings); bool animated=model.glitches.Changed(glitch);
            bool presented=renderer.Tick(model.settings,glitch,dirty,animated);
            float ms=std::chrono::duration<float,std::milli>(Clock::now()-start).count();
            slowFrames=ms>80?slowFrames+1:0; if(slowFrames>=3) { PauseForPerformance(L"Rendering was repeatedly slow."); return; }
            if(presented) {
                dirty=false;
                if(!SetLayeredWindowAttributes(overlay,0,255,LWA_ALPHA)) winrt::throw_last_error();
                if (confirmingResume) {
                    confirmingResume = false;
                    tray.Notify(L"CRT effect resumed", L"The system is stable and CRT is active again.");
                }
                ++frames;
                cpuTotal+=ms;
            }
            float elapsed=std::chrono::duration<float>(start-statsSince).count();
            if(elapsed>=2) { status=L"Active · "+renderer.Description()+L"\n"+std::to_wstring(frames)+L" presentations / "+std::to_wstring(int(elapsed))+L" s";
                if(frames) status+=L" · CPU submission "+std::to_wstring(cpuTotal/frames).substr(0,4)+L" ms/frame";
                status+=L"\nGPU frame time is not measured here."; statsSince=start; frames=0; cpuTotal=0; }
        } catch(const winrt::hresult_error& e) {
            const bool failedResume = confirmingResume;
            Stop(L"Stopped: "+std::wstring(e.message().c_str()));
            if (failedResume) tray.Notify(L"CRT restart failed", status.c_str());
        } catch(...) {
            const bool failedResume = confirmingResume;
            Stop(L"Stopped after an unexpected renderer error. Enable manually to retry.");
            if (failedResume) tray.Notify(L"CRT restart failed", status.c_str());
        }
    }
};
LRESULT CALLBACK OverlayProc(HWND w,UINT m,WPARAM a,LPARAM b) {
    if(m==WM_NCHITTEST) return HTTRANSPARENT; if(m==WM_MOUSEACTIVATE) return MA_NOACTIVATE;
    if(m==WM_ERASEBKGND) return 1; return DefWindowProcW(w,m,a,b);
}
LRESULT CALLBACK EngineProc(HWND w,UINT m,WPARAM a,LPARAM b) {
    auto* e=reinterpret_cast<Engine*>(GetWindowLongPtrW(w,GWLP_USERDATA));
    if(m==WM_NCCREATE) { e=static_cast<Engine*>(reinterpret_cast<CREATESTRUCTW*>(b)->lpCreateParams); e->window=w; SetWindowLongPtrW(w,GWLP_USERDATA,reinterpret_cast<LONG_PTR>(e)); }
    if(!e) return DefWindowProcW(w,m,a,b);
    if (e->tray.HandleMessage(m,a,b)) return 0;
    switch(m) {
    case WM_TIMER:
        if(a==RenderTimer) e->Tick();
        else if (a==RecoveryTimer) e->TickRecovery();
        else if (a==GpuJokeNotifications::TimerId) e->gpuJokes.Tick(w, e->tray);
        return 0;
    case WM_HOTKEY: if(a==1) e->Stop(L"Emergency stop · Ctrl + Alt + F11. Enable manually to resume."); return 0;
    case WM_WTSSESSION_CHANGE: if(a==WTS_SESSION_LOCK||a==WTS_SESSION_LOGOFF||a==WTS_CONSOLE_DISCONNECT||a==WTS_REMOTE_DISCONNECT) e->Stop(L"Stopped because the desktop session became unavailable."); return 0;
    case WM_POWERBROADCAST: if(a==PBT_APMSUSPEND) e->Stop(L"Stopped before system sleep."); return TRUE;
    case WM_DISPLAYCHANGE: e->Stop(L"Stopped after a display change."); return 0;
    }
    return DefWindowProcW(w,m,a,b);
}
LRESULT CALLBACK OwnerSubclass(HWND w,UINT m,WPARAM a,LPARAM b,UINT_PTR id,DWORD_PTR data) {
    auto* e=reinterpret_cast<Engine*>(data);
    if(m==WM_DPICHANGED) e->Stop(L"Stopped after a display scale change. Enable manually to resume.");
    if(m==WM_DESTROY) { e->Stop(L"Stopped because the control window closed."); RemoveWindowSubclass(w,OwnerSubclass,id); e->owner=nullptr; }
    return DefSubclassProc(w,m,a,b);
}
Engine* E(void* p) { return static_cast<Engine*>(p); }
}
#define API extern "C" __declspec(dllexport)
API void* __cdecl RetroCreate(HWND owner) {
 try {
    auto e=std::make_unique<Engine>(); e->owner=owner;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,reinterpret_cast<LPCWSTR>(&EngineProc),&e->instance);
    WNDCLASSEXW c{sizeof(c)}; c.hInstance=e->instance; c.lpfnWndProc=OverlayProc; c.lpszClassName=OverlayClass2;
    if(!RegisterClassExW(&c)&&GetLastError()!=ERROR_CLASS_ALREADY_EXISTS) return nullptr;
    c.lpfnWndProc=EngineProc; c.lpszClassName=EngineClass;
    if(!RegisterClassExW(&c)&&GetLastError()!=ERROR_CLASS_ALREADY_EXISTS) return nullptr;
    e->window=CreateWindowExW(WS_EX_TOOLWINDOW,EngineClass,L"",WS_POPUP,0,0,0,0,nullptr,nullptr,e->instance,e.get()); if(!e->window) return nullptr;
    e->tray.Initialize(e->window, owner);
    if(!SetWindowSubclass(owner,OwnerSubclass,1,reinterpret_cast<DWORD_PTR>(e.get()))) { DestroyWindow(e->window); return nullptr; }
    e->model.Load(); e->hotkey=RegisterHotKey(e->window,1,MOD_CONTROL|MOD_ALT|MOD_NOREPEAT,VK_F11)!=FALSE;
    if(!e->hotkey) e->status=L"Ctrl + Alt + F11 is already in use. Effect cannot start safely.";
    if(!WTSRegisterSessionNotification(e->window,NOTIFY_FOR_THIS_SESSION)) { e->hotkey=false; e->status=L"Session notifications unavailable. Effect cannot start safely."; }
    e->gpuJokes.SetEnabled(e->window, GpuJokeNotifications::ReadPreference());
    return e.release();
 } catch(...) { return nullptr; }
}
API void __cdecl RetroDestroy(void* p) { if(!p) return; auto* e=E(p); e->Stop(L"Stopped"); e->gpuJokes.SetEnabled(e->window, false); e->tray.Remove(); if(e->owner) RemoveWindowSubclass(e->owner,OwnerSubclass,1); UnregisterHotKey(e->window,1); WTSUnRegisterSessionNotification(e->window); DestroyWindow(e->window); delete e; }
API int __cdecl RetroSave(void* p) { return p&&E(p)->model.Save(); }
API int __cdecl RetroStart(void* p) { if(!p) return 0; E(p)->Start(); return E(p)->running; }
API void __cdecl RetroStop(void* p) { if(p) E(p)->Stop(L"Stopped by you."); }
API int __cdecl RetroRunning(void* p) { return p&&E(p)->running; }
API int __cdecl RetroRecoveryPending(void* p) { return p && E(p)->recovery.Pending(); }
API void __cdecl RetroSetRecovery(void* p, int enabled, int seconds) { if (p) E(p)->SetRecovery(enabled != 0, seconds); }
API int __cdecl RetroSetGpuJokes(void* p, int enabled) {
    if (!p) return 0;
    E(p)->gpuJokes.SetEnabled(E(p)->window, enabled != 0);
    return GpuJokeNotifications::SavePreference(enabled != 0);
}
API int __cdecl RetroKeepInTray(void* p) { return p && E(p)->tray.Keep(); }
API void __cdecl RetroLeaveTray(void* p) { if (p) E(p)->tray.Remove(); }
API int __cdecl RetroTakeTrayAction(void* p) { return p ? static_cast<int>(E(p)->tray.TakeAction()) : 0; }
API float __cdecl RetroGetSetting(void* p,int i) { return p&&i>=0&&size_t(i)<std::size(FloatControls)?E(p)->model.settings.*(FloatControls[i].field):0; }
API void __cdecl RetroSetSetting(void* p,int i,float v) { if(p&&i>=0&&size_t(i)<std::size(FloatControls)) { E(p)->model.settings.*(FloatControls[i].field)=v; E(p)->Change(); } }
#define GETSET(Name,Field,Type) API int __cdecl RetroGet##Name(void* p) { return p?int(E(p)->model.settings.Field):0; } API void __cdecl RetroSet##Name(void* p,int v) { if(p) { E(p)->model.settings.Field=Type(v); E(p)->Change(); } }
GETSET(Palette,palette,Palette)
GETSET(Mask,mask,PhosphorMask)
GETSET(Persistence,persistence,bool)
GETSET(Random,randomGlitches,bool)
GETSET(GlitchTypes,glitchTypes,unsigned)
API float __cdecl RetroGetColor(void* p,int i) { return p&&i>=0&&i<3?E(p)->model.settings.color[i]:0; }
API void __cdecl RetroSetColor(void* p,int i,float v) { if(p&&i>=0&&i<3) { E(p)->model.settings.color[i]=v; E(p)->Change(); } }
API int __cdecl RetroPreset(void* p,int i) { if(!p||i<1||i>6) return 0; auto& m=E(p)->model; if(i<=3) m.settings=BuiltInPreset(i-1); else if(!m.LoadPersonalPreset(i-3)) return 0; E(p)->Change(); return 1; }
API int __cdecl RetroSavePreset(void* p,int i) { return p&&i>=1&&i<=3&&E(p)->model.SavePersonalPreset(i); }
API int __cdecl RetroTryGlitch(void* p) { return p&&E(p)->running&&E(p)->model.glitches.Trigger(E(p)->Time(),E(p)->model.settings); }
API void __cdecl RetroStatus(void* p,wchar_t* buffer,int capacity) { if(buffer&&capacity>0) { buffer[0]=0; if(p) wcsncpy_s(buffer,size_t(capacity),E(p)->status.c_str(),_TRUNCATE); } }
