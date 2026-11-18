// SPDX-License-Identifier: MIT
#pragma once
#include "../../Shared/DesktopEffectInterop.h"

namespace retro_terminal {
namespace ipc = vibeware::desktop_effect;

class RippleFrameInput final
{
public:
    struct Sample
    {
        ID3D11ShaderResourceView* view{};
        bool updated{};
    };

    ~RippleFrameInput()
    {
        if (!owner) return;
        ClearAcknowledgment();
        RemovePropW(owner, ipc::CrtReady);
    }

    void Start(HWND window, HMONITOR display, const RECT& monitorBounds)
    {
        if (!SetPropW(window, ipc::CrtReady, reinterpret_cast<HANDLE>(1))) return;
        owner = window;
        monitor = display;
        bounds = monitorBounds;
    }

    bool UsingRipple() const { return acknowledged; }

    Sample Poll(ID3D11Device* device, ID3D11DeviceContext* context, IDXGIAdapter1* adapter, ipc::Clock::time_point now)
    {
        if (!owner) return {};
        if (now >= nextDiscovery)
        {
            nextDiscovery = now + ipc::DiscoveryInterval;
            HWND candidate = ipc::FindPeer(monitor, bounds, ipc::RippleTarget, owner);
            HANDLE serial = candidate ? GetPropW(candidate, ipc::RippleGeneration) : nullptr;
            if (candidate != producer || serial != generation || (serial && !shared))
            {
                Disconnect();
                producer = candidate;
                generation = serial;
                if (generation && !Connect(device, adapter)) nextDiscovery = now + std::chrono::seconds(2);
            }
            if (acknowledged) RaiseAboveRipple();
        }

        bool fresh = false;
        if (shared)
        {
            const HRESULT acquired = mutex->AcquireSync(ipc::ConsumerKey, 0);
            if (acquired == S_OK)
            {
                context->CopyResource(input.Get(), shared.Get());
                if (SUCCEEDED(mutex->ReleaseSync(ipc::ProducerKey)))
                {
                    fresh = true;
                    lastRead = now;
                    if (!acknowledged)
                    {
                        RaiseAboveRipple();
                        acknowledged = SetPropW(owner, ipc::CrtInput, producer) != FALSE;
                    }
                }
                else Disconnect();
            }
            else if (acquired != WAIT_TIMEOUT) Disconnect();
        }
        if (!view || lastRead == ipc::Clock::time_point{} || now - lastRead > std::chrono::milliseconds(500))
        {
            ClearAcknowledgment();
            return {};
        }
        return {view.Get(), fresh};
    }

private:
    HWND owner{}, producer{};
    HMONITOR monitor{};
    RECT bounds{};
    HANDLE generation{};
    ipc::Clock::time_point nextDiscovery{}, lastRead{};
    bool acknowledged{};
    ipc::ComPtr<ID3D11Texture2D> shared, input;
    ipc::ComPtr<IDXGIKeyedMutex> mutex;
    ipc::ComPtr<ID3D11ShaderResourceView> view;

    void ClearAcknowledgment()
    {
        if (acknowledged) RemovePropW(owner, ipc::CrtInput);
        acknowledged = false;
    }

    void Disconnect()
    {
        ClearAcknowledgment();
        lastRead = {};
        view.Reset();
        mutex.Reset();
        input.Reset();
        shared.Reset();
    }

    bool Connect(ID3D11Device* device, IDXGIAdapter1* adapter)
    {
        const auto width = UINT(bounds.right - bounds.left);
        const auto height = UINT(bounds.bottom - bounds.top);
        if (!ipc::HasTextureBudget(adapter, width, height)) return false;
        auto process = ipc::OpenPeer(producer, L"WaterRipple.exe", PROCESS_DUP_HANDLE);
        if (!process) return false;
        const auto handle = GetPropW(producer, ipc::RippleHandle);
        winrt::handle duplicate;
        if (!handle || !DuplicateHandle(process.get(), handle, GetCurrentProcess(), duplicate.put(),
            0, FALSE, DUPLICATE_SAME_ACCESS) || GetPropW(producer, ipc::RippleGeneration) != generation)
            return false;

        ipc::ComPtr<ID3D11Device1> device1;
        ipc::ComPtr<ID3D11Texture2D> source;
        if (FAILED(device->QueryInterface(IID_PPV_ARGS(&device1))) ||
            FAILED(device1->OpenSharedResource1(duplicate.get(), IID_PPV_ARGS(&source))))
            return false;
        D3D11_TEXTURE2D_DESC description{};
        source->GetDesc(&description);
        if (description.Width != width || description.Height != height || description.SampleDesc.Count != 1 ||
            description.MipLevels != 1 || description.ArraySize != 1 || description.Format != DXGI_FORMAT_B8G8R8A8_UNORM)
            return false;

        description.MiscFlags = 0;
        description.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        if (FAILED(source.As(&mutex)) ||
            FAILED(device->CreateTexture2D(&description, nullptr, &input)) ||
            FAILED(device->CreateShaderResourceView(input.Get(), nullptr, &view)))
        {
            Disconnect();
            return false;
        }
        shared = std::move(source);
        return true;
    }

    void RaiseAboveRipple() const
    {
        // Ripple may have started after CRT. Only adjust the overlay when the
        // matching Ripple surface really precedes it in the desktop Z order.
        HWND above = GetWindow(owner, GW_HWNDPREV);
        for (int index = 0; above && index < 1024; ++index, above = GetWindow(above, GW_HWNDPREV))
        {
            if (above != producer) continue;
            SetWindowPos(owner, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
            return;
        }
    }
};
}
