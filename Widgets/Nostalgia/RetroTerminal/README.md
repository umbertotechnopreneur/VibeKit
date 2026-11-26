> - **File name** `README.md`
> - **Full path** `E:\vibekit\Widgets\Nostalgia\RetroTerminal\README.md`
> - **Path relative to the VibeKit repository root** `Widgets/Nostalgia/RetroTerminal/README.md`

# Retro Terminal

[![VibeKit CI](https://github.com/umbertotechnopreneur/VibeKit/actions/workflows/monorepo.yml/badge.svg?branch=main)](https://github.com/umbertotechnopreneur/VibeKit/actions/workflows/monorepo.yml)
![Platform: Windows 11 · GNOME 50](https://img.shields.io/badge/platform-Windows_11_%C2%B7_GNOME_50-0078D4)
[![License: MIT](https://img.shields.io/badge/license-MIT-2EA44F)](LICENSE)

A separate desktop CRT utility: green phosphor, amber terminal, and arcade color.
Custom phosphor color, scanlines, glow, masks, temporal afterglow and optional
signal glitches transform the live desktop. This is an effect, not a terminal
emulator. Input coordinates remain unchanged; a horizontal-tear glitch briefly
displaces a narrow image band, then restores it. It does not change Ripple or
Ghostty settings.

## GNOME 50 / Wayland

The extension applies an offscreen GPU filter to GNOME's desktop UI group on
the selected monitors, including the top bar and windows. The hardware pointer
remains unchanged. The effect starts **off**. The terminal icon in the top bar
offers the power switch, three styles, **Try for 15 seconds**, Preferences and Stop.

In **Settings → Monitors**, switch each named display on or off. The changes apply
immediately while **Effect** is on. All displays are selected by default. Once
you change the list, the panel saves each selection, including the selections
of disconnected displays. Turning every display off leaves **Effect** armed
until a monitor is selected again.

The **Emergency shortcut** footer shows **Super + Shift + F12** and allows you
to enable or disable the binding. When registered, the shortcut stops the effect
on every monitor, including while a menu is open, and cancels pending recovery.
The footer reports an unavailable binding. **Stop** remains available in the panel
and the top-bar menu.

The Debian package adds **Retro Terminal** to Ubuntu's applications grid. Search
for "Retro Terminal" or "CRT" to open its native preferences. The same panel is
available from the top-bar menu or GNOME Extensions. Enable the extension in
GNOME Extensions before using the CRT switch; opening preferences does not enable
the extension automatically. The standalone extension ZIP does not install an
applications-grid launcher.

The **About** tab opens the VibeWare-branded information window with the installed
version, author, MIT license, source, website, manifesto, terms and privacy links.
**GPU excuses** rotates playful captions every 12 seconds while its panel
is visible. Turn off **Rotate GPU jokes** to hide them. This
preference is independent of saved looks. Captions are jokes, not telemetry or
hardware-price estimates; they never poll sensors or increase the effect workload.
Hidden panels have no caption timer.

The Effect page uses collapsible sections for phosphor, monitor surface,
afterglow and signal glitches. Power, status, pipeline selection and Stop remain
available above the sections. Preferences expose:

- Green, amber or source-color palettes, plus a native custom-color picker.
- Effect mix, black level and contrast.
- Scanline strength, spacing and thickness; glow strength and radius.
- No mask, RGB stripes or phosphor dots, with adjustable mask strength.
- Vignette and static grain.
- Optional temporal phosphor persistence, with independent decay and mix.
- Four individually allowed glitches: local RGB separation, horizontal tear,
  line interference and a soft scanning band.

**Pipelines** includes 40 CRT looks with light scanlines and fine static grain.
Try **Commodore PET Green** for green phosphor, **Amiga Workbench Blue** for cool
tints, **TV Composite PAL** for a soft composite signal, or **Trinitron Aperture
Grille** for subtle vertical RGB stripes. The built-ins keep random glitches and
temporal afterglow disabled. Choosing a preset changes the appearance without
turning the effect on.

Select a pipeline from the Effect page or the library. Built-ins are protected;
duplicate one to keep your changes. Personal pipelines update automatically as
you adjust controls. **New pipeline** saves the current appearance; personal
copies can be renamed or deleted, and **Import** / **Export** transfers JSON
libraries. **Custom** detaches the controls from the saved selection.

The library holds up to 64 personal pipelines and is stored in
`$XDG_CONFIG_HOME/VibeWare/RetroTerminal/pipelines.json`.
Opening the library preserves the active appearance. Power, status, monitor selection,
performance limits, shortcuts, preview requests and the jokes preference are local preferences.
Malformed libraries are preserved and
reported; concurrent panels cannot silently overwrite each other's changes.
A panel that loses a save conflict preserves the current shared settings and
blocks further library writes until reopened.
The file format is specific to Retro Terminal and is not a Ripple pipeline.

**Random glitches** starts off. Configure minimum/maximum intervals (5–300 s),
duration (80–1200 ms) and intensity. Reversed endpoints are normalized by the
scheduler. Only one glitch runs at a time, followed by at least two seconds of
rest. **Try a glitch now** works with random mode off, respects the allowed types
and rest period, and requires CRT to be active. Disabling random mode cancels a
current episode and its pending wakeup. Master effect mix zero suppresses events.
All glitches have a soft envelope and local interference. Random scheduling uses
a single future wakeup. The animation timer runs during an episode.

### Compositor and performance

Water Ripple draws local patches alongside their source actors. When both effects
run on a monitor, Retro Terminal filters the GNOME UI group containing those
patches. GNOME composites and presents the resulting monitor view. The base CRT
filter uses the compositor's ordinary redraws. With glitches and afterglow off,
the effect has no independent animation or capture loop.

```mermaid
flowchart LR
    subgraph GNOME[GNOME Shell compositor]
        Actors[Desktop and window actors]
        Ripple[Water Ripple local patches]
        CRT[Retro Terminal UI group filter]
        History[Retro Terminal optional GPU history]
        View[GNOME monitor view and presentation]
        Actors -->|source region| Ripple
        Actors -->|desktop pixels| CRT
        Ripple -->|ripple pixels in the UI group| CRT
        CRT -->|current filtered scene| View
        CRT -.->|bounded scene sample| History
        History -.->|decaying overlay| View
    end
```

**Animation limit** caps CRT-generated updates from 15 to 120 fps, with a
default of 60 fps. Ordinary GNOME redraws still follow the compositor's cadence.
**Capture limit** applies to optional temporal afterglow. Its range is 5 to 60 fps
and its default is 30 fps. The history scheduler also enforces an aggregate
ceiling of about 24 updates per second, cycling through selected monitors.
That sampling rate controls history updates. GNOME continues to own final display
presentation.

**Temporal afterglow** retains previous scene samples in two alternating GPU
textures per history region. Decay is an e-folding time on GNOME and a half-life
on Windows. Mix controls how much of the retained image is visible. Afterglow
is off by default and uses additional GPU time and memory when enabled. It keeps
history on the GPU without CPU readback or image files. Large or mixed-scale
layouts are downsampled to keep each dimension within 8192 pixels and the total
within 9 million pixels per set of history textures. The two RGBA8 sets have a
combined maximum payload of 72 MB before compositor and driver overhead. Scaling
can soften the trail. The input-transparent history overlay is released when you
stop the effect, deselect a monitor, or disable afterglow.

Layout and scale checks protect compositor resources before activation. Some
large or HiDPI layouts can be refused. **Resume automatically** is enabled by
default for a temporary performance pause. **Recovery** starts with a minimum
delay of 60 seconds, adjustable from 30 to 300 seconds, and requires 30 seconds of
continuous healthy operation. Recovery attempts are limited to two per manual
start. Stop and the emergency shortcut cancel recovery. Lock, display changes
that stop the effect, resource failures, and graphics errors require manual
restart. The performance guard measures desktop responsiveness.

Build the archive and Debian package (no installation or session changes):

```sh
python3 Linux/build-package.py
```

Install the Debian package, then enable `retro-terminal@vibeware.local` in
GNOME Extensions.

```sh
sudo apt install ../artifacts/packages/local/linux/all/RetroTerminal/vibeware-retro-terminal_0.4.12_all.deb
```

For a user installation,
use `gnome-extensions install` with the generated archive instead. New extension
discovery can require a new login; never restart or close the user's session
automatically. The top-bar switch remains off until explicitly enabled.

GNOME caches extension modules for the session. After installing a renderer
update, open a new login session manually to load it; reopening preferences
alone updates the panel but does not replace already-loaded rendering code.
The updated panel detects an older loaded version and explains the required
sign-in before allowing the effect to be enabled. Stop remains available.

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

> [!IMPORTANT]
> **A Mac port needs more than a green tint**
>
> GNOME and Windows use different compositor and capture paths. **macOS is unsupported**: I do not have a Mac to implement and validate its own integration.
>
> If you can maintain that port, [join in](https://github.com/umbertotechnopreneur/VibeKit/issues/new?title=%5BRetro%20Terminal%5D%20). GPU cost, input behavior and an emergency stop need checks on real hardware, alongside the phosphor effect.

## Verification

```sh
node tests/guard-checks.mjs
node tests/customization-checks.mjs
node tests/capture-plan-checks.mjs
node tests/pipeline-checks.mjs
python3 scripts/benchmark-shader.py --output-dir /tmp/retro-terminal-benchmark
python3 scripts/smoke-gnome.py --mode ubuntu --output-dir /tmp/retro-terminal-smoke
python3 scripts/smoke-gnome.py --mode ubuntu --presets --output-dir /tmp/retro-terminal-preset-smoke
python3 scripts/smoke-gnome.py --mode ubuntu --oversized-capture --output-dir /tmp/retro-terminal-large-smoke
```

The native library checks also compare every model default and numeric/enum
range against the compiled GSettings schema. GTK regressions retain pipeline row
and action identities across edits, renames and autosaves; callbacks always use
the current pipeline snapshot. Numeric control builders are separate from page
lifecycle and pipeline coordination.

The GTK checks run against an extracted extension archive with
`GSETTINGS_BACKEND=memory` and an empty temporary `XDG_CONFIG_HOME`:

```sh
gjs -m tests/gtk-checks.js /tmp/retro-terminal-extension
gjs -m tests/library-checks.js /tmp/retro-terminal-extension
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

---

### This is VibeWare · [Manifesto](https://umbertogiacobbi.biz/vibeware/manifesto)

I created Retro Terminal with AI assistance. I define its purpose, guide the implementation, and take responsibility for checking functionality, performance, and security. VibeWare is my initiative for building useful software openly with AI; I ask you to judge the result and the evidence behind it.

To learn what VibeWare means and why I created it, read the [VibeWare manifesto](https://umbertogiacobbi.biz/vibeware/manifesto).

[Retro Terminal on GitHub](https://github.com/umbertotechnopreneur/VibeKit/tree/main/Widgets/Nostalgia/RetroTerminal) · [VibeWare on GitHub](https://github.com/umbertotechnopreneur/VibeWare) · [umbertogiacobbi.biz](https://umbertogiacobbi.biz)

*You are welcome to explore, adapt and reuse the open-source VibeWare materials under the project license — my contribution to the developer community.*

[Terms](https://umbertogiacobbi.biz/terms/) · [Privacy](https://umbertogiacobbi.biz/privacy/)

<p align="right">
  <a href="https://umbertogiacobbi.biz/vibeware/manifesto">
    <img src="../Shared/Assets/VibeWare.png" alt="VibeWare floppy logo — read the manifesto" width="160">
  </a><br>
  <em>Human intent, AI execution, and plenty of tokens</em>
</p>

> - **File name** `README.md`
> - **Full path** `E:\vibekit\Widgets\Nostalgia\RetroTerminal\README.md`
> - **Path relative to the VibeKit repository root** `Widgets/Nostalgia/RetroTerminal/README.md`
