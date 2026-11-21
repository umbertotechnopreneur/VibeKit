// SPDX-License-Identifier: MIT
#pragma once

#include <windows.h>
#include <psapi.h>
#include <chrono>
#include <condition_variable>
#include <cstdio>
#include <exception>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <mutex>
#include <thread>

namespace RetroMemoryWatchdog
{
    inline constexpr UINT LimitMessage = WM_APP + 50;
    inline constexpr unsigned long long LimitBytes = 1'000'000'000;

    inline void WriteLog(unsigned long long bytes)
    {
        wchar_t local[32768]{};
        DWORD count = GetEnvironmentVariableW(L"LOCALAPPDATA", local, static_cast<DWORD>(std::size(local)));
        if (count == 0 || count >= std::size(local)) return;
        auto directory = std::filesystem::path(local) / L"VibeWare" / L"RetroTerminal" / L"logs";
        std::filesystem::create_directories(directory);
        auto path = directory / L"memory-watchdog.jsonl";
        std::error_code error;
        auto length = std::filesystem::file_size(path, error);
        if (!error && length >= 512 * 1024)
        {
            auto backup = directory / L"memory-watchdog.jsonl.1";
            std::filesystem::remove(backup, error);
            std::filesystem::rename(path, backup, error);
        }
        SYSTEMTIME utc{};
        GetSystemTime(&utc);
        char stamp[32]{};
        std::snprintf(stamp, sizeof(stamp), "%04u-%02u-%02uT%02u:%02u:%02uZ",
            static_cast<unsigned>(utc.wYear), static_cast<unsigned>(utc.wMonth), static_cast<unsigned>(utc.wDay),
            static_cast<unsigned>(utc.wHour), static_cast<unsigned>(utc.wMinute), static_cast<unsigned>(utc.wSecond));
        DWORD pid = GetCurrentProcessId();
        std::ofstream log(path, std::ios::app);
        log << "{\"atUtc\":\"" << stamp << "\",\"kind\":\"guard.memory-limit\",\"rootPid\":" << pid
            << ",\"bytes\":" << bytes << ",\"limitBytes\":" << LimitBytes
            << ",\"processes\":[{\"pid\":" << pid << ",\"name\":\"RetroTerminal.exe\",\"privateBytes\":" << bytes << "}]}\n";
    }

    // The native controller renders in its own process. The monitor stays independent of the message pump.
    inline std::jthread Start(HWND window)
    {
        return std::jthread([window](std::stop_token stop)
        {
            std::mutex gate;
            std::unique_lock lock(gate);
            std::condition_variable_any wait;
            bool previousOver = false;
            for (;;)
            {
                wait.wait_for(lock, stop, std::chrono::seconds(2), [] { return false; });
                if (stop.stop_requested()) return;
                PROCESS_MEMORY_COUNTERS_EX counters{};
                counters.cb = static_cast<DWORD>(sizeof(counters));
                if (!K32GetProcessMemoryInfo(GetCurrentProcess(), reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&counters), counters.cb))
                {
                    previousOver = false;
                    continue;
                }
                const bool over = counters.PrivateUsage > LimitBytes;
                if (over && previousOver)
                {
                    try { WriteLog(counters.PrivateUsage); }
                    catch (const std::exception&) { }
                    if (stop.stop_requested()) return;
                    PostMessageW(window, LimitMessage, 0, 0);
                    wait.wait_for(lock, stop, std::chrono::seconds(5), [] { return false; });
                    if (!stop.stop_requested()) TerminateProcess(GetCurrentProcess(), 137);
                    return;
                }
                previousOver = over;
            }
        });
    }
}
