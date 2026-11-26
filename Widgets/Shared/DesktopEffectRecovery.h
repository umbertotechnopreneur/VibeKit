// SPDX-License-Identifier: MIT
#pragma once

// Public Windows V1 participant. Include this file and link windowsapp.lib and
// ole32.lib. Initialize WinRT on the calling thread before constructing it.
#include <windows.h>
#include <objbase.h>
#include <wtsapi32.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Data.Json.h>
#include <algorithm>
#include <cstdint>
#include <functional>
#include <cmath>
#include <set>
#include <string>
#include <vector>

#pragma comment(lib, "windowsapp.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "wtsapi32.lib")

namespace vibeware::effects
{
    struct ParticipantStatus
    {
        std::wstring application;
        bool running{}, pending{}, leader{};
    };

    class Recovery final
    {
        using JsonObject = winrt::Windows::Data::Json::JsonObject;
        using JsonArray = winrt::Windows::Data::Json::JsonArray;
        using JsonValue = winrt::Windows::Data::Json::JsonValue;
        static constexpr DWORD Capacity = 32768;
        static constexpr std::uint64_t Stable = 30000, Stale = 10000;
        static constexpr wchar_t Protocol[] = L"Local\\VibeWare.DesktopEffects.Recovery.v1";

    public:
        explicit Recovery(std::wstring app) : Recovery(std::move(app), Protocol, {}, {}, {}) {}
#ifdef VIBEWARE_RECOVERY_TESTING
        Recovery(std::wstring app, std::wstring protocol, std::function<std::uint64_t()> clock,
            std::function<bool()> healthy, std::function<bool()> desktop)
#else
    private:
        Recovery(std::wstring app, std::wstring protocol, std::function<std::uint64_t()> clock,
            std::function<bool()> healthy, std::function<bool()> desktop)
#endif
            : app_(std::move(app)), clock_(std::move(clock)), healthyOverride_(std::move(healthy)), desktopOverride_(std::move(desktop))
        {
            if (app_.empty() || app_.size() > 80 || app_.find_first_not_of(L" \t\r\n") == std::wstring::npos) return;
            GUID id{};
            if (FAILED(CoCreateGuid(&id))) return;
            wchar_t text[40]{};
            if (!StringFromGUID2(id, text, ARRAYSIZE(text))) return;
            id_ = text;
            FILETIME created{}, exited{}, kernel{}, user{};
            if (!GetProcessTimes(GetCurrentProcess(), &created, &exited, &kernel, &user)) return;
            opened_ = FileTime(created) / 10000;
            gate_ = CreateMutexW(nullptr, FALSE, (protocol + L".Mutex").c_str());
            if (!gate_) return;
            map_ = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, Capacity, (protocol + L".State").c_str());
            if (!map_) { CloseHandle(gate_); gate_ = nullptr; return; }
            available_ = true;
        }

    public:
        Recovery(const Recovery&) = delete;
        Recovery& operator=(const Recovery&) = delete;
        ~Recovery() { Close(); }
        bool Available() const noexcept { return available_; }
        bool Pending() const noexcept { return !request_.empty(); }
        bool AllEnabled() const noexcept { return allEnabled_; }
        bool AnyEnabled() const noexcept { return anyEnabled_; }
        // Observe does not register the caller or participate in leader election.
        static bool Observe(std::vector<ParticipantStatus>& participants)
        {
            return ObserveCore(Protocol, GetTickCount64(), participants);
        }
#ifdef VIBEWARE_RECOVERY_TESTING
        static bool ObserveForTest(const std::wstring& protocol, std::uint64_t now, std::vector<ParticipantStatus>& participants)
        {
            return ObserveCore(protocol, now, participants);
        }
#endif
        void Reset() noexcept { request_.clear(); attempts_ = 0; runningSince_ = 0; }
        void Cancel() noexcept { request_.clear(); runningSince_ = 0; }

        bool Pause(bool enabled, int delaySeconds)
        {
            if (!enabled || !available_ || attempts_ >= 2) { request_.clear(); return false; }
            if (Pending()) return true;
            GUID request{};
            wchar_t text[40]{};
            if (FAILED(CoCreateGuid(&request)) || !StringFromGUID2(request, text, ARRAYSIZE(text))) return false;
            request_ = text;
            due_ = Now() + std::uint64_t(std::clamp(delaySeconds, 30, 300)) * 1000 * (attempts_ + 1);
            return true;
        }

        // Returns true exactly once for a granted resume. Call on the effect's
        // owning thread at least once per second and handle the resume there.
        bool Tick(bool canResume = true, bool running = false)
        {
            if (!available_ || !gate_ || !map_) return false;
            const auto now = Now();
            if (!running) runningSince_ = 0;
            else if (!runningSince_) runningSince_ = now;
            else if (now >= runningSince_ && now - runningSince_ >= 300000) attempts_ = 0;
            const DWORD wait = WaitForSingleObject(gate_, 0);
            if (wait != WAIT_OBJECT_0 && wait != WAIT_ABANDONED) return false;
            bool grant = false;
            try
            {
                auto state = Read(map_);
                auto members = state.GetNamedArray(L"Members");
                for (std::uint32_t i = members.Size(); i > 0; --i)
                {
                    auto member = members.GetObjectAt(i - 1);
                    const auto seen = Number(member, L"Seen");
                    if (now < seen || now - seen > Stale) members.RemoveAt(i - 1);
                }
                JsonObject me{nullptr};
                for (std::uint32_t i = 0; i < members.Size(); ++i)
                    if (String(members.GetObjectAt(i), L"Id") == id_) { me = members.GetObjectAt(i); break; }
                if (!me)
                {
                    me = JsonObject();
                    const auto order = Number(state, L"NextOrder") + 1;
                    Set(state, L"NextOrder", order);
                    Set(me, L"Id", id_); Set(me, L"App", app_); Set(me, L"Order", order); Set(me, L"Opened", opened_);
                    Set(me, L"Request", L""); Set(me, L"Grant", L""); Set(me, L"Due", std::uint64_t{0}); Set(me, L"GrantUntil", std::uint64_t{0});
                    members.Append(me);
                }
                Set(me, L"Seen", now);
                Set(me, L"CanResume", canResume);
                Set(me, L"Running", running);
                allEnabled_ = true;
                anyEnabled_ = false;
                for (std::uint32_t i = 0; i < members.Size(); ++i)
                {
                    auto member = members.GetObjectAt(i);
                    allEnabled_ = allEnabled_ && member.GetNamedBoolean(L"Running", false);
                    anyEnabled_ = anyEnabled_ || member.GetNamedBoolean(L"Running", false);
                    if (!String(member, L"Grant").empty() && now > Number(member, L"GrantUntil")) Set(member, L"Grant", L"");
                }
                if (String(me, L"Request") != request_)
                {
                    Set(me, L"Request", request_); Set(me, L"Grant", L""); Set(me, L"Due", due_);
                }
                auto leader = members.GetObjectAt(0);
                for (std::uint32_t i = 1; i < members.Size(); ++i)
                    if (Before(members.GetObjectAt(i), leader)) leader = members.GetObjectAt(i);
                if (String(state, L"Leader") != String(leader, L"Id"))
                {
                    Set(state, L"Leader", String(leader, L"Id"));
                    stableSince_ = lastSample_ = 0; haveSample_ = false;
                }
                if (String(leader, L"Id") == id_)
                {
                    const bool healthy = Desktop() && (healthyOverride_ ? healthyOverride_() : Healthy(now));
                    if (!healthy) stableSince_ = 0;
                    else if (!stableSince_) stableSince_ = now;
                    if (stableSince_ && now - stableSince_ >= Stable && now >= Number(state, L"NextGrant"))
                    {
                        JsonObject candidate{nullptr};
                        for (std::uint32_t i = 0; i < members.Size(); ++i)
                        {
                            auto member = members.GetObjectAt(i);
                            const auto request = String(member, L"Request");
                            if (!member.GetNamedBoolean(L"CanResume", false) || request.empty() ||
                                String(member, L"Grant") == request || now < Number(member, L"Due")) continue;
                            if (!candidate || Before(member, candidate)) candidate = member;
                        }
                        if (candidate)
                        {
                            Set(candidate, L"Grant", String(candidate, L"Request"));
                            Set(candidate, L"GrantUntil", now + 2000);
                            Set(state, L"NextGrant", now + Stable);
                            stableSince_ = 0;
                        }
                    }
                }
                if (canResume && Desktop() && !request_.empty() && String(me, L"Grant") == request_ && now <= Number(me, L"GrantUntil"))
                {
                    request_.clear(); Set(me, L"Request", L""); Set(me, L"Grant", L"");
                    ++attempts_;
                    grant = true;
                }
                Write(state);
            }
            catch (...) { available_ = allEnabled_ = anyEnabled_ = false; grant = false; }
            ReleaseMutex(gate_);
            return grant;
        }

    private:
        static std::uint64_t FileTime(FILETIME t) noexcept { return (std::uint64_t(t.dwHighDateTime) << 32) | t.dwLowDateTime; }
        std::uint64_t Now() const { return clock_ ? clock_() : GetTickCount64(); }
        static std::uint64_t Number(const JsonObject& o, const wchar_t* key)
        {
            const double value = o.GetNamedNumber(key, -1);
            if (!std::isfinite(value) || value < 0 || value > 9007199254740991.0 || std::floor(value) != value)
                throw winrt::hresult_invalid_argument();
            return static_cast<std::uint64_t>(value);
        }
        static std::wstring String(const JsonObject& o, const wchar_t* key) { return std::wstring(o.GetNamedString(key, L"")); }
        static void Set(const JsonObject& o, const wchar_t* key, std::uint64_t value) { o.Insert(key, JsonValue::CreateNumberValue(static_cast<double>(value))); }
        static void Set(const JsonObject& o, const wchar_t* key, bool value) { o.Insert(key, JsonValue::CreateBooleanValue(value)); }
        static void Set(const JsonObject& o, const wchar_t* key, const std::wstring& value) { o.Insert(key, JsonValue::CreateStringValue(value)); }
        static void Set(const JsonObject& o, const wchar_t* key, const wchar_t* value) { Set(o, key, std::wstring(value)); }
        static bool Before(const JsonObject& a, const JsonObject& b)
        {
            const auto ao = Number(a, L"Opened"), bo = Number(b, L"Opened");
            return ao < bo || (ao == bo && Number(a, L"Order") < Number(b, L"Order"));
        }
        bool Desktop() const
        {
            if (desktopOverride_) return desktopOverride_();
            LPWSTR state{}; DWORD bytes{};
            if (!WTSQuerySessionInformationW(WTS_CURRENT_SERVER_HANDLE, WTS_CURRENT_SESSION, WTSConnectState, &state, &bytes)) return false;
            const bool connected = state && bytes >= sizeof(WTS_CONNECTSTATE_CLASS) && *reinterpret_cast<WTS_CONNECTSTATE_CLASS*>(state) == WTSActive;
            WTSFreeMemory(state);
            if (!connected) return false;
            HDESK desktop = OpenInputDesktop(0, FALSE, DESKTOP_READOBJECTS);
            if (!desktop) return false;
            wchar_t name[256]{}; DWORD needed{};
            const bool ready = GetUserObjectInformationW(desktop, UOI_NAME, name, sizeof(name), &needed) && _wcsicmp(name, L"Default") == 0;
            CloseDesktop(desktop);
            return ready;
        }
        bool Healthy(std::uint64_t now)
        {
            const bool timely = lastSample_ && now >= lastSample_ && now - lastSample_ <= 2000;
            lastSample_ = now;
            FILETIME idle{}, kernel{}, user{}; MEMORYSTATUSEX memory{sizeof(memory)};
            if (!GetSystemTimes(&idle, &kernel, &user) || !GlobalMemoryStatusEx(&memory)) { haveSample_ = false; return false; }
            const auto i = FileTime(idle), k = FileTime(kernel), u = FileTime(user);
            const bool valid = haveSample_ && i >= lastIdle_ && k >= lastKernel_ && u >= lastUser_;
            const auto total = valid ? k - lastKernel_ + u - lastUser_ : 0, idleDelta = valid ? i - lastIdle_ : 0;
            lastIdle_ = i; lastKernel_ = k; lastUser_ = u; haveSample_ = true;
            return timely && total > 0 && idleDelta <= total && double(idleDelta) / double(total) > .15 &&
                memory.ullTotalPhys > 0 && memory.ullAvailPhys >= (std::max)(memory.ullTotalPhys / 10, 512ULL * 1024 * 1024);
        }
        static JsonObject Read(HANDLE mapping)
        {
            const auto data = static_cast<const unsigned char*>(MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, Capacity));
            if (!data) throw winrt::hresult_error(HRESULT_FROM_WIN32(GetLastError()));
            const auto length = *reinterpret_cast<const std::int32_t*>(data);
            if (length < 0 || length > int(Capacity - 4)) { UnmapViewOfFile(data); throw winrt::hresult_invalid_argument(); }
            std::string json(reinterpret_cast<const char*>(data + 4), length);
            UnmapViewOfFile(data);
            JsonObject state;
            if (length) state = JsonObject::Parse(winrt::to_hstring(json));
            else
            {
                state.Insert(L"Members", JsonArray());
                Set(state, L"NextOrder", std::uint64_t{0}); Set(state, L"NextGrant", std::uint64_t{0}); Set(state, L"Leader", L"");
            }
            const auto nextOrder = Number(state, L"NextOrder");
            (void)Number(state, L"NextGrant");
            auto members = state.GetNamedArray(L"Members");
            std::set<std::wstring> ids;
            std::set<std::uint64_t> orders;
            for (std::uint32_t i = 0; i < members.Size(); ++i)
            {
                const auto member = members.GetObjectAt(i);
                const auto id = String(member, L"Id"), app = String(member, L"App");
                const auto request = String(member, L"Request"), grant = String(member, L"Grant");
                const auto order = Number(member, L"Order"), opened = Number(member, L"Opened");
                (void)Number(member, L"Seen"); (void)Number(member, L"Due"); (void)Number(member, L"GrantUntil");
                if (id.empty() || id.size() > 64 || app.empty() || app.size() > 80 ||
                    app.find_first_not_of(L" \t\r\n") == std::wstring::npos || request.size() > 64 || grant.size() > 64 ||
                    (!grant.empty() && grant != request) || !order || order > nextOrder || !opened ||
                    !ids.insert(id).second || !orders.insert(order).second) throw winrt::hresult_invalid_argument();
            }
            return state;
        }
        static bool ObserveCore(const std::wstring& protocol, std::uint64_t now, std::vector<ParticipantStatus>& participants)
        {
            participants.clear();
            HANDLE gate = OpenMutexW(SYNCHRONIZE | MUTEX_MODIFY_STATE, FALSE, (protocol + L".Mutex").c_str());
            if (!gate) return false;
            const DWORD wait = WaitForSingleObject(gate, 0);
            if (wait != WAIT_OBJECT_0 && wait != WAIT_ABANDONED) { CloseHandle(gate); return false; }
            bool success = false;
            HANDLE map = OpenFileMappingW(FILE_MAP_READ, FALSE, (protocol + L".State").c_str());
            if (map)
            {
                try
                {
                    auto state = Read(map); auto members = state.GetNamedArray(L"Members");
                    const auto leader = String(state, L"Leader");
                    for (std::uint32_t i = 0; i < members.Size(); ++i)
                    {
                        auto member = members.GetObjectAt(i);
                        const auto seen = Number(member, L"Seen");
                        if (now < seen || now - seen > Stale) continue;
                        participants.push_back({String(member, L"App"), member.GetNamedBoolean(L"Running", false),
                            !String(member, L"Request").empty(), String(member, L"Id") == leader});
                    }
                    success = true;
                }
                catch (...) { participants.clear(); }
                CloseHandle(map);
            }
            ReleaseMutex(gate);
            CloseHandle(gate);
            return success;
        }
        void Write(const JsonObject& state)
        {
            const auto json = winrt::to_string(state.Stringify());
            if (json.size() > Capacity - 4) throw winrt::hresult_invalid_argument();
            auto data = static_cast<unsigned char*>(MapViewOfFile(map_, FILE_MAP_WRITE, 0, 0, Capacity));
            if (!data) throw winrt::hresult_error(HRESULT_FROM_WIN32(GetLastError()));
            memcpy(data + 4, json.data(), json.size());
            *reinterpret_cast<std::int32_t*>(data) = static_cast<std::int32_t>(json.size());
            UnmapViewOfFile(data);
        }
        void Close() noexcept
        {
            if (gate_ && map_)
            {
                const DWORD wait = WaitForSingleObject(gate_, 0);
                if (wait == WAIT_OBJECT_0 || wait == WAIT_ABANDONED)
                {
                    try
                    {
                        auto state = Read(map_); auto members = state.GetNamedArray(L"Members");
                        for (std::uint32_t i = members.Size(); i > 0; --i)
                            if (String(members.GetObjectAt(i - 1), L"Id") == id_) members.RemoveAt(i - 1);
                        Write(state);
                    }
                    catch (...) {}
                    ReleaseMutex(gate_);
                }
            }
            if (map_) CloseHandle(map_);
            if (gate_) CloseHandle(gate_);
            map_ = gate_ = nullptr; available_ = false;
        }
        HANDLE gate_{}, map_{};
        std::wstring app_, id_, request_;
        std::function<std::uint64_t()> clock_;
        std::function<bool()> healthyOverride_, desktopOverride_;
        std::uint64_t opened_{}, due_{}, lastSample_{}, stableSince_{}, runningSince_{};
        std::uint64_t lastIdle_{}, lastKernel_{}, lastUser_{};
        int attempts_{};
        bool available_{}, haveSample_{}, allEnabled_{}, anyEnabled_{};
    };
}
