// SPDX-License-Identifier: MIT
#pragma once
#include <windows.h>
#include <shellapi.h>
#include <cwchar>

class TrayIcon final
{
public:
    static constexpr UINT CallbackMessage = WM_APP + 17;
    enum class Action { None, Open, Quit, Preferences, About };

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

    bool HandleMessage(UINT message, WPARAM anchor, LPARAM value)
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
            ShowMenu(anchor);
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

    void ShowMenu(WPARAM anchor)
    {
        constexpr int miniUtilitiesCommand = 100;
        const auto menu = CreatePopupMenu();
        if (!menu) return;
        POINT point{static_cast<short>(LOWORD(anchor)), static_cast<short>(HIWORD(anchor))};
        if (!versionFour || (point.x == -1 && point.y == -1)) GetCursorPos(&point);
        if (!AppendMenuW(menu, MF_STRING, UINT_PTR(Action::Preferences), L"Preferences") ||
            (owner != window && !AppendMenuW(menu, MF_STRING, UINT_PTR(Action::Open), L"Open Retro Terminal")) ||
            !AppendMenuW(menu, MF_SEPARATOR, 0, nullptr) ||
            !AppendMenuW(menu, MF_STRING, UINT_PTR(Action::Quit), L"Quit") ||
            !AppendMenuW(menu, MF_SEPARATOR, 0, nullptr) ||
            !AppendMenuW(menu, MF_STRING, miniUtilitiesCommand, L"Mini Utilities") ||
            !AppendMenuW(menu, MF_STRING, UINT_PTR(Action::About), L"About Retro Terminal")) {
            DestroyMenu(menu);
            return;
        }
        SetForegroundWindow(window);
        const auto result = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY,
            point.x, point.y, 0, window, nullptr);
        if (result == miniUtilitiesCommand)
            ShellExecuteW(owner, L"open", L"https://umbertogiacobbi.biz/mini-utilities/", nullptr, nullptr, SW_SHOWNORMAL);
        else if (result)
            action = static_cast<Action>(result);
        PostMessageW(window, WM_NULL, 0, 0);
        DestroyMenu(menu);
    }
};
