// SPDX-License-Identifier: MIT
#pragma once
#include "DesktopEffectInterop.h"
#include <d3dcompiler.h>
#include <array>
#include <cstdint>

#pragma comment(lib, "d3dcompiler.lib")

namespace vibeware::desktop_effect
{
    // Window properties carry only bounded control data. Frame textures stay on the presenting GPU.
    inline constexpr wchar_t EffectStage[] = L"VibeWare.DisplayPipeline.Stage.v1";
    inline constexpr wchar_t DegaussOverlayClass[] = L"VibeWareCrtDegaussOverlay";
    inline constexpr wchar_t PulseSource[] = L"VibeWare.DisplayPipeline.Degauss.v1";
    inline constexpr wchar_t PulseTarget[] = L"VibeWare.DisplayPipeline.DegaussTarget.v1";
    inline constexpr wchar_t PulseFrame[] = L"VibeWare.DisplayPipeline.DegaussFrame.v1";
    inline constexpr wchar_t PulsePresented[] = L"VibeWare.DisplayPipeline.PulsePresented.v1";
    inline constexpr wchar_t VignetteParameters[] = L"VibeWare.DisplayPipeline.Vignette.v1";
    enum class Stage : uintptr_t { Ripple = 1, RetroTerminal = 2, Degauss = 3 };
    inline constexpr uint64_t TimeMask = (1ull << 24) - 1;
    inline constexpr uint64_t PulseExpiryMs = 220;

    using PulseValues = std::array<float, 4>;
    struct VignetteFrame
    {
        HWND window{};
        std::array<float, 4> shape{}; // intensity, radius, softness, aspect
        std::array<float, 4> color{}; // RGB and the enabled flag
    };

    inline VignetteFrame ReadVignette(HWND window)
    {
        if (!window) return {};
        const auto packed = reinterpret_cast<uintptr_t>(GetPropW(window, VignetteParameters));
        // A window with no matching property cannot be faithfully included in a pulse.
        if (!(packed >> 60)) return {window};
        const auto unit = [packed](int shift) { return float((packed >> shift) & 511) / 511.f; };
        return {window,
            {unit(24), unit(33), unit(42), .65f + unit(51) * .90f},
            {float((packed >> 16) & 255) / 255.f, float((packed >> 8) & 255) / 255.f,
                float(packed & 255) / 255.f, 1.f}};
    }

    inline VignetteFrame ReadVignette(HMONITOR monitor, const RECT& bounds)
    {
        return ReadVignette(FindVignetteOverlay(monitor, bounds));
    }

    inline bool IsRecentPulse(HWND presenter) noexcept
    {
        const auto tick = reinterpret_cast<uintptr_t>(GetPropW(presenter, PulsePresented));
        return tick && GetTickCount64() - tick <= PulseExpiryMs;
    }

    inline bool RequestPulse(HWND source, HWND target, const PulseValues& values) noexcept
    {
        // Shift into one x64 property so a reader can never see fields from two frames.
        static_assert(sizeof(uintptr_t) == 8);
        const auto fixed = [](float value, uint64_t scale) {
            return static_cast<uint64_t>(value * float(scale) + .5f);
        };
        const uint64_t word = fixed(values[2], 2047) | (fixed(values[0], 1023) << 11) |
            (uint64_t(values[1]) << 21) | (uint64_t(values[3]) << 24) |
            ((GetTickCount64() & TimeMask) << 38) | (1ull << 62);
        return SetPropW(source, PulseTarget, target) && SetPropW(source, PulseFrame, reinterpret_cast<HANDLE>(word));
    }

    inline void ClearPulse(HWND source) noexcept
    {
        RemovePropW(source, PulseSource);
        RemovePropW(source, PulseFrame);
        RemovePropW(source, PulseTarget);
    }

    // The existing renderer remains the presenter. An active Retro Terminal stage outranks Ripple.
    struct Upstream
    {
        HWND window{};
        bool acceptsPulse{};
    };

    inline Upstream FindPulsePresenter(HMONITOR monitor, const RECT& bounds)
    {
        struct Search { HMONITOR monitor; const RECT& bounds; const wchar_t* title; HWND window{}; };
        for (const auto stage : {Stage::RetroTerminal, Stage::Ripple}) {
            const bool retro = stage == Stage::RetroTerminal;
            Search search{monitor, bounds, retro ? L"Retro Terminal screen effect" : L"Water Ripple screen effect"};
            EnumWindows([](HWND window, LPARAM context) -> BOOL {
                auto& filter = *reinterpret_cast<Search*>(context);
                wchar_t title[64]{};
                if (GetWindowTextW(window, title, ARRAYSIZE(title)) && !std::wcscmp(title, filter.title) &&
                    IsEffectWindow(window, filter.monitor, filter.bounds)) {
                    filter.window = window;
                    return FALSE;
                }
                return TRUE;
            }, reinterpret_cast<LPARAM>(&search));
            if (search.window && OpenPeer(search.window, retro ? L"RetroStudio.exe" : L"WaterRipple.exe", 0))
                return {search.window, GetPropW(search.window, EffectStage) ==
                    reinterpret_cast<HANDLE>(static_cast<uintptr_t>(stage))};
        }
        return {};
    }

    // Each presenter handles only requests directed at its monitor window.
    class PulseEndpoint final
    {
    public:
        ~PulseEndpoint() { Reset(); }
        void Bind(HWND window, HMONITOR display, const RECT& area, Stage role)
        {
            if (owner == window && monitor == display && EqualRect(&bounds, &area)) return;
            Reset();
            owner = window;
            monitor = display;
            bounds = area;
            stage = role;
        }

        bool Read(Clock::time_point now, PulseValues& values)
        {
            if (!owner) return false;
            if (now >= nextDiscovery) {
                nextDiscovery = now + std::chrono::milliseconds(100);
                const HWND candidate = FindWindowW(DegaussOverlayClass, nullptr);
                if (candidate != source)
                    source = candidate && IsEffectWindow(candidate, monitor, bounds) &&
                        OpenPeer(candidate, L"CrtDegauss.exe", 0) ? candidate : nullptr;
            }
            if (!source || GetPropW(source, PulseSource) != reinterpret_cast<HANDLE>(1) ||
                GetPropW(source, PulseTarget) != owner) return false;
            const auto word = reinterpret_cast<uintptr_t>(GetPropW(source, PulseFrame));
            const auto age = ((GetTickCount64() & TimeMask) - ((word >> 38) & TimeMask)) & TimeMask;
            if (!word || age > PulseExpiryMs) return false;
            values = {float((word >> 11) & 1023) / 1023.f, float((word >> 21) & 7),
                float(word & 2047) / 2047.f, float((word >> 24) & 16383)};
            return values[1] >= 1 && values[1] <= 6;
        }

        VignetteFrame Vignette(Clock::time_point now, bool needed)
        {
            if (!needed) {
                nextVignetteDiscovery = {};
                return {};
            }
            if (now >= nextVignetteDiscovery) {
                vignetteWindow = FindVignetteOverlay(monitor, bounds);
                nextVignetteDiscovery = now + std::chrono::milliseconds(100);
            }
            return ReadVignette(vignetteWindow);
        }

        void Presented(bool pulse, HWND vignette) noexcept
        {
            if (!owner) return;
            if (!registered)
                registered = SetPropW(owner, EffectStage, reinterpret_cast<HANDLE>(static_cast<uintptr_t>(stage))) != FALSE;
            if (pulse) {
                if (SetPropW(owner, PulsePresented, reinterpret_cast<HANDLE>(GetTickCount64())) &&
                    vignette && IsWindowAbove(vignette, owner) && !Place(HWND_TOPMOST))
                    RemovePropW(owner, PulsePresented);
            } else if (wasPulse) {
                RemovePropW(owner, PulsePresented);
                if (vignette && IsWindowAbove(owner, vignette)) Place(vignette);
            }
            wasPulse = pulse;
        }

        void Reset() noexcept
        {
            if (owner) {
                if (wasPulse && vignetteWindow && IsWindowAbove(owner, vignetteWindow)) Place(vignetteWindow);
                RemovePropW(owner, EffectStage);
                RemovePropW(owner, PulsePresented);
            }
            owner = source = vignetteWindow = nullptr;
            nextDiscovery = nextVignetteDiscovery = {};
            registered = wasPulse = false;
        }

    private:
        bool Place(HWND after) const noexcept
        {
            return SetWindowPos(owner, after, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER) != FALSE;
        }

        HWND owner{}, source{}, vignetteWindow{};
        HMONITOR monitor{};
        RECT bounds{};
        Stage stage{};
        Clock::time_point nextDiscovery{}, nextVignetteDiscovery{};
        bool registered{}, wasPulse{};
    };

    // The full-size intermediate exists only while a pulse needs the finished upstream frame.
    class DegaussPass final
    {
    public:
        void Prepare(ID3D11Device* device, IDXGIAdapter1* adapter, UINT width, UINT height, const wchar_t* path,
            bool needsIntermediate = true)
        {
            if (!pixel) {
                ComPtr<ID3DBlob> bytecode;
                winrt::check_hresult(D3DCompileFromFile(path, nullptr, nullptr, "PSMain", "ps_5_0",
                    D3DCOMPILE_OPTIMIZATION_LEVEL3 | D3DCOMPILE_WARNINGS_ARE_ERRORS, 0, &bytecode, nullptr));
                winrt::check_hresult(device->CreatePixelShader(bytecode->GetBufferPointer(),
                    bytecode->GetBufferSize(), nullptr, &pixel));
                D3D11_BUFFER_DESC buffer{};
                buffer.ByteWidth = sizeof(Constants);
                buffer.Usage = D3D11_USAGE_DEFAULT;
                buffer.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
                winrt::check_hresult(device->CreateBuffer(&buffer, nullptr, &constants));
            }
            if (width == textureWidth && height == textureHeight && bool(target) == needsIntermediate) return;
            ReleaseFrame();
            textureWidth = width;
            textureHeight = height;
            if (!needsIntermediate) return;
            if (!HasTextureBudget(adapter, width, height))
                throw winrt::hresult_error(E_OUTOFMEMORY, L"The GPU has insufficient room for the degauss pass.");
            D3D11_TEXTURE2D_DESC description{};
            description.Width = width;
            description.Height = height;
            description.MipLevels = description.ArraySize = 1;
            description.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
            description.SampleDesc.Count = 1;
            description.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
            winrt::check_hresult(device->CreateTexture2D(&description, nullptr, &frame));
            winrt::check_hresult(device->CreateShaderResourceView(frame.Get(), nullptr, &view));
            winrt::check_hresult(device->CreateRenderTargetView(frame.Get(), nullptr, &target));
        }

        ID3D11RenderTargetView* Target() const noexcept { return target.Get(); }
        ID3D11Texture2D* Frame() const noexcept { return frame.Get(); }
        void ReleaseFrame() noexcept { target.Reset(); view.Reset(); frame.Reset(); }

        // Both upstream shaders generate the same fullscreen triangle and UVs.
        void Draw(ID3D11DeviceContext* context, ID3D11RenderTargetView* output, ID3D11ShaderResourceView* input,
            const PulseValues& pulse, const VignetteFrame& vignette)
        {
            const Constants values{{float(textureWidth), float(textureHeight)}, {}, pulse, vignette.shape, vignette.color};
            context->UpdateSubresource(constants.Get(), 0, nullptr, &values, 0, 0);
            context->OMSetRenderTargets(1, &output, nullptr);
            context->PSSetShader(pixel.Get(), nullptr, 0);
            ID3D11Buffer* buffer = constants.Get();
            context->PSSetConstantBuffers(0, 1, &buffer);
            ID3D11ShaderResourceView* source = input ? input : view.Get();
            context->PSSetShaderResources(0, 1, &source);
            context->Draw(3, 0);
            source = nullptr;
            context->PSSetShaderResources(0, 1, &source);
            context->OMSetRenderTargets(0, nullptr, nullptr);
        }

    private:
        struct alignas(16) Constants {
            std::array<float, 4> surface;
            std::array<float, 24> unused;
            PulseValues pulse;
            std::array<float, 4> vignette, color;
        };
        static_assert(sizeof(Constants) == 160);
        UINT textureWidth{}, textureHeight{};
        ComPtr<ID3D11PixelShader> pixel;
        ComPtr<ID3D11Buffer> constants;
        ComPtr<ID3D11Texture2D> frame;
        ComPtr<ID3D11ShaderResourceView> view;
        ComPtr<ID3D11RenderTargetView> target;
    };
}
