# Retro Terminal

A separate desktop CRT utility: green phosphor, amber terminal, and arcade color.
Custom phosphor color, scanlines, glow, masks, temporal afterglow and optional
signal glitches transform the live desktop. This is an effect, not a terminal
emulator. Input coordinates remain unchanged; a horizontal-tear glitch briefly
displaces a narrow image band, then restores it. It does not change Ripple or
Ghostty settings.

## GNOME 50 / Wayland

The extension applies one offscreen GPU effect to GNOME's desktop UI group,
including the top bar and windows. The hardware pointer remains unchanged.
It starts **off**. The terminal icon in the top bar offers the power switch,
three styles, **Try for 15 seconds**, Preferences and Stop.
**Super+Shift+F12** stops the effect, including while a menu is open.

The Debian package adds **Retro Terminal** to Ubuntu's applications grid. Search
for "Retro Terminal" or "CRT" to open its native preferences. The same panel is
available from the top-bar menu or GNOME Extensions. Enable the extension in
GNOME Extensions before using the CRT switch; opening preferences does not enable
the extension automatically. The standalone extension ZIP does not install an
applications-grid launcher.

The **About** tab opens the VibeWare-branded information window with the installed
version, author, MIT license, source, website, manifesto, terms and privacy links.
**GPU excuses** rotates playful captions every 12 seconds while the CRT
preferences page is visible. Turn off **Rotate GPU jokes** to hide them. This
preference is independent of saved looks. Captions are jokes, not telemetry or
hardware-price estimates; they never poll sensors or increase the effect workload.
Hidden panels have no caption timer.

Preferences expose:

- Green, amber or source-color palettes, plus a native custom-color picker.
- Effect mix, black level and contrast.
- Scanline strength, spacing and thickness; glow strength and radius.
- No mask, RGB stripes or phosphor dots, with adjustable mask strength.
- Vignette and static grain.
- Optional temporal phosphor persistence, with independent decay and mix.
- Four individually allowed glitches: local RGB separation, horizontal tear,
  line interference and a soft scanning band.

**Looks** contains Clean green, Worn terminal, Arcade and Long phosphor presets.
Load a look, adjust its controls, and use **Save new look** with a unique name.
Up to 32 personal looks are stored in GSettings. Saved looks omit power, runtime
status, shortcuts and preview requests. Duplicate names and malformed libraries
are rejected without overwriting existing data. Deleting applies only to saved
looks. Ordinary control values persist immediately.

**Random glitches** starts off. Configure minimum/maximum intervals (5–300 s),
duration (80–1200 ms) and intensity. Reversed endpoints are normalized by the
scheduler. Only one glitch runs at a time, followed by at least two seconds of
rest. **Try a glitch now** works with random mode off, respects the allowed types
and rest period, and requires CRT to be active. Disabling random mode cancels a
current episode and its pending wakeup. Master effect mix zero suppresses events.
All glitches have a soft envelope, and interference is localized; there are no
full-screen flashes. Random scheduling uses a single future wakeup; a 34 ms
animation timer runs only during an episode.

With persistence and glitches disabled, the filter adds no animation or capture
loop. Its static shader samples the scene once, with four extra samples when glow
is on; RGB separation adds two and a tear adds one during its episode. Compositor
redraws still cost more while the filter is enabled; it follows the desktop's
redraw cadence, not a separate FPS limit. GNOME owns the main offscreen allocation.

**Temporal afterglow** is actual previous-frame content, not spatial blur. Two
fixed GPU textures alternate, retaining exponentially decaying scene history.
Decay is an e-folding time on GNOME; Windows uses a half-life, so the same numeric
value does not imply identical trails. Visible mix is separate from history decay.
GNOME captures/composes at most 24 times per second while afterglow is enabled,
including on an otherwise idle desktop. No CPU readback or image file is involved.
The history overlay does not receive input. Stop or disabling persistence removes
it and releases the history references. A capture error or gap over 250 ms stops
CRT without automatic recovery. This mode uses additional GPU time and memory;
it is off by default. The two RGBA8 texture payloads alone require
`width × height × 4 × 2` bytes, excluding compositor resources and driver overhead.

Before enabling, the desktop layout must fit within 9 million physical pixels
and 8192 pixels per dimension. This is a layout preflight check, not a GPU memory
budget measurement or a hard bound on GNOME's internal allocations.
The scale factor is conservatively included. Some multi-monitor/HiDPI layouts
will therefore be refused. Vignette coordinates cover the combined desktop.
The effect stops on lock, monitor/scale changes, a delay over 1.5 seconds, or
three accumulated desktop event-loop delays over 0.5 seconds. These observations
are responsiveness checks, not GPU timings. It never resumes automatically.

Build the archive and Debian package (no installation or session changes):

```sh
python3 Linux/build-package.py --output-dir /tmp/retro-terminal
```

Install using the normal package manager, then enable
`retro-terminal@vibeware.local` in GNOME Extensions. For a user installation,
use `gnome-extensions install` with the generated archive instead. New extension
discovery can require a new login; never restart or close the user's session
automatically. The top-bar switch remains off until explicitly enabled.

Stop from a terminal if needed:

```sh
gsettings set org.gnome.shell.extensions.vibeware-retro-terminal active false
gnome-extensions disable retro-terminal@vibeware.local
```

For user-only installs, supply `--schemadir` pointing to the extension's schemas
directory when using `gsettings`.

## Windows

See [Windows/README.md](Windows/README.md) for the native Win32 / Direct3D 11
implementation, build requirements, capture behavior and emergency shortcut.
The two platforms share the visual direction, not their compositor architecture.

## Verification

```sh
node tests/guard-checks.mjs
node tests/customization-checks.mjs
python3 scripts/benchmark-shader.py --output-dir /tmp/retro-terminal-benchmark
python3 scripts/smoke-gnome.py --mode ubuntu --output-dir /tmp/retro-terminal-smoke
```

The GTK checks run against an extracted extension archive with
`GSETTINGS_BACKEND=memory` and an empty temporary `XDG_CONFIG_HOME`:

```sh
gjs -m tests/gtk-checks.js /tmp/retro-terminal-extension
```

GPU shader timings from an isolated framebuffer do not include GNOME's offscreen
scene composition, application redraws, capture on Windows or display latency.
Windows runtime validation requires a Windows 11 host and its graphics drivers.

The GPU harness needs a C compiler, GL/GLX development headers, X11/Xwayland,
and Python Pillow. It uses the exact production shader in a real framebuffer,
checks pixel identity at zero intensity, scanline contrast on a flat source,
custom colors and optics, all glitch types, and exact restoration after a glitch. The GNOME smoke test starts a separate headless compositor with private
configuration, runtime and D-Bus directories; it never replaces the live shell.
It captures only a synthetic test scene, exercises custom controls and all four
glitches, verifies real retained history using a moving marker, and checks that
Stop releases the effect, history, pending wakeup and animation timers. Short-lived headless GNOME sessions
can log shell-owned calendar/portal teardown warnings after the test completes.

## Sources and license

Original shader and implementation, MIT licensed. GNOME integration was checked
against the installed GNOME 50 sources and the upstream
[layout hierarchy](https://gitlab.gnome.org/GNOME/gnome-shell/-/blob/50.1/js/ui/layout.js)
and [GLSL effect API](https://gitlab.gnome.org/GNOME/gnome-shell/-/blob/50.1/src/shell-glsl-effect.c).
Only GNOME 50 is declared supported. This is an experimental utility.
