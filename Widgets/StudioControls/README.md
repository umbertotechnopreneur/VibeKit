# Studio Controls

A native control desk for local OBS Studio demonstrations: scenes, recording,
screen details, independent vignettes and project cards. Studio Controls belongs
to [VibeKit](https://github.com/umbertotechnopreneur/VibeKit) and is maintained as
a self-contained desktop utility under `Widgets/StudioControls`.

The Linux application is GTK 4/Python. The Windows 11 edition uses .NET 10 and
WinUI 3, with a shared, UI-independent .NET core. Windows compilation and synthetic
checks are reported separately from validation on real Windows hardware. macOS
is not supported.

## Interface languages

Choose Italiano, English, Tiếng Việt, Français, Deutsch, 한국어, 中文 (Simplified
Chinese) or 日本語 from the native header dropdown. The choice is saved locally.
Switching language updates controls, messages and accessibility labels in place;
it does not reconnect OBS, change recording, rename sources or translate your
project text/cards. Technical error details retain their original language.
Linux CJK interface text uses the distribution's font fallback; the Debian package recommends
`fonts-noto-cjk`. No font binaries or translation services are bundled.

The native adapters use `Shared/ui-translations.json`. Keep every message complete
for all eight language codes, in the catalog's declared order. Protocol identifiers
and editable values must never pass through the presentation translator.

## What it controls

- Twelve scenes: Test card, Intro, That's me, Software Demo, Screen Only, Detail,
  Comparison A/B, Chapter, Hardware Demo, On the Table, Thank you and Privacy.
- Explicit recording start/stop, pause/resume, microphone and desktop mute.
- Screen-only zoom and horizontal/vertical framing, reset and three presets.
- Independent screen/webcam/hardware vignette switches and intensity.
- Webcam zoom, pan, tilt and framing presets through the platform's camera API.
- Project name, eyebrow, subtitle, description, up to three displayed links,
  project logo, chapter/comparison labels, optional VibeWare branding and intro cue.
- Light, Dark and System appearance. Scene icons are individually coloured,
  titles bold and subtitles single-line. Controls retain native accessibility.
- Local bounded logs and explicit diagnostic export. No cloud telemetry or
  additional preview/video capture stream.

The application never starts OBS, recording or a recording resume automatically.
Failed mutations are not replayed. Rendering cards leaves recording controls
available; closing the controller does not stop or resume OBS.

## Start on Linux

Install Python 3, GTK 4/PyGObject, Pillow, DejaVu fonts and OBS through your Linux
distribution. The licensed websocket-client 1.9.2 source is included. Use the
portable system's existing packages rather than installing a second GTK build.

```sh
python3 scripts/install-linux.py
```

The installer creates a versioned per-user application and desktop launcher. It
never starts the panel, OBS, a capture or a desktop session. Open Studio Controls
from the applications menu. For source development, run:

```sh
GDK_BACKEND=x11 python3 Linux/controlli-brio.py
```

Generated project cards go to per-user working storage, not to this checkout.
Existing flat installations remain compatible. Configuration uses the existing
`~/.config/umbertogiacobbi/` files; see `Linux/QUICK-START.txt` for details.

## Build on Windows 11

Install the .NET 10 SDK and Windows SDK. From PowerShell 7:

```powershell
./scripts/Build-Windows.ps1 -Configuration Release -Verify -Package
```

Run `artifacts/windows-x64/StudioControls.Windows.exe`. Configure OBS's local
WebSocket connection and use Connect. Windows connection passwords are encrypted
for the current user with DPAPI; they are not portable plaintext files.

The build uses locked dependencies and a fresh publish folder. Choose a new or
empty `-OutputDirectory` for another build; existing files are never mixed into
the release. `-Package` produces
`artifacts/StudioControls-0.3.1-windows-x64.zip` and its `.zip.sha256` checksum.
Extract the complete ZIP before starting the executable. It includes the .NET
and WinUI runtimes, generic assets, the sanitized scene template and licences.
No Python installation is required on Windows.

CI can run the isolated native `--ui-smoke` check after publishing, then package
the same verified output without starting the app again:

```powershell
./scripts/Test-WindowsPackage.ps1
./scripts/Package-Windows.ps1 -PublishDirectory ./artifacts/windows-x64 -OutputDirectory ./artifacts
```

Packaging validates the clean-build hash manifest and refuses changed or extra
files. Preferences, generated project data, logs, PDBs and CI verification
reports are excluded. The ZIP has a versioned top-level folder and includes a
relative-path integrity manifest and dependency licence notices.

The `--verify` mode exits before constructing the control window or any camera
controller. It uses only synthetic project data, temporary preferences and local
rendered images. It does not enumerate devices, capture media or contact OBS.

Opening camera hardware controls is an explicit action. Camera support and
ranges depend on the actual driver. Windows camera/monitor selection and an
interactive recording check must be performed on the target Windows machine.

## Import the scene package

Use OBS Studio 33 for the packaged collection. OBS 28 or later includes the
WebSocket protocol used by the controller. Enable password authentication in
Tools → WebSocket Server Settings. Keep it local to the computer unless you
intentionally configure remote access.

The Windows setup action prepares a **new import file** and opens its location.
Import that JSON using OBS's Scene Collection → Import menu. The bundled
collection starts on Privacy, with global audio muted and capture devices
unassigned. Select the display, webcams, microphone and desktop output in OBS's
source properties, then enable the sources/audio you intend to use.

A Python alternative, using only bundled files:

```sh
python3 scripts/prepare-scenes.py --platform linux --output /tmp/studio-scenes.json
```

Use `--platform windows` on Windows and choose a fresh output path. Existing files
are never overwritten. Templates use `${ASSET_ROOT}` until preparation resolves
paths to the selected asset directory. Device IDs, PipeWire restore tokens,
project text and credentials are never exported from a personal collection.

The portable template uses a 1920×1080 canvas. OBS output/encoder settings belong
to its profile and are configured separately. Comparison A/B uses the two halves
of one display capture; select it through the controller to calibrate the split
to the selected display's actual dimensions. Privacy hides video only: audio
remains under the explicit mute controls.

Background blur is optional. Install the appropriate platform release of
[obs-backgroundremoval](https://github.com/royshil/obs-backgroundremoval) if you
want it; no plugin binary or model is bundled. Missing optional effects/devices
leave core scene and recording controls available.

## Checks

All automated checks use project files, fake transports and synthetic data.
They never query real cameras, capture the screen, contact a live OBS instance
or overwrite an installed scene collection.

```sh
dotnet run --project Shared/StudioControls.Core.Tests -c Release --artifacts-path /tmp/studio-core-checks
cd Linux
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -v
python3 tests/run_gui_smoke.py
```

The Linux GUI smoke uses a private X display/home/D-Bus with fake devices.
Windows CI builds/publishes WinUI and runs its synthetic verifier. Build and
fixture checks do not establish real-device compatibility or measure capture,
encoding, CPU or GPU performance.

## Source layout and licensing

`Linux/` holds the GTK implementation and regressions; `Shared/` the .NET
protocol/models/service and fake-only checks; `Windows/` native integration;
`Scenes/` sanitized import templates; `Assets/` generic defaults and media;
`scripts/` project-specific setup/build commands.

Original code/artwork/cue: MIT, © 2026 Umberto Giacobbi. Third-party assets retain
their licenses. Keep the CRT footage attribution in the overlay and package;
see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
