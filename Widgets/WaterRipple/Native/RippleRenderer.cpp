#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <d3d11.h>
#include <d3d11_4.h>
#include <dxgi1_4.h>
#include <wrl/client.h>
#include <roapi.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstring>
#include <memory>
#include <limits>
#include <new>
#include <string>
#include <vector>
#include "RippleRenderer.h"
#include "CrtFrameExport.h"
#include "../../Shared/EffectWindowCoverage.h"

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "windowsapp.lib")
#pragma comment(lib, "advapi32.lib")

using Microsoft::WRL::ComPtr;
using Clock = std::chrono::steady_clock;
namespace CaptureApi = winrt::Windows::Graphics::Capture;
namespace DirectXApi = winrt::Windows::Graphics::DirectX;
namespace Direct3DApi = winrt::Windows::Graphics::DirectX::Direct3D11;

// WinUI 3's native SwapChainPanel interface. No WinRT activation or XAML implementation
// is needed here: the UI passes the existing panel's IUnknown on its owning thread.
struct __declspec(uuid("63AAD0B8-7C24-40FF-85A8-640D944CC325")) RippleSwapChainPanelNative : IUnknown
{
    virtual HRESULT STDMETHODCALLTYPE SetSwapChain(IDXGISwapChain* swapChain) = 0;
};

namespace
{
    // The minimum is decimal GB: Windows can report an 8 GB-class GPU's dedicated
    // capacity below 8 GiB after reservations. The reported value must still be >= 8e9.
    constexpr uint64_t MinimumDedicatedVideoMemory = 8'000'000'000ull;

    struct ApartmentScope
    {
        HRESULT Result = RoInitialize(RO_INIT_MULTITHREADED);
        ~ApartmentScope() { if (SUCCEEDED(Result)) RoUninitialize(); }
        bool Available() const { return SUCCEEDED(Result) || Result == RPC_E_CHANGED_MODE; }
    };

    CaptureApi::GraphicsCaptureItem ItemForMonitor(HMONITOR monitor)
    {
        auto factory = winrt::get_activation_factory<CaptureApi::GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
        CaptureApi::GraphicsCaptureItem item{ nullptr };
        winrt::check_hresult(factory->CreateForMonitor(monitor,
            winrt::guid_of<CaptureApi::GraphicsCaptureItem>(), winrt::put_abi(item)));
        return item;
    }

    Direct3DApi::IDirect3DDevice CaptureDevice(ID3D11Device* device)
    {
        ComPtr<IDXGIDevice> dxgiDevice;
        winrt::check_hresult(device->QueryInterface(IID_PPV_ARGS(&dxgiDevice)));
        winrt::com_ptr<IInspectable> inspectable;
        winrt::check_hresult(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.Get(), inspectable.put()));
        return inspectable.as<Direct3DApi::IDirect3DDevice>();
    }

    enum CapabilityReason : int32_t
    {
        CapabilitySupported = 0,
        CapabilityInsufficientVram = 1,
        CapabilitySoftwareAdapter = 2,
        CapabilityFeatureLevel = 3,
        CapabilityCaptureUnavailable = 5,
        CapabilityUnknown = 6
    };

    // Bind capture and policy checks to the adapter that actually owns this monitor.
    HRESULT FindOutput(HMONITOR monitor, ComPtr<IDXGIAdapter1>& adapter,
        ComPtr<IDXGIOutput1>& output, DXGI_OUTPUT_DESC& outputDescription)
    {
        adapter.Reset(); output.Reset(); outputDescription = {};
        ComPtr<IDXGIFactory1> factory;
        HRESULT result = CreateDXGIFactory1(IID_PPV_ARGS(&factory));
        if (FAILED(result)) return result;
        for (UINT ai = 0; ; ++ai)
        {
            ComPtr<IDXGIAdapter1> candidateAdapter;
            result = factory->EnumAdapters1(ai, &candidateAdapter);
            if (result == DXGI_ERROR_NOT_FOUND) break;
            if (FAILED(result)) return result;
            for (UINT oi = 0; ; ++oi)
            {
                ComPtr<IDXGIOutput> candidateOutput;
                result = candidateAdapter->EnumOutputs(oi, &candidateOutput);
                if (result == DXGI_ERROR_NOT_FOUND) break;
                if (FAILED(result)) return result;
                DXGI_OUTPUT_DESC description{};
                result = candidateOutput->GetDesc(&description);
                if (FAILED(result)) return result;
                if (description.AttachedToDesktop && description.Monitor == monitor)
                {
                    result = candidateOutput.As(&output);
                    if (FAILED(result)) return result;
                    adapter = candidateAdapter;
                    outputDescription = description;
                    return S_OK;
                }
            }
        }
        return DXGI_ERROR_NOT_FOUND;
    }

    HRESULT CreateProbeDevice(IDXGIAdapter1* adapter, ComPtr<ID3D11Device>& device,
        D3D_FEATURE_LEVEL& actual)
    {
        constexpr D3D_FEATURE_LEVEL levels[]{ D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0,
            D3D_FEATURE_LEVEL_10_1, D3D_FEATURE_LEVEL_10_0 };
        HRESULT result = D3D11CreateDevice(adapter, D3D_DRIVER_TYPE_UNKNOWN, nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, ARRAYSIZE(levels), D3D11_SDK_VERSION,
            &device, &actual, nullptr);
        if (result == E_INVALIDARG)
            result = D3D11CreateDevice(adapter, D3D_DRIVER_TYPE_UNKNOWN, nullptr,
                D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels + 1, ARRAYSIZE(levels) - 1,
                D3D11_SDK_VERSION, &device, &actual, nullptr);
        return result;
    }

    HRESULT CheckCapabilities(int x, int y, int w, int h, RippleCapabilities& capabilities)
    {
        capabilities = {};
        capabilities.Reason = CapabilityUnknown;
        POINT center{ x + w / 2, y + h / 2 };
        ComPtr<IDXGIAdapter1> adapter;
        ComPtr<IDXGIOutput1> output;
        DXGI_OUTPUT_DESC outputDescription{};
        HRESULT result = FindOutput(MonitorFromPoint(center, MONITOR_DEFAULTTONEAREST), adapter, output, outputDescription);
        if (FAILED(result)) return result;
        DXGI_ADAPTER_DESC1 description{};
        result = adapter->GetDesc1(&description);
        if (FAILED(result)) return result;
        std::memcpy(capabilities.AdapterName, description.Description, sizeof(capabilities.AdapterName));
        capabilities.AdapterName[127] = L'\0';
        capabilities.DedicatedVideoMemory = static_cast<uint64_t>(description.DedicatedVideoMemory);
        capabilities.SharedSystemMemory = static_cast<uint64_t>(description.SharedSystemMemory);
        capabilities.Left = outputDescription.DesktopCoordinates.left;
        capabilities.Top = outputDescription.DesktopCoordinates.top;
        capabilities.Width = outputDescription.DesktopCoordinates.right - capabilities.Left;
        capabilities.Height = outputDescription.DesktopCoordinates.bottom - capabilities.Top;
        if (description.Flags & DXGI_ADAPTER_FLAG_SOFTWARE)
        {
            capabilities.Reason = CapabilitySoftwareAdapter;
            return S_OK;
        }
        if (capabilities.DedicatedVideoMemory < MinimumDedicatedVideoMemory)
        {
            capabilities.Reason = CapabilityInsufficientVram;
            return S_OK;
        }
        ComPtr<ID3D11Device> device;
        D3D_FEATURE_LEVEL actual{};
        result = CreateProbeDevice(adapter.Get(), device, actual);
        capabilities.FeatureLevel = static_cast<uint32_t>(actual);
        if (FAILED(result) || actual < D3D_FEATURE_LEVEL_11_0)
        {
            capabilities.Reason = CapabilityFeatureLevel;
            return S_OK;
        }
        // Probe the same Windows Graphics Capture path used by the renderer.
        // No picker, capture start or permission dialog is invoked by this check.
        ApartmentScope apartment;
        if (!apartment.Available()) return apartment.Result;
        try
        {
            if (!CaptureApi::GraphicsCaptureSession::IsSupported())
            {
                capabilities.Reason = CapabilityCaptureUnavailable;
                return S_OK;
            }
            // Windows Graphics Capture targets the monitor in desktop coordinates,
            // so the WGC size must match the desktop bounds in any orientation.
            auto item = ItemForMonitor(outputDescription.Monitor);
            auto size = item.Size();
            if (size.Width != capabilities.Width || size.Height != capabilities.Height ||
                size.Width < 1 || size.Height < 1)
            {
                capabilities.Reserved = HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
                capabilities.Reason = CapabilityCaptureUnavailable;
                return S_OK;
            }
            auto captureDevice = CaptureDevice(device.Get());
            auto pool = CaptureApi::Direct3D11CaptureFramePool::CreateFreeThreaded(captureDevice,
                DirectXApi::DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, size);
            pool.Close();
        }
        catch (const winrt::hresult_error& error)
        {
            capabilities.Reserved = error.code();
            capabilities.Reason = CapabilityCaptureUnavailable;
            return S_OK;
        }
        capabilities.Supported = 1;
        capabilities.Reason = CapabilitySupported;
        return S_OK;
    }

    float Milliseconds(Clock::time_point start)
    {
        return std::chrono::duration<float, std::milli>(Clock::now() - start).count();
    }

    HRESULT ReadShader(const std::wstring& path, std::vector<unsigned char>& bytes)
    {
        HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) return HRESULT_FROM_WIN32(GetLastError());
        LARGE_INTEGER size{};
        if (!GetFileSizeEx(file, &size))
        {
            HRESULT error = HRESULT_FROM_WIN32(GetLastError());
            CloseHandle(file);
            return error;
        }
        if (size.QuadPart < 1 || size.QuadPart > 1024 * 1024)
        {
            CloseHandle(file);
            return E_INVALIDARG;
        }
        try { bytes.resize(static_cast<size_t>(size.QuadPart)); }
        catch (...)
        {
            CloseHandle(file);
            return E_OUTOFMEMORY;
        }
        DWORD read{};
        BOOL succeeded = ReadFile(file, bytes.data(), static_cast<DWORD>(bytes.size()), &read, nullptr);
        HRESULT result = succeeded && read == bytes.size() ? S_OK : HRESULT_FROM_WIN32(succeeded ? ERROR_HANDLE_EOF : GetLastError());
        CloseHandle(file);
        return result;
    }

    struct ShaderConstants
    {
        float Surface[4];
        float Timing[4];
        float Chromatic[4];
        float Filters[4];
        RippleWave Waves[8];
    };
    static_assert(sizeof(ShaderConstants) == 192);
    static_assert(sizeof(RippleWave) == 16);
    static_assert(sizeof(RippleStats) == 32);
    static_assert(sizeof(RippleCapabilities) == 304);
    static_assert(sizeof(RippleGpuMemory) == 32);

    using vibeware::desktop_effect::EffectTarget;

    class Renderer
    {
    public:
        HRESULT Initialize(void* panelPointer, const wchar_t* directory)
        {
            HRESULT result = static_cast<IUnknown*>(panelPointer)->QueryInterface(IID_PPV_ARGS(&panel));
            if (FAILED(result)) return result;
            std::wstring path(directory);
            if (!path.empty() && path.back() != L'\\' && path.back() != L'/') path += L'\\';
            result = ReadShader(path + L"RippleVS.cso", vertexBytes);
            if (FAILED(result)) return result;
            return ReadShader(path + L"RipplePS.cso", pixelBytes);
        }

        ~Renderer()
        {
            ResetDevice();
        }

        HRESULT Render(int x, int y, int w, int h, float widthDip, float heightDip,
            float strength, float band, float speed, float duration, float chromaticSeparation, float chromaticBlend, float crest, float ripples, float liquid,
            const RippleWave* waves, int count, bool captureEnabled, bool captureDue)
        {
            const auto start = Clock::now();
            stats.Status &= StatusClipped | StatusWaiting | StatusAccessLost | StatusValidCapture | StatusPresentedCapture;
            if (!captureEnabled) crtExport.Reset();
            HRESULT result = S_OK;
            // A frozen snapshot stays on its current device even if the window moves.
            if (!device || captureEnabled || screenMode)
            {
                POINT center{ x + w / 2, y + h / 2 };
                HMONITOR target = MonitorFromPoint(center, MONITOR_DEFAULTTONEAREST);
                result = EnsureOutput(target);
                if (FAILED(result)) return Finish(start, result);
                const RECT overlayBounds{ x, y, x + w, y + h };
                if (screenMode && !EqualRect(&overlayBounds, &outputDescription.DesktopCoordinates))
                {
                    InvalidateCapture(true);
                    return Finish(start, HRESULT_FROM_WIN32(ERROR_INVALID_DATA));
                }
            }
            else if (FAILED(device->GetDeviceRemovedReason()))
            {
                ResetDevice();
                return Finish(start, DXGI_ERROR_DEVICE_REMOVED);
            }
            result = EnsureSwapChain(w, h, widthDip, heightDip);
            if (FAILED(result)) return Finish(start, result);

            // A preview snapshot owns only its cropped texture while frozen.
            // Stop the producer and release both capture-pool buffers immediately.
            if (!captureEnabled && framePool) CloseCapture();

            if (captureEnabled && hasValidCapture && (captureX != x || captureY != y || captureWidth != w || captureHeight != h))
                InvalidateCapture(false);

            const auto previousCapture = stats.CaptureCount;
            if (captureEnabled && captureDue)
            {
                auto captureStart = Clock::now();
                result = Capture(x, y, w, h);
                stats.LastCaptureMs = Milliseconds(captureStart);
                if (FAILED(result)) { InvalidateCapture(true); return Finish(start, result); }
            }

            // A screen overlay must never cover the desktop with the placeholder
            // while capture starts, reconnects, or changes its source geometry.
            if (screenMode && (!hasValidCapture || !captureView))
            {
                stats.Status |= StatusWaiting;
                return Finish(start, S_OK);
            }

            const bool hasWaves = count > 0;
            const bool changed = stats.CaptureCount != previousCapture || hasWaves || hadWaves;
            hadWaves = hasWaves;
            const bool localFramePending = changed || presentPending || !(stats.Status & StatusPresentedCapture);
            const bool shareCapture = screenMode && captureEnabled;
            auto publication = shareCapture ? crtExport.Observe(device.Get(), adapter.Get(), monitor,
                outputDescription.DesktopCoordinates, start, changed) : water_ripple::CrtFrameExport::Publication{};
            if (screenMode && !hasWaves)
            {
                // The full-monitor capture already has the size and format needed by both effects.
                // No ripple shader or intermediate swap-chain image is needed while CRT presents it.
                if (shareCapture && crtExport.NeedsFrame(start) &&
                    !crtExport.Publish(context.Get(), captureTexture.Get(), start, localFramePending && !publication.presentedByCrt))
                    publication = {};
                if (UseCrtPresentation(publication) || !localFramePending) return Finish(start, S_OK);
                context->CopyResource(backBuffer.Get(), captureTexture.Get());
            }
            else if (hasValidCapture && captureView)
            {
                ShaderConstants constants{};
                constants.Surface[0] = widthDip;
                constants.Surface[1] = heightDip;
                constants.Surface[2] = strength;
                constants.Surface[3] = std::max(1.0f, band);
                constants.Timing[0] = speed;
                constants.Timing[1] = std::max(0.01f, duration);
                constants.Timing[2] = static_cast<float>(count);
                if (count && effectTarget != EffectTarget::Desktop)
                {
                    result = coverage.Update(device.Get(), context.Get(), overlayWindow, RECT{x, y, x + w, y + h}, start);
                    if (FAILED(result)) return Finish(start, result);
                    constants.Timing[3] = static_cast<float>(effectTarget);
                }
                constants.Chromatic[0] = chromaticSeparation;
                constants.Chromatic[1] = chromaticBlend;
                constants.Filters[0] = crest;
                constants.Filters[1] = ripples;
                constants.Filters[2] = liquid;
                if (count) std::memcpy(constants.Waves, waves, static_cast<size_t>(count) * sizeof(RippleWave));
                context->UpdateSubresource(constantBuffer.Get(), 0, nullptr, &constants, 0, 0);
                ID3D11RenderTargetView* target = targetView.Get();
                context->OMSetRenderTargets(1, &target, nullptr);
                D3D11_VIEWPORT viewport{ 0.0f, 0.0f, static_cast<float>(w), static_cast<float>(h), 0.0f, 1.0f };
                context->RSSetViewports(1, &viewport);
                context->RSSetState(rasterizer.Get());
                context->IASetInputLayout(nullptr);
                context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
                context->VSSetShader(vertexShader.Get(), nullptr, 0);
                context->PSSetShader(pixelShader.Get(), nullptr, 0);
                ID3D11Buffer* buffer = constantBuffer.Get();
                ID3D11SamplerState* samplerPointer = sampler.Get();
                ID3D11ShaderResourceView* sources[]{ captureView.Get(), constants.Timing[3] ? coverage.View() : nullptr };
                context->PSSetConstantBuffers(0, 1, &buffer);
                context->PSSetSamplers(0, 1, &samplerPointer);
                context->PSSetShaderResources(0, ARRAYSIZE(sources), sources);
                context->Draw(3, 0);
                // Unbind before the next frame writes to the capture texture.
                ID3D11ShaderResourceView* nullViews[2]{};
                context->PSSetShaderResources(0, ARRAYSIZE(nullViews), nullViews);
                context->OMSetRenderTargets(0, nullptr, nullptr);
                if (shareCapture && crtExport.NeedsFrame(start) &&
                    !crtExport.Publish(context.Get(), backBuffer.Get(), start, !publication.presentedByCrt))
                    publication = {};
                if (UseCrtPresentation(publication)) return Finish(start, S_OK);
            }
            else
            {
                const float background[4]{ 0.035f, 0.065f, 0.10f, 1.0f };
                context->ClearRenderTargetView(targetView.Get(), background);
            }
            result = swapChain->Present(0, DXGI_PRESENT_DO_NOT_WAIT);
            presentPending = result == DXGI_ERROR_WAS_STILL_DRAWING || result == DXGI_STATUS_OCCLUDED;
            if (result == DXGI_ERROR_WAS_STILL_DRAWING)
            {
                stats.Status |= StatusPresentBusy;
                result = S_OK;
            }
            else if (result == DXGI_STATUS_OCCLUDED)
            {
                stats.Status |= StatusOccluded;
                result = S_OK;
            }
            else if (result == S_OK)
            {
                ++stats.PresentCount;
                if (hasValidCapture && captureView) stats.Status |= StatusPresentedCapture;
            }
            return Finish(start, result);
        }

        RippleStats stats{};

        HRESULT SetScreenMode(bool enabled)
        {
            if (screenMode != enabled)
            {
                screenMode = enabled;
                InvalidateCapture(true);
            }
            return screenMode && adapter ? ValidateScreenAdapter() : S_OK;
        }

        void SetInteropWindow(HWND window) { overlayWindow = window; crtExport.SetWindow(window); }
        void SetTarget(EffectTarget target)
        {
            effectTarget = target;
            if (target == EffectTarget::Desktop) coverage.Reset();
        }

        HRESULT GetGpuMemory(RippleGpuMemory& memory)
        {
            memory = {};
            if (!adapter) return S_FALSE;
            DXGI_ADAPTER_DESC1 description{};
            HRESULT result = adapter->GetDesc1(&description);
            if (FAILED(result)) return result;
            memory.DedicatedCapacity = static_cast<uint64_t>(description.DedicatedVideoMemory);
            ComPtr<IDXGIAdapter3> adapter3;
            result = adapter.As(&adapter3);
            if (FAILED(result)) return result;
            DXGI_QUERY_VIDEO_MEMORY_INFO local{}, nonLocal{};
            result = adapter3->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &local);
            if (FAILED(result)) return result;
            result = adapter3->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL, &nonLocal);
            if (FAILED(result)) return result;
            // CurrentUsage is process-wide on this adapter, not specific to this renderer.
            memory.LocalUsage = local.CurrentUsage;
            memory.LocalBudget = local.Budget;
            memory.SharedUsage = nonLocal.CurrentUsage;
            return S_OK;
        }

    private:
        ComPtr<RippleSwapChainPanelNative> panel;
        ComPtr<IDXGIAdapter1> adapter;
        ComPtr<IDXGIOutput1> output;
        CaptureApi::GraphicsCaptureItem captureItem{ nullptr };
        Direct3DApi::IDirect3DDevice captureDevice{ nullptr };
        CaptureApi::Direct3D11CaptureFramePool framePool{ nullptr };
        CaptureApi::GraphicsCaptureSession captureSession{ nullptr };
        winrt::Windows::Graphics::SizeInt32 poolSize{};
        ComPtr<ID3D11Device> device;
        ComPtr<ID3D11DeviceContext> context;
        ComPtr<IDXGISwapChain1> swapChain;
        ComPtr<ID3D11Texture2D> backBuffer;
        ComPtr<ID3D11RenderTargetView> targetView;
        ComPtr<ID3D11VertexShader> vertexShader;
        ComPtr<ID3D11PixelShader> pixelShader;
        ComPtr<ID3D11Buffer> constantBuffer;
        ComPtr<ID3D11SamplerState> sampler;
        ComPtr<ID3D11RasterizerState> rasterizer;
        ComPtr<ID3D11Texture2D> desktopCache;
        ComPtr<ID3D11Texture2D> captureTexture;
        ComPtr<ID3D11ShaderResourceView> captureView;
        ComPtr<ID3D11RenderTargetView> captureTarget;
        vibeware::desktop_effect::WindowCoverage coverage;
        EffectTarget effectTarget{};
        HWND overlayWindow{};
        std::vector<unsigned char> vertexBytes, pixelBytes;
        DXGI_OUTPUT_DESC outputDescription{};
        HMONITOR monitor{};
        LUID adapterLuid{};
        int swapWidth{}, swapHeight{};
        float swapScaleX{}, swapScaleY{};
        bool hasSwapScale{}, hadWaves{}, presentPending{};
        UINT desktopWidth{}, desktopHeight{};
        int captureWidth{}, captureHeight{};
        bool screenMode{};
        bool hasValidCapture{};
        int captureX{}, captureY{};
        water_ripple::CrtFrameExport crtExport;

        bool UseCrtPresentation(const water_ripple::CrtFrameExport::Publication& publication)
        {
            if (publication.composed) stats.Status |= StatusCrtComposition;
            if (!publication.presentedByCrt) return false;
            // Reacquire local visibility only after a new local Present succeeds.
            stats.Status &= ~StatusPresentedCapture;
            stats.Status |= StatusCrtPresentation;
            presentPending = false;
            return true;
        }

        void InvalidateCapture(bool discardDesktop)
        {
            crtExport.Reset();
            hasValidCapture = false;
            stats.Status &= ~(StatusValidCapture | StatusPresentedCapture);
            if (discardDesktop)
            {
                desktopCache.Reset();
                desktopWidth = desktopHeight = 0;
            }
        }

        HRESULT Finish(Clock::time_point start, HRESULT result)
        {
            stats.LastRenderCpuMs = Milliseconds(start);
            stats.Reserved = result;
            return result;
        }

        void ResetDevice()
        {
            crtExport.Reset();
            CloseCapture();
            if (context) context->ClearState();
            if (panel) panel->SetSwapChain(nullptr);
            captureTarget.Reset(); captureView.Reset(); captureTexture.Reset(); desktopCache.Reset();
            coverage.Reset();
            targetView.Reset(); backBuffer.Reset(); swapChain.Reset();
            // Release deferred D3D objects before a replacement composition swap
            // chain is attached to the same panel on the next start cycle.
            if (context) context->Flush();
            rasterizer.Reset(); sampler.Reset(); constantBuffer.Reset(); pixelShader.Reset(); vertexShader.Reset();
            context.Reset(); device.Reset(); output.Reset(); adapter.Reset();
            monitor = nullptr;
            adapterLuid = {};
            swapWidth = swapHeight = captureWidth = captureHeight = 0;
            hasSwapScale = hadWaves = presentPending = false;
            desktopWidth = desktopHeight = 0;
            stats.CaptureWidth = stats.CaptureHeight = 0;
            stats.Status = 0;
            hasValidCapture = false;
            captureX = captureY = 0;
        }

        HRESULT EnsureOutput(HMONITOR targetMonitor)
        {
            if (device && FAILED(device->GetDeviceRemovedReason())) ResetDevice();
            if (device && monitor == targetMonitor && output)
            {
                HRESULT result = output->GetDesc(&outputDescription);
                if (FAILED(result)) return result;
                return screenMode ? ValidateScreenAdapter() : S_OK;
            }
            ComPtr<IDXGIFactory1> factory;
            HRESULT result = CreateDXGIFactory1(IID_PPV_ARGS(&factory));
            if (FAILED(result)) return result;
            ComPtr<IDXGIAdapter1> selectedAdapter;
            ComPtr<IDXGIOutput1> selectedOutput;
            DXGI_OUTPUT_DESC selectedDescription{};
            for (UINT ai = 0; ; ++ai)
            {
                ComPtr<IDXGIAdapter1> candidateAdapter;
                result = factory->EnumAdapters1(ai, &candidateAdapter);
                if (result == DXGI_ERROR_NOT_FOUND) break;
                if (FAILED(result)) return result;
                for (UINT oi = 0; ; ++oi)
                {
                    ComPtr<IDXGIOutput> candidateOutput;
                    result = candidateAdapter->EnumOutputs(oi, &candidateOutput);
                    if (result == DXGI_ERROR_NOT_FOUND) break;
                    if (FAILED(result)) return result;
                    DXGI_OUTPUT_DESC description{};
                    result = candidateOutput->GetDesc(&description);
                    if (FAILED(result)) return result;
                    if (description.AttachedToDesktop && description.Monitor == targetMonitor)
                    {
                        result = candidateOutput.As(&selectedOutput);
                        if (FAILED(result)) return result;
                        selectedAdapter = candidateAdapter;
                        selectedDescription = description;
                        break;
                    }
                }
                if (selectedOutput) break;
            }
            if (!selectedOutput) return DXGI_ERROR_NOT_FOUND;
            DXGI_ADAPTER_DESC1 description{};
            result = selectedAdapter->GetDesc1(&description);
            if (FAILED(result)) return result;
            if (screenMode)
            {
                if (description.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) return DXGI_ERROR_UNSUPPORTED;
                if (static_cast<uint64_t>(description.DedicatedVideoMemory) < MinimumDedicatedVideoMemory)
                    return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
            }
            bool differentAdapter = !device || description.AdapterLuid.LowPart != adapterLuid.LowPart || description.AdapterLuid.HighPart != adapterLuid.HighPart;
            if (differentAdapter)
            {
                ResetDevice();
                adapter = selectedAdapter;
                adapterLuid = description.AdapterLuid;
                result = CreateDevice();
                if (FAILED(result)) { ResetDevice(); return result; }
            }
            CloseCapture();
            InvalidateCapture(true);
            output = selectedOutput;
            outputDescription = selectedDescription;
            monitor = targetMonitor;
            if (!screenMode) return S_OK;

            result = ValidateScreenAdapter();
            if (SUCCEEDED(result) && overlayWindow)
            {
                // On first attachment, honor the optional capture-excluded post effect.
                // Existing CRT/Ripple handoff remains responsible for the order between them.
                namespace ipc = vibeware::desktop_effect;
                auto vignette = ipc::FindVignetteOverlay(monitor, outputDescription.DesktopCoordinates);
                if (vignette && ipc::IsWindowAbove(overlayWindow, vignette))
                    SetWindowPos(overlayWindow, vignette, 0, 0, 0, 0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
            }
            return result;
        }

        HRESULT ValidateScreenAdapter()
        {
            if (!adapter || !device) return E_UNEXPECTED;
            DXGI_ADAPTER_DESC1 description{};
            HRESULT result = adapter->GetDesc1(&description);
            if (FAILED(result)) return result;
            if (description.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) return DXGI_ERROR_UNSUPPORTED;
            if (static_cast<uint64_t>(description.DedicatedVideoMemory) < MinimumDedicatedVideoMemory)
                return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
            return device->GetFeatureLevel() >= D3D_FEATURE_LEVEL_11_0 ? S_OK : DXGI_ERROR_UNSUPPORTED;
        }

        HRESULT CreateDevice()
        {
            constexpr D3D_FEATURE_LEVEL levels[]{ D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
            D3D_FEATURE_LEVEL actual{};
            HRESULT result = D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                levels, ARRAYSIZE(levels), D3D11_SDK_VERSION, &device, &actual, &context);
            if (result == E_INVALIDARG)
                result = D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                    levels + 1, 1, D3D11_SDK_VERSION, &device, &actual, &context);
            if (FAILED(result)) return result;
            ComPtr<ID3D11Multithread> multithread;
            if (SUCCEEDED(context.As(&multithread))) multithread->SetMultithreadProtected(TRUE);
            ComPtr<IDXGIDevice1> dxgiDevice;
            if (SUCCEEDED(device.As(&dxgiDevice))) dxgiDevice->SetMaximumFrameLatency(1);
            result = device->CreateVertexShader(vertexBytes.data(), vertexBytes.size(), nullptr, &vertexShader);
            if (FAILED(result)) return result;
            result = device->CreatePixelShader(pixelBytes.data(), pixelBytes.size(), nullptr, &pixelShader);
            if (FAILED(result)) return result;
            D3D11_BUFFER_DESC bufferDescription{};
            bufferDescription.ByteWidth = sizeof(ShaderConstants);
            bufferDescription.Usage = D3D11_USAGE_DEFAULT;
            bufferDescription.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
            result = device->CreateBuffer(&bufferDescription, nullptr, &constantBuffer);
            if (FAILED(result)) return result;
            D3D11_SAMPLER_DESC samplerDescription{};
            samplerDescription.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
            samplerDescription.AddressU = samplerDescription.AddressV = samplerDescription.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
            samplerDescription.MaxLOD = D3D11_FLOAT32_MAX;
            result = device->CreateSamplerState(&samplerDescription, &sampler);
            if (FAILED(result)) return result;
            D3D11_RASTERIZER_DESC rasterizerDescription{};
            rasterizerDescription.FillMode = D3D11_FILL_SOLID;
            rasterizerDescription.CullMode = D3D11_CULL_NONE;
            rasterizerDescription.DepthClipEnable = TRUE;
            return device->CreateRasterizerState(&rasterizerDescription, &rasterizer);
        }

        HRESULT EnsureSwapChain(int w, int h, float widthDip, float heightDip)
        {
            HRESULT result = S_OK;
            if (!swapChain)
            {
                stats.Status &= ~StatusPresentedCapture;
                ComPtr<IDXGIFactory2> factory;
                result = adapter->GetParent(IID_PPV_ARGS(&factory));
                if (FAILED(result)) return result;
                DXGI_SWAP_CHAIN_DESC1 description{};
                description.Width = w; description.Height = h;
                description.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
                description.SampleDesc.Count = 1;
                description.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
                description.BufferCount = 2;
                description.Scaling = DXGI_SCALING_STRETCH;
                description.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
                description.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
                result = factory->CreateSwapChainForComposition(device.Get(), &description, nullptr, &swapChain);
                if (FAILED(result)) return result;
                result = panel->SetSwapChain(swapChain.Get());
                if (FAILED(result)) { swapChain.Reset(); return result; }
                swapWidth = w; swapHeight = h;
            }
            else if (swapWidth != w || swapHeight != h)
            {
                stats.Status &= ~StatusPresentedCapture;
                context->OMSetRenderTargets(0, nullptr, nullptr);
                targetView.Reset();
                backBuffer.Reset();
                result = swapChain->ResizeBuffers(0, w, h, DXGI_FORMAT_UNKNOWN, 0);
                if (FAILED(result)) return result;
                swapWidth = w; swapHeight = h;
            }
            if (!targetView)
            {
                result = swapChain->GetBuffer(0, IID_PPV_ARGS(&backBuffer));
                if (FAILED(result)) return result;
                result = device->CreateRenderTargetView(backBuffer.Get(), nullptr, &targetView);
                if (FAILED(result)) return result;
            }
            float scaleX = widthDip / w;
            float scaleY = heightDip / h;
            if (hasSwapScale && scaleX == swapScaleX && scaleY == swapScaleY) return S_OK;
            ComPtr<IDXGISwapChain2> swapChain2;
            result = swapChain.As(&swapChain2);
            if (FAILED(result)) return result;
            DXGI_MATRIX_3X2_F transform{ scaleX, 0.0f, 0.0f, scaleY, 0.0f, 0.0f };
            result = swapChain2->SetMatrixTransform(&transform);
            if (SUCCEEDED(result))
            {
                swapScaleX = scaleX;
                swapScaleY = scaleY;
                hasSwapScale = presentPending = true;
            }
            return result;
        }

        HRESULT EnsureCaptureTexture(int w, int h)
        {
            if (captureTexture && captureWidth == w && captureHeight == h) return S_OK;
            InvalidateCapture(false);
            captureTarget.Reset(); captureView.Reset(); captureTexture.Reset();
            captureWidth = captureHeight = 0;
            D3D11_TEXTURE2D_DESC description{};
            description.Width = w; description.Height = h;
            description.MipLevels = 1; description.ArraySize = 1;
            description.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
            description.SampleDesc.Count = 1;
            description.Usage = D3D11_USAGE_DEFAULT;
            description.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
            HRESULT result = device->CreateTexture2D(&description, nullptr, &captureTexture);
            if (FAILED(result)) return result;
            result = device->CreateShaderResourceView(captureTexture.Get(), nullptr, &captureView);
            if (FAILED(result)) return result;
            result = device->CreateRenderTargetView(captureTexture.Get(), nullptr, &captureTarget);
            if (FAILED(result)) return result;
            captureWidth = w; captureHeight = h;
            stats.CaptureWidth = w; stats.CaptureHeight = h;
            const float background[4]{ 0.035f, 0.065f, 0.10f, 1.0f };
            context->ClearRenderTargetView(captureTarget.Get(), background);
            return S_OK;
        }

        void CloseCapture() noexcept
        {
            // Close the producer before releasing its frame pool and GPU device wrapper.
            if (captureSession) { try { captureSession.Close(); } catch (...) { } }
            captureSession = nullptr;
            if (framePool) { try { framePool.Close(); } catch (...) { } }
            framePool = nullptr;
            captureItem = nullptr;
            captureDevice = nullptr;
            poolSize = {};
        }

        HRESULT EnsureCaptureSession()
        {
            if (framePool && captureSession) return S_OK;
            try
            {
                if (!CaptureApi::GraphicsCaptureSession::IsSupported()) return DXGI_ERROR_UNSUPPORTED;
                captureItem = ItemForMonitor(monitor);
                captureDevice = CaptureDevice(device.Get());
                poolSize = captureItem.Size();
                if (poolSize.Width < 1 || poolSize.Height < 1)
                {
                    CloseCapture();
                    return DXGI_ERROR_UNSUPPORTED;
                }
                framePool = CaptureApi::Direct3D11CaptureFramePool::CreateFreeThreaded(captureDevice,
                    DirectXApi::DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, poolSize);
                captureSession = framePool.CreateCaptureSession(captureItem);
                captureSession.IsCursorCaptureEnabled(false);
                // Windows honors this only after the user grants Borderless access.
                // A denied permission keeps the OS border; capture must remain usable.
                try { captureSession.IsBorderRequired(false); }
                catch (const winrt::hresult_error&) { }
                captureSession.StartCapture();
                return S_OK;
            }
            catch (const winrt::hresult_error& error)
            {
                CloseCapture();
                return error.code();
            }
        }

        void MarkCapture(int x, int y)
        {
            hasValidCapture = true;
            captureX = x;
            captureY = y;
            stats.Status |= StatusValidCapture;
            stats.Status &= ~(StatusWaiting | StatusAccessLost);
            ++stats.CaptureCount;
        }

        HRESULT Capture(int x, int y, int w, int h)
        {
            stats.Status &= ~(StatusClipped | StatusWaiting | StatusAccessLost);
            HRESULT result = EnsureCaptureSession();
            if (FAILED(result)) return result;
            bool desktopChanged = false;
            try
            {
                // Poll at most the two allocated frames. GPU processing stays on this
                // renderer's owning UI thread; the producer never calls its context.
                CaptureApi::Direct3D11CaptureFrame frame{ nullptr };
                for (int i = 0; i < 2; ++i)
                {
                    auto next = framePool.TryGetNextFrame();
                    if (!next) break;
                    if (frame) frame.Close();
                    frame = std::move(next);
                }
                if (frame)
                {
                    struct FrameGuard
                    {
                        CaptureApi::Direct3D11CaptureFrame& Frame;
                        ~FrameGuard() { if (Frame) { try { Frame.Close(); } catch (...) { } } }
                    } guard{ frame };
                    auto size = frame.ContentSize();
                    if (size.Width < 1 || size.Height < 1)
                    {
                        InvalidateCapture(true);
                        stats.Status |= StatusWaiting;
                        return S_OK;
                    }
                    if (screenMode && (size.Width != w || size.Height != h))
                    {
                        InvalidateCapture(true);
                        return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
                    }
                    if (size.Width != poolSize.Width || size.Height != poolSize.Height)
                    {
                        frame.Close(); frame = nullptr;
                        InvalidateCapture(true);
                        poolSize = size;
                        framePool.Recreate(captureDevice,
                            DirectXApi::DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, poolSize);
                        stats.Status |= StatusWaiting;
                        return S_OK;
                    }
                    auto access = frame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
                    ComPtr<ID3D11Texture2D> desktop;
                    winrt::check_hresult(access->GetInterface(IID_PPV_ARGS(&desktop)));
                    D3D11_TEXTURE2D_DESC description{};
                    desktop->GetDesc(&description);
                    if (description.Format != DXGI_FORMAT_B8G8R8A8_UNORM || description.Width < static_cast<UINT>(size.Width) ||
                        description.Height < static_cast<UINT>(size.Height)) return DXGI_ERROR_UNSUPPORTED;
                    D3D11_BOX content{ 0, 0, 0, static_cast<UINT>(size.Width), static_cast<UINT>(size.Height), 1 };
                    if (screenMode)
                    {
                        // A monitor overlay never crops or moves within the capture. Keep one GPU copy
                        // directly from WGC instead of maintaining a second full-monitor cache.
                        result = EnsureCaptureTexture(w, h);
                        if (FAILED(result)) return result;
                        context->CopySubresourceRegion(captureTexture.Get(), 0, 0, 0, 0, desktop.Get(), 0, &content);
                        MarkCapture(x, y);
                        return S_OK;
                    }
                    if (!desktopCache || desktopWidth != static_cast<UINT>(size.Width) || desktopHeight != static_cast<UINT>(size.Height))
                    {
                        desktopCache.Reset();
                        description.Width = static_cast<UINT>(size.Width);
                        description.Height = static_cast<UINT>(size.Height);
                        description.MipLevels = 1;
                        description.ArraySize = 1;
                        description.Usage = D3D11_USAGE_DEFAULT;
                        description.BindFlags = 0;
                        description.CPUAccessFlags = 0;
                        description.MiscFlags = 0;
                        result = device->CreateTexture2D(&description, nullptr, &desktopCache);
                        if (FAILED(result)) return result;
                        desktopWidth = description.Width; desktopHeight = description.Height;
                    }
                    context->CopySubresourceRegion(desktopCache.Get(), 0, 0, 0, 0, desktop.Get(), 0, &content);
                    desktopChanged = true;
                }
            }
            catch (const winrt::hresult_error& error)
            {
                CloseCapture();
                InvalidateCapture(true);
                stats.Status |= StatusAccessLost | StatusWaiting;
                return error.code();
            }
            if (screenMode || !desktopCache)
            {
                if (!hasValidCapture) stats.Status |= StatusWaiting;
                return S_OK;
            }
            if (!desktopChanged && hasValidCapture && captureX == x && captureY == y && captureWidth == w && captureHeight == h)
                return S_OK;
            result = EnsureCaptureTexture(w, h);
            if (FAILED(result)) return result;
            const RECT bounds = outputDescription.DesktopCoordinates;
            LONG left = std::max<LONG>(x, bounds.left);
            LONG top = std::max<LONG>(y, bounds.top);
            LONG right = std::min<LONG>(x + w, bounds.right);
            LONG bottom = std::min<LONG>(y + h, bounds.bottom);
            right = std::min<LONG>(right, bounds.left + static_cast<LONG>(desktopWidth));
            bottom = std::min<LONG>(bottom, bounds.top + static_cast<LONG>(desktopHeight));
            bool clipped = left != x || top != y || right != x + w || bottom != y + h;
            if (clipped)
            {
                stats.Status |= StatusClipped;
                const float background[4]{ 0.035f, 0.065f, 0.10f, 1.0f };
                context->ClearRenderTargetView(captureTarget.Get(), background);
            }
            if (right > left && bottom > top)
            {
                D3D11_BOX box{};
                box.left = left - bounds.left; box.top = top - bounds.top;
                box.right = right - bounds.left; box.bottom = bottom - bounds.top;
                box.back = 1;
                context->CopySubresourceRegion(captureTexture.Get(), 0, left - x, top - y, 0, desktopCache.Get(), 0, &box);
                MarkCapture(x, y);
            }
            else
            {
                InvalidateCapture(false);
                stats.Status |= StatusWaiting;
            }
            return S_OK;
        }
    };
}

extern "C" __declspec(dllexport) int32_t __cdecl RippleCreate(void* panel, const wchar_t* directory, void** handle)
{
    if (!panel || !directory || !handle) return E_POINTER;
    *handle = nullptr;
    try
    {
        auto renderer = std::make_unique<Renderer>();
        HRESULT result = renderer->Initialize(panel, directory);
        if (FAILED(result)) return result;
        *handle = renderer.release();
        return S_OK;
    }
    catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
    catch (...) { return E_FAIL; }
}

extern "C" __declspec(dllexport) int32_t __cdecl RippleRender(void* handle, int32_t x, int32_t y,
    int32_t w, int32_t h, float widthDip, float heightDip, float strength, float band, float speed, float duration, float chromaticSeparation, float chromaticBlend, float crest, float ripples, float liquid,
    const RippleWave* waves, int32_t count, int32_t captureEnabled, int32_t captureDue)
{
    if (!handle || (count > 0 && !waves)) return E_POINTER;
    if (w < 1 || h < 1 || w > 8192 || h > 8192 || widthDip <= 0 || heightDip <= 0 || count < 0 || count > 8) return E_INVALIDARG;
    try
    {
        return static_cast<Renderer*>(handle)->Render(x, y, w, h, widthDip, heightDip,
            strength, band, speed, duration, chromaticSeparation, chromaticBlend, crest, ripples, liquid, waves, count, captureEnabled != 0, captureDue != 0);
    }
    catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
    catch (...) { return E_FAIL; }
}

extern "C" __declspec(dllexport) int32_t __cdecl RippleGetStats(void* handle, RippleStats* stats)
{
    if (!handle || !stats) return E_POINTER;
    *stats = static_cast<Renderer*>(handle)->stats;
    return S_OK;
}

extern "C" __declspec(dllexport) int32_t __cdecl RippleCheckCapabilities(int32_t x, int32_t y,
    int32_t w, int32_t h, RippleCapabilities* capabilities)
{
    if (!capabilities) return E_POINTER;
    *capabilities = {};
    capabilities->Reason = CapabilityUnknown;
    if (w < 1 || h < 1) return E_INVALIDARG;
    const int64_t centerX = static_cast<int64_t>(x) + w / 2;
    const int64_t centerY = static_cast<int64_t>(y) + h / 2;
    if (centerX < std::numeric_limits<LONG>::min() || centerX > std::numeric_limits<LONG>::max() ||
        centerY < std::numeric_limits<LONG>::min() || centerY > std::numeric_limits<LONG>::max()) return E_INVALIDARG;
    try { return CheckCapabilities(x, y, w, h, *capabilities); }
    catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
    catch (...) { return E_FAIL; }
}

extern "C" __declspec(dllexport) int32_t __cdecl RippleSetScreenMode(void* handle, int32_t enabled)
{
    if (!handle) return E_POINTER;
    return static_cast<Renderer*>(handle)->SetScreenMode(enabled != 0);
}

extern "C" __declspec(dllexport) int32_t __cdecl RippleGetGpuMemory(void* handle, RippleGpuMemory* memory)
{
    if (!handle || !memory) return E_POINTER;
    return static_cast<Renderer*>(handle)->GetGpuMemory(*memory);
}

extern "C" __declspec(dllexport) void __cdecl RippleSetInteropWindow(void* handle, void* window)
{
    if (handle) static_cast<Renderer*>(handle)->SetInteropWindow(static_cast<HWND>(window));
}

extern "C" __declspec(dllexport) void __cdecl RippleSetTarget(void* handle, int32_t target)
{
    if (handle && target >= 0 && target <= static_cast<int32_t>(EffectTarget::Wallpaper))
        static_cast<Renderer*>(handle)->SetTarget(static_cast<EffectTarget>(target));
}











extern "C" __declspec(dllexport) void __cdecl RippleDestroy(void* handle)
{
    delete static_cast<Renderer*>(handle);
}
