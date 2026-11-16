// SPDX-License-Identifier: MIT
#include "Renderer.h"
#include "SettingsStore.h"
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
        TryId = 109, PresetId = 110, SavePresetId = 111, SliderBase = 200, GlitchTypeBase = 300;
    constexpr UINT_PTR RenderTimer = 1;
    constexpr int EmergencyHotkey = 1;
    using Clock = std::chrono::steady_clock;
    struct Application
    {
        HINSTANCE instance{};
        HWND window{}, overlay{}, toggle{}, palette{}, status{}, tabs{}, color{}, mask{}, persistence{}, randomToggle{}, preset{};
        std::array<HWND, 4> typeChecks{};
        std::vector<HWND> sliderLabels, sliders;
        std::array<std::vector<HWND>, 2> pages;
        EffectSettings settings;
        GlitchSchedule glitches;
        int currentPage = 0, scrollOffset = 0;
        bool syncing = false;
        HFONT font{};
        UINT currentDpi = 96;
        Renderer renderer;
        bool running = false, hotkeyAvailable = false, dirty = false;
        int slowFrames = 0, frames = 0;
        float cpuTotal = 0;
        Clock::time_point previousTick{}, statsSince{};

        int Dip(int value) const { return MulDiv(value, int(GetDpiForWindow(window)), 96); }
        void Status(const std::wstring& text) { SetWindowTextW(status, text.c_str()); }
        double Time() const { return std::chrono::duration<double>(Clock::now().time_since_epoch()).count(); }
        void SavePreferences()
        {
            if (!WriteSettings(settings)) Status(L"Could not save preferences. Current values still apply until closing.");
        }
        void Changed(bool save = true)
        {
            settings.Clamp();
            glitches.Cancel(Time(), settings);
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
            SendMessageW(palette, CB_SETCURSEL, settings.palette, 0);
            SendMessageW(mask, CB_SETCURSEL, settings.mask, 0);
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
            EnableWindow(color, settings.palette == 3);
            syncing = false;
        }
        void CreateControls()
        {
            ReadSettings(settings);
            currentDpi = GetDpiForWindow(window);
            LOGFONTW info{};
            info.lfHeight = -Dip(13);
            wcscpy_s(info.lfFaceName, L"Segoe UI");
            font = CreateFontIndirectW(&info);
            Control(L"STATIC", L"Retro Terminal", SS_LEFT, 20, 16, 300, 26);
            preset = Control(L"COMBOBOX", L"", CBS_DROPDOWNLIST | WS_TABSTOP | WS_VSCROLL, 20, 50, 340, 200, PresetId);
            for (auto label : {L"Custom", L"Clean terminal", L"Worn terminal", L"Arcade", L"My preset 1", L"My preset 2", L"My preset 3"})
                SendMessageW(preset, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(label));
            SendMessageW(preset, CB_SETCURSEL, 0, 0);
            Control(L"BUTTON", L"Save to selected personal preset", BS_PUSHBUTTON | WS_TABSTOP, 375, 48, 290, 28, SavePresetId);
            tabs = Control(WC_TABCONTROLW, L"", WS_TABSTOP, 15, 92, 660, 510, TabsId);
            TCITEMW tab{}; tab.mask = TCIF_TEXT;
            tab.pszText = const_cast<wchar_t*>(L"Appearance"); TabCtrl_InsertItem(tabs, 0, &tab);
            tab.pszText = const_cast<wchar_t*>(L"Glitches"); TabCtrl_InsertItem(tabs, 1, &tab);
            palette = PageControl(0, L"COMBOBOX", L"", CBS_DROPDOWNLIST | WS_TABSTOP, 30, 128, 305, 180, PaletteId);
            for (auto label : {L"Green phosphor", L"Amber terminal", L"Arcade color", L"Custom phosphor color"})
                SendMessageW(palette, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(label));
            color = PageControl(0, L"BUTTON", L"Choose color…", BS_PUSHBUTTON | WS_TABSTOP, 350, 126, 300, 27, ColorId);
            mask = PageControl(0, L"COMBOBOX", L"", CBS_DROPDOWNLIST | WS_TABSTOP, 30, 164, 305, 150, MaskId);
            for (auto label : {L"Mask: off", L"Mask: RGB stripes", L"Mask: phosphor dots"})
                SendMessageW(mask, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(label));
            persistence = PageControl(0, L"BUTTON", L"Temporal phosphor afterglow", BS_AUTOCHECKBOX | WS_TABSTOP, 350, 162, 300, 28, PersistenceId);
            randomToggle = PageControl(1, L"BUTTON", L"Random glitches", BS_AUTOCHECKBOX | WS_TABSTOP, 30, 130, 300, 26, RandomId);
            const wchar_t* names[]{L"RGB misalignment", L"Horizontal tear", L"Local line noise", L"Soft scan band"};
            for (unsigned i = 0; i < 4; ++i)
                typeChecks[i] = PageControl(1, L"BUTTON", names[i], BS_AUTOCHECKBOX | WS_TABSTOP,
                    30 + int(i % 2) * 320, 170 + int(i / 2) * 32, 300, 28, GlitchTypeBase + int(i));
            int row[2]{};
            for (size_t i = 0; i < std::size(FloatControls); ++i) {
                const auto& spec = FloatControls[i];
                int y = (spec.page == 0 ? 204 : 255) + row[spec.page]++ * 29;
                sliderLabels.push_back(PageControl(spec.page, L"STATIC", spec.title, SS_LEFT, 30, y + 4, 325, 24));
                HWND slider = PageControl(spec.page, TRACKBAR_CLASSW, spec.title, TBS_HORZ | WS_TABSTOP,
                    350, y, 300, 27, SliderBase + int(i));
                SendMessageW(slider, TBM_SETRANGE, TRUE, MAKELPARAM(0, 1000));
                sliders.push_back(slider);
            }
            PageControl(1, L"BUTTON", L"Try now", BS_PUSHBUTTON | WS_TABSTOP, 30, 430, 200, 32, TryId);
            PageControl(1, L"STATIC", L"Try now uses the allowed types, even with random glitches off.\nOne episode at a time; at least two seconds of rest.\nStop cancels all episodes. No full-screen flashes.", SS_LEFT, 30, 480, 600, 80);
            toggle = Control(L"BUTTON", L"Enable effect", BS_PUSHBUTTON | WS_TABSTOP, 20, 620, 315, 36, ToggleId);
            Control(L"BUTTON", L"Stop", BS_PUSHBUTTON | WS_TABSTOP, 350, 620, 315, 36, StopId);
            Control(L"STATIC", L"Ctrl + Alt + F11 stops CRT. Applies to the monitor containing this panel.", SS_LEFT, 20, 670, 645, 22);
            status = Control(L"STATIC", L"Stopped. Your desktop is unchanged.", SS_LEFT, 20, 704, 645, 54);
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
            if (index == 0) return;
            if (index <= 3) settings = BuiltInPreset(index - 1);
            else {
                EffectSettings saved;
                if (!ReadSettings(saved, std::wstring(SettingsPath) + L"\\Preset" + std::to_wstring(index - 3))) {
                    Status(L"This personal preset is empty. Choose your settings and save to this slot.");
                    return;
                }
                settings = saved;
            }
            Changed(); SyncControls();
        }
        void SavePreset()
        {
            int index = int(SendMessageW(preset, CB_GETCURSEL, 0, 0));
            if (index < 4) { Status(L"Select My preset 1, 2 or 3 before saving. Existing slot contents will be replaced."); return; }
            if (WriteSettings(settings, std::wstring(SettingsPath) + L"\\Preset" + std::to_wstring(index - 3)))
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
            info.nMin = 0; info.nMax = Dip(775) - 1;
            info.nPage = UINT(client.bottom);
            info.nPos = scrollOffset;
            SetScrollInfo(window, SB_VERT, &info, TRUE);
            ScrollTo(GetScrollPos(window, SB_VERT));
        }
        void ScrollTo(int position)
        {
            RECT client{}; GetClientRect(window, &client);
            position = std::clamp(position, 0, std::max(0, Dip(775) - int(client.bottom)));
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
            running = false;
            glitches.Cancel(Time(), settings);
            KillTimer(window, RenderTimer);
            if (overlay) ShowWindow(overlay, SW_HIDE);
            renderer.Stop();
            if (overlay) { DestroyWindow(overlay); overlay = nullptr; }
            SetWindowTextW(toggle, L"Enable effect");
            Status(reason);
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
                if (presented)
                {
                    dirty = false;
                    if (!SetLayeredWindowAttributes(overlay, 0, 255, LWA_ALPHA)) winrt::throw_last_error();
                    ++frames;
                    cpuTotal += milliseconds;
                }
                float elapsed = std::chrono::duration<float>(start - statsSince).count();
                if (elapsed >= 2)
                {
                    std::wstring stats = frames ? std::to_wstring(frames) + L" presentations / " + std::to_wstring(int(elapsed)) + L" s" : L"No new frames in this interval";
                    if (frames) stats += L" · CPU submission " + std::to_wstring(cpuTotal / frames).substr(0, 4) + L" ms/frame";
                    Status(L"Active · " + renderer.Description() + L"\n" + stats + L"\nGPU frame time is not measured here.");
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
        switch (message)
        {
        case WM_COMMAND:
        {
            const int id = LOWORD(wParam);
            if (id == ToggleId) { if (app->running) app->Stop(L"Stopped by you."); else app->Start(); }
            else if (id == StopId) app->Stop(L"Stopped by you.");
            else if (id == ColorId) app->PickColor();
            else if (id == SavePresetId) app->SavePreset();
            else if (id == PresetId && HIWORD(wParam) == CBN_SELCHANGE) app->SelectPreset();
            else if (id == TryId) {
                if (!app->running) app->Status(L"Enable CRT before trying a glitch.");
                else if (!app->glitches.Trigger(app->Time(), app->settings)) app->Status(L"Select an allowed glitch and wait for the current episode or rest period to finish.");
            }
            else if (id == PaletteId && HIWORD(wParam) == CBN_SELCHANGE) {
                app->settings.palette = int(SendMessageW(app->palette, CB_GETCURSEL, 0, 0));
                app->Changed(); app->SyncControls();
            }
            else if (id == MaskId && HIWORD(wParam) == CBN_SELCHANGE) {
                app->settings.mask = int(SendMessageW(app->mask, CB_GETCURSEL, 0, 0)); app->Changed();
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
            if (wParam == RenderTimer) app->Tick();
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
        case WM_CLOSE:
            app->SavePreferences();
            app->Stop(L"Stopped");
            DestroyWindow(window);
            return 0;
        case WM_DESTROY:
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
        RECT rect{0, 0, MulDiv(690, dpi, 96), MulDiv(775, dpi, 96)};
        AdjustWindowRectExForDpi(&rect, style, FALSE, 0, dpi);
        RECT work{};
        if (SystemParametersInfoW(SPI_GETWORKAREA, 0, &work, 0))
            rect.bottom = rect.top + std::min(rect.bottom - rect.top, work.bottom - work.top - 24);
        app.window = CreateWindowExW(0, ControllerClass, L"Retro Terminal · VibeWare", style,
            CW_USEDEFAULT, CW_USEDEFAULT, rect.right - rect.left, rect.bottom - rect.top, nullptr, nullptr, instance, &app);
        if (!app.window) winrt::throw_last_error();
        app.CreateControls();
        ShowWindow(app.window, show);
        MSG message{};
        int result;
        while ((result = GetMessageW(&message, nullptr, 0, 0)) > 0)
            if (!IsDialogMessageW(app.window, &message)) { TranslateMessage(&message); DispatchMessageW(&message); }
        app.renderer.Stop();
        if (app.font) DeleteObject(app.font);
        CloseHandle(singleton);
        return result < 0 ? 1 : 0;
    }
    catch (const winrt::hresult_error& error) { MessageBoxW(nullptr, error.message().c_str(), L"Retro Terminal", MB_OK | MB_ICONERROR); }
    catch (...) { MessageBoxW(nullptr, L"Retro Terminal could not start.", L"Retro Terminal", MB_OK | MB_ICONERROR); }
    CloseHandle(singleton);
    return 1;
}
