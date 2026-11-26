// SPDX-License-Identifier: MIT
#pragma once
#include <cstddef>
#include <cstdint>

#if defined(_WIN32) && defined(HDD_DLL)
#define HDD_API __declspec(dllexport)
#else
#define HDD_API
#endif

extern "C" {
HDD_API void* hdd_create();
HDD_API void hdd_destroy(void* mixer);
// 1 loaded, 0 invalid profile, -1 missing error.wav with retries enabled.
HDD_API int hdd_load(void* mixer, const char* folder, int retrySeconds);
HDD_API void hdd_configure(void* mixer, float volume, int idle, int enabled);
HDD_API int hdd_frequency(void* mixer, int quietSeconds, int density, int retrySeconds);
HDD_API void hdd_activity(void* mixer, float operationsPerSecond);
// 0 = spin-up, 1 = ending-session spin-down, 2 = seek burst, 3 = preview spin-down.
HDD_API void hdd_trigger(void* mixer, int kind);
HDD_API int hdd_pending(void* mixer);
HDD_API void hdd_render(void* mixer, int16_t* pcm, size_t frames);
}
