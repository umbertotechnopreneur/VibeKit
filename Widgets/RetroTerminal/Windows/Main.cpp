// SPDX-License-Identifier: MIT
#include "Renderer.h"
#include "ControllerState.h"
#include "BuiltInPresets.h"
#include "CloseDialog.h"
#include "GpuJokeNotifications.h"
#include "MemoryWatchdog.h"
#include <commdlg.h>
#include <vector>
#include <iterator>
#include <cstdio>
#include <commctrl.h>
#include <wtsapi32.h>
#include <winrt/base.h>
#include <chrono>
#include <algorithm>
#include <memory>
#include <string>

namespace
{
    constexpr wchar_t ControllerClass[] = L"VibeWareRetroTerminalController";
    constexpr wchar_t OverlayClass[] = L"VibeWareRetroTerminalOverlay";
    constexpr int ToggleId = 100, PaletteId = 101, StopId = 103, TabsId = 104,
        ColorId = 105, MaskId = 106, PersistenceId = 107, RandomId = 108,
        TryId = 109, PresetId = 110, SavePresetId = 111, GpuJokesId = 112, NextJokeId = 113, SliderBase = 200, GlitchTypeBase = 300;
    constexpr UINT_PTR RenderTimer = 1;
    constexpr int PanelHeight = 833;
    constexpr int EmergencyHotkey = 1;
    using Clock = std::chrono::steady_clock;
    struct Application : ControllerState
    {
        HINSTANCE instance{};
        HWND window{}, overlay{}, toggle{}, palette{}, status{}, tabs{}, color{}, mask{}, persistence{}, randomToggle{}, preset{}, heading{}, eyebrow{};
        std::array<HWND, 4> typeChecks{};
        std::vector<HWND> sliderLabels, sliders;
        std::array<std::vector<HWND>, 2> pages;
        int currentPage = 0, scrollOffset = 0;
        bool syncing = false;
        HFONT font{}, headingFont{};
        HBRUSH background = CreateSolidBrush(RGB(248, 247, 252));
        UINT currentDpi = 96;
        Renderer renderer;
        TrayIcon tray;
        GpuJokeNotifications gpuJokes;
        HWND jokeCaption{};
        unsigned jokeIndex{};
        bool running = false, hotkeyAvailable = false, dirty = false, firstFrame = false, overlayOpaque = false;
        int slowFrames = 0, frames = 0;
        float cpuTotal = 0;
        Clock::time_point previousTick{}, statsSince{};

        int Dip(int value) const { return MulDiv(value, int(GetDpiForWindow(window)), 96); }
        std::wstring statusText = L"Stopped";
        void Status(const std::wstring& text) { statusText = text; SetWindowTextW(status, text.c_str()); }
        double Time() const { return std::chrono::duration<double>(Clock::now().time_since_epoch()).count(); }
        bool SavePreferences()
        {
            if (Save()) return true;
            Status(L"Could not save preferences. Your current values are retained in this panel. Close to retry saving.");
            return false;
        }
        bool RequestClose()
        {
            return RequestSafeClose(
                [this] { Stop(L"Stopped"); },
                [this] { return SavePreferences(); },
                [this] { return ShowSaveFailure(window); });
        }
        void Changed(bool save = true)
        {
            Change(Time());
            dirty = true;
            if (save) SavePreferences();
        }
        HWND Control(const wchar_t* type, const wchar_t* label, DWORD style, int x, int y, int w, int h, int id = 0)
        {
            HWND child = CreateWindowExW(0, type, label, WS_CHILD | WS_VISIBLE | style,
                Dip(x), Dip(y), Dip(w), Dip(h), window, reinterpret_cast<HMENU>(INT_PTR(id)), instance, nullptr);
            if (!child) winrt::throw_last_error();
            SendMessageW(child, WM_SETFONT, reinterpret_cast<WPARAM>(font), TRUE);
            return child;
        }
        HWND PageControl(int page, const wchar_t* type, const wchar_t* label, DWORD style, int x, int y, int w, int h, int id = 0)
        {
            HWND child = Control(type, label, style, x, y, w, h, id);
            pages[page].push_back(child);
            return child;
        }
        void ShowPage()
        {
            currentPage = TabCtrl_GetCurSel(tabs);
            for (int i = 0; i < 2; ++i) for (HWND child : pages[i])
                ShowWindow(child, i == currentPage ? SW_SHOW : SW_HIDE);
        }
        void SyncControls()
        {
            syncing = true;
            SendMessageW(palette, CB_SETCURSEL, int(settings.palette), 0);
            SendMessageW(mask, CB_SETCURSEL, int(settings.mask), 0);
            SendMessageW(persistence, BM_SETCHECK, settings.persistence ? BST_CHECKED : BST_UNCHECKED, 0);
            SendMessageW(randomToggle, BM_SETCHECK, settings.randomGlitches ? BST_CHECKED : BST_UNCHECKED, 0);
            for (unsigned i = 0; i < 4; ++i)
                SendMessageW(typeChecks[i], BM_SETCHECK, settings.glitchTypes & (1u << i) ? BST_CHECKED : BST_UNCHECKED, 0);
            for (size_t i = 0; i < std::size(FloatControls); ++i) {
                const auto& spec = FloatControls[i];
                float value = settings.*(spec.field);
                SendMessageW(sliders[i], TBM_SETPOS, TRUE, int((value - spec.low) / (spec.high - spec.low) * 1000));
                wchar_t label[128]{};
                swprintf_s(label, L"%s · %.2f", spec.title, double(value));
                SetWindowTextW(sliderLabels[i], label);
            }
            EnableWindow(color, settings.palette == Palette::Custom);
            syncing = false;
        }
        void CreateControls()
        {
            Load();
            currentDpi = GetDpiForWindow(window);
            LOGFONTW info{};
            info.lfHeight = -Dip(13);
            wcscpy_s(info.lfFaceName, L"Segoe UI");
            font = CreateFontIndirectW(&info);
            info.lfHeight = -Dip(22);
            info.lfWeight = FW_SEMIBOLD;
            headingFont = CreateFontIndirectW(&info);
            eyebrow = Control(L"STATIC", L"VIBEWARE  /  DESKTOP EFFECTS", SS_LEFT, 20, 12, 600, 19);
            heading = Control(L"STATIC", L"Retro Terminal", SS_LEFT, 20, 31, 400, 32);
            SendMessageW(heading, WM_SETFONT, reinterpret_cast<WPARAM>(headingFont), TRUE);
            preset = Control(L"COMBOBOX", L"", CBS_DROPDOWNLIST | WS_TABSTOP | WS_VSCROLL, 20, 78, 340, 200, PresetId);
            SendMessageW(preset, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"Custom"));
            for (const auto& entry : BuiltInPresets)
                SendMessageW(preset, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(entry.name));
            for (auto label : {L"My preset 1", L"My preset 2", L"My preset 3"})
                SendMessageW(preset, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(label));
            SendMessageW(preset, CB_SETCURSEL, 0, 0);
            Control(L"BUTTON", L"Save to selected personal preset", BS_PUSHBUTTON | WS_TABSTOP, 375, 76, 290, 28, SavePresetId);
            tabs = Control(WC_TABCONTROLW, L"", WS_TABSTOP, 15, 120, 660, 510, TabsId);
            TCITEMW tab{}; tab.mask = TCIF_TEXT;
            tab.pszText = const_cast<wchar_t*>(L"Appearance"); TabCtrl_InsertItem(tabs, 0, &tab);
            tab.pszText = const_cast<wchar_t*>(L"Glitches"); TabCtrl_InsertItem(tabs, 1, &tab);
            palette = PageControl(0, L"COMBOBOX", L"", CBS_DROPDOWNLIST | WS_TABSTOP, 30, 156, 305, 180, PaletteId);
            for (auto label : {L"Green phosphor", L"Amber terminal", L"Arcade color", L"Custom phosphor color"})
                SendMessageW(palette, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(label));
            color = PageControl(0, L"BUTTON", L"Choose color…", BS_PUSHBUTTON | WS_TABSTOP, 350, 154, 300, 27, ColorId);
            mask = PageControl(0, L"COMBOBOX", L"", CBS_DROPDOWNLIST | WS_TABSTOP, 30, 192, 305, 150, MaskId);
            for (auto label : {L"Mask: off", L"Mask: RGB stripes", L"Mask: phosphor dots"})
                SendMessageW(mask, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(label));
            persistence = PageControl(0, L"BUTTON", L"Temporal phosphor afterglow", BS_AUTOCHECKBOX | WS_TABSTOP, 350, 190, 300, 28, PersistenceId);
            randomToggle = PageControl(1, L"BUTTON", L"Random glitches", BS_AUTOCHECKBOX | WS_TABSTOP, 30, 158, 300, 26, RandomId);
            const wchar_t* names[]{L"RGB misalignment", L"Horizontal tear", L"Local line noise", L"Soft scan band"};
            for (unsigned i = 0; i < 4; ++i)
                typeChecks[i] = PageControl(1, L"BUTTON", names[i], BS_AUTOCHECKBOX | WS_TABSTOP,
                    30 + int(i % 2) * 320, 198 + int(i / 2) * 32, 300, 28, GlitchTypeBase + int(i));
            int row[2]{};
            for (size_t i = 0; i < std::size(FloatControls); ++i) {
                const auto& spec = FloatControls[i];
                int y = (spec.page == 0 ? 232 : 283) + row[spec.page]++ * 29;
                sliderLabels.push_back(PageControl(spec.page, L"STATIC", spec.title, SS_LEFT, 30, y + 4, 325, 24));
                HWND slider = PageControl(spec.page, TRACKBAR_CLASSW, spec.title, TBS_HORZ | WS_TABSTOP,
                    350, y, 300, 27, SliderBase + int(i));
                SendMessageW(slider, TBM_SETRANGE, TRUE, MAKELPARAM(0, 1000));
                sliders.push_back(slider);
            }
            PageControl(1, L"BUTTON", L"Try now", BS_PUSHBUTTON | WS_TABSTOP, 30, 458, 200, 32, TryId);
            PageControl(1, L"STATIC", L"Try now uses the allowed types, even with random glitches off.\nOne episode at a time; at least two seconds of rest.\nStop cancels all episodes. No full-screen flashes.", SS_LEFT, 30, 508, 600, 80);
            toggle = Control(L"BUTTON", L"Enable effect", BS_PUSHBUTTON | WS_TABSTOP, 20, 648, 315, 36, ToggleId);
            Control(L"BUTTON", L"Stop", BS_PUSHBUTTON | WS_TABSTOP, 350, 648, 315, 36, StopId);
            Control(L"STATIC", L"Ctrl + Alt + F11 stops CRT. Applies to the monitor containing this panel.", SS_LEFT, 20, 693, 645, 20);
            HWND jokesToggle = Control(L"BUTTON", L"GPU joke notification at startup", BS_AUTOCHECKBOX | WS_TABSTOP,
                20, 714, 645, 22, GpuJokesId);
            const bool jokesEnabled = GpuJokeNotifications::ReadPreference();
            SendMessageW(jokesToggle, BM_SETCHECK, jokesEnabled ? BST_CHECKED : BST_UNCHECKED, 0);
            tray.Initialize(window, window);
            if (jokesEnabled) gpuJokes.Start(window);
            jokeCaption = Control(L"STATIC", GpuJokeNotifications::Caption(jokeIndex), SS_LEFT, 20, 742, 530, 26);
            Control(L"BUTTON", L"Next joke", BS_PUSHBUTTON | WS_TABSTOP, 550, 738, 115, 24, NextJokeId);
            status = Control(L"STATIC", L"Stopped. Your desktop is unchanged.", SS_LEFT, 20, 772, 645, 49);
            SyncControls(); ShowPage(); UpdateScroll();
            hotkeyAvailable = RegisterHotKey(window, EmergencyHotkey, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_F11) != FALSE;
            if (!hotkeyAvailable) {
                EnableWindow(toggle, FALSE);
                Status(L"Ctrl + Alt + F11 is already in use. The CRT effect cannot start until the shortcut is available.");
            }
            if (!WTSRegisterSessionNotification(window, NOTIFY_FOR_THIS_SESSION)) {
                EnableWindow(toggle, FALSE); hotkeyAvailable = false;
                Status(L"Session notifications are unavailable. The effect cannot start safely.");
            }
        }
        void SelectPreset()
        {
            int index = int(SendMessageW(preset, CB_GETCURSEL, 0, 0));
            if (index <= 0) return;
            if (index <= BuiltInPresetCount) settings = BuiltInPresets[index - 1].settings;
            else {
                if (!LoadPersonalPreset(index - BuiltInPresetCount)) {
                    Status(L"This personal preset is empty. Choose your settings and save to this slot.");
                    return;
                }
            }
            Changed(); SyncControls();
        }
        void SavePreset()
        {
            int index = int(SendMessageW(preset, CB_GETCURSEL, 0, 0));
            if (index <= BuiltInPresetCount) { Status(L"Select My preset 1, 2 or 3 before saving. Existing slot contents will be replaced."); return; }
            if (SavePersonalPreset(index - BuiltInPresetCount))
                Status(L"Personal preset saved.");
            else Status(L"Could not save the personal preset.");
        }
        void PickColor()
        {
            static COLORREF custom[16]{};
            CHOOSECOLORW dialog{sizeof(dialog)};
            dialog.hwndOwner = window;
            dialog.rgbResult = RGB(int(settings.color[0] * 255), int(settings.color[1] * 255), int(settings.color[2] * 255));
            dialog.lpCustColors = custom;
            dialog.Flags = CC_FULLOPEN | CC_RGBINIT;
            if (ChooseColorW(&dialog)) {
                settings.color = {GetRValue(dialog.rgbResult) / 255.f, GetGValue(dialog.rgbResult) / 255.f, GetBValue(dialog.rgbResult) / 255.f};
                Changed();
            }
        }
        void UpdateScroll()
        {
            RECT client{}; GetClientRect(window, &client);
            SCROLLINFO info{sizeof(info)};
            info.fMask = SIF_RANGE | SIF_PAGE | SIF_POS;
            info.nMin = 0; info.nMax = Dip(PanelHeight) - 1;
            info.nPage = UINT(client.bottom);
            info.nPos = scrollOffset;
            SetScrollInfo(window, SB_VERT, &info, TRUE);
            ScrollTo(GetScrollPos(window, SB_VERT));
        }
        void ScrollTo(int position)
        {
            RECT client{}; GetClientRect(window, &client);
            position = std::clamp(position, 0, std::max(0, Dip(PanelHeight) - int(client.bottom)));
            const int delta = scrollOffset - position;
            scrollOffset = position;
            if (delta) ScrollWindowEx(window, 0, delta, nullptr, nullptr, nullptr, nullptr,
                SW_SCROLLCHILDREN | SW_INVALIDATE | SW_ERASE);
            SetScrollPos(window, SB_VERT, position, TRUE);
        }
        void ScaleControls(UINT dpi)
        {
            LOGFONTW info{};
            info.lfHeight = -MulDiv(13, int(dpi), 96);
            wcscpy_s(info.lfFaceName, L"Segoe UI");
            HFONT nextFont = CreateFontIndirectW(&info);
            for (HWND child = GetWindow(window, GW_CHILD); child; child = GetWindow(child, GW_HWNDNEXT))
            {
                RECT bounds{};
                GetWindowRect(child, &bounds);
                MapWindowPoints(nullptr, window, reinterpret_cast<POINT*>(&bounds), 2);
                SetWindowPos(child, nullptr, MulDiv(bounds.left, dpi, currentDpi), MulDiv(bounds.top, dpi, currentDpi),
                    MulDiv(bounds.right - bounds.left, dpi, currentDpi), MulDiv(bounds.bottom - bounds.top, dpi, currentDpi),
                    SWP_NOZORDER | SWP_NOACTIVATE);
                if (nextFont) SendMessageW(child, WM_SETFONT, reinterpret_cast<WPARAM>(nextFont), TRUE);
            }
            if (nextFont) { DeleteObject(font); font = nextFont; }
            info.lfHeight = -MulDiv(22, int(dpi), 96);
            info.lfWeight = FW_SEMIBOLD;
            HFONT nextHeading = CreateFontIndirectW(&info);
            if (nextHeading) {
                SendMessageW(heading, WM_SETFONT, reinterpret_cast<WPARAM>(nextHeading), TRUE);
                DeleteObject(headingFont); headingFont = nextHeading;
            }
            scrollOffset = MulDiv(scrollOffset, int(dpi), int(currentDpi));
            currentDpi = dpi;
            UpdateScroll();
        }
        std::wstring ShaderPath()
        {
            std::wstring path(32768, L'\0');
            DWORD count = GetModuleFileNameW(nullptr, path.data(), DWORD(path.size()));
            if (!count || count >= path.size()) winrt::throw_last_error();
            path.resize(count);
            return path.substr(0, path.find_last_of(L"\\/")) + L"\\Shaders\\Retro.hlsl";
        }
        void Stop(const std::wstring& reason)
        {
            tray.CancelPreview();
            const bool wasRunning = running;
            running = false;
            firstFrame = false;
            glitches.Cancel(Time(), settings);
            KillTimer(window, RenderTimer);
            if (overlay) ShowWindow(overlay, SW_HIDE);
            renderer.Stop();
            if (overlay) { DestroyWindow(overlay); overlay = nullptr; }
            SetWindowTextW(toggle, L"Enable effect");
            Status(reason);
            if (wasRunning) tray.Notify(L"CRT effect off", reason.c_str());
        }
        void Start()
        {
            if (running || !hotkeyAvailable) return;
            try
            {
                HMONITOR monitor = MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST);
                MONITORINFO info{sizeof(info)};
                if (!GetMonitorInfoW(monitor, &info)) winrt::throw_last_error();
                overlay = CreateWindowExW(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
                    OverlayClass, L"Retro Terminal screen effect", WS_POPUP,
                    info.rcMonitor.left, info.rcMonitor.top, info.rcMonitor.right - info.rcMonitor.left,
                    info.rcMonitor.bottom - info.rcMonitor.top, nullptr, nullptr, instance, nullptr);
                if (!overlay) winrt::throw_last_error();
                overlayOpaque = false;
                if (!SetLayeredWindowAttributes(overlay, 0, 0, LWA_ALPHA)) winrt::throw_last_error();
                if (!SetWindowDisplayAffinity(overlay, WDA_EXCLUDEFROMCAPTURE))
                    throw winrt::hresult_error(HRESULT_FROM_WIN32(GetLastError()), L"The overlay could not be excluded from screen capture. The effect will stay off.");
                ShowWindow(overlay, SW_SHOWNOACTIVATE);
                renderer.Start(overlay, monitor, ShaderPath());
                previousTick = statsSince = Clock::now();
                slowFrames = frames = 0;
                cpuTotal = 0;
                dirty = true;
                glitches.Reset(Time(), settings);
                if (!SetTimer(window, RenderTimer, 34, nullptr)) winrt::throw_last_error();
                running = true;
                firstFrame = true;
                SetWindowTextW(toggle, L"Disable effect");
                Status(L"Waiting for a screen frame…");
            }
            catch (const winrt::hresult_error& error) { Stop(L"Stopped: " + std::wstring(error.message().c_str())); }
            catch (...) { Stop(L"Stopped: the effect could not start. No automatic restart."); }
        }
        void Tick()
        {
            if (!running) return;
            auto start = Clock::now();
            if (start - previousTick > std::chrono::milliseconds(250))
            {
                Stop(L"Stopped after a delayed desktop update. Enable manually when the system is stable.");
                return;
            }
            previousTick = start;
            try
            {
                const auto glitch = glitches.Sample(Time(), settings);
                const bool animated = glitches.Changed(glitch);
                bool presented = renderer.Tick(settings, glitch, dirty, animated);
                const float milliseconds = std::chrono::duration<float, std::milli>(Clock::now() - start).count();
                slowFrames = milliseconds > 80 ? slowFrames + 1 : 0;
                if (slowFrames >= 3)
                {
                    Stop(L"Stopped after repeated slow render submissions. Enable manually to retry.");
                    return;
                }
                if (renderer.WaitingForInput() && overlayOpaque)
                {
                    renderer.ClearPresentation();
                    if (!SetLayeredWindowAttributes(overlay, 0, 0, LWA_ALPHA)) winrt::throw_last_error();
                    overlayOpaque = false;
                }
                if (presented)
                {
                    dirty = false;
                    if (!overlayOpaque)
                    {
                        if (!SetLayeredWindowAttributes(overlay, 0, 255, LWA_ALPHA)) winrt::throw_last_error();
                        overlayOpaque = true;
                    }
                    renderer.ConfirmVisiblePresentation();
                    if (firstFrame) {
                        firstFrame = false;
                        tray.Notify(L"CRT effect on", L"The screen effect is active.");
                    }
                    ++frames;
                    cpuTotal += milliseconds;
                }
                float elapsed = std::chrono::duration<float>(start - statsSince).count();
                if (elapsed >= 2)
                {
                    std::wstring stats = frames ? std::to_wstring(frames) + L" presentations / " + std::to_wstring(int(elapsed)) + L" s" : L"No new frames in this interval";
                    if (frames) stats += L" · CPU submission " + std::to_wstring(cpuTotal / frames).substr(0, 4) + L" ms/frame";
                    Status(overlayOpaque
                        ? L"Active · " + renderer.Description() + L"\n" + stats + L"\nGPU frame time is not measured here."
                        : L"Waiting for a screen frame…");
                    statsSince = start;
                    frames = 0;
                    cpuTotal = 0;
                }
            }
            catch (const winrt::hresult_error& error) { Stop(L"Stopped: " + std::wstring(error.message().c_str())); }
            catch (...) { Stop(L"Stopped after an unexpected renderer error. Enable manually to retry."); }
        }
    };

    LRESULT CALLBACK OverlayProcedure(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
    {
        if (message == WM_NCHITTEST) return HTTRANSPARENT;
        if (message == WM_MOUSEACTIVATE) return MA_NOACTIVATE;
        if (message == WM_ERASEBKGND) return 1;
        return DefWindowProcW(window, message, wParam, lParam);
    }

    LRESULT CALLBACK ControllerProcedure(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
    {
        auto app = reinterpret_cast<Application*>(GetWindowLongPtrW(window, GWLP_USERDATA));
        if (message == WM_NCCREATE)
        {
            app = static_cast<Application*>(reinterpret_cast<CREATESTRUCTW*>(lParam)->lpCreateParams);
            app->window = window;
            SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(app));
        }
        if (!app) return DefWindowProcW(window, message, wParam, lParam);
        if (app->tray.HandleMessage(message, wParam, lParam, app->running, app->running, app->statusText))
        {
            const auto action = app->tray.TakeAction();
            switch (action)
            {
            case TrayIcon::Action::Enable:
            case TrayIcon::Action::Preview:
                if (!app->running) {
                    app->Start();
                    if (app->running && action == TrayIcon::Action::Preview && !app->tray.BeginPreview())
                        app->Stop(L"Could not schedule the end of the preview.");
                }
                break;
            case TrayIcon::Action::Stop: app->Stop(L"Stopped by you."); break;
            case TrayIcon::Action::Glitch: SendMessageW(window, WM_COMMAND, TryId, 0); break;
            case TrayIcon::Action::Green:
            case TrayIcon::Action::Amber:
            case TrayIcon::Action::Arcade:
                app->settings.palette = Palette(int(action) - int(TrayIcon::Action::Green));
                app->Changed(); app->SyncControls();
                break;
            case TrayIcon::Action::Open:
            case TrayIcon::Action::Preferences:
                ShowWindow(window, SW_RESTORE);
                SetForegroundWindow(window);
                break;
            case TrayIcon::Action::About:
                MessageBoxW(window, L"Retro Terminal · VibeWare\nA CRT effect for your desktop.\nMIT License",
                    L"About Retro Terminal", MB_OK | MB_ICONINFORMATION);
                break;
            case TrayIcon::Action::Quit:
                PostMessageW(window, WM_CLOSE, 0, 0);
                break;
            case TrayIcon::Action::None:
                break;
            }
            return 0;
        }
        switch (message)
        {
        case WM_ERASEBKGND:
        {
            RECT bounds{}; GetClientRect(window, &bounds);
            FillRect(reinterpret_cast<HDC>(wParam), &bounds, app->background);
            return 1;
        }
        case WM_CTLCOLORSTATIC:
        {
            HDC dc = reinterpret_cast<HDC>(wParam);
            HWND child = reinterpret_cast<HWND>(lParam);
            SetBkColor(dc, RGB(248, 247, 252));
            SetTextColor(dc, child == app->eyebrow ? RGB(108, 65, 158) : RGB(35, 30, 48));
            return reinterpret_cast<LRESULT>(app->background);
        }
        case WM_COMMAND:
        {
            const int id = LOWORD(wParam);
            if (id == ToggleId) { if (app->running) app->Stop(L"Stopped by you."); else app->Start(); }
            else if (id == StopId) app->Stop(L"Stopped by you.");
            else if (id == GpuJokesId && HIWORD(wParam) == BN_CLICKED) {
                const bool enabled = SendMessageW(reinterpret_cast<HWND>(lParam), BM_GETCHECK, 0, 0) == BST_CHECKED;
                if (!enabled) app->gpuJokes.Cancel(window);
                if (!GpuJokeNotifications::SavePreference(enabled)) app->Status(L"Could not save the GPU jokes preference.");
            }
            else if (id == NextJokeId && HIWORD(wParam) == BN_CLICKED) {
                ++app->jokeIndex;
                SetWindowTextW(app->jokeCaption, GpuJokeNotifications::Caption(app->jokeIndex));
            }
            else if (id == ColorId) app->PickColor();
            else if (id == SavePresetId) app->SavePreset();
            else if (id == PresetId && HIWORD(wParam) == CBN_SELCHANGE) app->SelectPreset();
            else if (id == TryId) {
                if (!app->running) app->Status(L"Enable CRT before trying a glitch.");
                else if (!app->glitches.Trigger(app->Time(), app->settings)) app->Status(L"Select an allowed glitch and wait for the current episode or rest period to finish.");
            }
            else if (id == PaletteId && HIWORD(wParam) == CBN_SELCHANGE) {
                app->settings.palette = Palette(SendMessageW(app->palette, CB_GETCURSEL, 0, 0));
                app->Changed(); app->SyncControls();
            }
            else if (id == MaskId && HIWORD(wParam) == CBN_SELCHANGE) {
                app->settings.mask = PhosphorMask(SendMessageW(app->mask, CB_GETCURSEL, 0, 0)); app->Changed();
            }
            else if (id == PersistenceId) {
                app->settings.persistence = SendMessageW(app->persistence, BM_GETCHECK, 0, 0) == BST_CHECKED; app->Changed();
            }
            else if (id == RandomId) {
                app->settings.randomGlitches = SendMessageW(app->randomToggle, BM_GETCHECK, 0, 0) == BST_CHECKED; app->Changed();
            }
            else if (id >= GlitchTypeBase && id < GlitchTypeBase + 4) {
                unsigned bit = 1u << (id - GlitchTypeBase);
                if (SendMessageW(app->typeChecks[id - GlitchTypeBase], BM_GETCHECK, 0, 0) == BST_CHECKED) app->settings.glitchTypes |= bit;
                else app->settings.glitchTypes &= ~bit;
                app->Changed();
            }
            return 0;
        }
        case WM_NOTIFY:
            if (reinterpret_cast<NMHDR*>(lParam)->idFrom == TabsId && reinterpret_cast<NMHDR*>(lParam)->code == TCN_SELCHANGE)
                app->ShowPage();
            return 0;
        case WM_HSCROLL:
            if (!app->syncing) {
                const int index = GetDlgCtrlID(reinterpret_cast<HWND>(lParam)) - SliderBase;
                if (index >= 0 && size_t(index) < std::size(FloatControls)) {
                    const auto& spec = FloatControls[index];
                    const float fraction = float(SendMessageW(app->sliders[index], TBM_GETPOS, 0, 0)) / 1000;
                    app->settings.*(spec.field) = spec.low + fraction * (spec.high - spec.low);
                    app->Changed(LOWORD(wParam) == TB_ENDTRACK);
                    app->SyncControls();
                }
            }
            return 0;
        case WM_SIZE:
            app->UpdateScroll();
            return 0;
        case WM_VSCROLL:
        {
            SCROLLINFO info{sizeof(info)}; info.fMask = SIF_ALL;
            GetScrollInfo(window, SB_VERT, &info);
            int next = app->scrollOffset;
            switch (LOWORD(wParam)) {
            case SB_LINEUP: next -= app->Dip(30); break;
            case SB_LINEDOWN: next += app->Dip(30); break;
            case SB_PAGEUP: next -= int(info.nPage); break;
            case SB_PAGEDOWN: next += int(info.nPage); break;
            case SB_THUMBTRACK: case SB_THUMBPOSITION: next = info.nTrackPos; break;
            default: break;
            }
            app->ScrollTo(next);
            return 0;
        }
        case WM_MOUSEWHEEL:
            app->ScrollTo(app->scrollOffset - GET_WHEEL_DELTA_WPARAM(wParam) / WHEEL_DELTA * app->Dip(60));
            return 0;
        case WM_TIMER:
            if (wParam == TrayIcon::PreviewTimer && app->tray.PreviewDue()) app->Stop(L"Preview finished");
            else if (wParam == RenderTimer) app->Tick();
            else if (wParam == GpuJokeNotifications::TimerId) app->gpuJokes.Tick(window, app->tray);
            return 0;
        case WM_HOTKEY:
            if (wParam == EmergencyHotkey) app->Stop(L"Emergency stop · Ctrl + Alt + F11. Enable manually to resume.");
            return 0;
        case WM_WTSSESSION_CHANGE:
            if (wParam == WTS_SESSION_LOCK || wParam == WTS_SESSION_LOGOFF || wParam == WTS_CONSOLE_DISCONNECT || wParam == WTS_REMOTE_DISCONNECT)
                app->Stop(L"Stopped because the desktop session became unavailable.");
            return 0;
        case WM_POWERBROADCAST:
            if (wParam == PBT_APMSUSPEND) app->Stop(L"Stopped before system sleep. Enable manually to resume.");
            return TRUE;
        case WM_DISPLAYCHANGE:
            app->Stop(L"Stopped after a display change. Enable manually to select this monitor again.");
            return 0;
        case WM_DPICHANGED:
        {
            app->Stop(L"Stopped after a display scale change. Enable manually to resume.");
            app->ScaleControls(HIWORD(wParam));
            auto rect = reinterpret_cast<RECT*>(lParam);
            SetWindowPos(window, nullptr, rect->left, rect->top, rect->right - rect->left, rect->bottom - rect->top, SWP_NOZORDER | SWP_NOACTIVATE);
            return 0;
        }
        case RetroMemoryWatchdog::LimitMessage:
            app->Stop(L"Stopped because Retro Terminal exceeded its memory limit.");
            MessageBoxW(window, L"Retro Terminal reached its RAM limit. Details are in the local diagnostic log.",
                L"Retro Terminal", MB_OK | MB_ICONWARNING);
            if (IsWindow(window)) DestroyWindow(window);
            return 0;
        case WM_CLOSE:
            if (app->RequestClose()) DestroyWindow(window);
            return 0;
        case WM_DESTROY:
            app->gpuJokes.Cancel(window);
            app->tray.Remove();
            UnregisterHotKey(window, EmergencyHotkey);
            WTSUnRegisterSessionNotification(window);
            PostQuitMessage(0);
            return 0;
        }
        return DefWindowProcW(window, message, wParam, lParam);
    }
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int show)
{
    HANDLE singleton = CreateMutexW(nullptr, FALSE, L"Local\\VibeWare.RetroTerminal");
    if (!singleton) return 1;
    if (GetLastError() == ERROR_ALREADY_EXISTS)
    {
        HWND existing = FindWindowW(ControllerClass, nullptr);
        if (existing) { ShowWindow(existing, SW_RESTORE); SetForegroundWindow(existing); }
        CloseHandle(singleton);
        return 0;
    }
    try
    {
        winrt::init_apartment(winrt::apartment_type::single_threaded);
        INITCOMMONCONTROLSEX controls{sizeof(controls), ICC_BAR_CLASSES | ICC_STANDARD_CLASSES | ICC_TAB_CLASSES};
        if (!InitCommonControlsEx(&controls)) winrt::throw_last_error();
        WNDCLASSEXW overlay{sizeof(overlay)};
        overlay.hInstance = instance;
        overlay.lpfnWndProc = OverlayProcedure;
        overlay.lpszClassName = OverlayClass;
        if (!RegisterClassExW(&overlay)) winrt::throw_last_error();
        WNDCLASSEXW controller{sizeof(controller)};
        controller.hInstance = instance;
        controller.lpfnWndProc = ControllerProcedure;
        controller.lpszClassName = ControllerClass;
        controller.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        controller.hIcon = LoadIconW(nullptr, IDI_APPLICATION);
        controller.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
        if (!RegisterClassExW(&controller)) winrt::throw_last_error();
        Application app;
        app.instance = instance;
        const DWORD style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX | WS_VSCROLL;
        UINT dpi = GetDpiForSystem();
        RECT rect{0, 0, MulDiv(690, dpi, 96), MulDiv(PanelHeight, dpi, 96)};
        AdjustWindowRectExForDpi(&rect, style, FALSE, 0, dpi);
        RECT work{};
        if (SystemParametersInfoW(SPI_GETWORKAREA, 0, &work, 0))
            rect.bottom = rect.top + std::min(rect.bottom - rect.top, work.bottom - work.top - 24);
        app.window = CreateWindowExW(0, ControllerClass, L"Retro Terminal · VibeWare", style,
            CW_USEDEFAULT, CW_USEDEFAULT, rect.right - rect.left, rect.bottom - rect.top, nullptr, nullptr, instance, &app);
        if (!app.window) winrt::throw_last_error();
        app.CreateControls();
        auto memoryWatchdog = RetroMemoryWatchdog::Start(app.window);
        ShowWindow(app.window, show);
        MSG message{};
        int result;
        while ((result = GetMessageW(&message, nullptr, 0, 0)) > 0)
            if (!IsDialogMessageW(app.window, &message)) { TranslateMessage(&message); DispatchMessageW(&message); }
        app.renderer.Stop();
        if (app.font) DeleteObject(app.font);
        if (app.headingFont) DeleteObject(app.headingFont);
        if (app.background) DeleteObject(app.background);
        CloseHandle(singleton);
        return result < 0 ? 1 : 0;
    }
    catch (const winrt::hresult_error& error) { MessageBoxW(nullptr, error.message().c_str(), L"Retro Terminal", MB_OK | MB_ICONERROR); }
    catch (...) { MessageBoxW(nullptr, L"Retro Terminal could not start.", L"Retro Terminal", MB_OK | MB_ICONERROR); }
    CloseHandle(singleton);
    return 1;
}
