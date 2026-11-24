// SPDX-License-Identifier: MIT
#pragma once
#include <windows.h>
#include <shellapi.h>
#include <cwchar>
#include <string>

class TrayIcon final
{
public:
    static constexpr UINT CallbackMessage = WM_APP + 17;
    enum class Action { None, Open, Quit, Preferences, About, Enable, Stop, Preview, Green, Amber, Arcade, Glitch };
    static constexpr UINT_PTR PreviewTimer = 4;

    bool BeginPreview()
    {
        previewUntil = GetTickCount64() + 15000;
        if (SetTimer(window, PreviewTimer, 15000, nullptr)) return true;
        previewUntil = 0;
        return false;
    }

    bool Previewing() const { return previewUntil != 0; }
    bool PreviewDue() const { return Previewing() && GetTickCount64() >= previewUntil; }
    void CancelPreview()
    {
        if (Previewing()) KillTimer(window, PreviewTimer);
        previewUntil = 0;
    }

    void Initialize(HWND messageWindow, HWND appWindow)
    {
        window = messageWindow;
        owner = appWindow;
        taskbarCreated = RegisterWindowMessageW(L"TaskbarCreated");
    }

    bool Keep()
    {
        kept = Show();
        return kept;
    }

    void Remove()
    {
        kept = false;
        Hide();
    }

    void Notify(const wchar_t* title, const wchar_t* detail, bool silent = false)
    {
        if (!Show()) return;
        auto data = Data();
        data.uFlags = NIF_INFO;
        data.dwInfoFlags = NIIF_INFO | (silent ? NIIF_NOSOUND : 0);
        wcsncpy_s(data.szInfoTitle, title, _TRUNCATE);
        wcsncpy_s(data.szInfo, detail, _TRUNCATE);
        if (!Shell_NotifyIconW(NIM_MODIFY, &data) && !kept) Hide();
    }

    bool HandleMessage(UINT message, WPARAM anchor, LPARAM value, bool active, bool running, const std::wstring& status)
    {
        if (taskbarCreated && message == taskbarCreated) {
            visible = false;
            if (kept && !Show()) {
                kept = false;
                action = Action::Open;
            }
            return true;
        }
        if (message != CallbackMessage) return false;
        if (versionFour && HIWORD(value) != 1) return true;
        const UINT event = versionFour ? LOWORD(value) : UINT(value);
        switch (event) {
        case NIN_BALLOONUSERCLICK:
            if (!kept) Hide();
            [[fallthrough]];
        case NIN_SELECT:
        case NIN_KEYSELECT:
        case WM_LBUTTONUP:
            action = Action::Open;
            break;
        case WM_CONTEXTMENU:
        case WM_RBUTTONUP:
            ShowMenu(anchor, active, running, status);
            break;
        case NIN_BALLOONHIDE:
        case NIN_BALLOONTIMEOUT:
            if (!kept) Hide();
            break;
        }
        return true;
    }

    Action TakeAction()
    {
        const auto result = action;
        action = Action::None;
        return result;
    }

private:
    HWND window{}, owner{};
    ULONGLONG previewUntil{};
    UINT taskbarCreated{};
    bool kept{}, visible{}, versionFour{};
    Action action = Action::None;

    NOTIFYICONDATAW Data() const
    {
        NOTIFYICONDATAW data{};
        data.cbSize = sizeof(data);
        data.hWnd = window;
        data.uID = 1;
        return data;
    }

    bool Show()
    {
        if (visible) return true;
        auto data = Data();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = reinterpret_cast<HICON>(SendMessageW(owner, WM_GETICON, ICON_SMALL2, 0));
        if (!data.hIcon) data.hIcon = LoadIconW(nullptr, IDI_APPLICATION);
        wcscpy_s(data.szTip, L"Retro Terminal");
        visible = Shell_NotifyIconW(NIM_ADD, &data) != FALSE;
        if (visible) {
            data.uVersion = NOTIFYICON_VERSION_4;
            versionFour = Shell_NotifyIconW(NIM_SETVERSION, &data) != FALSE;
        }
        return visible;
    }

    void Hide()
    {
        if (!visible) return;
        auto data = Data();
        Shell_NotifyIconW(NIM_DELETE, &data);
        visible = versionFour = false;
    }

    void ShowMenu(WPARAM anchor, bool active, bool running, const std::wstring& status)
    {
        constexpr int miniUtilitiesCommand = 100, disableEffectCommand = 101;
        const auto menu = CreatePopupMenu();
        if (!menu) return;
        POINT point{static_cast<short>(LOWORD(anchor)), static_cast<short>(HIWORD(anchor))};
        if (!versionFour || (point.x == -1 && point.y == -1)) GetCursorPos(&point);
        const auto palette = CreatePopupMenu();
        if (!palette) { DestroyMenu(menu); return; }
        if (!AppendMenuW(palette, MF_STRING, UINT_PTR(Action::Green), L"Green phosphor") ||
            !AppendMenuW(palette, MF_STRING, UINT_PTR(Action::Amber), L"Amber terminal") ||
            !AppendMenuW(palette, MF_STRING, UINT_PTR(Action::Arcade), L"Arcade color") ||
            !AppendMenuW(menu, MF_STRING, UINT_PTR(Action::Preferences), L"Preferences") ||
            !AppendMenuW(menu, MF_STRING, UINT_PTR(Action::Open), L"Open") ||
            !AppendMenuW(menu, MF_SEPARATOR, 0, nullptr) ||
            !AppendMenuW(menu, active ? MF_CHECKED : MF_STRING, (active ? disableEffectCommand : UINT_PTR(Action::Enable)), L"Effect") ||
            !AppendMenuW(menu, active ? MF_GRAYED : MF_STRING, UINT_PTR(Action::Preview), L"Try for 15 seconds") ||
            !AppendMenuW(menu, MF_POPUP, reinterpret_cast<UINT_PTR>(palette), L"Palette")) {
            DestroyMenu(palette); DestroyMenu(menu);
            return;
        }
        // The parent menu now owns the palette submenu.
        const auto summary = status.substr(0, status.find(L'\n'));
        if (!AppendMenuW(menu, running ? MF_STRING : MF_GRAYED, UINT_PTR(Action::Glitch), L"Try a glitch now") ||
            !AppendMenuW(menu, MF_STRING, UINT_PTR(Action::Stop), L"Stop") ||
            !AppendMenuW(menu, MF_GRAYED, 0, summary.c_str()) ||
            !AppendMenuW(menu, MF_SEPARATOR, 0, nullptr) ||
            !AppendMenuW(menu, MF_STRING, UINT_PTR(Action::Quit), L"Quit") ||
            !AppendMenuW(menu, MF_SEPARATOR, 0, nullptr) ||
            !AppendMenuW(menu, MF_STRING, miniUtilitiesCommand, L"Mini Utilities") ||
            !AppendMenuW(menu, MF_STRING, UINT_PTR(Action::About), L"About")) {
            DestroyMenu(menu);
            return;
        }
        SetForegroundWindow(window);
        const auto result = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY,
            point.x, point.y, 0, window, nullptr);
        if (result == miniUtilitiesCommand)
            ShellExecuteW(owner, L"open", L"https://umbertogiacobbi.biz/mini-utilities/", nullptr, nullptr, SW_SHOWNORMAL);
        else if (result == disableEffectCommand) action = Action::Stop;
        else if (result)
            action = static_cast<Action>(result);
        PostMessageW(window, WM_NULL, 0, 0);
        DestroyMenu(menu);
    }
};
