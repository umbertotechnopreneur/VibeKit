// SPDX-License-Identifier: MIT
#pragma once

#include <windows.h>
#include <d3d11_1.h>
#include <dxgi1_4.h>
#include <winrt/base.h>
#include <wrl/client.h>
#include <chrono>
#include <cwchar>

// Local Windows-only protocol. All frame data stays on the graphics adapter.
namespace vibeware::desktop_effect
{
    inline constexpr wchar_t CrtReady[] = L"VibeWare.CRT.FrameConsumer.v1";
    inline constexpr wchar_t RippleHandle[] = L"VibeWare.Ripple.FrameHandle.v1";
    inline constexpr wchar_t RippleTarget[] = L"VibeWare.Ripple.FrameTarget.v1";
    inline constexpr wchar_t RippleGeneration[] = L"VibeWare.Ripple.FrameGeneration.v1";
    inline constexpr wchar_t CrtInput[] = L"VibeWare.CRT.RippleInput.v1";
    inline constexpr UINT64 ProducerKey = 0;
    inline constexpr UINT64 ConsumerKey = 1;
    inline constexpr auto DiscoveryInterval = std::chrono::milliseconds(250);
    using Clock = std::chrono::steady_clock;
    using Microsoft::WRL::ComPtr;

    inline bool IsEffectWindow(HWND window, HMONITOR monitor, const RECT& bounds)
    {
        RECT actual{};
        DWORD affinity{};
        return window && IsWindowVisible(window) && MonitorFromWindow(window, MONITOR_DEFAULTTONULL) == monitor
            && GetWindowRect(window, &actual) && EqualRect(&actual, &bounds)
            && GetWindowDisplayAffinity(window, &affinity) && affinity == WDA_EXCLUDEFROMCAPTURE;
    }

    inline bool ReadUserSid(HANDLE process, void* data, DWORD dataSize)
    {
        winrt::handle token;
        DWORD required{};
        return OpenProcessToken(process, TOKEN_QUERY, token.put())
            && GetTokenInformation(token.get(), TokenUser, data, dataSize, &required);
    }

    inline winrt::handle OpenPeer(HWND window, const wchar_t* executable, DWORD access)
    {
        DWORD pid{};
        if (!GetWindowThreadProcessId(window, &pid) || pid == GetCurrentProcessId()) return {};
        winrt::handle process{OpenProcess(access | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid)};
        if (!process) return {};
        wchar_t path[32768];
        DWORD length = ARRAYSIZE(path);
        if (!QueryFullProcessImageNameW(process.get(), 0, path, &length)) return {};
        const auto separator = std::wcsrchr(path, L'\\');
        if (!separator || _wcsicmp(separator + 1, executable) != 0) return {};
        alignas(TOKEN_USER) BYTE localUser[sizeof(TOKEN_USER) + SECURITY_MAX_SID_SIZE]{};
        alignas(TOKEN_USER) BYTE peerUser[sizeof(TOKEN_USER) + SECURITY_MAX_SID_SIZE]{};
        if (!ReadUserSid(GetCurrentProcess(), localUser, sizeof(localUser)) ||
            !ReadUserSid(process.get(), peerUser, sizeof(peerUser)) ||
            !EqualSid(reinterpret_cast<TOKEN_USER*>(localUser)->User.Sid, reinterpret_cast<TOKEN_USER*>(peerUser)->User.Sid))
            return {};
        DWORD currentPid{};
        if (!GetWindowThreadProcessId(window, &currentPid) || pid != currentPid) return {};
        return process;
    }

    struct PeerSearch
    {
        HMONITOR monitor{};
        RECT bounds{};
        const wchar_t* property{};
        HANDLE value{};
        HWND peer{};
    };

    inline BOOL CALLBACK FindPeerWindow(HWND window, LPARAM context)
    {
        auto& search = *reinterpret_cast<PeerSearch*>(context);
        if (GetPropW(window, search.property) == search.value && IsEffectWindow(window, search.monitor, search.bounds))
        {
            search.peer = window;
            return FALSE;
        }
        return TRUE;
    }

    inline HWND FindPeer(HMONITOR monitor, const RECT& bounds, const wchar_t* property, HANDLE value)
    {
        PeerSearch search{monitor, bounds, property, value};
        EnumWindows(FindPeerWindow, reinterpret_cast<LPARAM>(&search));
        return search.peer;
    }

    inline bool HasTextureBudget(IDXGIAdapter1* adapter, UINT width, UINT height)
    {
        ComPtr<IDXGIAdapter3> budgetAdapter;
        DXGI_QUERY_VIDEO_MEMORY_INFO budget{};
        constexpr UINT64 reserve = 64 * 1024 * 1024;
        return SUCCEEDED(adapter->QueryInterface(IID_PPV_ARGS(&budgetAdapter))) &&
            SUCCEEDED(budgetAdapter->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &budget)) &&
            budget.Budget > budget.CurrentUsage &&
            budget.Budget - budget.CurrentUsage >= reserve + UINT64(width) * height * 4;
    }
}
