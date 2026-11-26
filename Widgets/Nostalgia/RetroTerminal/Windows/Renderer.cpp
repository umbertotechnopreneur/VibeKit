// SPDX-License-Identifier: MIT
#include "Renderer.h"
#include "PresentationRetry.h"
#include "RippleFrameInput.h"
#include "../../../Shared/EffectWindowCoverage.h"
#include "../../../Shared/MonitorEffects.h"
#include <d3d11.h>
#include <dxgi1_4.h>
#include <dcomp.h>
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Metadata.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <array>
#include <cmath>
#include <filesystem>

using Microsoft::WRL::ComPtr;
using namespace winrt::Windows::Graphics;
namespace DirectXApi = winrt::Windows::Graphics::DirectX;
namespace effects = vibeware::desktop_effect;
using Clock = std::chrono::steady_clock;

namespace
{
    void Check(HRESULT result) { winrt::check_hresult(result); }
    [[noreturn]] void Fail(const wchar_t* text) { throw winrt::hresult_error(E_FAIL, text); }
    [[noreturn]] void FailTransient(const wchar_t* text) { throw winrt::hresult_error(HRESULT_FROM_WIN32(ERROR_RETRY), text); }
    constexpr uint64_t MiB = 1024 * 1024;
    constexpr uint64_t MaxPixels = 3840 * 2160;
    constexpr int DesktopCaptureBuffers = 2;
    // Each process can take one discovery interval to notice its peer.
    constexpr auto RippleStartupGrace = 3 * vibeware::desktop_effect::DiscoveryInterval;
    struct alignas(16) Constants {
        float Surface[4], Phosphor[4], Scan[4], Tone[4], Finish[4], Glitch[4], Target[4], Effect[4];
        std::array<float, 4> Vignette, VignetteColor;
    };
    static_assert(sizeof(Constants) == 160);

    ComPtr<IDXGIAdapter1> FindAdapter(HMONITOR monitor, std::wstring& name)
    {
        ComPtr<IDXGIFactory1> factory;
        Check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)));
        for (UINT index = 0;; ++index)
        {
            ComPtr<IDXGIAdapter1> adapter;
            HRESULT hr = factory->EnumAdapters1(index, &adapter);
            if (hr == DXGI_ERROR_NOT_FOUND) break;
            Check(hr);
            DXGI_ADAPTER_DESC1 description{};
            Check(adapter->GetDesc1(&description));
            if (description.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) continue;
            for (UINT outputIndex = 0;; ++outputIndex)
            {
                ComPtr<IDXGIOutput> output;
                hr = adapter->EnumOutputs(outputIndex, &output);
                if (hr == DXGI_ERROR_NOT_FOUND) break;
                Check(hr);
                DXGI_OUTPUT_DESC info{};
                Check(output->GetDesc(&info));
                if (info.AttachedToDesktop && info.Monitor == monitor)
                {
                    name = description.Description;
                    return adapter;
                }
            }
        }
        Fail(L"No hardware graphics adapter owns the selected monitor.");
    }

    ComPtr<ID3DBlob> Compile(const std::wstring& path, const char* entry, const char* target)
    {
        ComPtr<ID3DBlob> code, errors;
        HRESULT hr = D3DCompileFromFile(path.c_str(), nullptr, nullptr, entry, target,
            D3DCOMPILE_OPTIMIZATION_LEVEL3 | D3DCOMPILE_WARNINGS_ARE_ERRORS, 0, &code, &errors);
        if (FAILED(hr))
        {
            if (errors) OutputDebugStringA(static_cast<const char*>(errors->GetBufferPointer()));
            throw winrt::hresult_error(hr, L"The desktop effect shader could not compile. Check the Shaders folder beside the application.");
        }
        return code;
    }
}

struct Renderer::State
{
    HWND overlay{};
    HMONITOR monitor{};
    RECT monitorBounds{};
    vibeware::desktop_effect::WindowCoverage coverage;
    UINT width{}, height{};
    bool validFrame = false, presentPending = false;
    PresentationRetry visibilityRetry;
    retro_terminal::RippleFrameInput rippleInput;
    effects::PulseEndpoint pulseEndpoint;
    effects::DegaussPass degaussPass;
    std::wstring degaussShader;
    bool degaussOverlay{}, drewPulse{}, declinedPulse{};
    effects::PulseValues drawnPulse{};
    HWND vignetteWindow{};
    bool usedRippleFrame = false, waitingForInput = true, presentedRippleFrame = false;
    Clock::time_point busySince{};
    std::wstring adapterName;
    Clock::time_point started = Clock::now();
    Clock::time_point waitingSince = started;
    Clock::time_point waitForRippleUntil{};
    Clock::time_point lastBudgetCheck = started;
    std::shared_ptr<std::atomic_bool> sourceClosed;
    winrt::event_token closedToken{};
    bool subscribed = false;
    bool supportsCaptureInterval = false;
    int captureFps{};
    winrt::Windows::Foundation::TimeSpan captureInterval{};
    Clock::time_point lastCapture{};
    Capture::GraphicsCaptureItem item{nullptr};
    Capture::Direct3D11CaptureFramePool pool{nullptr};
    Capture::GraphicsCaptureSession session{nullptr};
    ComPtr<IDXGIAdapter1> adapter;
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<IDXGISwapChain1> swap;
    ComPtr<ID3D11RenderTargetView> target;
    ComPtr<ID3D11Texture2D> desktop;
    ComPtr<ID3D11ShaderResourceView> desktopView;
    std::array<ComPtr<ID3D11Texture2D>, 2> history;
    std::array<ComPtr<ID3D11ShaderResourceView>, 2> historyViews;
    std::array<ComPtr<ID3D11RenderTargetView>, 2> historyTargets;
    unsigned historyRead = 0;
    bool historyValid = false, wasTail = false;
    Clock::time_point lastDraw = started, tailUntil = started;
    ComPtr<ID3D11VertexShader> vertex;
    ComPtr<ID3D11PixelShader> pixel;
    ComPtr<ID3D11Buffer> constants;
    ComPtr<ID3D11SamplerState> sampler;
    ComPtr<ID3D11RasterizerState> rasterizer;
    ComPtr<IDCompositionDevice> composition;
    ComPtr<IDCompositionTarget> compositionTarget;
    ComPtr<IDCompositionVisual> visual;

    void SetCaptureLimit(int fps)
    {
        if (captureFps == fps) return;
        captureFps = fps;
        captureInterval = std::chrono::ceil<winrt::Windows::Foundation::TimeSpan>(std::chrono::duration<double>(1.0 / fps));
        if (session && supportsCaptureInterval) session.MinUpdateInterval(captureInterval);
    }

    D3D11_TEXTURE2D_DESC FrameDescription(UINT bindFlags) const
    {
        D3D11_TEXTURE2D_DESC description{};
        description.Width = width;
        description.Height = height;
        description.MipLevels = description.ArraySize = 1;
        description.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        description.SampleDesc.Count = 1;
        description.Usage = D3D11_USAGE_DEFAULT;
        description.BindFlags = bindFlags;
        return description;
    }

    void OpenDesktopCapture()
    {
        if (!Capture::GraphicsCaptureSession::IsSupported()) Fail(L"Windows Graphics Capture is unavailable.");
        auto interop = winrt::get_activation_factory<Capture::GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
        Check(interop->CreateForMonitor(monitor, winrt::guid_of<Capture::GraphicsCaptureItem>(), winrt::put_abi(item)));
        sourceClosed = std::make_shared<std::atomic_bool>(false);
        closedToken = item.Closed([flag = sourceClosed](auto const&, auto const&) { flag->store(true); });
        subscribed = true;
        supportsCaptureInterval = winrt::Windows::Foundation::Metadata::ApiInformation::IsPropertyPresent(
            L"Windows.Graphics.Capture.GraphicsCaptureSession", L"MinUpdateInterval");
        const auto size = item.Size();
        if (size.Width != int(width) || size.Height != int(height))
            Fail(L"Capture dimensions do not match this monitor. Enable again after the display settles.");

        auto description = FrameDescription(D3D11_BIND_SHADER_RESOURCE);
        Check(device->CreateTexture2D(&description, nullptr, &desktop));
        Check(device->CreateShaderResourceView(desktop.Get(), nullptr, &desktopView));

        ComPtr<IDXGIDevice> dxgi;
        Check(device.As(&dxgi));
        winrt::com_ptr<IInspectable> inspectable;
        Check(CreateDirect3D11DeviceFromDXGIDevice(dxgi.Get(), inspectable.put()));
        auto captureDevice = inspectable.as<DirectXApi::Direct3D11::IDirect3DDevice>();
        pool = Capture::Direct3D11CaptureFramePool::CreateFreeThreaded(captureDevice,
            DirectXApi::DirectXPixelFormat::B8G8R8A8UIntNormalized, DesktopCaptureBuffers, size);
        session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled(false);
        if (supportsCaptureInterval) session.MinUpdateInterval(captureInterval);
        // Keep the Windows-owned capture border and wait for a fresh frame.
        session.StartCapture();
        lastCapture = {};
        waitingSince = Clock::now();
    }

    void CloseDesktopCapture() noexcept
    {
        // A closing capture cannot invalidate the independent shared input.
        try { if (subscribed) item.Closed(closedToken); } catch (...) {}
        subscribed = false;
        sourceClosed.reset();
        try { if (session) session.Close(); } catch (...) {}
        session = nullptr;
        try { if (pool) pool.Close(); } catch (...) {}
        pool = nullptr;
        item = nullptr;
        desktopView.Reset();
        desktop.Reset();
        validFrame = false;
    }

    bool PollDesktopCapture(Clock::time_point now)
    {
        if (now < lastCapture + captureInterval) return false;
        Capture::Direct3D11CaptureFrame frame{ nullptr };
        // Keep the newest frame available without increasing the number of GPU copies.
        for (int i = 0; i < DesktopCaptureBuffers; ++i)
        {
            auto next = pool.TryGetNextFrame();
            if (!next) break;
            if (frame) frame.Close();
            frame = std::move(next);
        }
        if (!frame) return false;
        const auto size = frame.ContentSize();
        if (size.Width != int(width) || size.Height != int(height))
            FailTransient(L"Display size changed. The effect stopped.");
        auto access = frame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
        ComPtr<ID3D11Texture2D> source;
        Check(access->GetInterface(IID_PPV_ARGS(&source)));
        D3D11_TEXTURE2D_DESC description{};
        source->GetDesc(&description);
        if (description.Width != width || description.Height != height || description.Format != DXGI_FORMAT_B8G8R8A8_UNORM)
            Fail(L"Unexpected capture format. The effect stopped.");
        context->CopyResource(desktop.Get(), source.Get());
        frame.Close();
        validFrame = true;
        lastCapture = now;
        return true;
    }

    void CheckBudget()
    {
        ComPtr<IDXGIAdapter3> budgetAdapter;
        Check(adapter.As(&budgetAdapter));
        DXGI_QUERY_VIDEO_MEMORY_INFO info{};
        Check(budgetAdapter->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &info));
        const uint64_t reserve = uint64_t(width) * height * 4 * 7 + 128 * MiB;
        if (info.CurrentUsage >= info.Budget || info.Budget - info.CurrentUsage < reserve)
            Fail(L"Stopped: insufficient free GPU memory budget. Free resources and enable manually.");
    }

    void HistoryEnabled(bool enabled)
    {
        if (!enabled) {
            context->OMSetRenderTargets(0, nullptr, nullptr);
            ID3D11ShaderResourceView* empty[2]{};
            context->PSSetShaderResources(0, 2, empty);
            for (auto& view : historyViews) view.Reset();
            for (auto& historyTarget : historyTargets) historyTarget.Reset();
            for (auto& texture : history) texture.Reset();
            historyValid = wasTail = false;
            return;
        }
        if (history[0]) return;
        CheckBudget();
        auto desc = FrameDescription(D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET);
        constexpr float clear[4]{};
        for (unsigned i = 0; i < 2; ++i) {
            Check(device->CreateTexture2D(&desc, nullptr, &history[i]));
            Check(device->CreateShaderResourceView(history[i].Get(), nullptr, &historyViews[i]));
            Check(device->CreateRenderTargetView(history[i].Get(), nullptr, &historyTargets[i]));
            context->ClearRenderTargetView(historyTargets[i].Get(), clear);
        }
        historyRead = 0;
        historyValid = false;
    }

    bool Present(Clock::time_point now, bool usingRipple)
    {
        HRESULT presented = swap->Present(0, DXGI_PRESENT_DO_NOT_WAIT);
        if (presented == DXGI_ERROR_WAS_STILL_DRAWING)
        {
            if (!presentPending) busySince = now;
            presentPending = true;
            if (now - busySince >= std::chrono::seconds(1))
                Fail(L"The GPU presentation queue stayed busy. Enable manually to retry.");
            return false;
        }
        if (presented == DXGI_STATUS_OCCLUDED)
        {
            visibilityRetry.Occluded(now);
            rippleInput.ClearPresentation();
            waitingForInput = presentPending = true;
            busySince = now;
            return false;
        }
        Check(presented);
        visibilityRetry.Presented();
        presentPending = false;
        waitingForInput = false;
        presentedRippleFrame = usingRipple && presented == S_OK;
        if (presented == S_OK) pulseEndpoint.Presented(drewPulse, vignetteWindow);
        return true;
    }

    ~State()
    {
        if (overlay) ShowWindow(overlay, SW_HIDE);
        CloseDesktopCapture();
        if (context) { context->ClearState(); context->Flush(); }
    }
};

Renderer::Renderer() = default;
Renderer::~Renderer() = default;
void Renderer::Stop() noexcept { state.reset(); }
bool Renderer::WaitingForInput() const noexcept { return state && state->waitingForInput; }
void Renderer::ConfirmVisiblePresentation() noexcept
{
    if (state && state->presentedRippleFrame) state->rippleInput.ConfirmVisiblePresentation();
}

void Renderer::ClearPresentation() noexcept
{
    if (state) state->rippleInput.ClearPresentation();
}

void Renderer::Start(HWND overlay, HMONITOR monitor, const std::wstring& shaderPath, int captureFps, bool useRippleInput)
{
    Stop();
    auto next = std::make_unique<State>();
    next->overlay = overlay;
    next->monitor = monitor;
    next->degaussOverlay = !useRippleInput;
    next->degaussShader = (std::filesystem::path(shaderPath).parent_path() / L"Degauss.hlsl").wstring();
    MONITORINFO info{sizeof(info)};
    if (!GetMonitorInfoW(monitor, &info)) winrt::throw_last_error();
    next->monitorBounds = info.rcMonitor;
    next->pulseEndpoint.Bind(overlay, monitor, info.rcMonitor,
        useRippleInput ? effects::Stage::RetroTerminal : effects::Stage::Degauss);
    next->width = info.rcMonitor.right - info.rcMonitor.left;
    next->height = info.rcMonitor.bottom - info.rcMonitor.top;
    if (next->width < 16 || next->height < 16 || uint64_t(next->width) * next->height > MaxPixels)
        Fail(L"This prototype supports a single monitor up to 3840 x 2160 pixels (8.3 MP).");
    next->adapter = FindAdapter(monitor, next->adapterName);
    next->CheckBudget();
    D3D_FEATURE_LEVEL level{};
    constexpr D3D_FEATURE_LEVEL levels[]{D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0};
    Check(D3D11CreateDevice(next->adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
        D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, ARRAYSIZE(levels), D3D11_SDK_VERSION,
        &next->device, &level, &next->context));
    ComPtr<IDXGIDevice> dxgi;
    Check(next->device.As(&dxgi));
    ComPtr<IDXGIFactory2> factory;
    Check(next->adapter->GetParent(IID_PPV_ARGS(&factory)));
    DXGI_SWAP_CHAIN_DESC1 swapDescription{};
    swapDescription.Width = next->width;
    swapDescription.Height = next->height;
    swapDescription.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    swapDescription.SampleDesc.Count = 1;
    swapDescription.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    swapDescription.BufferCount = 2;
    swapDescription.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
    swapDescription.Scaling = DXGI_SCALING_STRETCH;
    swapDescription.AlphaMode = DXGI_ALPHA_MODE_PREMULTIPLIED;
    Check(factory->CreateSwapChainForComposition(next->device.Get(), &swapDescription, nullptr, &next->swap));
    ComPtr<IDXGIDevice1> latencyDevice;
    Check(dxgi.As(&latencyDevice));
    Check(latencyDevice->SetMaximumFrameLatency(1));
    ComPtr<ID3D11Texture2D> backBuffer;
    Check(next->swap->GetBuffer(0, IID_PPV_ARGS(&backBuffer)));
    Check(next->device->CreateRenderTargetView(backBuffer.Get(), nullptr, &next->target));
    Check(DCompositionCreateDevice(dxgi.Get(), IID_PPV_ARGS(&next->composition)));
    Check(next->composition->CreateTargetForHwnd(overlay, TRUE, &next->compositionTarget));
    Check(next->composition->CreateVisual(&next->visual));
    Check(next->visual->SetContent(next->swap.Get()));
    Check(next->compositionTarget->SetRoot(next->visual.Get()));
    Check(next->composition->Commit());
    auto vs = Compile(shaderPath, "VSMain", "vs_5_0");
    auto ps = Compile(shaderPath, "PSMain", "ps_5_0");
    Check(next->device->CreateVertexShader(vs->GetBufferPointer(), vs->GetBufferSize(), nullptr, &next->vertex));
    Check(next->device->CreatePixelShader(ps->GetBufferPointer(), ps->GetBufferSize(), nullptr, &next->pixel));
    D3D11_BUFFER_DESC buffer{};
    buffer.ByteWidth = sizeof(Constants);
    buffer.Usage = D3D11_USAGE_DEFAULT;
    buffer.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    Check(next->device->CreateBuffer(&buffer, nullptr, &next->constants));
    D3D11_SAMPLER_DESC sampler{};
    sampler.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
    sampler.AddressU = sampler.AddressV = sampler.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    sampler.MaxLOD = D3D11_FLOAT32_MAX;
    Check(next->device->CreateSamplerState(&sampler, &next->sampler));
    D3D11_RASTERIZER_DESC raster{};
    raster.FillMode = D3D11_FILL_SOLID;
    raster.CullMode = D3D11_CULL_NONE;
    raster.DepthClipEnable = TRUE;
    Check(next->device->CreateRasterizerState(&raster, &next->rasterizer));
    const auto vignette = vibeware::desktop_effect::FindVignetteOverlay(monitor, info.rcMonitor);
    if (vignette && !vibeware::desktop_effect::IsWindowAbove(vignette, overlay))
        SetWindowPos(overlay, vignette, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);

    next->SetCaptureLimit(captureFps);
    if (useRippleInput) next->rippleInput.Start(overlay, monitor, info.rcMonitor);
    // Preserve immediate standalone capture. A matching Ripple already on this
    // display gets a bounded opportunity to supply input before capture starts.
    if (useRippleInput && next->rippleInput.HasRunningProducer())
        next->waitForRippleUntil = Clock::now() + RippleStartupGrace;
    else if (useRippleInput)
        next->OpenDesktopCapture();
    state = std::move(next);
}

bool Renderer::Tick(const EffectSettings& settings, const GlitchFrame& glitch, bool settingsChanged, bool animationChanged,
    int effectTarget, int captureFps, const std::array<float, 4>& effectParameters)
{
    if (!state) return false;
    auto& s = *state;
    s.presentedRippleFrame = false;
    if (s.sourceClosed && s.sourceClosed->load())
        FailTransient(L"The captured monitor became unavailable.");
    Check(s.device->GetDeviceRemovedReason());
    auto now = Clock::now();
    if (s.visibilityRetry.Expired(now))
        FailTransient(L"The display remained unavailable for three seconds.");
    if (s.visibilityRetry.Waiting(now)) return false;
    if (now - s.lastBudgetCheck > std::chrono::seconds(2))
    {
        s.CheckBudget();
        s.lastBudgetCheck = now;
    }
    const auto ripple = s.rippleInput.Poll(s.device.Get(), s.context.Get(), s.adapter.Get(), now);
    const bool usingRipple = ripple.view != nullptr;
    const bool sourceChanged = s.usedRippleFrame != usingRipple;
    if (sourceChanged || ripple.newSource)
    {
        s.historyValid = s.wasTail = s.presentPending = false;
        s.tailUntil = now;
    }
    if (sourceChanged)
    {
        s.usedRippleFrame = usingRipple;
        if (usingRipple)
        {
            s.waitForRippleUntil = {};
            s.CloseDesktopCapture();
        }
        else s.waitingForInput = true;
    }
    s.SetCaptureLimit(captureFps);
    const bool localPulse = s.degaussOverlay && effectParameters[2] >= 0.f;
    const bool needsDesktop = !s.degaussOverlay || localPulse;
    if (!needsDesktop && s.session) s.CloseDesktopCapture();
    if (needsDesktop && !usingRipple && !s.session)
    {
        if (now < s.waitForRippleUntil) return false;
        s.OpenDesktopCapture();
    }
    const bool inputUpdated = usingRipple ? ripple.updated : needsDesktop && s.PollDesktopCapture(now);
    const bool newFrame = sourceChanged || inputUpdated;
    if (needsDesktop && !s.validFrame && !usingRipple)
    {
        if (now - s.waitingSince > std::chrono::seconds(3)) FailTransient(L"No screen frame arrived. The effect stopped.");
        return false;
    }
    const bool persistence = settings.persistence && settings.persistenceStrength > 0 && settings.intensity > 0;
    if (persistence != bool(s.history[0])) s.HistoryEnabled(persistence);
    if (settingsChanged) s.historyValid = false;
    if (newFrame || animationChanged || settingsChanged)
        s.tailUntil = now + std::chrono::milliseconds(int(settings.persistenceMs * 8));
    const bool tail = persistence && now < s.tailUntil;
    const bool finishTail = s.wasTail && !tail;
    s.wasTail = tail;
    if (finishTail) s.historyValid = false;
    effects::PulseValues remotePulse{};
    const bool requested = !s.degaussOverlay && s.pulseEndpoint.Read(now, remotePulse);
    const auto vignette = s.pulseEndpoint.Vignette(now, requested || localPulse || s.drewPulse);
    if (!requested) s.declinedPulse = false;
    bool downstream = requested && !s.declinedPulse && (!vignette.window || vignette.color[3] > 0.f);
    if (downstream) {
        try {
            s.degaussPass.Prepare(s.device.Get(), s.adapter.Get(), s.width, s.height, s.degaussShader.c_str());
        } catch (const winrt::hresult_error&) {
            // A failed optional pass leaves Retro Terminal visible and unacknowledged.
            downstream = false;
            s.declinedPulse = true;
        }
    }
    if (!downstream) s.degaussPass.ReleaseFrame();
    const bool drawPulse = downstream || localPulse;
    const bool redraw = newFrame || settingsChanged || animationChanged || tail || finishTail ||
        drawPulse != s.drewPulse || (downstream && remotePulse != s.drawnPulse);
    // A busy swap chain still owns the drawn back buffer. Retry presenting
    // it without updating constants or drawing again if the contents are current.
    if (!redraw) return s.presentPending && s.Present(now, usingRipple);
    if (effectTarget == 0) s.coverage.Reset();
    else Check(s.coverage.Update(s.device.Get(), s.context.Get(), s.overlay, s.monitorBounds, now));
    const float dtMs = std::chrono::duration<float, std::milli>(now - s.lastDraw).count();
    const float decay = s.historyValid ? std::exp2(-std::max(0.f, dtMs) / settings.persistenceMs) : 0.f;
    Constants values{
        {float(s.width), float(s.height), settings.intensity, float(settings.palette)},
        {settings.color[0], settings.color[1], settings.color[2], float(settings.mask)},
        {settings.scanStrength, settings.scanSpacing, settings.scanThickness, settings.maskStrength},
        {settings.glowRadius, settings.glow, settings.blackLevel, settings.contrast},
        {settings.vignette, settings.grain, decay, persistence ? settings.persistenceStrength : 0.f},
        {float(glitch.type), glitch.amount, glitch.position, glitch.phase},
        {float(effectTarget), 0.f, 0.f, 0.f},
        {effectParameters[0], effectParameters[1], effectParameters[2], effectParameters[3]},
        vignette.shape, vignette.color
    };
    s.context->UpdateSubresource(s.constants.Get(), 0, nullptr, &values, 0, 0);
    const unsigned historyWrite = 1 - s.historyRead;
    ID3D11RenderTargetView* targets[]{downstream ? s.degaussPass.Target() : s.target.Get(),
        persistence ? s.historyTargets[historyWrite].Get() : nullptr};
    s.context->OMSetRenderTargets(persistence ? 2 : 1, targets, nullptr);
    D3D11_VIEWPORT viewport{0, 0, float(s.width), float(s.height), 0, 1};
    s.context->RSSetViewports(1, &viewport);
    s.context->RSSetState(s.rasterizer.Get());
    s.context->IASetInputLayout(nullptr);
    s.context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    s.context->VSSetShader(s.vertex.Get(), nullptr, 0);
    s.context->PSSetShader(s.pixel.Get(), nullptr, 0);
    ID3D11Buffer* constants = s.constants.Get();
    s.context->PSSetConstantBuffers(0, 1, &constants);
    ID3D11SamplerState* sampler = s.sampler.Get();
    s.context->PSSetSamplers(0, 1, &sampler);
    ID3D11ShaderResourceView* views[]{usingRipple ? ripple.view : s.desktopView.Get(),
        persistence ? s.historyViews[s.historyRead].Get() : nullptr, s.coverage.View()};
    s.context->PSSetShaderResources(0, ARRAYSIZE(views), views);
    s.context->Draw(3, 0);
    ID3D11ShaderResourceView* empty[ARRAYSIZE(views)]{};
    s.context->PSSetShaderResources(0, ARRAYSIZE(empty), empty);
    s.context->OMSetRenderTargets(0, nullptr, nullptr);
    if (downstream) s.degaussPass.Draw(s.context.Get(), s.target.Get(), nullptr, remotePulse, vignette);
    s.drewPulse = drawPulse;
    s.drawnPulse = remotePulse;
    s.vignetteWindow = vignette.window;
    if (persistence) { s.historyRead = historyWrite; s.historyValid = true; }
    s.lastDraw = now;
    return s.Present(now, usingRipple);
}

std::wstring Renderer::Description() const
{
    if (!state) return L"Stopped";
    return state->adapterName + L" · " + std::to_wstring(state->width) + L" × " + std::to_wstring(state->height)
        + (state->usedRippleFrame ? L" · Water Ripple shared input" : L" · Windows Graphics Capture");
}
