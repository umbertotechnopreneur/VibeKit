// SPDX-License-Identifier: MIT
#pragma once
#include <windows.h>
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
    void Start(HWND overlay, HMONITOR monitor, const std::wstring& shaderPath);
    // Returns true only after a new frame has been successfully presented.
    bool Tick(const EffectSettings& settings, const GlitchFrame& glitch, bool settingsChanged, bool animationChanged);
    void Stop() noexcept;
    std::wstring Description() const;
private:
    struct State;
    std::unique_ptr<State> state;
};
