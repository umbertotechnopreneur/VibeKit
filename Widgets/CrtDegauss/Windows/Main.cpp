// SPDX-License-Identifier: MIT
#include "MagnetizationCycle.h"
#include "Renderer.h"
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
    constexpr wchar_t WindowClass[] = L"VibeWareCrtDegaussSettings";
    constexpr wchar_t OverlayClass[] = L"VibeWareCrtDegaussOverlay";
    constexpr wchar_t RegistryPath[] = L"Software\\VibeWare\\CrtDegauss";
    constexpr UINT TrayMessage = WM_APP + 1, ReadSettingsMessage = WM_APP + 2;
    constexpr UINT RenderTimer = 1, PanicHotkey = 1;
    constexpr int CaptureFps = 30;
    enum Control { Effect = 100, Minutes, Patches, Degauss, Stop, Preferences, StopAll, Quit, MinutesArrow, Sound, SoundVariant };
    using Clock = std::chrono::steady_clock;

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

        void Load()
        {
            Settings saved;
            DWORD size = sizeof(saved);
            if (RegGetValueW(HKEY_CURRENT_USER, RegistryPath, L"Preferences", RRF_RT_REG_BINARY,
                    nullptr, &saved, &size) == ERROR_SUCCESS && (size == sizeof(saved) || size == 3 * sizeof(DWORD) || size == 2 * sizeof(DWORD)) &&
                saved.minutes >= 1 && saved.minutes <= degauss::MaximumMinutes &&
                saved.patches >= 1 && saved.patches <= 2 && saved.sound <= 1 && saved.soundVariant < std::size(SoundProfiles))
                *this = saved;
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

    struct Application
    {
        HINSTANCE instance{};
        HWND window{}, overlay{}, effect{}, minutes{}, patches{}, sound{}, soundVariant{}, degaussButton{}, stopButton{}, status{};
        struct Layout { HWND window; int x, y, width, height; };
        HFONT font{};
        std::vector<Layout> layout;
        HWND minutesArrow{};
        Settings settings;
        Renderer renderer;
        degauss::Cycle cycle;
        std::array<float, 4> drawn{};
        Clock::time_point previousTick{};
        std::wstring statusText = L"Stopped";
        UINT taskbarCreated{}, disableAll{};
        int slowFrames{};
        bool running{}, opaque{}, rendered{}, tray{}, hotkey{}, sessionNotifications{}, applyError{};
        bool settingsReady{};

        int Dip(int n) const { return MulDiv(n, GetDpiForWindow(window), 96); }

        HWND Add(const wchar_t* type, const wchar_t* text, DWORD style,
            int x, int y, int width, int height, int id = 0)
        {
            const auto control = CreateWindowExW(0, type, text, WS_CHILD | WS_VISIBLE | style,
                Dip(x), Dip(y), Dip(width), Dip(height), window,
                reinterpret_cast<HMENU>(static_cast<INT_PTR>(id)), instance, nullptr);
            if (!control) winrt::throw_last_error();
            SendMessageW(control, WM_SETFONT, reinterpret_cast<WPARAM>(font), TRUE);
            if (id != MinutesArrow) layout.push_back({control, x, y, width, height});
            return control;
        }

        static HFONT MessageFont(UINT dpi)
        {
            NONCLIENTMETRICSW metrics{sizeof(metrics)};
            if (!SystemParametersInfoForDpi(SPI_GETNONCLIENTMETRICS, sizeof(metrics), &metrics, 0, dpi))
                return nullptr;
            return CreateFontIndirectW(&metrics.lfMessageFont);
        }

        void Rescale(UINT dpi, const RECT& bounds)
        {
            const auto previous = font;
            if (auto updated = MessageFont(dpi)) font = updated;
            SetWindowPos(window, nullptr, bounds.left, bounds.top, bounds.right - bounds.left,
                bounds.bottom - bounds.top, SWP_NOZORDER | SWP_NOACTIVATE);
            for (const auto& item : layout) {
                SetWindowPos(item.window, nullptr, Dip(item.x), Dip(item.y), Dip(item.width), Dip(item.height),
                    SWP_NOZORDER | SWP_NOACTIVATE);
                SendMessageW(item.window, WM_SETFONT, reinterpret_cast<WPARAM>(font), TRUE);
            }
            if (minutesArrow) SendMessageW(minutesArrow, UDM_SETBUDDY, reinterpret_cast<WPARAM>(minutes), 0);
            if (previous && previous != font) DeleteObject(previous);
        }

        void CreateControls()
        {
            font = MessageFont(GetDpiForWindow(window));
            if (!font) winrt::throw_last_error();

            settings.Load();
            effect = Add(L"BUTTON", L"Effect", BS_AUTOCHECKBOX | WS_TABSTOP, 22, 18, 125, 26, Effect);
            Add(L"STATIC", L"Primary display", SS_LEFT, 147, 23, 190, 22);
            Add(L"STATIC", L"Minutes before Degauss", SS_LEFT, 22, 68, 241, 22);
            minutes = Add(L"EDIT", L"", ES_NUMBER | ES_AUTOHSCROLL | WS_BORDER | WS_TABSTOP, 272, 64, 90, 27, Minutes);
            SendMessageW(minutes, EM_SETLIMITTEXT, 3, 0);
            minutesArrow = Add(UPDOWN_CLASSW, L"", UDS_ALIGNRIGHT | UDS_SETBUDDYINT | UDS_ARROWKEYS,
                0, 0, 0, 0, MinutesArrow);
            SendMessageW(minutesArrow, UDM_SETBUDDY, reinterpret_cast<WPARAM>(minutes), 0);
            SendMessageW(minutesArrow, UDM_SETRANGE32, 1, degauss::MaximumMinutes);
            SendMessageW(minutesArrow, UDM_SETPOS32, 0, settings.minutes);
            Add(L"STATIC", L"Color patches", SS_LEFT, 22, 110, 241, 22);
            patches = Add(L"COMBOBOX", L"", CBS_DROPDOWNLIST | WS_VSCROLL | WS_TABSTOP, 272, 106, 90, 92, Patches);
            SendMessageW(patches, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"1"));
            SendMessageW(patches, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(L"2"));
            SendMessageW(patches, CB_SETCURSEL, settings.patches - 1, 0);
            sound = Add(L"BUTTON", L"Degauss sound", BS_AUTOCHECKBOX | WS_TABSTOP, 22, 148, 220, 27, Sound);
            SendMessageW(sound, BM_SETCHECK, settings.sound ? BST_CHECKED : BST_UNCHECKED, 0);
            Add(L"STATIC", L"Sound profile", SS_LEFT, 22, 193, 130, 22);
            soundVariant = Add(L"COMBOBOX", L"", CBS_DROPDOWNLIST | WS_VSCROLL | WS_TABSTOP,
                170, 188, 192, 180, SoundVariant);
            for (const auto& profile : SoundProfiles)
                SendMessageW(soundVariant, CB_ADDSTRING, 0, reinterpret_cast<LPARAM>(profile.title));
            SendMessageW(soundVariant, CB_SETCURSEL, settings.soundVariant, 0);
            EnableWindow(soundVariant, settings.sound != 0);
            degaussButton = Add(L"BUTTON", L"Degauss", BS_PUSHBUTTON | WS_TABSTOP, 22, 237, 100, 31, Degauss);
            stopButton = Add(L"BUTTON", L"Stop", BS_PUSHBUTTON | WS_TABSTOP, 133, 237, 100, 31, Stop);
            status = Add(L"STATIC", L"Stopped", SS_LEFT, 22, 281, 340, 64);
            taskbarCreated = RegisterWindowMessageW(L"TaskbarCreated");
            disableAll = RegisterWindowMessageW(L"VibeWare.DesktopEffects.DisableAll.v1");
            hotkey = RegisterHotKey(window, PanicHotkey, MOD_WIN | MOD_SHIFT | MOD_NOREPEAT, VK_F9) != FALSE;
            sessionNotifications = WTSRegisterSessionNotification(window, NOTIFY_FOR_THIS_SESSION) != FALSE;
            AddTray();
            settingsReady = true;
            UpdateControls();
        }

        void SaveControls()
        {
            if (!settingsReady) return;
            BOOL number;
            const UINT requested = GetDlgItemInt(window, Minutes, &number, FALSE);
            const auto selected = SendMessageW(patches, CB_GETCURSEL, 0, 0);
            const Settings before = settings;
            if (number && requested >= 1 && requested <= degauss::MaximumMinutes)
                settings.minutes = requested;
            else
                SetDlgItemInt(window, Minutes, settings.minutes, FALSE);
            if (selected == 0 || selected == 1)
                settings.patches = static_cast<DWORD>(selected + 1);
            settings.sound = SendMessageW(sound, BM_GETCHECK, 0, 0) == BST_CHECKED;
            const LRESULT variant = SendMessageW(soundVariant, CB_GETCURSEL, 0, 0);
            if (variant >= 0 && static_cast<size_t>(variant) < std::size(SoundProfiles))
                settings.soundVariant = static_cast<DWORD>(variant);
            EnableWindow(soundVariant, settings.sound != 0);
            if (before.sound && !settings.sound) PlaySoundW(nullptr, nullptr, 0);
            if (applyError || before.minutes != settings.minutes || before.patches != settings.patches ||
                before.sound != settings.sound || before.soundVariant != settings.soundVariant) {
                applyError = !settings.Save();
                if (applyError) Notify(L"Could not save preferences. The current values still apply.");
            }
        }

        void Status(const std::wstring& text)
        {
            if (statusText == text) return;
            statusText = text;
            SetWindowTextW(status, text.c_str());
        }

        void UpdateControls()
        {
            SendMessageW(effect, BM_SETCHECK, running ? BST_CHECKED : BST_UNCHECKED, 0);
            EnableWindow(degaussButton, running && !cycle.Degaussing());
            EnableWindow(stopButton, running);
        }

        void Start()
        {
            if (running) return;
            if (!hotkey || !sessionNotifications) {
                UpdateControls();
                Status(L"The emergency shortcut and session notifications must be available.");
                return;
            }
            try
            {
                const auto monitor = MonitorFromPoint(POINT{0, 0}, MONITOR_DEFAULTTOPRIMARY);
                MONITORINFO info{sizeof(info)};
                if (!GetMonitorInfoW(monitor, &info)) winrt::throw_last_error();
                const auto& bounds = info.rcMonitor;
                overlay = CreateWindowExW(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW |
                        WS_EX_NOACTIVATE | WS_EX_TOPMOST, OverlayClass, AppName, WS_POPUP,
                    bounds.left, bounds.top, bounds.right - bounds.left, bounds.bottom - bounds.top,
                    nullptr, nullptr, instance, nullptr);
                if (!overlay || !SetLayeredWindowAttributes(overlay, 0, 0, LWA_ALPHA) ||
                    !SetWindowDisplayAffinity(overlay, WDA_EXCLUDEFROMCAPTURE))
                    throw winrt::hresult_error(E_FAIL, L"Could not create a capture-excluded desktop overlay.");
                ShowWindow(overlay, SW_SHOWNOACTIVATE);
                renderer.Start(overlay, monitor, (AppDirectory() / L"Shaders" / L"Degauss.hlsl").wstring(),
                    CaptureFps, false);
                if (!SetTimer(window, RenderTimer, 34, nullptr)) winrt::throw_last_error();
                slowFrames = 0;
                previousTick = Clock::now();
                cycle.Start(Seconds());
                rendered = false;
                running = true;
                UpdateControls();
                Status(L"Waiting for a screen frame…");
            }
            catch (const winrt::hresult_error& error) { StopEffect(error.message().c_str(), true); }
            catch (...) { StopEffect(L"Could not start the effect. Enable it to retry.", true); }
        }

        void StopEffect(const std::wstring& reason, bool notify = false)
        {
            KillTimer(window, RenderTimer);
            running = opaque = false;
            if (cycle.Degaussing()) PlaySoundW(nullptr, nullptr, 0);
            cycle.Start(0);
            if (overlay) ShowWindow(overlay, SW_HIDE);
            renderer.Stop();
            if (overlay) DestroyWindow(overlay);
            overlay = nullptr;
            UpdateControls();
            Status(reason);
            if (notify) Notify(reason.c_str());
        }

        void RunDegauss()
        {
            if (running && cycle.Degauss(Seconds(), settings.minutes)) {
                UpdateControls();
                Status(L"Degaussing…");
                if (settings.sound) {
                    try {
                        PlaySoundW((AppDirectory() / SoundProfiles[settings.soundVariant].path).c_str(), nullptr,
                            SND_FILENAME | SND_ASYNC | SND_NODEFAULT);
                    } catch (const winrt::hresult_error&) {
                        // The visual degauss does not depend on audio availability.
                    }
                }
            }
        }

        void Tick()
        {
            if (!running) return;
            const auto start = Clock::now();
            if (start - previousTick > std::chrono::milliseconds(450)) {
                StopEffect(L"The desktop stopped responding. Enable the effect to retry.", true);
                return;
            }
            previousTick = start;
            try
            {
                const bool wasDegaussing = cycle.Degaussing();
                const auto frame = cycle.Sample(Seconds(), settings.minutes);
                // Quantize slow buildup so a still desktop is only redrawn for visible changes.
                const std::array<float, 4> parameters{static_cast<float>(std::round(frame.amount * 1024) / 1024),
                    settings.patches == 2 ? 1.0f : 0.0f, static_cast<float>(frame.pulse), 0.0f};
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
                if (wasDegaussing != cycle.Degaussing()) UpdateControls();
                if (frame.pulse >= 0) Status(L"Degaussing…");
                else if (frame.due) Status(L"Degauss due");
                else if (opaque) Status(L"Degauss in " + std::to_wstring(int(std::ceil(frame.remaining / 60))) + L" min");
                if (frame.notify)
                    Notify(L"Is it time for a degauss? Use Degauss in the notification area menu.");
            }
            catch (const winrt::hresult_error& error) { StopEffect(error.message().c_str(), true); }
            catch (...) { StopEffect(L"The desktop effect stopped. Enable it to retry.", true); }
        }

        NOTIFYICONDATAW TrayData() const
        {
            NOTIFYICONDATAW data{sizeof(data)};
            data.hWnd = window;
            data.uID = 1;
            return data;
        }

        void AddTray()
        {
            auto data = TrayData();
            data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
            data.uCallbackMessage = TrayMessage;
            data.hIcon = LoadIconW(nullptr, IDI_APPLICATION);
            wcscpy_s(data.szTip, AppName);
            tray = Shell_NotifyIconW(NIM_ADD, &data) != FALSE;
            if (tray) {
                data.uVersion = NOTIFYICON_VERSION_4;
                if (!Shell_NotifyIconW(NIM_SETVERSION, &data)) {
                    Shell_NotifyIconW(NIM_DELETE, &data);
                    tray = false;
                }
            }
        }

        void Notify(const wchar_t* text) const
        {
            if (!tray) return;
            auto data = TrayData();
            data.uFlags = NIF_INFO;
            data.dwInfoFlags = NIIF_INFO | NIIF_NOSOUND;
            wcscpy_s(data.szInfoTitle, AppName);
            wcsncpy_s(data.szInfo, text, _TRUNCATE);
            Shell_NotifyIconW(NIM_MODIFY, &data);
        }

        void Open() const
        {
            ShowWindow(window, SW_RESTORE);
            SetForegroundWindow(window);
        }

        void TrayEvent(WPARAM anchor, LPARAM event)
        {
            if (LOWORD(event) == NIN_SELECT || LOWORD(event) == NIN_KEYSELECT || LOWORD(event) == NIN_BALLOONUSERCLICK) {
                Open();
                return;
            }
            if (LOWORD(event) != WM_CONTEXTMENU) return;
            const HMENU menu = CreatePopupMenu();
            if (!menu) return;
            AppendMenuW(menu, MF_STRING, Preferences, L"Preferences");
            AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
            AppendMenuW(menu, running ? MF_CHECKED : MF_STRING, Effect, L"Effect");
            AppendMenuW(menu, running && !cycle.Degaussing() ? MF_STRING : MF_GRAYED, Degauss, L"Degauss");
            AppendMenuW(menu, running ? MF_STRING : MF_GRAYED, Stop, L"Stop");
            AppendMenuW(menu, MF_STRING, StopAll, L"Disable all effects");
            AppendMenuW(menu, MF_GRAYED, 0, statusText.c_str());
            AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
            AppendMenuW(menu, MF_STRING, Quit, L"Quit");
            POINT point{static_cast<short>(LOWORD(anchor)), static_cast<short>(HIWORD(anchor))};
            if (point.x == -1 && point.y == -1) GetCursorPos(&point);
            SetForegroundWindow(window);
            const auto command = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, point.x, point.y, 0, window, nullptr);
            DestroyMenu(menu);
            PostMessageW(window, WM_NULL, 0, 0);
            if (command) Command(command);
        }

        void Command(UINT id)
        {
            SaveControls();
            switch (id) {
            case Preferences: Open(); break;
            case Effect:
                if (running) StopEffect(L"Stopped");
                else Start();
                break;
            case Degauss: RunDegauss(); break;
            case Stop: StopEffect(L"Stopped"); break;
            case StopAll:
                StopEffect(L"All effects stopped manually");
                if (disableAll) PostMessageW(HWND_BROADCAST, disableAll, 0, 0);
                break;
            case Quit: DestroyWindow(window); break;
            }
        }
    };

    LRESULT CALLBACK OverlayProcedure(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
    {
        if (message == WM_NCHITTEST) return HTTRANSPARENT;
        if (message == WM_MOUSEACTIVATE) return MA_NOACTIVATE;
        return DefWindowProcW(window, message, wParam, lParam);
    }

    LRESULT CALLBACK SettingsProcedure(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
    {
        if (message == WM_NCCREATE) {
            const auto app = static_cast<Application*>(reinterpret_cast<const CREATESTRUCTW*>(lParam)->lpCreateParams);
            app->window = window;
            SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(app));
        }
        const auto app = reinterpret_cast<Application*>(GetWindowLongPtrW(window, GWLP_USERDATA));
        if (!app) return DefWindowProcW(window, message, wParam, lParam);
        if (app->disableAll && message == app->disableAll) {
            if (app->running) app->StopEffect(L"All effects stopped manually");
            return 0;
        }
        if (app->taskbarCreated && message == app->taskbarCreated) {
            app->AddTray();
            if (!app->tray) app->Open();
            return 0;
        }
        switch (message) {
        case TrayMessage: app->TrayEvent(wParam, lParam); return 0;
        case ReadSettingsMessage: app->SaveControls(); return 0;
        case WM_COMMAND:
            if ((LOWORD(wParam) == Minutes && HIWORD(wParam) == EN_KILLFOCUS) ||
                (LOWORD(wParam) == Patches && HIWORD(wParam) == CBN_SELCHANGE) ||
                (LOWORD(wParam) == SoundVariant && HIWORD(wParam) == CBN_SELCHANGE) ||
                (LOWORD(wParam) == Sound && HIWORD(wParam) == BN_CLICKED) || LOWORD(wParam) == IDOK)
                app->SaveControls();
            else if (LOWORD(wParam) >= Effect && LOWORD(wParam) <= Quit && HIWORD(wParam) == BN_CLICKED)
                app->Command(LOWORD(wParam));
            return 0;
        case WM_NOTIFY:
            if (lParam && reinterpret_cast<const NMHDR*>(lParam)->code == UDN_DELTAPOS)
                PostMessageW(window, ReadSettingsMessage, 0, 0);
            return 0;
        case WM_TIMER:
            if (wParam == RenderTimer) app->Tick();
            return 0;
        case WM_HOTKEY:
            if (wParam == PanicHotkey && app->running) app->StopEffect(L"Emergency stop");
            return 0;
        case WM_DISPLAYCHANGE:
            if (app->running) app->StopEffect(L"Display changed. Enable the effect to resume.");
            return 0;
        case WM_DPICHANGED:
            if (!app->settingsReady) break;
            app->Rescale(HIWORD(wParam), *reinterpret_cast<const RECT*>(lParam));
            return 0;
        case WM_WTSSESSION_CHANGE:
            if (app->running && (wParam == WTS_SESSION_LOCK || wParam == WTS_SESSION_LOGOFF ||
                wParam == WTS_CONSOLE_DISCONNECT || wParam == WTS_REMOTE_DISCONNECT))
                app->StopEffect(L"Stopped when the desktop locked or disconnected");
            return 0;
        case WM_POWERBROADCAST:
            if (app->running && wParam == PBT_APMSUSPEND) app->StopEffect(L"Stopped before system sleep");
            return TRUE;
        case WM_CLOSE:
            app->SaveControls();
            if (app->tray) ShowWindow(window, SW_HIDE);
            else DestroyWindow(window);
            return 0;
        case WM_DESTROY:
            app->StopEffect(L"Stopped");
            if (app->tray) {
                auto data = app->TrayData();
                Shell_NotifyIconW(NIM_DELETE, &data);
            }
            if (app->sessionNotifications) WTSUnRegisterSessionNotification(window);
            if (app->hotkey) UnregisterHotKey(window, PanicHotkey);
            PostQuitMessage(0);
            return 0;
        }
        return DefWindowProcW(window, message, wParam, lParam);
    }
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR, int show)
{
    winrt::handle singleton{CreateMutexW(nullptr, FALSE, L"Local\\VibeWare.CrtDegauss")};
    if (!singleton) return 1;
    if (GetLastError() == ERROR_ALREADY_EXISTS) {
        if (const auto window = FindWindowW(WindowClass, nullptr)) {
            ShowWindow(window, SW_RESTORE);
            SetForegroundWindow(window);
        }
        return 0;
    }
    try
    {
        winrt::init_apartment(winrt::apartment_type::single_threaded);
        const INITCOMMONCONTROLSEX controls{sizeof(controls), ICC_STANDARD_CLASSES | ICC_UPDOWN_CLASS};
        if (!InitCommonControlsEx(&controls)) winrt::throw_last_error();

        WNDCLASSW overlay{};
        overlay.hInstance = instance;
        overlay.lpfnWndProc = OverlayProcedure;
        overlay.lpszClassName = OverlayClass;
        if (!RegisterClassW(&overlay)) winrt::throw_last_error();

        WNDCLASSW settings{};
        settings.hInstance = instance;
        settings.lpfnWndProc = SettingsProcedure;
        settings.lpszClassName = WindowClass;
        settings.hIcon = LoadIconW(nullptr, IDI_APPLICATION);
        settings.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        settings.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
        if (!RegisterClassW(&settings)) winrt::throw_last_error();

        Application app;
        app.instance = instance;
        constexpr DWORD style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;
        const UINT dpi = GetDpiForSystem();
        RECT rect{0, 0, MulDiv(384, dpi, 96), MulDiv(354, dpi, 96)};
        AdjustWindowRectExForDpi(&rect, style, FALSE, 0, dpi);
        const auto window = CreateWindowExW(WS_EX_CONTROLPARENT, WindowClass, AppName, style,
            CW_USEDEFAULT, CW_USEDEFAULT, rect.right - rect.left, rect.bottom - rect.top,
            nullptr, nullptr, instance, &app);
        if (!window) winrt::throw_last_error();
        try { app.CreateControls(); }
        catch (...) {
            DestroyWindow(window);
            if (app.font) DeleteObject(app.font);
            throw;
        }
        ShowWindow(window, show);
        MSG message{};
        int result;
        while ((result = GetMessageW(&message, nullptr, 0, 0)) > 0) {
            if (!IsDialogMessageW(window, &message)) {
                TranslateMessage(&message);
                DispatchMessageW(&message);
            }
        }
        if (IsWindow(window)) DestroyWindow(window);
        DeleteObject(app.font);
        return result < 0 ? 1 : 0;
    }
    catch (const winrt::hresult_error& error) {
        MessageBoxW(nullptr, error.message().c_str(), AppName, MB_OK | MB_ICONERROR);
    }
    catch (...) {
        MessageBoxW(nullptr, L"CRT Degauss could not start.", AppName, MB_OK | MB_ICONERROR);
    }
    return 1;
}
