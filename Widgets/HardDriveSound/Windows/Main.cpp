// SPDX-License-Identifier: MIT
#include "Audio.h"
#include <shellapi.h>
#include <shlobj.h>
#include <algorithm>
#include <string>

namespace {
constexpr wchar_t Name[] = L"Hard Drive Sound";
constexpr wchar_t WindowClass[] = L"VibeWareHardDriveSound";
constexpr wchar_t Key[] = L"Software\\VibeWare\\HardDriveSound";
constexpr wchar_t RunKey[] = L"Software\\Microsoft\\Windows\\CurrentVersion\\Run";
constexpr UINT TrayEvent = WM_APP + 1;
constexpr UINT ShowPreferences = WM_APP + 2;
enum Id { Effect = 100, StartWithSession, Idle, Volume, Quiet, Density, Retry,
          Choose, BuiltIn, SpinUp, Seek, SpinDown, Stop, Quit, Preferences };
constexpr unsigned Poll = 1, Render = 2;

std::filesystem::path Executable()
{
    std::wstring path(32768, L'\0');
    const DWORD count = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
    if (!count || count >= path.size()) return {};
    path.resize(count);
    return path;
}

struct Options
{
    bool enabled = true, session = false, idle = false;
    unsigned volume = 55, quiet = 8, density = 100, retry = 0;
    std::wstring profile;

    void Load()
    {
        auto number = [](const wchar_t* name, unsigned fallback, unsigned limit) {
            DWORD value{}, size = sizeof(value);
            return RegGetValueW(HKEY_CURRENT_USER, Key, name, RRF_RT_REG_DWORD, nullptr, &value, &size) == ERROR_SUCCESS
                ? std::min<unsigned>(value, limit) : fallback;
        };
        enabled = number(L"Enabled", enabled, 1);
        session = number(L"Session", session, 1);
        idle = number(L"Idle", idle, 1);
        volume = number(L"Volume", volume, 100);
        quiet = number(L"Quiet", quiet, 120);
        density = number(L"Density", density, 150);
        retry = number(L"Retry", retry, 600);
        DWORD bytes{};
        if (RegGetValueW(HKEY_CURRENT_USER, Key, L"Profile", RRF_RT_REG_SZ, nullptr, nullptr, &bytes) == ERROR_SUCCESS &&
            bytes > 0 && bytes <= 65536) {
            std::wstring saved(bytes / sizeof(wchar_t), L'\0');
            if (RegGetValueW(HKEY_CURRENT_USER, Key, L"Profile", RRF_RT_REG_SZ, nullptr, saved.data(), &bytes) == ERROR_SUCCESS)
                profile = saved.c_str();
        }
    }

    bool Save() const
    {
        HKEY key{};
        if (RegCreateKeyExW(HKEY_CURRENT_USER, Key, 0, nullptr, 0, KEY_SET_VALUE, nullptr, &key, nullptr) != ERROR_SUCCESS)
            return false;
        bool ok = true;
        auto number = [&](const wchar_t* name, DWORD value) {
            ok &= RegSetValueExW(key, name, 0, REG_DWORD, reinterpret_cast<const BYTE*>(&value), sizeof(value)) == ERROR_SUCCESS;
        };
        number(L"Enabled", enabled); number(L"Session", session); number(L"Idle", idle);
        number(L"Volume", volume); number(L"Quiet", quiet); number(L"Density", density); number(L"Retry", retry);
        ok &= RegSetValueExW(key, L"Profile", 0, REG_SZ, reinterpret_cast<const BYTE*>(profile.c_str()),
            static_cast<DWORD>((profile.size() + 1) * sizeof(wchar_t))) == ERROR_SUCCESS;
        RegCloseKey(key);
        return ok;
    }

    bool SetStartup() const
    {
        if (!session) {
            const auto result = RegDeleteKeyValueW(HKEY_CURRENT_USER, RunKey, WindowClass);
            return result == ERROR_SUCCESS || result == ERROR_FILE_NOT_FOUND;
        }
        const auto command = L"\"" + Executable().wstring() + L"\" --autostart";
        return RegSetKeyValueW(HKEY_CURRENT_USER, RunKey, WindowClass, REG_SZ, command.c_str(),
            static_cast<DWORD>((command.size() + 1) * sizeof(wchar_t))) == ERROR_SUCCESS;
    }
};

struct Application
{
    HWND window{}, profile{}, status{};
    HINSTANCE instance{};
    Audio audio;
    DiskCounter disk;
    Options options;
    std::filesystem::path defaults;
    UINT taskbar{};
    bool failed{}, tray{}, ready{}, ending{};

    HWND Control(const wchar_t* type, const wchar_t* text, int id, DWORD style, int x, int y, int w, int h)
    {
        const auto handle = CreateWindowW(type, text, WS_CHILD | WS_VISIBLE | style, x, y, w, h,
            window, reinterpret_cast<HMENU>(static_cast<INT_PTR>(id)), instance, nullptr);
        SendMessageW(handle, WM_SETFONT, reinterpret_cast<WPARAM>(GetStockObject(DEFAULT_GUI_FONT)), TRUE);
        return handle;
    }

    bool Initialize()
    {
        if (!audio.mixer) return false;
        options.Load();
        defaults = Executable().parent_path() / L"default";
        if (!audio.Load(options.profile.empty() ? defaults : std::filesystem::path(options.profile))) {
            options.profile.clear();
            if (!audio.Load(defaults)) return false;
        }
        struct Label { const wchar_t* text; Id id; };
        const Label checks[]{{L"Effect", Effect}, {L"Play spin-up at sign-in and spin-down at sign-out", StartWithSession},
                             {L"Quiet spindle sound", Idle}};
        for (size_t i = 0; i < std::size(checks); ++i)
            Control(L"BUTTON", checks[i].text, checks[i].id, BS_AUTOCHECKBOX | WS_TABSTOP, 20, 18 + int(i) * 30, 355, 25);
        const Label fields[]{{L"Volume (%)", Volume}, {L"Idle clicks (seconds, 0 off)", Quiet},
                             {L"Seek density (%)", Density}, {L"Head retry (seconds, 0 off)", Retry}};
        for (size_t i = 0; i < std::size(fields); ++i) {
            const int y = 117 + int(i) * 36;
            Control(L"STATIC", fields[i].text, 0, 0, 20, y + 3, 245, 24);
            Control(L"EDIT", L"", fields[i].id, ES_NUMBER | WS_BORDER | WS_TABSTOP, 275, y, 60, 25);
        }
        profile = Control(L"STATIC", L"", 0, SS_ENDELLIPSIS, 20, 269, 335, 24);
        Control(L"BUTTON", L"Choose folder…", Choose, WS_TABSTOP, 20, 297, 128, 27);
        Control(L"BUTTON", L"Built-in", BuiltIn, WS_TABSTOP, 154, 297, 90, 27);
        Control(L"BUTTON", L"Spin-up", SpinUp, WS_TABSTOP, 20, 336, 91, 28);
        Control(L"BUTTON", L"Test seeks", Seek, WS_TABSTOP, 120, 336, 100, 28);
        Control(L"BUTTON", L"Spin-down", SpinDown, WS_TABSTOP, 229, 336, 105, 28);
        status = Control(L"STATIC", L"", 0, 0, 20, 380, 336, 38);
        Sync();
        ready = true;
        Apply();
        taskbar = RegisterWindowMessageW(L"TaskbarCreated");
        AddTray();
        return SetTimer(window, Poll, 250, nullptr) != 0;
    }

    void Status(const wchar_t* value) { SetWindowTextW(status, value); }
    void Sync()
    {
        CheckDlgButton(window, Effect, options.enabled ? BST_CHECKED : BST_UNCHECKED);
        CheckDlgButton(window, StartWithSession, options.session ? BST_CHECKED : BST_UNCHECKED);
        CheckDlgButton(window, Idle, options.idle ? BST_CHECKED : BST_UNCHECKED);
        SetDlgItemInt(window, Volume, options.volume, FALSE);
        SetDlgItemInt(window, Quiet, options.quiet, FALSE);
        SetDlgItemInt(window, Density, options.density, FALSE);
        SetDlgItemInt(window, Retry, options.retry, FALSE);
        SetWindowTextW(profile, options.profile.empty() ? L"Built-in vintage drive" :
            std::filesystem::path(options.profile).filename().c_str());
    }

    void Save() { if (!options.Save()) Status(L"Could not save preferences."); }
    void Apply()
    {
        hdd_configure(audio.mixer, options.volume / 100.f, options.idle, options.enabled && !failed);
        hdd_frequency(audio.mixer, options.quiet, options.density, options.retry);
        if (!options.enabled) { audio.Stop(); KillTimer(window, Render); }
        if (!failed) Status(options.enabled ? L"Following disk activity" : L"Stopped");
    }
    void SetEnabled(bool enabled)
    {
        options.enabled = enabled;
        failed = false;
        Sync();
        Apply();
        Save();
    }
    void SaveNumbers()
    {
        const auto get = [&](Id id, unsigned previous, unsigned max) {
            BOOL valid{};
            const auto value = GetDlgItemInt(window, id, &valid, FALSE);
            return valid ? std::min(value, max) : previous;
        };
        options.volume = get(Volume, options.volume, 100);
        options.quiet = get(Quiet, options.quiet, 120);
        options.density = get(Density, options.density, 150);
        options.retry = get(Retry, options.retry, 600);
        Sync();
        Apply();
        Save();
    }

    void Pump()
    {
        if (!audio.Tick()) {
            failed = true;
            Apply();
            Status(L"Could not play sound. Toggle Effect to retry.");
        }
        if (!audio.Playing()) KillTimer(window, Render);
    }

    void BeginOutput()
    {
        if (!audio.Playing() && hdd_pending(audio.mixer)) {
            Pump();
            if (audio.Playing()) SetTimer(window, Render, 15, nullptr);
        }
    }

    void Preview(int kind)
    {
        if (!options.enabled || failed) SetEnabled(true);
        if (kind != 2) audio.Stop();
        hdd_trigger(audio.mixer, kind);
        BeginOutput();
    }

    void ChooseProfile(bool builtIn)
    {
        std::filesystem::path selected;
        if (!builtIn) {
            BROWSEINFOW dialog{};
            dialog.hwndOwner = window;
            dialog.lpszTitle = L"Choose a sound profile folder";
            dialog.ulFlags = BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE;
            auto* item = SHBrowseForFolderW(&dialog);
            if (!item) return;
            wchar_t folder[MAX_PATH]{};
            const bool found = SHGetPathFromIDListW(item, folder) != FALSE;
            CoTaskMemFree(item);
            if (!found) return;
            selected = folder;
        }
        if (!audio.Load(builtIn ? defaults : selected)) {
            Status(L"This folder must contain the seven WAV files from a sample profile.");
            return;
        }
        options.profile = selected.wstring();
        Sync();
        Apply();
        Save();
    }

    NOTIFYICONDATAW TrayData() const
    {
        NOTIFYICONDATAW data{};
        data.cbSize = sizeof(data);
        data.hWnd = window;
        data.uID = 1;
        return data;
    }
    void AddTray()
    {
        auto data = TrayData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = TrayEvent;
        data.hIcon = LoadIconW(nullptr, IDI_APPLICATION);
        std::copy_n(Name, std::size(Name), data.szTip);
        tray = Shell_NotifyIconW(NIM_ADD, &data) != FALSE;
    }
    void Open()
    {
        ShowWindow(window, SW_RESTORE);
        SetForegroundWindow(window);
    }
    void Menu()
    {
        const auto menu = CreatePopupMenu();
        if (!menu) return;
        AppendMenuW(menu, MF_STRING, Preferences, L"Preferences");
        AppendMenuW(menu, MF_STRING, SpinUp, L"Play spin-up");
        AppendMenuW(menu, MF_STRING, SpinDown, L"Play spin-down");
        AppendMenuW(menu, MF_STRING, Stop, L"Stop");
        AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
        AppendMenuW(menu, MF_STRING, Quit, L"Quit");
        POINT pointer{};
        GetCursorPos(&pointer);
        SetForegroundWindow(window);
        const auto id = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pointer.x, pointer.y, 0, window, nullptr);
        DestroyMenu(menu);
        PostMessageW(window, WM_NULL, 0, 0);
        if (id) Command(id, BN_CLICKED);
    }
    void Command(unsigned id, unsigned event)
    {
        if (id >= Volume && id <= Retry && event == EN_KILLFOCUS) { SaveNumbers(); return; }
        if (event != BN_CLICKED) return;
        switch (id) {
        case Effect: SetEnabled(IsDlgButtonChecked(window, Effect) == BST_CHECKED); break;
        case StartWithSession:
            options.session = IsDlgButtonChecked(window, StartWithSession) == BST_CHECKED;
            if (options.SetStartup()) Save(); else Status(L"Could not change sign-in startup.");
            break;
        case Idle:
            options.idle = IsDlgButtonChecked(window, Idle) == BST_CHECKED;
            Apply(); Save(); BeginOutput(); break;
        case Choose: ChooseProfile(false); break;
        case BuiltIn: ChooseProfile(true); break;
        case SpinUp: Preview(0); break;
        case Seek: Preview(2); break;
        case SpinDown: Preview(3); break;
        case Stop: SetEnabled(false); break;
        case Preferences: Open(); break;
        case Quit: DestroyWindow(window); break;
        }
    }

    void EndSession()
    {
        if (ending || !options.session || !options.enabled || options.volume == 0 || failed) return;
        ending = true;
        audio.Stop();
        hdd_trigger(audio.mixer, 1);
        BeginOutput();
    }
};

LRESULT CALLBACK WindowProc(HWND window, UINT message, WPARAM wp, LPARAM lp)
{
    if (message == WM_NCCREATE) {
        auto* app = static_cast<Application*>(reinterpret_cast<CREATESTRUCTW*>(lp)->lpCreateParams);
        app->window = window;
        SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(app));
    }
    auto* app = reinterpret_cast<Application*>(GetWindowLongPtrW(window, GWLP_USERDATA));
    if (!app || !app->ready) return DefWindowProcW(window, message, wp, lp);
    if (app->taskbar && message == app->taskbar) { app->AddTray(); return 0; }
    switch (message) {
    case ShowPreferences: app->Open(); return 0;
    case TrayEvent:
        if (lp == WM_LBUTTONUP) app->Open();
        else if (lp == WM_RBUTTONUP) app->Menu();
        return 0;
    case WM_COMMAND: app->Command(LOWORD(wp), HIWORD(wp)); return 0;
    case WM_TIMER:
        if (wp == Poll) {
            const float rate = app->disk.Read();
            if (!app->failed && app->options.enabled) {
                hdd_activity(app->audio.mixer, rate);
                app->BeginOutput();
            }
        } else if (wp == Render) app->Pump();
        return 0;
    case WM_QUERYENDSESSION: return TRUE;
    case WM_ENDSESSION:
        if (wp && (lp & ENDSESSION_LOGOFF)) app->EndSession();
        return 0;
    case WM_CLOSE:
        if (app->tray) ShowWindow(window, SW_HIDE); else DestroyWindow(window);
        return 0;
    case WM_DESTROY: {
        auto data = app->TrayData();
        Shell_NotifyIconW(NIM_DELETE, &data);
        PostQuitMessage(0);
        return 0;
    }
    }
    return DefWindowProcW(window, message, wp, lp);
}
}

int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, PWSTR args, int show)
{
    HANDLE mutex = CreateMutexW(nullptr, FALSE, L"Local\\VibeWare.HardDriveSound");
    if (!mutex) return 1;
    const bool startup = std::wstring_view(args) == L"--autostart";
    if (GetLastError() == ERROR_ALREADY_EXISTS) {
        if (!startup)
            if (const auto window = FindWindowW(WindowClass, nullptr)) PostMessageW(window, ShowPreferences, 0, 0);
        CloseHandle(mutex);
        return 0;
    }
    const auto com = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    int result = 1;
    {
        Application app;
        app.instance = instance;
        WNDCLASSW type{};
        type.hInstance = instance;
        type.lpszClassName = WindowClass;
        type.lpfnWndProc = WindowProc;
        type.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        type.hIcon = LoadIconW(nullptr, IDI_APPLICATION);
        type.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
        if (RegisterClassW(&type)) {
            const auto window = CreateWindowW(WindowClass, Name, WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX,
                CW_USEDEFAULT, CW_USEDEFAULT, 391, 470, nullptr, nullptr, instance, &app);
            if (window && app.Initialize()) {
                if (startup && app.options.session && app.options.enabled) app.Preview(0);
                if (!startup || !app.tray) ShowWindow(window, show);
                MSG message{};
                while ((result = GetMessageW(&message, nullptr, 0, 0)) > 0) {
                    if (!IsDialogMessageW(window, &message)) {
                        TranslateMessage(&message);
                        DispatchMessageW(&message);
                    }
                }
            } else MessageBoxW(window, L"Could not initialize Hard Drive Sound.", Name, MB_OK | MB_ICONERROR);
        }
    }
    if (SUCCEEDED(com)) CoUninitialize();
    CloseHandle(mutex);
    return result == 0 ? 0 : 1;
}
