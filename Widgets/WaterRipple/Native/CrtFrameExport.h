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
        nextDiscovery = {};
    }

    Publication Publish(ID3D11Device* device, ID3D11DeviceContext* context, IDXGIAdapter1* adapter,
        IDXGISwapChain1* swapChain, HMONITOR monitor, const RECT& bounds)
    {
        if (!owner) return {};
        const auto now = ipc::Clock::now();
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

        const auto status = PresentationStatus(monitor, bounds);
        const HRESULT acquired = mutex->AcquireSync(ipc::ProducerKey, 0);
        if (acquired == WAIT_TIMEOUT) return status;
        if (acquired != S_OK)
        {
            Reset();
            return {};
        }

        Microsoft::WRL::ComPtr<ID3D11Texture2D> result;
        if (FAILED(swapChain->GetBuffer(0, IID_PPV_ARGS(&result))))
        {
            mutex->ReleaseSync(ipc::ProducerKey);
            Reset();
            return {};
        }
        context->OMSetRenderTargets(0, nullptr, nullptr);
        context->CopyResource(shared.Get(), result.Get());
        // When CRT presents, our swap chain no longer submits the producer's work.
        if (status.presentedByCrt) context->Flush();
        if (FAILED(mutex->ReleaseSync(ipc::ConsumerKey)))
        {
            Reset();
            return {};
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
                return {};
            }
            generation = reinterpret_cast<HANDLE>(serial.QuadPart);
        }
        return status;
    }

private:
    Publication PresentationStatus(HMONITOR monitor, const RECT& bounds) const
    {
        if (!generation || GetPropW(peer, ipc::CrtInput) != owner) return {};
        if (GetPropW(peer, ipc::CrtPresentationGeneration) != generation) return {true, false};
        const auto presentedAt = reinterpret_cast<LONG_PTR>(GetPropW(peer, ipc::CrtPresentation));
        LARGE_INTEGER now{}, frequency{};
        if (presentedAt <= 0 || !QueryPerformanceCounter(&now) || now.QuadPart < presentedAt ||
            !QueryPerformanceFrequency(&frequency) || frequency.QuadPart <= 0 ||
            std::chrono::duration<double>(double(now.QuadPart - presentedAt) / double(frequency.QuadPart)) >= ipc::PresentationLease)
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
    ipc::Clock::time_point nextDiscovery{};
    winrt::handle sharedHandle;
    Microsoft::WRL::ComPtr<ID3D11Texture2D> shared;
    Microsoft::WRL::ComPtr<IDXGIKeyedMutex> mutex;
};
} // namespace water_ripple
