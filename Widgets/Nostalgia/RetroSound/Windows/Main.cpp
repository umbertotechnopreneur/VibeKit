// SPDX-License-Identifier: MIT
#include "Audio.h"
#include "../../Shared/Native/Engine.h"
#include <algorithm>
#include <string>
#include <type_traits>

namespace {
constexpr wchar_t Name[] = L"Retro Sound";
constexpr wchar_t WindowClass[] = L"VibeWareHardDriveSound";
constexpr wchar_t Key[] = L"Software\\VibeWare\\HardDriveSound";
constexpr wchar_t RunKey[] = L"Software\\Microsoft\\Windows\\CurrentVersion\\Run";
constexpr UINT_PTR Poll = 0x5653504C, Render = 0x5653524E;
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

    bool Load()
    {
        auto number = [](const wchar_t* name, auto& target, unsigned limit) {
            DWORD value{}, size = sizeof(value);
            const auto result = RegGetValueW(HKEY_CURRENT_USER, Key, name, RRF_RT_REG_DWORD, nullptr, &value, &size);
            if (result == ERROR_FILE_NOT_FOUND || result == ERROR_PATH_NOT_FOUND) return true;
            if (result != ERROR_SUCCESS || size != sizeof(value) || value > limit) return false;
            target = static_cast<std::remove_reference_t<decltype(target)>>(value);
            return true;
        };
        if (!number(L"Enabled", enabled, 1) || !number(L"Session", session, 1) ||
            !number(L"Idle", idle, 1) || !number(L"Volume", volume, 100) || !number(L"Quiet", quiet, 120) ||
            !number(L"Density", density, 150) || !number(L"Retry", retry, 600)) return false;
        DWORD bytes{};
        const auto result = RegGetValueW(HKEY_CURRENT_USER, Key, L"Profile", RRF_RT_REG_SZ, nullptr, nullptr, &bytes);
        if (result == ERROR_FILE_NOT_FOUND || result == ERROR_PATH_NOT_FOUND) return true;
        if (result != ERROR_SUCCESS || !bytes || bytes > 65536 || bytes % sizeof(wchar_t)) return false;
        std::wstring saved(bytes / sizeof(wchar_t), L'\0');
        if (RegGetValueW(HKEY_CURRENT_USER, Key, L"Profile", RRF_RT_REG_SZ, nullptr, saved.data(), &bytes) != ERROR_SUCCESS ||
            !bytes || bytes % sizeof(wchar_t) || bytes > saved.size() * sizeof(wchar_t)) return false;
        // The size probe may include room for a terminator already present in the stored value.
        saved.resize(bytes / sizeof(wchar_t));
        if (saved.find(L'\0') != saved.size() - 1) return false;
        saved.pop_back();
        profile = std::move(saved);
        return true;
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
        const auto executable = Executable();
        if (executable.empty()) return false;
        const auto command = L"\"" + executable.wstring() + L"\" --autostart";
        return RegSetKeyValueW(HKEY_CURRENT_USER, RunKey, WindowClass, REG_SZ, command.c_str(),
            static_cast<DWORD>((command.size() + 1) * sizeof(wchar_t))) == ERROR_SUCCESS;
    }
};

struct Application final : WidgetEngine
{
    Application(HWND owner, NotifyUser notify) : WidgetEngine(owner, Name, notify) {}
    ~Application() override { KillTimer(window, Poll); KillTimer(window, Render); }

    Audio audio;
    DiskCounter disk;
    Options options;
    std::filesystem::path directory;
    bool profileReady{}, failed{}, ending{};

    bool LoadProfile(const std::wstring& profile, int retry)
    {
        std::filesystem::path folder;
        if (profile.empty()) folder = directory / L"default";
        else if (profile == L"@warm") folder = directory / L"warm";
        else if (profile == L"@computer") folder = directory / L"computer";
        else folder = profile;
        const int result = folder.is_absolute() ? audio.Load(folder, retry) : 0;
        if (result != 1) {
            Status(result == -1 ? L"Head retry requires error.wav. Add the recording or set Head retry to 0." :
                L"Could not load the sound profile. Check its 24 kHz mono 16-bit PCM WAV files or choose another profile.");
            Notify(statusText.c_str());
            return false;
        }
        hdd_frequency(audio.mixer, options.quiet, options.density, retry);
        profileReady = true;
        failed = false;
        return true;
    }

    bool Initialize(bool startup) override
    {
        if (!audio.mixer) { Status(L"Could not initialize the audio mixer."); return false; }
        if (!options.Load()) {
            Status(L"Retro Sound preferences are invalid or unreadable at HKEY_CURRENT_USER\\Software\\VibeWare\\HardDriveSound.");
            return false;
        }
        directory = Executable().parent_path();
        if (directory.empty()) { Status(L"Could not locate the application directory."); return false; }
        if (LoadProfile(options.profile, options.retry)) Apply();
        if (options.session && !options.SetStartup()) Status(L"Could not update sign-in startup.");
        if (startup && options.session && Active()) Preview(0);
        if (SetTimer(window, Poll, 250, nullptr)) return true;
        Status(L"Could not start disk activity monitoring.");
        return false;
    }

    bool Active() const override { return profileReady && options.enabled && !failed; }
    void Save() { if (!options.Save()) Status(L"Could not save preferences."); }
    void Apply()
    {
        hdd_configure(audio.mixer, static_cast<float>(options.volume) / 100.f, options.idle, Active());
        if (!Active()) { audio.Stop(); KillTimer(window, Render); }
        if (profileReady && !failed) Status(options.enabled ? L"Following disk activity" : L"Stopped");
    }
    void SetEnabled(bool enabled) override
    {
        if (enabled && !profileReady && !LoadProfile(options.profile, options.retry)) return;
        options.enabled = enabled;
        failed = false;
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
        if (!Active()) SetEnabled(true);
        if (!Active()) return;
        if (kind != 2) audio.Stop();
        hdd_trigger(audio.mixer, kind);
        BeginOutput();
    }

    void EndSession()
    {
        if (ending || !options.session || !Active() || options.volume == 0) return;
        ending = true;
        audio.Stop();
        hdd_trigger(audio.mixer, 1);
        BeginOutput();
    }
    void OnMessage(UINT message, WPARAM wp, LPARAM lp) override
    {
        if (message == WM_TIMER && wp == Poll) {
            const float rate = disk.Read();
            if (Active()) { hdd_activity(audio.mixer, rate); BeginOutput(); }
        } else if (message == WM_TIMER && wp == Render) Pump();
        else if (message == WM_ENDSESSION && wp && (lp & ENDSESSION_LOGOFF)) EndSession();
    }
};
Application& Sound() { return static_cast<Application&>(CurrentEngine()); }
}

std::unique_ptr<WidgetEngine> CreateEngine(HWND window, NotifyUser notify)
{
    return std::make_unique<Application>(window, notify);
}

struct Playback { int session, idle, volume, quiet, density, retry; };
WIDGET_API void SoundRead(Playback* playback, const wchar_t** profile)
{
    const auto& o = Sound().options;
    *playback = {o.session, o.idle, int(o.volume), int(o.quiet), int(o.density), int(o.retry)};
    *profile = o.profile.c_str();
}

WIDGET_API void SoundConfigure(Playback next)
{
    auto& app = Sound();
    auto& o = app.options;
    if (next.volume < 0 || next.volume > 100 || next.quiet < 0 || next.quiet > 120 ||
        next.density < 0 || next.density > 150 || next.retry < 0 || next.retry > 600) return;
    if (app.profileReady && !hdd_frequency(app.audio.mixer, next.quiet, next.density, next.retry)) {
        app.Status(L"Head retry requires error.wav in the selected profile.");
        app.Notify(app.statusText.c_str());
        return;
    }
    if (o.session != (next.session != 0)) {
        o.session = next.session != 0;
        if (!o.SetStartup()) {
            o.session = !o.session;
            if (app.profileReady) hdd_frequency(app.audio.mixer, o.quiet, o.density, o.retry);
            app.Status(L"Could not change sign-in startup.");
            return;
        }
    }
    o.idle = next.idle != 0;
    o.volume = next.volume;
    o.quiet = next.quiet;
    o.density = next.density;
    const bool retryChanged = o.retry != static_cast<unsigned>(next.retry);
    o.retry = next.retry;
    if (!app.profileReady && retryChanged) app.LoadProfile(o.profile, next.retry);
    app.Apply();
    app.Save();
    app.BeginOutput();
}

WIDGET_API void SoundProfile(const wchar_t* value)
{
    auto& app = Sound();
    try {
        std::wstring profile(value);
        if (!app.LoadProfile(profile, app.options.retry)) return;
        app.options.profile = std::move(profile);
        app.Apply();
        app.Save();
    } catch (...) { app.Status(L"Could not open the selected sound profile."); }
}

WIDGET_API void SoundPreview(int kind)
{
    if (kind == 0 || kind == 2 || kind == 3) Sound().Preview(kind);
}
