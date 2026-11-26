// SPDX-License-Identifier: MIT
#pragma once
#include <windows.h>
#include <mmsystem.h>
#include <pdh.h>
#include <array>
#include <filesystem>
#include "../Shared/Mixer.h"

class DiskCounter
{
    PDH_HQUERY query{};
    PDH_HCOUNTER transfers{};
public:
    DiskCounter()
    {
        if (PdhOpenQueryW(nullptr, 0, &query) == ERROR_SUCCESS)
            PdhAddEnglishCounterW(query, L"\\PhysicalDisk(_Total)\\Disk Transfers/sec", 0, &transfers);
    }
    ~DiskCounter() { if (query) PdhCloseQuery(query); }

    float Read()
    {
        PDH_FMT_COUNTERVALUE value{};
        if (!transfers || PdhCollectQueryData(query) != ERROR_SUCCESS ||
            PdhGetFormattedCounterValue(transfers, PDH_FMT_DOUBLE, nullptr, &value) != ERROR_SUCCESS ||
            value.CStatus > 1)
            return 0;
        return static_cast<float>(value.doubleValue);
    }
};

class Audio
{
    struct Buffer {
        WAVEHDR header{};
        std::array<int16_t, 480> pcm{};
    };
    std::array<Buffer, 4> buffers;
    HWAVEOUT handle{};
    unsigned empty{};
public:
    void* const mixer = hdd_create();
    ~Audio() { Stop(); hdd_destroy(mixer); }

    bool Load(const std::filesystem::path& path)
    {
        const auto utf8 = path.u8string();
        if (!mixer || !hdd_load(mixer, reinterpret_cast<const char*>(utf8.c_str()))) return false;
        Stop();
        return true;
    }

    void Stop()
    {
        if (!handle) return;
        waveOutReset(handle);
        for (auto& buffer : buffers) {
            if (buffer.header.dwFlags & WHDR_PREPARED)
                waveOutUnprepareHeader(handle, &buffer.header, sizeof(WAVEHDR));
            buffer.header = {};
        }
        waveOutClose(handle);
        handle = nullptr;
    }

    bool Tick()
    {
        const bool needed = hdd_pending(mixer) != 0;
        if (!needed && !handle) return true;
        if (!handle) {
            const WAVEFORMATEX format{WAVE_FORMAT_PCM, 1, 24000, 48000, 2, 16, 0};
            if (waveOutOpen(&handle, WAVE_MAPPER, &format, 0, 0, CALLBACK_NULL) != MMSYSERR_NOERROR)
                return false;
            for (auto& buffer : buffers) {
                buffer.header.lpData = reinterpret_cast<char*>(buffer.pcm.data());
                buffer.header.dwBufferLength = static_cast<DWORD>(sizeof(buffer.pcm));
                if (waveOutPrepareHeader(handle, &buffer.header, sizeof(WAVEHDR)) != MMSYSERR_NOERROR) {
                    Stop();
                    return false;
                }
            }
        }
        empty = needed ? 0 : empty + 1;
        if (empty > 20) { Stop(); return true; }
        for (auto& buffer : buffers) {
            if (buffer.header.dwFlags & WHDR_INQUEUE) continue;
            hdd_render(mixer, buffer.pcm.data(), buffer.pcm.size());
            if (waveOutWrite(handle, &buffer.header, sizeof(WAVEHDR)) != MMSYSERR_NOERROR) {
                Stop();
                return false;
            }
        }
        return true;
    }

    bool Playing() const { return handle != nullptr; }
};
