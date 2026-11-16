# Retro Terminal for Windows (prototype)

Native Win32 controls and a Direct3D 11 / DirectComposition desktop CRT effect,
separate from Water Ripple and the Ghostty collection. It transforms the existing
desktop; it is not a terminal emulator and does not execute commands.

## Build

Requires Windows 11, MSVC, CMake 3.25+, and Windows SDK **10.0.26100.0 or newer**
with C++/WinRT headers. No external dependencies are downloaded. From a Visual
Studio developer terminal in this directory:

```powershell
cmake -S . -B build -A x64 -DCMAKE_SYSTEM_VERSION=10.0.26100.0
cmake --build build --config Release
.\build\Release\RetroTerminal.exe
```

Keep `Shaders/Retro.hlsl` beside the executable. Windows compiles that shader when
the effect starts. The MSVC runtime is linked statically. There is no installer,
autostart entry or elevation requirement. Preferences and three personal preset
slots live in `HKCU\Software\VibeWare\RetroTerminal`; enabled state is never saved.

## Controls

Place the panel on the target monitor and choose **Enable effect**. **Disable
effect**, **Stop**, **Ctrl+Alt+F11**, closing the panel, or an unsafe display/session
change removes the overlay and releases capture and history resources. Startup is
always stopped. An unavailable emergency shortcut prevents startup. Minimizing
the panel leaves an enabled effect running. The panel scrolls on shorter screens.

Appearance controls include:

- Green, amber, original color or a custom phosphor color selected with the native
  Windows color dialog.
- Effect mix; scanline strength, spacing and width; glow strength and radius.
- Darkened edges, static grain, black level and contrast.
- Phosphor mask off, RGB stripes or dots, with adjustable strength.
- Optional real temporal phosphor afterglow, half-life 40–500 ms and mix 0–0.6.

**Clean terminal**, **Worn terminal** and **Arcade** are built-in starting points.
Select **My preset 1**, **2** or **3**, then choose **Save to selected personal
preset** to save current settings. Saving replaces that selected personal slot.
Preset changes apply immediately and cancel an active glitch.

Glitches are four independent allowed types: RGB misalignment, a narrow
horizontal tear, local line noise, and a soft moving scan band. The random master
switch starts off. Min/max interval defaults to 20–60 seconds (range 5–300 s),
min/max duration defaults to 100–250 ms (range 80–1200 ms), and intensity defaults
to 0.25. Endpoints are clamped into a valid ordered range. **Try now** uses one of
the allowed types while CRT is active even when automatic glitches are off.
Episodes cannot overlap; manual and automatic episodes obey at least two seconds
of rest. Changing settings or stopping cancels the current episode. There are no
full-screen flashes or automatic restarts.

The steady CRT preserves input coordinates. The tear briefly moves displayed
pixels in a narrow band, so it is optional. The hardware pointer is not captured
or filtered. The Windows-owned capture border stays enabled. Capture exclusion
prevents the overlay from feeding itself back into capture; failure to establish
exclusion prevents startup.

## Rendering and limits

The normal shader uses six desktop texture samples (the original unmodified
pixel, current signal and four glow neighbors). RGB glitches add two samples.
Phosphor persistence adds one previous-frame sample and a second render target.
The driver may optimize duplicate samples, but no cost reduction is assumed.

Temporal persistence uses two lazily allocated full-size GPU textures, read and
written alternately. One render target stores `max(current, previous * decay)`;
the other mixes that memory with the current desktop. Keeping these outputs
separate preserves the configured half-life independently of mix strength.
History resets after parameter changes and is freed when persistence or CRT is
disabled. With no new capture or glitch, decay draws stop after eight half-lives
and one final clean frame. With mix zero, the output is an exact desktop passthrough.

- One hardware monitor, at most 8,294,400 pixels; rotated outputs rejected. HDR
  color fidelity is not validated by this SDR prototype.
- Two capture buffers, two presentation buffers and one retained desktop texture,
  plus two optional history textures. No CPU pixel readback or screenshot logging.
- A 34 ms timer limits render submissions to under 30 Hz. Drawing requires a new
  capture frame, changed setting, glitch update, pending presentation or active
  persistence decay. Random selection uses only the existing timer; there is no
  second busy-polling loop. The capture session remains alive while enabled.
- Where available, Windows capture receives a 34 ms minimum update interval.
- Hardware GPU memory budget is checked before allocations and every two seconds,
  reserving seven frame surfaces plus 128 MiB. There is no software fallback.
- Stop after a UI timer delay over 250 ms, three submissions above 80 ms, 30 busy
  presentation attempts, no initial frame for three seconds, device/capture error,
  display/scale changes, screen lock/disconnection or suspend. Resume is manual.

The panel reports presentations and **CPU submission time**, not GPU frame time
or compositor FPS. No Windows performance measurements are claimed.

## Validation

The portable settings and scheduler checks can run on Linux or Windows:

```sh
c++ -std=c++20 -Wall -Wextra -pedantic tests/settings-checks.cpp -o /tmp/retro-settings-checks
/tmp/retro-settings-checks
```

They exercise bounds and malformed numbers, allowed glitch types, interval and
duration ordering, no overlapping events, mandatory rest, cancellation and
presets. On Windows an MSVC developer terminal can use
`cl /std:c++20 /EHsc tests\settings-checks.cpp /Fe:settings-checks.exe`.

**The native application and HLSL have not been built with MSVC or executed on
Windows.** Required Windows checks include compilation, all controls at different
DPI settings, personal preset round trips, capture exclusion/click-through,
emergency stop during native color dialogs, each glitch, zero mix, two-buffer
history with a moving test shape, decay after motion stops, repeated enable/stop
cycles, display changes, lock and device/capture failure. Profile idle, active
motion, glitches and persistence separately on that host.

## API references

- [Monitor capture interop](https://learn.microsoft.com/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createformonitor)
- [Capture exclusion](https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity)
- [Free-threaded capture frame pool](https://learn.microsoft.com/uwp/api/windows.graphics.capture.direct3d11captureframepool.createfreethreaded?view=winrt-26100)
- [Capture minimum update interval](https://learn.microsoft.com/uwp/api/windows.graphics.capture.graphicscapturesession.minupdateinterval?view=winrt-26100)
- [Multiple render targets](https://learn.microsoft.com/windows/win32/direct3d11/d3d10-graphics-programming-guide-output-merger-stage)

Original MIT-licensed code; no third-party shader collection is copied.
