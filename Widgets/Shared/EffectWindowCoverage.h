#pragma once
#include "DesktopEffectInterop.h"
#include <array>
#include <dwmapi.h>

#pragma comment(lib, "dwmapi.lib")

namespace vibeware::desktop_effect
{
    enum class EffectTarget : int32_t { Desktop, Windows, Wallpaper };

    class WindowCoverage final
    {
    public:
        ID3D11ShaderResourceView* View() const { return view.Get(); }
        void Reset() { target.Reset(); view.Reset(); }

        HRESULT Update(ID3D11Device* device, ID3D11DeviceContext* context, HWND overlay, const RECT& bounds, Clock::time_point now)
        {
            const LONG width = bounds.right - bounds.left;
            const LONG height = bounds.bottom - bounds.top;
            if (!target || width != previous.right - previous.left || height != previous.bottom - previous.top)
            {
                Reset();
                D3D11_TEXTURE2D_DESC description{};
                description.Width = width;
                description.Height = height;
                description.MipLevels = description.ArraySize = 1;
                description.Format = DXGI_FORMAT_R8_UNORM;
                description.SampleDesc.Count = 1;
                description.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
                ComPtr<ID3D11Texture2D> texture;
                HRESULT result = device->CreateTexture2D(&description, nullptr, &texture);
                if (FAILED(result)) return result;
                result = device->CreateShaderResourceView(texture.Get(), nullptr, &view);
                if (FAILED(result)) return result;
                result = device->CreateRenderTargetView(texture.Get(), nullptr, &target);
                if (FAILED(result)) return result;
                due = {};
            }
            if (now < due && EqualRect(&bounds, &previous)) return S_OK;

            ComPtr<ID3D11DeviceContext1> context1;
            HRESULT result = context->QueryInterface(IID_PPV_ARGS(&context1));
            if (FAILED(result)) return result;
            const float wallpaper[4]{};
            context->ClearRenderTargetView(target.Get(), wallpaper);
            Regions regions{bounds, overlay, GetShellWindow(), context1.Get(), target.Get()};
            if (!EnumWindows(Regions::Add, reinterpret_cast<LPARAM>(&regions))) return E_FAIL;
            regions.Flush();
            previous = bounds;
            due = now + std::chrono::milliseconds(33);
            return S_OK;
        }

    private:
        struct Regions
        {
            RECT bounds;
            HWND overlay, shell;
            ID3D11DeviceContext1* context;
            ID3D11RenderTargetView* target;
            std::array<D3D11_RECT, 64> rectangles{};
            UINT count{};

            void Flush()
            {
                if (!count) return;
                const float windowColor[4]{1, 1, 1, 1};
                context->ClearView(target, windowColor, rectangles.data(), count);
                count = 0;
            }

            static BOOL CALLBACK Add(HWND window, LPARAM parameter)
            {
                auto& regions = *reinterpret_cast<Regions*>(parameter);
                if (window == regions.overlay || window == regions.shell || !IsWindowVisible(window) || IsIconic(window))
                    return TRUE;
                wchar_t name[64]{};
                GetClassNameW(window, name, ARRAYSIZE(name));
                if (wcscmp(name, L"Progman") == 0 || wcscmp(name, L"WorkerW") == 0 ||
                    GetPropW(window, CrtReady) || GetPropW(window, RippleTarget)) return TRUE;
                DWORD cloaked{}, affinity{};
                if ((SUCCEEDED(DwmGetWindowAttribute(window, DWMWA_CLOAKED, &cloaked, sizeof(cloaked))) && cloaked) ||
                    (GetWindowDisplayAffinity(window, &affinity) && affinity == WDA_EXCLUDEFROMCAPTURE)) return TRUE;
                RECT frame{}, clipped{};
                if (FAILED(DwmGetWindowAttribute(window, DWMWA_EXTENDED_FRAME_BOUNDS, &frame, sizeof(frame))) &&
                    !GetWindowRect(window, &frame)) return TRUE;
                if (!IntersectRect(&clipped, &frame, &regions.bounds)) return TRUE;
                OffsetRect(&clipped, -regions.bounds.left, -regions.bounds.top);
                regions.rectangles[regions.count++] = clipped;
                if (regions.count == regions.rectangles.size()) regions.Flush();
                return TRUE;
            }
        };

        ComPtr<ID3D11RenderTargetView> target;
        ComPtr<ID3D11ShaderResourceView> view;
        RECT previous{};
        Clock::time_point due{};
    };
}
