#pragma once
#include <cstdint>

struct RippleWave
{
    float X;
    float Y;
    float Age;
    float Active;
};

// Keep these values aligned with CaptureStatus in NativeInterop.cs.
enum RippleCaptureStatus : int32_t
{
    StatusClipped = 1,
    StatusWaiting = 2,
    StatusAccessLost = 4,
    StatusPresentBusy = 8,
    StatusOccluded = 16,
    StatusValidCapture = 32,
    StatusPresentedCapture = 64,
    StatusCrtComposition = 128,
};

struct RippleStats
{
    int32_t CaptureWidth;
    int32_t CaptureHeight;
    int32_t CaptureCount;
    int32_t PresentCount;
    float LastCaptureMs;
    float LastRenderCpuMs;
    // CaptureCount is cumulative and is not a validity test. Reveal a screen
    // overlay only when StatusValidCapture and StatusPresentedCapture are both set.
    int32_t Status;
    int32_t Reserved;
};

struct RippleCapabilities
{
    wchar_t AdapterName[128];
    uint64_t DedicatedVideoMemory;
    uint64_t SharedSystemMemory;
    uint32_t FeatureLevel;
    int32_t Supported;
    int32_t Reason;
    int32_t Reserved;
    int32_t Left;
    int32_t Top;
    int32_t Width;
    int32_t Height;
};

struct RippleGpuMemory
{
    uint64_t DedicatedCapacity;
    uint64_t LocalUsage;
    uint64_t LocalBudget;
    uint64_t SharedUsage;
};

extern "C"
{
    __declspec(dllexport) int32_t __cdecl RippleCreate(void* panel, const wchar_t* shaderDirectory, void** handle);
    __declspec(dllexport) int32_t __cdecl RippleRender(void* handle, int32_t screenX, int32_t screenY,
        int32_t widthPixels, int32_t heightPixels, float widthDips, float heightDips,
        float strength, float band, float speed, float duration, float chromaticSeparation, float chromaticBlend, float crest, float ripples, float liquid,
        const RippleWave* waves, int32_t count, int32_t captureEnabled, int32_t captureDue);
    __declspec(dllexport) int32_t __cdecl RippleGetStats(void* handle, RippleStats* stats);
    __declspec(dllexport) int32_t __cdecl RippleCheckCapabilities(int32_t screenX, int32_t screenY,
        int32_t widthPixels, int32_t heightPixels, RippleCapabilities* capabilities);
    __declspec(dllexport) int32_t __cdecl RippleSetScreenMode(void* handle, int32_t enabled);
    __declspec(dllexport) void __cdecl RippleSetInteropWindow(void* handle, void* window);
    __declspec(dllexport) void __cdecl RippleSetTarget(void* handle, int32_t target);
    __declspec(dllexport) int32_t __cdecl RippleGetGpuMemory(void* handle, RippleGpuMemory* memory);
    __declspec(dllexport) void __cdecl RippleDestroy(void* handle);
}
