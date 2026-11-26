// SPDX-License-Identifier: MIT
// Native lifecycle bridge for the WinUI studio. All calls and the message loop
// must run on the same UI thread. The window owns safety notifications and timers.
#include "Renderer.h"
#include "ControllerState.h"
#include "BuiltInPresets.h"
#include "PerformanceRecovery.h"
#include "GpuJokeNotifications.h"
#include <windows.h>
#include <wtsapi32.h>
#include <commctrl.h>
#include <dbt.h>
#include <winrt/base.h>
#include <chrono>
#include <cstdint>
#include <memory>
#include <string>
#include <algorithm>
#include <iterator>
#include <utility>
#include <vector>

namespace {
using Clock = std::chrono::steady_clock;
constexpr wchar_t EngineClass[] = L"VibeWareRetroEngineMessage";
constexpr wchar_t OverlayClass2[] = L"VibeWareRetroEngineOverlay";
constexpr UINT RenderMessage = WM_APP + 1;
constexpr UINT_PTR RecoveryTimer = 2;
using TimerTicks = std::chrono::duration<LONGLONG, std::ratio<1, 10000000>>;
struct RenderWakeup { HWND window{}; WPARAM generation{}; };
enum class SystemClose { None, Saved, SaveFailed };
enum class EmergencyShortcutState { Disabled = 0, Registered = 1, Unavailable = 2 };
constexpr int EmergencyHotkeyId = 1;
constexpr wchar_t StoppedStatus[] = L"Stopped. Your desktop is unchanged.";
constexpr wchar_t ShortcutUnavailable[] = L"Ctrl + Alt + F11 is unavailable. Effect cannot start safely.";
constexpr wchar_t SessionUnavailable[] = L"Session notifications unavailable. Effect cannot start safely.";

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

struct DisplaySession
{
    HMONITOR monitor{};
    HWND overlay{};
    Renderer renderer;
    std::wstring name = L"Selected display";
    bool opaque{}, dirty{true};

    explicit DisplaySession(HMONITOR target) : monitor(target) {}

    ~DisplaySession()
    {
        Hide();
        renderer.Stop();
        if (overlay) DestroyWindow(overlay);
    }

    void Hide() noexcept
    {
        renderer.ClearPresentation();
        if (overlay && IsWindowVisible(overlay)) ShowWindow(overlay, SW_HIDE);
        opaque = false;
    }

    void Start(HINSTANCE instance, const std::wstring& shaderPath, int captureFps)
    {
        MONITORINFOEXW info{};
        info.cbSize = sizeof(info);
        if (!GetMonitorInfoW(monitor, &info))
            throw winrt::hresult_error(E_FAIL, L"The display is no longer available.");
        const std::wstring device = info.szDevice;
        const std::wstring prefix = L"\\\\.\\DISPLAY";
        name = device.starts_with(prefix) ? L"Display " + device.substr(prefix.size()) : device;
        const auto& bounds = info.rcMonitor;
        overlay = CreateWindowExW(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
            OverlayClass2, L"Retro Terminal screen effect", WS_POPUP, bounds.left, bounds.top,
            bounds.right - bounds.left, bounds.bottom - bounds.top, nullptr, nullptr, instance, nullptr);
        if (!overlay) winrt::throw_last_error();
        if (!SetLayeredWindowAttributes(overlay, 0, 0, LWA_ALPHA)) winrt::throw_last_error();
        if (!SetWindowDisplayAffinity(overlay, WDA_EXCLUDEFROMCAPTURE))
            throw winrt::hresult_error(HRESULT_FROM_WIN32(GetLastError()), L"Capture exclusion unavailable.");
        ShowWindow(overlay, SW_SHOWNOACTIVATE);
        renderer.Start(overlay, monitor, shaderPath, captureFps);
    }

    bool Tick(const EffectSettings& settings, const GlitchFrame& glitch, bool animated, int effectTarget, int captureFps)
    {
        const bool presented = renderer.Tick(settings, glitch, dirty, animated, effectTarget, captureFps);
        if (renderer.WaitingForInput())
        {
            if (opaque)
            {
                renderer.ClearPresentation();
                if (!SetLayeredWindowAttributes(overlay, 0, 0, LWA_ALPHA)) winrt::throw_last_error();
                opaque = false;
            }
            return false;
        }
        if (!presented) return false;
        if (!opaque)
        {
            if (!SetLayeredWindowAttributes(overlay, 0, 255, LWA_ALPHA)) winrt::throw_last_error();
            opaque = true;
        }
        dirty = false;
        renderer.ConfirmVisiblePresentation();
        return true;
    }
};

struct DisplayFailure
{
    HMONITOR monitor{};
    std::wstring name, reason;
};

struct Engine {
    HWND window{}, owner{};
    HINSTANCE instance{};
    ControllerState model;
    TrayIcon tray;
    GpuJokeNotifications gpuJokes;
    PerformanceRecovery recovery;
    HealthSampler health;
    std::vector<HMONITOR> requestedDisplays;
    std::vector<std::unique_ptr<DisplaySession>> displays;
    std::vector<DisplayFailure> displayFailures;
    std::uint32_t displayTopologyGeneration{};
    bool explicitDisplays{}, armed{}, firstFrame{};
    bool emergencyShortcutEnabled{true}, hotkeyRegistered{}, sessionNotifications{};
    SystemClose systemClose{};
    int slowFrames{}, frames{};
    int effectTarget{};
    float cpuTotal{};
    Clock::time_point previousTick{}, statsSince{};
    int animationFps = 60;
    int captureFps = 30;
    winrt::handle renderTimer;
    PTP_WAIT renderWait{};
    RenderWakeup renderWakeup;
    std::wstring status = StoppedStatus;
    bool Running() const noexcept { return !displays.empty(); }
    int ActiveDisplayCount() const noexcept { return static_cast<int>(displays.size()); }
    double Time() const { return std::chrono::duration<double>(Clock::now().time_since_epoch()).count(); }
    void MarkDirty() { for (const auto& display : displays) display->dirty = true; }
    void Change() { model.Change(Time()); MarkDirty(); }
    EmergencyShortcutState SetEmergencyShortcut(bool enabled) {
        emergencyShortcutEnabled = enabled;
        if (enabled && !hotkeyRegistered) {
            hotkeyRegistered = RegisterHotKey(window, EmergencyHotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_F11) != FALSE;
        } else if (!enabled && hotkeyRegistered) {
            hotkeyRegistered = UnregisterHotKey(window, EmergencyHotkeyId) == FALSE;
        }
        if (enabled && !hotkeyRegistered) {
            if (armed || recovery.Pending()) Stop(ShortcutUnavailable);
            else if (sessionNotifications) status = ShortcutUnavailable;
            return EmergencyShortcutState::Unavailable;
        }
        if (status == ShortcutUnavailable) status = StoppedStatus;
        return enabled ? EmergencyShortcutState::Registered : EmergencyShortcutState::Disabled;
    }
    void Stop(const std::wstring& reason, bool notify = true) {
        tray.CancelPreview();
        const bool wasRunning = Running();
        recovery.Cancel();
        firstFrame = false;
        armed = false;
        KillTimer(window, RecoveryTimer);
        model.glitches.Cancel(Time(), model.settings);
        for (const auto& display : displays) display->Hide();
        StopRenderTimer();
        displays.clear();
        status=reason;
        if (wasRunning && notify) tray.Notify(L"CRT effect off", status.c_str());
    }
    void PrepareForSystemClose() {
        if (systemClose != SystemClose::None) return;
        systemClose = SystemClose::SaveFailed;
        Stop(L"Stopped for Windows servicing.", false);
        gpuJokes.Cancel(window);
        if (model.Save()) systemClose = SystemClose::Saved;
        PostMessageW(owner, WM_CLOSE, 0, 0);
    }
    std::wstring ShaderPath() {
        std::wstring path(32768,L'\0'); DWORD n=GetModuleFileNameW(nullptr,path.data(),DWORD(path.size()));
        if(!n || n>=path.size()) winrt::throw_last_error(); path.resize(n);
        return path.substr(0,path.find_last_of(L"\\/"))+L"\\Shaders\\Retro.hlsl";
    }
    void ScheduleRender(Clock::time_point due)
    {
        // Leave a short input window when a draw has already exceeded its time budget.
        const auto remaining = std::max(due - Clock::now(), std::chrono::ceil<Clock::duration>(std::chrono::milliseconds(1)));
        LARGE_INTEGER delay{};
        delay.QuadPart = -std::chrono::ceil<TimerTicks>(remaining).count();
        SetThreadpoolWait(renderWait, renderTimer.get(), nullptr);
        if (!SetWaitableTimer(renderTimer.get(), &delay, 0, nullptr, nullptr, FALSE)) winrt::throw_last_error();
    }
    void StartRenderTimer()
    {
        renderTimer.attach(CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
            SYNCHRONIZE | TIMER_MODIFY_STATE));
        if (!renderTimer) winrt::throw_last_error();
        // Discard wakeups already posted by an earlier render session.
        renderWakeup.window = window;
        ++renderWakeup.generation;
        renderWait = CreateThreadpoolWait(RenderTimerReady, &renderWakeup, nullptr);
        if (!renderWait) winrt::throw_last_error();
        ScheduleRender(Clock::now());
    }
    static void CALLBACK RenderTimerReady(PTP_CALLBACK_INSTANCE, void* context, PTP_WAIT, TP_WAIT_RESULT)
    {
        // The worker only queues a wakeup. Each render schedules its successor on the owning thread.
        const auto& wakeup = *static_cast<const RenderWakeup*>(context);
        PostMessageW(wakeup.window, RenderMessage, wakeup.generation, 0);
    }
    void StopRenderTimer()
    {
        if (renderWait) {
            SetThreadpoolWait(renderWait, nullptr, nullptr);
            WaitForThreadpoolWaitCallbacks(renderWait, TRUE);
            CloseThreadpoolWait(renderWait);
            renderWait = nullptr;
        }
        renderTimer.close();
    }
    bool IsRequested(HMONITOR display) const
    {
        return std::find(requestedDisplays.begin(), requestedDisplays.end(), display) != requestedDisplays.end();
    }

    void UpdateDisplayStatus(float elapsed = 0)
    {
        const auto visible = std::count_if(displays.begin(), displays.end(), [](const auto& display) { return display->opaque; });
        if (visible == 0) status = L"Waiting for a screen frame…";
        else if (displays.size() == 1 && displayFailures.empty()) status = L"Active · " + displays.front()->renderer.Description();
        else status = L"Active · " + std::to_wstring(visible) + L" of " + std::to_wstring(requestedDisplays.size()) + L" displays";

        status += L"\n";
        if (elapsed > 0)
        {
            status += std::to_wstring(frames) + L" presentations / " + std::to_wstring(int(elapsed)) + L" s";
            if (frames) status += L" · CPU submission " + std::to_wstring(cpuTotal / frames).substr(0, 4) + L" ms/frame";
        }
        for (const auto& display : displays)
            status += L"\n" + display->name + L" · " + (display->opaque ? display->renderer.Description() : L"Waiting for a screen frame");
        for (const auto& failure : displayFailures)
            status += L"\n" + failure.name + L" · " + failure.reason;
    }

    void StopAfterDisplayFailures(bool notify = false)
    {
        std::wstring reason = L"The effect could not run on the selected displays.";
        for (const auto& failure : displayFailures)
            reason += L" " + failure.name + L" · " + failure.reason;
        Stop(reason);
        if (notify) tray.Notify(L"CRT effect off", status.c_str());
    }

    void ReconcileDisplays()
    {
        if (!IsWindow(owner) || !IsWindow(window))
            throw winrt::hresult_error(E_FAIL, L"The control window is unavailable.");
        for (const auto& display : displays)
            if (!IsRequested(display->monitor)) display->Hide();
        std::erase_if(displays, [&](const auto& display) { return !IsRequested(display->monitor); });
        const bool retainedDisplay = Running();
        if (!retainedDisplay) StopRenderTimer();
        if (requestedDisplays.empty())
        {
            firstFrame = false;
            model.glitches.Cancel(Time(), model.settings);
            status = L"Select a display to show the effect.";
            return;
        }

        std::wstring shaderPath;
        for (const auto monitor : requestedDisplays)
        {
            const auto active = std::find_if(displays.begin(), displays.end(), [=](const auto& display) { return display->monitor == monitor; });
            const auto failed = std::find_if(displayFailures.begin(), displayFailures.end(), [=](const auto& failure) { return failure.monitor == monitor; });
            if (active != displays.end() || failed != displayFailures.end()) continue;
            if (shaderPath.empty()) shaderPath = ShaderPath();
            auto display = std::make_unique<DisplaySession>(monitor);
            try { display->Start(instance, shaderPath, captureFps); }
            catch (const winrt::hresult_error& error)
            {
                displayFailures.push_back({monitor, display->name, error.message().c_str()});
                continue;
            }
            displays.push_back(std::move(display));
        }
        if (!Running()) { StopAfterDisplayFailures(); return; }
        if (!retainedDisplay)
        {
            model.glitches.Reset(Time(), model.settings);
            firstFrame = true;
            StartRenderTimer();
        }
        previousTick = statsSince = Clock::now();
        slowFrames = frames = 0;
        cpuTotal = 0;
        UpdateDisplayStatus();
    }

    void Start(bool automatic = false, HMONITOR requestedDisplay = nullptr)
    {
        if (Running() || systemClose != SystemClose::None) return;
        if (!sessionNotifications) { Stop(SessionUnavailable); return; }
        if (emergencyShortcutEnabled && !hotkeyRegistered) { Stop(ShortcutUnavailable); return; }
        try
        {
            if (!automatic)
            {
                recovery.Reset();
                displayFailures.clear();
                if (!explicitDisplays)
                {
                    const auto target = requestedDisplay ? requestedDisplay : MonitorFromWindow(owner, MONITOR_DEFAULTTONEAREST);
                    if (!target) throw winrt::hresult_error(E_FAIL, L"No display is available.");
                    requestedDisplays = {target};
                }
            }
            KillTimer(window, RecoveryTimer);
            armed = true;
            ReconcileDisplays();
        }
        catch (const winrt::hresult_error& error) { Stop(L"Stopped: " + std::wstring(error.message().c_str())); }
        catch (...) { Stop(L"Stopped: the effect could not start. Enable manually to retry."); }
        if (automatic && !armed) tray.Notify(L"CRT restart failed", status.c_str());
    }

    int SetDisplays(const HMONITOR* monitors, int count)
    {
        if (count < 0 || (count && !monitors)) return ActiveDisplayCount();
        try
        {
            std::vector<HMONITOR> selected;
            for (int i = 0; i < count; ++i)
            {
                const auto monitor = monitors[i];
                if (monitor && std::find(selected.begin(), selected.end(), monitor) == selected.end()) selected.push_back(monitor);
            }
            if (explicitDisplays && requestedDisplays == selected) return ActiveDisplayCount();
            explicitDisplays = true;
            requestedDisplays = std::move(selected);
            std::erase_if(displayFailures, [&](const auto& failure) { return !IsRequested(failure.monitor); });
            if (armed && !recovery.Pending()) ReconcileDisplays();
        }
        catch (const winrt::hresult_error& error) { Stop(L"Stopped: " + std::wstring(error.message().c_str())); }
        catch (...) { Stop(L"Stopped: the selected displays could not be updated. Enable manually to retry."); }
        return ActiveDisplayCount();
    }
    void PauseForPerformance(const wchar_t* reason) {
        const bool preview = tray.Previewing();
        Stop(reason, false);
        health = {};
        if (!preview && recovery.Pause(Clock::now()) && SetTimer(window, RecoveryTimer, 1000, nullptr)) {
            armed = true;
            status += L" Waiting for system stability.";
        } else {
            recovery.Cancel();
            status += L" Turn CRT on when ready.";
        }
        tray.Notify(L"CRT effect off", status.c_str());
    }
    void TickRecovery() {
        if (!recovery.Pending() || !recovery.Ready(Clock::now(), health.Healthy())) return;
        recovery.Resume();
        Start(true);
    }
    void SetRecovery(bool enabled, int seconds) {
        if (recovery.Configure(enabled, seconds)) {
            KillTimer(window, RecoveryTimer);
            armed = false;
            status = L"Automatic restart cancelled. CRT is off.";
        }
    }
    void Tick()
    {
        if (!Running()) return;
        const auto start = Clock::now();
        if (!IsWindow(owner)) { Stop(L"Stopped because the control window closed."); return; }
        if (start - previousTick > std::chrono::milliseconds(250)) { PauseForPerformance(L"The desktop stopped responding."); return; }
        previousTick = start;
        try
        {
            const auto glitch = model.glitches.Sample(Time(), model.settings);
            const bool animated = model.glitches.Changed(glitch);
            int presented = 0;
            bool displayChanged = false;
            for (auto current = displays.begin(); current != displays.end();)
            {
                auto& display = **current;
                const bool wasOpaque = display.opaque;
                try
                {
                    if (display.Tick(model.settings, glitch, animated, effectTarget, captureFps)) ++presented;
                    displayChanged |= wasOpaque != display.opaque;
                    ++current;
                }
                catch (const winrt::hresult_error& error)
                {
                    displayFailures.push_back({display.monitor, display.name, error.message().c_str()});
                    current = displays.erase(current);
                    displayChanged = true;
                }
            }
            if (!Running()) { StopAfterDisplayFailures(true); return; }

            const float ms = std::chrono::duration<float, std::milli>(Clock::now() - start).count();
            slowFrames = ms > 80 ? slowFrames + 1 : 0;
            if (slowFrames >= 3) { PauseForPerformance(L"Rendering was repeatedly slow."); return; }
            if (presented)
            {
                frames += presented;
                cpuTotal += ms;
                if (firstFrame)
                {
                    firstFrame = false;
                    tray.Notify(L"CRT effect on", L"The screen effect is active.");
                }
            }
            const float elapsed = std::chrono::duration<float>(start - statsSince).count();
            if (elapsed >= 2)
            {
                UpdateDisplayStatus(elapsed);
                statsSince = start;
                frames = 0;
                cpuTotal = 0;
            }
            else if (displayChanged) UpdateDisplayStatus();
            const auto interval = std::chrono::ceil<Clock::duration>(std::chrono::duration<double>(1.0 / animationFps));
            ScheduleRender(start + interval);
        }
        catch (const winrt::hresult_error& error) { Stop(L"Stopped: " + std::wstring(error.message().c_str())); }
        catch (...) { Stop(L"Stopped after an unexpected renderer error. Enable manually to retry."); }
    }
};
LRESULT CALLBACK OverlayProc(HWND w,UINT m,WPARAM a,LPARAM b) {
    if(m==WM_CLOSE) return 0; // The engine owns the overlay lifetime.
    if(m==WM_NCHITTEST) return HTTRANSPARENT; if(m==WM_MOUSEACTIVATE) return MA_NOACTIVATE;
    if(m==WM_ERASEBKGND) return 1; return DefWindowProcW(w,m,a,b);
}
LRESULT CALLBACK EngineProc(HWND w,UINT m,WPARAM a,LPARAM b) {
    auto* e=reinterpret_cast<Engine*>(GetWindowLongPtrW(w,GWLP_USERDATA));
    if(m==WM_NCCREATE) { e=static_cast<Engine*>(reinterpret_cast<CREATESTRUCTW*>(b)->lpCreateParams); e->window=w; SetWindowLongPtrW(w,GWLP_USERDATA,reinterpret_cast<LONG_PTR>(e)); }
    if(!e) return DefWindowProcW(w,m,a,b);
    if (e->tray.IsDisableAll(m)) {
        e->tray.CancelPendingStart();
        e->Stop(L"All effects disabled by you.");
        return 0;
    }
    if (e->tray.HandleMessage(m,a,b,e->armed,e->Running(),e->status)) return 0;
    switch(m) {
    case WM_CLOSE:
        // A servicing fallback can broadcast WM_CLOSE to hidden top-level windows.
        // Keep the timer and tray target alive until RetroDestroy.
        return 0;
    case WM_QUERYENDSESSION: if(b & ENDSESSION_CLOSEAPP) return TRUE; break;
    case WM_ENDSESSION:
        if(b & ENDSESSION_CLOSEAPP) {
            if(a) e->PrepareForSystemClose();
            return 0;
        }
        break;
    case RenderMessage:
        if (a == e->renderWakeup.generation) e->Tick();
        return 0;
    case WM_TIMER:
        if (a == TrayIcon::PreviewTimer && e->tray.PreviewDue()) e->Stop(L"Preview finished");
        else if (a==RecoveryTimer) e->TickRecovery();
        else if (a==GpuJokeNotifications::TimerId) e->gpuJokes.Tick(w, e->tray);
        return 0;
    case WM_HOTKEY:
        if (a == EmergencyHotkeyId && e->emergencyShortcutEnabled && e->hotkeyRegistered)
            e->Stop(L"Emergency stop · Ctrl + Alt + F11. Enable manually to resume.");
        return 0;
    case WM_WTSSESSION_CHANGE: if(a==WTS_SESSION_LOCK||a==WTS_SESSION_LOGOFF||a==WTS_CONSOLE_DISCONNECT||a==WTS_REMOTE_DISCONNECT) e->Stop(L"Stopped because the desktop session became unavailable."); return 0;
    case WM_POWERBROADCAST: if(a==PBT_APMSUSPEND) e->Stop(L"Stopped before system sleep."); return TRUE;
    case WM_DISPLAYCHANGE:
        ++e->displayTopologyGeneration;
        e->Stop(L"Stopped after a display change.");
        return 0;
    case WM_DEVICECHANGE:
        if (a == DBT_DEVNODES_CHANGED) ++e->displayTopologyGeneration;
        break;
    }
    return DefWindowProcW(w,m,a,b);
}
LRESULT CALLBACK OwnerSubclass(HWND w,UINT m,WPARAM a,LPARAM b,UINT_PTR id,DWORD_PTR data) {
    auto* e=reinterpret_cast<Engine*>(data);
    if(m==WM_QUERYENDSESSION && (b & ENDSESSION_CLOSEAPP)) return TRUE;
    if(m==WM_ENDSESSION && (b & ENDSESSION_CLOSEAPP)) {
        if(a) e->PrepareForSystemClose();
        return 0;
    }
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
    e->model.Load();
    e->sessionNotifications = WTSRegisterSessionNotification(e->window, NOTIFY_FOR_THIS_SESSION) != FALSE;
    if (!e->sessionNotifications) e->status = SessionUnavailable;
    e->SetEmergencyShortcut(true);
    if (GpuJokeNotifications::ReadPreference()) e->gpuJokes.Start(e->window);
    return e.release();
 } catch(...) { return nullptr; }
}
API void __cdecl RetroDestroy(void* p) {
    if (!p) return;
    auto* e = E(p);
    e->Stop(L"Stopped");
    e->gpuJokes.Cancel(e->window);
    e->tray.Remove();
    if (e->owner) RemoveWindowSubclass(e->owner, OwnerSubclass, 1);
    if (e->hotkeyRegistered) UnregisterHotKey(e->window, EmergencyHotkeyId);
    if (e->sessionNotifications) WTSUnRegisterSessionNotification(e->window);
    DestroyWindow(e->window);
    delete e;
}
API int __cdecl RetroSave(void* p) { return p&&E(p)->model.Save(); }
API int __cdecl RetroStart(void* p, HMONITOR monitor) { if(!p) return 0; E(p)->Start(false, monitor); return E(p)->Running(); }
API int __cdecl RetroSetDisplays(void* p, const HMONITOR* monitors, int count) { return p ? E(p)->SetDisplays(monitors, count) : 0; }
API int __cdecl RetroActiveDisplayCount(void* p) { return p ? E(p)->ActiveDisplayCount() : 0; }
API int __cdecl RetroIsArmed(void* p) { return p && E(p)->armed; }
API std::uint32_t __cdecl RetroDisplayTopologyGeneration(void* p) { return p ? E(p)->displayTopologyGeneration : 0; }
API void __cdecl RetroPreview(void* p) {
    if (!p || E(p)->armed) return;
    auto& engine = *E(p);
    engine.Start();
    if (!engine.armed) return;
    if (!engine.Running()) engine.Stop(L"Select a display before starting the preview.");
    else if (!engine.tray.BeginPreview()) engine.Stop(L"Could not schedule the end of the preview.");
}
API void __cdecl RetroStop(void* p) { if(p) E(p)->Stop(L"Stopped by you."); }
API int __cdecl RetroSetEmergencyShortcut(void* p, int enabled) {
    return static_cast<int>(p ? E(p)->SetEmergencyShortcut(enabled != 0) : EmergencyShortcutState::Unavailable);
}
API int __cdecl RetroSystemClose(void* p) { return p ? static_cast<int>(E(p)->systemClose) : 0; }
API int __cdecl RetroRunning(void* p) { return p && E(p)->Running(); }
API int __cdecl RetroRecoveryPending(void* p) { return p && E(p)->recovery.Pending(); }
API void __cdecl RetroSetRecovery(void* p, int enabled, int seconds) { if (p) E(p)->SetRecovery(enabled != 0, seconds); }
API void __cdecl RetroSetFrameLimits(void* p, int animationFps, int captureFps)
{
    if (!p) return;
    E(p)->animationFps = std::clamp(animationFps, 15, 120);
    E(p)->captureFps = std::clamp(captureFps, 5, 60);
}
API void __cdecl RetroSetTarget(void* p, int target) {
    if (!p || target < 0 || target > 2) return;
    E(p)->effectTarget = target;
    E(p)->MarkDirty();
}
API const wchar_t* __cdecl RetroGpuJoke(unsigned index) { return GpuJokeNotifications::Caption(index); }
API int __cdecl RetroSetGpuJokes(void* p, int enabled) {
    if (!p) return 0;
    if (!enabled) E(p)->gpuJokes.Cancel(E(p)->window);
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
API int __cdecl RetroBuiltInPresetCount() { return BuiltInPresetCount; }
API const wchar_t* __cdecl RetroBuiltInPresetName(int index)
{
    return index >= 0 && index < BuiltInPresetCount ? BuiltInPresets[index].name : nullptr;
}
API int __cdecl RetroApplyBuiltInPreset(void* p, int index)
{
    if (!p || index < 0 || index >= BuiltInPresetCount) return 0;
    E(p)->model.settings = BuiltInPresets[index].settings;
    E(p)->Change();
    return 1;
}
API int __cdecl RetroPreset(void* p, int index)
{
    if (!p || index < 1 || index > 6) return 0;
    if (index <= 3) return RetroApplyBuiltInPreset(p, index - 1);
    if (!E(p)->model.LoadPersonalPreset(index - 3)) return 0;
    E(p)->Change();
    return 1;
}
API int __cdecl RetroSavePreset(void* p,int i) { return p&&i>=1&&i<=3&&E(p)->model.SavePersonalPreset(i); }
API int __cdecl RetroTryGlitch(void* p) { return p && E(p)->Running() && E(p)->model.glitches.Trigger(E(p)->Time(), E(p)->model.settings); }
API void __cdecl RetroStatus(void* p,wchar_t* buffer,int capacity) { if(buffer&&capacity>0) { buffer[0]=0; if(p) wcsncpy_s(buffer,size_t(capacity),E(p)->status.c_str(),_TRUNCATE); } }
