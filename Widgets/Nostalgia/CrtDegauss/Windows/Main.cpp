// SPDX-License-Identifier: MIT
#include "MagnetizationCycle.h"
#include "PreviewAudio.h"
#include "Renderer.h"
#include "../../Shared/Native/Engine.h"
#include "../../../Shared/MonitorEffects.h"
#include <commctrl.h>
#include <mmsystem.h>
#include <shellapi.h>
#include <wtsapi32.h>
#include <winrt/base.h>
#include <chrono>
#include <cmath>
#include <cwchar>
#include <filesystem>
#include <string>
#include <vector>

namespace
{
    constexpr wchar_t AppName[] = L"CRT Degauss";
    constexpr wchar_t RegistryPath[] = L"Software\\VibeWare\\CrtDegauss";
    constexpr UINT RenderTimer = 0x5644524E, PanicHotkey = 0x4447;
    constexpr int CaptureFps = 30;
    constexpr DWORD PatchPresetCount = 6;
    constexpr double PatchPreviewSeconds = 3.0;
    using Clock = std::chrono::steady_clock;
    namespace desktop = vibeware::desktop_effect;

    // Maintain a single overlay order when a sibling effect starts on this monitor.
    void PositionAboveDesktopEffects(HWND overlay, HMONITOR monitor, const RECT& bounds)
    {
        for (HWND sibling = GetWindow(overlay, GW_HWNDPREV); sibling; sibling = GetWindow(sibling, GW_HWNDPREV)) {
            wchar_t title[64]{};
            GetWindowTextW(sibling, title, ARRAYSIZE(title));
            if (std::wcscmp(title, L"Retro Terminal screen effect") && std::wcscmp(title, L"Water Ripple screen effect"))
                continue;
            if (!desktop::IsEffectWindow(sibling, monitor, bounds)) continue;
            const auto vignette = desktop::FindVignetteOverlay(monitor, bounds);
            const auto position = vignette && desktop::IsWindowAbove(vignette, sibling) ? vignette : HWND_TOPMOST;
            if (!SetWindowPos(overlay, position, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER))
                winrt::throw_last_error();
            return;
        }
    }

    double Seconds()
    {
        return std::chrono::duration<double>(Clock::now().time_since_epoch()).count();
    }

    std::filesystem::path AppDirectory()
    {
        std::wstring filename(32768, L'\0');
        const auto length = GetModuleFileNameW(nullptr, filename.data(), static_cast<DWORD>(filename.size()));
        if (!length || length >= filename.size())
            throw winrt::hresult_error(E_FAIL, L"The application directory is unavailable.");
        filename.resize(length);
        return std::filesystem::path(filename).parent_path();
    }

    struct SoundProfile { const wchar_t* title; const wchar_t* path; };
    constexpr SoundProfile SoundProfiles[]{
        {L"Classic", L"degauss.wav"},
        {L"Dry relay", L"sounds/dry-relay.wav"},
        {L"Low coil", L"sounds/low-coil.wav"},
        {L"Desktop monitor", L"sounds/desktop-monitor.wav"},
        {L"PAL television", L"sounds/pal-television.wav"},
        {L"Long buzz", L"sounds/long-buzz.wav"},
    };

    struct Settings
    {
        DWORD minutes = degauss::DefaultMinutes;
        DWORD patches = 1;
        DWORD sound = 1;
        DWORD soundVariant = 0;

        bool Valid() const
        {
            return minutes >= 1 && minutes <= degauss::MaximumMinutes && patches >= 1 &&
                patches <= PatchPresetCount && sound <= 1 && soundVariant < std::size(SoundProfiles);
        }

        bool Load()
        {
            Settings saved;
            DWORD size = sizeof(saved);
            const auto result = RegGetValueW(HKEY_CURRENT_USER, RegistryPath, L"Preferences", RRF_RT_REG_BINARY,
                nullptr, &saved, &size);
            if (result == ERROR_FILE_NOT_FOUND || result == ERROR_PATH_NOT_FOUND) return true;
            if (result != ERROR_SUCCESS || size != sizeof(saved) || !saved.Valid()) return false;
            *this = saved;
            return true;
        }

        bool Save() const
        {
            HKEY key{};
            if (RegCreateKeyExW(HKEY_CURRENT_USER, RegistryPath, 0, nullptr, 0, KEY_SET_VALUE,
                    nullptr, &key, nullptr) != ERROR_SUCCESS)
                return false;
            const auto result = RegSetValueExW(key, L"Preferences", 0, REG_BINARY,
                reinterpret_cast<const BYTE*>(this), sizeof(*this));
            RegCloseKey(key);
            return result == ERROR_SUCCESS;
        }
    };

    struct Application final : WidgetEngine
    {
        Application(HWND window, NotifyUser notify) : WidgetEngine(window, AppName, notify) {}
        HINSTANCE instance = GetModuleHandleW(nullptr);
        HWND overlay{};
        HWND pulsePresenter{};
        HMONITOR targetMonitor{};
        RECT targetBounds{};
        Settings settings;
        Renderer renderer;
        degauss::Cycle cycle;
        std::mt19937 random{std::random_device{}()};
        float patchSeed{};
        std::array<float, 4> drawn{};
        std::vector<unsigned char> previewSound;
        Clock::time_point previousTick{};
        Clock::time_point nextPlacementCheck{};
        Clock::time_point pulseDeadline{};
        Clock::time_point patchPreviewStarted{};
        int slowFrames{};
        bool running{}, opaque{}, rendered{}, hotkey{}, sessionNotifications{}, overlayRegistered{}, pulseAcknowledged{};
        bool Initialize(bool) override;
        ~Application() override
        {
            StopEffect(L"Stopped");
            if (hotkey) UnregisterHotKey(window, PanicHotkey);
            if (sessionNotifications) WTSUnRegisterSessionNotification(window);
            if (overlayRegistered) UnregisterClassW(desktop::DegaussOverlayClass, instance);
        }
        bool Active() const override { return running; }
        void SetEnabled(bool enabled) override
        {
            if (enabled) Start();
            else StopEffect(L"Stopped");
        }

        void Start(bool previewOnly = false)
        {
            if (running) return;
            if (!hotkey || !sessionNotifications) {
                Status(L"The emergency shortcut and session notifications must be available.");
                return;
            }
            if (overlay) StopEffect(L"Stopped");
            try
            {
                const auto monitor = MonitorFromPoint(POINT{0, 0}, MONITOR_DEFAULTTOPRIMARY);
                MONITORINFO info{sizeof(info)};
                if (!GetMonitorInfoW(monitor, &info)) winrt::throw_last_error();
                const auto& bounds = info.rcMonitor;
                overlay = CreateWindowExW(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW |
                        WS_EX_NOACTIVATE | WS_EX_TOPMOST, desktop::DegaussOverlayClass, AppName, WS_POPUP,
                    bounds.left, bounds.top, bounds.right - bounds.left, bounds.bottom - bounds.top,
                    nullptr, nullptr, instance, nullptr);
                if (!overlay || !SetLayeredWindowAttributes(overlay, 0, 0, LWA_ALPHA) ||
                    !SetWindowDisplayAffinity(overlay, WDA_EXCLUDEFROMCAPTURE))
                    throw winrt::hresult_error(E_FAIL, L"Could not create a capture-excluded desktop overlay.");
                ShowWindow(overlay, SW_SHOWNOACTIVATE);
                renderer.Start(overlay, monitor, (AppDirectory() / L"Shaders" / L"Degauss.hlsl").wstring(),
                    CaptureFps, false);
                if (!SetTimer(window, RenderTimer, 34, nullptr)) winrt::throw_last_error();
                targetMonitor = monitor;
                targetBounds = bounds;
                nextPlacementCheck = {};
                slowFrames = 0;
                previousTick = Clock::now();
                cycle.Start(Seconds());
                patchSeed = static_cast<float>(std::uniform_int_distribution<int>(1, 10000)(random));
                rendered = false;
                running = !previewOnly;
                Status(L"Preparing color patches…");
            }
            catch (const winrt::hresult_error& error) { StopEffect(error.message().c_str(), true); }
            catch (...) { StopEffect(L"Could not start the effect. Enable it to retry.", true); }
        }

        void StopEffect(const std::wstring& reason, bool notify = false)
        {
            KillTimer(window, RenderTimer);
            running = opaque = false;
            patchPreviewStarted = {};
            if (cycle.Degaussing() || !previewSound.empty()) PlaySoundW(nullptr, nullptr, 0);
            previewSound.clear();
            cycle.Start(0);
            FinishPulse();
            if (overlay) ShowWindow(overlay, SW_HIDE);
            renderer.Stop();
            if (overlay) DestroyWindow(overlay);
            overlay = nullptr;
            Status(reason);
            if (notify) Notify(reason.c_str());
        }

        void RunDegauss()
        {
            if (!running || cycle.Degaussing()) return;
            const auto upstream = desktop::FindPulsePresenter(targetMonitor, targetBounds);
            const auto vignette = desktop::ReadVignette(targetMonitor, targetBounds);
            if ((upstream.window && !upstream.acceptsPulse) || (vignette.window && !vignette.color[3])) {
                Status(L"An active effect on this display does not provide a frame for Degauss. Update it or wait until it is ready.");
                Notify(statusText.c_str());
                return;
            }
            if (cycle.Degauss(Seconds(), settings.minutes)) {
                pulsePresenter = upstream.window;
                pulseDeadline = Clock::now() + std::chrono::milliseconds(575);
                patchPreviewStarted = {};
                Status(L"Degaussing…");
                if (settings.sound) PlaySelectedSound();
            }
        }

        void FinishPulse() noexcept
        {
            if (pulsePresenter) {
                desktop::ClearPulse(overlay);
                rendered = false;
            }
            pulsePresenter = nullptr;
            pulseAcknowledged = false;
        }

        // The upstream presenter supplies the composited pixels. Our overlay must stay transparent.
        void PresentThroughPeer(const desktop::PulseValues& pulse, Clock::time_point now)
        {
            if (opaque) {
                if (!SetLayeredWindowAttributes(overlay, 0, 0, LWA_ALPHA)) winrt::throw_last_error();
                opaque = false;
            }
            if (!SetPropW(overlay, desktop::PulseSource, reinterpret_cast<HANDLE>(1)) ||
                !desktop::RequestPulse(overlay, pulsePresenter, pulse))
                winrt::throw_last_error();
            if (desktop::IsRecentPulse(pulsePresenter)) pulseAcknowledged = true;
            else if (pulseAcknowledged || now >= pulseDeadline || !IsWindowVisible(pulsePresenter)) {
                StopEffect(L"The presenter could not draw the combined degauss pulse. Enable the effect to retry.", true);
            }
        }

        void PlaySelectedSound(int adjustment = 0)
        {
            try {
                const auto path = AppDirectory() / SoundProfiles[settings.soundVariant].path;
                if (adjustment == 0) {
                    if (PlaySoundW(path.c_str(), nullptr, SND_FILENAME | SND_ASYNC | SND_NODEFAULT)) return;
                } else if (auto wav = degauss::AmplifyPreview(path, adjustment); !wav.empty()) {
                    // Keep the PCM buffer alive for asynchronous playback. Stop before replacing it.
                    PlaySoundW(nullptr, nullptr, 0);
                    previewSound.swap(wav);
                    if (PlaySoundW(reinterpret_cast<LPCWSTR>(previewSound.data()), nullptr,
                        SND_MEMORY | SND_ASYNC | SND_NODEFAULT)) return;
                }
            } catch (const winrt::hresult_error&) {}
            Status(L"Could not play the selected degauss sound.");
            Notify(statusText.c_str());
        }

        void PreviewPatches()
        {
            if (cycle.Degaussing()) return;
            if (!overlay) Start(true);
            if (!overlay) return;
            patchPreviewStarted = Clock::now();
            Status(L"Previewing color patches…");
        }

        void Tick()
        {
            if (!overlay) return;
            const auto start = Clock::now();
            if (start - previousTick > std::chrono::milliseconds(450)) {
                StopEffect(L"The desktop stopped responding. Enable the effect to retry.", true);
                return;
            }
            previousTick = start;
            try
            {
                if (!pulsePresenter && start >= nextPlacementCheck) {
                    PositionAboveDesktopEffects(overlay, targetMonitor, targetBounds);
                    nextPlacementCheck = start + desktop::DiscoveryInterval;
                }
                const bool wasDegaussing = cycle.Degaussing();
                const auto frame = cycle.Sample(Seconds(), settings.minutes);
                if (wasDegaussing && !cycle.Degaussing()) {
                    FinishPulse();
                    patchSeed = static_cast<float>(std::uniform_int_distribution<int>(1, 10000)(random));
                }
                double amount = frame.amount;
                if (patchPreviewStarted != Clock::time_point{}) {
                    const double elapsed = std::chrono::duration<double>(start - patchPreviewStarted).count();
                    if (elapsed < PatchPreviewSeconds) {
                        const double blend = std::clamp(std::min(elapsed / 0.18, (PatchPreviewSeconds - elapsed) / 0.45), 0.0, 1.0);
                        amount = std::lerp(amount, 1.0, blend);
                    } else {
                        patchPreviewStarted = {};
                        if (!running) {
                            StopEffect(L"Stopped");
                            return;
                        }
                    }
                }
                // Quantize slow buildup so a still desktop is only redrawn for visible changes.
                const desktop::PulseValues parameters{static_cast<float>(std::round(amount * 1024) / 1024),
                    static_cast<float>(settings.patches), static_cast<float>(frame.pulse), patchSeed};
                if (pulsePresenter) {
                    PresentThroughPeer(parameters, start);
                    return;
                }
                bool changed = !rendered || parameters != drawn;
                const auto presented = renderer.Tick(EffectSettings{}, {}, false, changed, 0, CaptureFps, parameters);
                if (renderer.WaitingForInput() && opaque) {
                    if (!SetLayeredWindowAttributes(overlay, 0, 0, LWA_ALPHA)) winrt::throw_last_error();
                    opaque = false;
                }
                if (presented) {
                    if (!opaque && !SetLayeredWindowAttributes(overlay, 0, 255, LWA_ALPHA))
                        winrt::throw_last_error();
                    opaque = rendered = true;
                    drawn = parameters;
                }
                slowFrames = Clock::now() - start > std::chrono::milliseconds(80) ? slowFrames + 1 : 0;
                if (slowFrames >= 3) {
                    StopEffect(L"Rendering repeatedly took too long. Enable the effect to retry.", true);
                    return;
                }
                if (patchPreviewStarted != Clock::time_point{}) Status(L"Previewing color patches…");
                else if (frame.pulse >= 0) Status(L"Degaussing…");
                else if (frame.due) Status(L"Degauss due");
                else if (opaque) Status(L"Degauss in " + std::to_wstring(int(std::ceil(frame.remaining / 60))) + L" min");
                if (frame.notify)
                    Notify(L"Is it time for a degauss? Use Degauss in the notification area menu.");
            }
            catch (const winrt::hresult_error& error) { StopEffect(error.message().c_str(), true); }
            catch (...) { StopEffect(L"The desktop effect stopped. Enable it to retry.", true); }
        }

        void OnMessage(UINT message, WPARAM wp, LPARAM) override
        {
            if (message == WM_TIMER && wp == RenderTimer) Tick();
            if (!overlay) return;
            if (message == WM_HOTKEY && wp == PanicHotkey) StopEffect(L"Emergency stop");
            if (message == WM_DISPLAYCHANGE) StopEffect(L"Display changed. Enable the effect to resume.");
            if (message == WM_WTSSESSION_CHANGE && (wp == WTS_SESSION_LOCK || wp == WTS_SESSION_LOGOFF ||
                wp == WTS_CONSOLE_DISCONNECT || wp == WTS_REMOTE_DISCONNECT))
                StopEffect(L"Stopped when the desktop locked or disconnected");
            if (message == WM_POWERBROADCAST && wp == PBT_APMSUSPEND) StopEffect(L"Stopped before system sleep");
        }
    };

    LRESULT CALLBACK OverlayProcedure(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
    {
        if (message == WM_NCHITTEST) return HTTRANSPARENT;
        if (message == WM_MOUSEACTIVATE) return MA_NOACTIVATE;
        return DefWindowProcW(window, message, wParam, lParam);
    }

    bool Application::Initialize(bool)
    {
        if (!settings.Load()) {
            Status(L"CRT Degauss preferences are invalid or unreadable. Check the Preferences value at "
                L"HKEY_CURRENT_USER\\Software\\VibeWare\\CrtDegauss.");
            return false;
        }
        WNDCLASSW overlayClass{};
        overlayClass.hInstance = instance;
        overlayClass.lpfnWndProc = OverlayProcedure;
        overlayClass.lpszClassName = desktop::DegaussOverlayClass;
        overlayRegistered = RegisterClassW(&overlayClass) != 0;
        if (!overlayRegistered) return false;
        hotkey = RegisterHotKey(window, PanicHotkey, MOD_WIN | MOD_SHIFT | MOD_NOREPEAT, VK_F9) != FALSE;
        sessionNotifications = WTSRegisterSessionNotification(window, NOTIFY_FOR_THIS_SESSION) != FALSE;
        if (!hotkey) Status(L"Emergency shortcut unavailable. Close the other application using Windows + Shift + F9.");
        return true;
    }

    Application& Crt() { return static_cast<Application&>(CurrentEngine()); }
}

std::unique_ptr<WidgetEngine> CreateEngine(HWND window, NotifyUser notify)
{
    return std::make_unique<Application>(window, notify);
}

WIDGET_API void CrtRead(Settings* settings, int* pulsing, int* hotkey)
{
    *settings = Crt().settings;
    *pulsing = Crt().cycle.Degaussing();
    *hotkey = Crt().hotkey;
}

WIDGET_API void CrtConfigure(Settings settings)
{
    if (!settings.Valid()) return;
    if (Crt().settings.sound && !settings.sound) PlaySoundW(nullptr, nullptr, 0);
    Crt().settings = settings;
    if (!settings.Save()) {
        Crt().Status(L"Could not save preferences. The current values still apply.");
        Crt().Notify(Crt().statusText.c_str());
    }
}

WIDGET_API void CrtDegaussNow() { Crt().RunDegauss(); }
WIDGET_API void CrtPreviewSound(int adjustment) { Crt().PlaySelectedSound(adjustment); }
WIDGET_API void CrtPreviewPatches() { Crt().PreviewPatches(); }
WIDGET_API void CrtShowNow()
{
    auto& app = Crt();
    if (app.running && app.cycle.ShowNow(Seconds(), app.settings.minutes))
        app.patchPreviewStarted = {};
}
