# Water Ripple

A small Windows 11 click-ripple app by Umberto Giacobbi, using C# / WinUI 3 and C++ / Direct3D 11 HLSL.

## Use

**Ripple Studio** previews water ripples over a desktop snapshot. Click the preview or use **Drop in the center**. Choose a preset or adjust refraction, wave size, speed and duration. **Refresh background** captures a new snapshot while briefly hiding Studio; the main window remains available in ordinary screenshots.

**Apply to screen** applies click ripples to the current monitor. Clicks pass through the overlay. Stop with **Stop screen effect** or **Ctrl+Alt+Esc**. The overlay stays hidden until capture and presentation are valid and hides before GPU resources are released. The screen overlay alone is excluded from capture to prevent feedback.

Closing offers tray minimization, exit or cancel. Tray mode keeps an enabled click ripple running; the idle Studio renderer is released while hidden. Acrylic, About, VibeWare branding and resource monitoring are retained.

## Scope and branches

`main` contains only Studio and click-driven water ripples. There are no window-event hooks, transitions, window texture caches, click pulses, border/flash effects or automatic drops. The complete experimental desktop/window effects version is preserved in `codex/window-effects`.

User preferences are bound to `AppSettings`, transient UI state belongs to `AppState`, and settings save immediately by atomic replacement in `%LOCALAPPDATA%\VibeWare\WaterRipple\settings-ripple.json`. This separate file preserves the full version's original `settings.json` when switching branches. Enabled screen mode is never restored automatically on launch.

## Performance

Capture uses Windows.Graphics.Capture with two GPU buffers and no CPU pixel readback. The ripple-only shader uses one desktop texture, a 160-byte constant buffer and at most eight waves. Frozen preview snapshots close their capture session. Capture and animation rates have separate limits. One monitor is supported at a time; rotated displays remain unsupported.

Screen mode retains the hardware Direct3D 11 requirement and at least 8,000,000,000 bytes of dedicated VRAM. Shared memory does not count. The resource monitor samples private RAM, working set and CPU; it warns at 1 GiB private RAM. GPU budget and CPU submission timings are shown separately from presentation rates.

Serilog logs rotate weekly under `%LOCALAPPDATA%\VibeWare\WaterRipple\logs`, with a bounded background queue, four weekly files and an 8 MiB weekly cap. Frames, screenshots and private window titles are not logged.

## Build from source

Requires .NET 10 SDK, Microsoft C++ x64 build tools and Windows SDK 10.0.26100.0 with C++/WinRT headers and `windowsapp.lib`. The native build script uses the installed Visual Studio 18 Insiders toolchain.

Before first use of a new external library or prebuilt binary, explicitly scan its archive and extracted dependencies with Microsoft Defender. Confirm the official source, version and clean scanner completion in that staging session; reuse the result for unchanged imports. Builds do not initiate scans. Retain no scan reports or build histories.

Build with `dotnet build -p:Platform=x64`. Launch the resulting `bin/Debug/net10.0-windows10.0.26100.0/win-x64/WaterRipple.exe`, keeping its DLLs, Assets and Shaders together. Use the apphost EXE rather than `dotnet WaterRipple.dll` for manifest-based WinRT activation.

Debug checks: `--check-settings`, `--check-resource-guard`, `--check-logging` and `--exercise-overlay`. The overlay exercise checks three Apply/Stop cycles and tray release/restore, then exits. **Pause capture for screenshot** pauses capture for 20 seconds before making the overlay capturable, then restores capture exclusion before resuming.

## Branding

The app icon was generated with ImageGen. The approved VibeWare badge is unchanged; its separate artwork license is in `Assets/VibeWare-LICENSE.txt` and does not relicense this repository.

Created by [Umberto Giacobbi](https://umbertogiacobbi.biz). **Human intent, AI execution, and plenty of tokens**

This is VibeWare · [Manifesto](https://umbertogiacobbi.biz/vibeware/manifesto)
