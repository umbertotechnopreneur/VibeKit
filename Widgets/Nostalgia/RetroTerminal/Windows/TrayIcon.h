// SPDX-License-Identifier: MIT
#pragma once
#include <windows.h>
#include <shellapi.h>
#include <cwchar>

class TrayIcon final
{
public:
    static constexpr UINT CallbackMessage = WM_APP + 17;
    using StudioMenu = void (__cdecl*)(int x, int y);
    void SetStudioMenu(StudioMenu menu) { studioMenu = menu; }
    enum class Action { None, Open, Enable };
    static constexpr UINT_PTR PreviewTimer = 4;
    bool IsDisableAll(UINT message) const { return disableAllMessage && message == disableAllMessage; }
    bool IsEnableAll(UINT message) const { return enableAllMessage && message == enableAllMessage; }
    void RequestEnable() { action = Action::Enable; }
    void CancelPendingStart()
    {
        if (action == Action::Enable) action = Action::None;
    }

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

    void Initialize(HWND messageWindow)
    {
        window = messageWindow;
        taskbarCreated = RegisterWindowMessageW(L"TaskbarCreated");
        disableAllMessage = RegisterWindowMessageW(L"VibeWare.DesktopEffects.DisableAll.v1");
        enableAllMessage = RegisterWindowMessageW(L"VibeWare.DesktopEffects.EnableAll.v1");
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
    HWND window{};
    StudioMenu studioMenu{};
    ULONGLONG previewUntil{};
    UINT taskbarCreated{}, disableAllMessage{}, enableAllMessage{};
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
        // The published executable carries the same multi-size icon as the app window.
        wchar_t executable[32768];
        const auto length = GetModuleFileNameW(nullptr, executable, ARRAYSIZE(executable));
        HICON applicationIcon{};
        if (length && length < ARRAYSIZE(executable))
            ExtractIconExW(executable, 0, nullptr, &applicationIcon, 1);
        if (!applicationIcon) return false;
        data.hIcon = applicationIcon;
        wcscpy_s(data.szTip, L"Retro Terminal");
        visible = Shell_NotifyIconW(NIM_ADD, &data) != FALSE;
        DestroyIcon(applicationIcon);
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
        if (!studioMenu) return;
        POINT point{static_cast<short>(LOWORD(anchor)), static_cast<short>(HIWORD(anchor))};
        if (!versionFour || (point.x == -1 && point.y == -1)) {
            if (!GetCursorPos(&point)) return;
        }
        studioMenu(point.x, point.y);
    }
};
