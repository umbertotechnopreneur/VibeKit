// SPDX-License-Identifier: MIT
#pragma once
#include <windows.h>
#include <array>
#include <memory>
#include <string>
#include "EffectSettings.h"

class Renderer final
{
public:
    Renderer();
    ~Renderer();
    Renderer(const Renderer&) = delete;
    Renderer& operator=(const Renderer&) = delete;
    // Throws on unavailable capture, unsupported geometry, software adapters or GPU errors.
    void Start(HWND overlay, HMONITOR monitor, const std::wstring& shaderPath, int captureFps = 30, bool useRippleInput = true);
    // Returns true only after a new frame has been successfully presented.
    bool Tick(const EffectSettings& settings, const GlitchFrame& glitch, bool settingsChanged, bool animationChanged,
        int effectTarget = 0, int captureFps = 30, const std::array<float, 4>& effectParameters = {});
    bool WaitingForInput() const noexcept;
    void ConfirmVisiblePresentation() noexcept;
    void ClearPresentation() noexcept;
    void Stop() noexcept;
    std::wstring Description() const;
private:
    struct State;
    std::unique_ptr<State> state;
};
