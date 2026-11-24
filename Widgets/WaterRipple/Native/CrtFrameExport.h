#pragma once
#include "../../Shared/DesktopEffectInterop.h"
#include <dwmapi.h>

#pragma comment(lib, "dwmapi.lib")

namespace water_ripple {
namespace ipc = vibeware::desktop_effect;

class CrtFrameExport final
{
public:
    struct Publication
    {
        bool composed{};
        bool presentedByCrt{};
    };

    void SetWindow(HWND window) { owner = window; }
    ~CrtFrameExport() { Reset(); }

    void Reset()
    {
        if (owner && sharedHandle)
        {
            RemovePropW(owner, ipc::RippleGeneration);
            RemovePropW(owner, ipc::RippleHandle);
            RemovePropW(owner, ipc::RippleTarget);
        }
        sharedHandle.close();
        mutex.Reset();
        shared.Reset();
        peer = nullptr;
        generation = nullptr;
        nextDiscovery = lastPublish = {};
        framePending = false;
    }

    Publication Observe(ID3D11Device* device, IDXGIAdapter1* adapter, HMONITOR monitor,
        const RECT& bounds, ipc::Clock::time_point now, bool changed)
    {
        if (!owner) return {};
        if (now >= nextDiscovery)
        {
            auto candidate = ipc::FindPeer(monitor, bounds, ipc::CrtReady, reinterpret_cast<HANDLE>(1));
            if (candidate != peer)
            {
                Reset();
                if (candidate && ipc::OpenPeer(candidate, L"RetroStudio.exe", 0)) peer = candidate;
            }
            nextDiscovery = now + ipc::DiscoveryInterval;
            if (peer && !sharedHandle && !Create(device, adapter, bounds))
            {
                Reset();
                nextDiscovery = now + std::chrono::seconds(2);
            }
        }
        if (!sharedHandle) return {};
        framePending |= changed;
        return PresentationStatus(monitor, bounds);
    }

    bool NeedsFrame(ipc::Clock::time_point now) const
    {
        // The consumer still expires shared input after 500 ms without a copy.
        return sharedHandle && (framePending || now >= lastPublish + ipc::PresentationLease / 4);
    }

    bool Publish(ID3D11DeviceContext* context, ID3D11Texture2D* texture, ipc::Clock::time_point now, bool willPresentLocally)
    {
        const HRESULT acquired = mutex->AcquireSync(ipc::ProducerKey, 0);
        if (acquired == WAIT_TIMEOUT) return true;
        if (acquired != S_OK)
        {
            Reset();
            return false;
        }

        context->CopyResource(shared.Get(), texture);
        // No local Present submits this copy while CRT owns presentation or our local frame is unchanged.
        if (!willPresentLocally) context->Flush();
        if (FAILED(mutex->ReleaseSync(ipc::ConsumerKey)))
        {
            Reset();
            return false;
        }
        if (!generation)
        {
            LARGE_INTEGER serial{};
            if (!QueryPerformanceCounter(&serial) || serial.QuadPart <= 0 ||
                !SetPropW(owner, ipc::RippleHandle, sharedHandle.get()) ||
                !SetPropW(owner, ipc::RippleTarget, peer) ||
                !SetPropW(owner, ipc::RippleGeneration, reinterpret_cast<HANDLE>(serial.QuadPart)))
            {
                Reset();
                return false;
            }
            generation = reinterpret_cast<HANDLE>(serial.QuadPart);
        }
        lastPublish = now;
        framePending = false;
        return true;
    }

private:
    Publication PresentationStatus(HMONITOR monitor, const RECT& bounds) const
    {
        if (!generation || GetPropW(peer, ipc::CrtInput) != owner) return {};
        if (GetPropW(peer, ipc::CrtPresentationGeneration) != generation) return {true, false};
        const auto presentedAt = reinterpret_cast<LONG_PTR>(GetPropW(peer, ipc::CrtPresentation));
        static const auto frequency = [] {
            LARGE_INTEGER ticks{};
            return QueryPerformanceFrequency(&ticks) ? ticks.QuadPart : 0LL;
        }();
        LARGE_INTEGER now{};
        if (presentedAt <= 0 || frequency <= 0 || !QueryPerformanceCounter(&now) || now.QuadPart < presentedAt ||
            std::chrono::duration<double>(double(now.QuadPart - presentedAt) / double(frequency)) >= ipc::PresentationLease)
            return {true, false};

        BYTE alpha{};
        DWORD flags{}, cloaked{};
        const bool opaque = GetLayeredWindowAttributes(peer, nullptr, &alpha, &flags) &&
            (flags & LWA_ALPHA) && !(flags & LWA_COLORKEY) && alpha == 255;
        const bool presenting = opaque && !IsIconic(peer) && ipc::IsEffectWindow(peer, monitor, bounds) &&
            ipc::IsEffectWindow(owner, monitor, bounds) &&
            SUCCEEDED(DwmGetWindowAttribute(peer, DWMWA_CLOAKED, &cloaked, sizeof(cloaked))) && !cloaked &&
            ipc::IsWindowAbove(peer, owner) && GetPropW(owner, ipc::RippleGeneration) == generation &&
            GetPropW(peer, ipc::CrtPresentationGeneration) == generation && GetPropW(peer, ipc::CrtInput) == owner;
        return {true, presenting};
    }

    // An unnamed, non-inheritable NT handle is duplicated by the matched peer.
    // DXGI rejects copies across adapters. Only the consumer can read its copy.
    bool Create(ID3D11Device* device, IDXGIAdapter1* adapter, const RECT& bounds)
    {
        const auto width = UINT(bounds.right - bounds.left);
        const auto height = UINT(bounds.bottom - bounds.top);
        if (!ipc::HasTextureBudget(adapter, width, height)) return false;
        D3D11_TEXTURE2D_DESC description{};
        description.Width = width;
        description.Height = height;
        description.MipLevels = description.ArraySize = 1;
        description.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        description.SampleDesc.Count = 1;
        description.Usage = D3D11_USAGE_DEFAULT;
        description.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
        description.MiscFlags = D3D11_RESOURCE_MISC_SHARED_NTHANDLE | D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;
        Microsoft::WRL::ComPtr<IDXGIResource1> resource;
        return SUCCEEDED(device->CreateTexture2D(&description, nullptr, &shared)) &&
            SUCCEEDED(shared.As(&resource)) &&
            SUCCEEDED(shared.As(&mutex)) &&
            SUCCEEDED(resource->CreateSharedHandle(nullptr, DXGI_SHARED_RESOURCE_READ, nullptr, sharedHandle.put()));
    }

    HWND owner{}, peer{};
    HANDLE generation{};
    ipc::Clock::time_point nextDiscovery{}, lastPublish{};
    bool framePending{};
    winrt::handle sharedHandle;
    Microsoft::WRL::ComPtr<ID3D11Texture2D> shared;
    Microsoft::WRL::ComPtr<IDXGIKeyedMutex> mutex;
};
} // namespace water_ripple
