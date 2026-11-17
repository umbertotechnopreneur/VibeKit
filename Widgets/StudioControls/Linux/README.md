# Umberto's Lab Controls

Native Linux GTK 4 control panel for the packaged OBS scenes, camera framing,
screen framing, vignettes and editable project cards. The panel sends commands
to the authenticated OBS WebSocket on the local computer. It does not capture
another video stream or upload diagnostics.

## Runtime

Use the distribution's Python and GTK packages. The existing portable installation
uses Python 3.14.4, PyGObject 3.56.2 and Pillow 12.1.1. These are the versions checked
on this machine, not a claim that every version in the manifest has been tested.

Required components:

- Python 3.11 or newer; GTK 4 and its PyGObject introspection bindings.
- Pillow and DejaVu Sans/Serif fonts for title cards.
- websocket-client 1.9.2; the existing licensed copy is under `_vendor`.
- Linux V4L2 for Brio hardware controls, X11/XWayland and `libX11` for optional
  window positioning. `xrandr` is used only for that positioning.
- The packaged OBS scene collection, its named sources and media assets, or a
  compatible existing collection.

Do not install a second PyGObject build just to run the panel. Prefer the packages
already provided by the portable Linux installation. `pyproject.toml` records
Python dependencies and formatter/linter settings; it does not install native
packages or replace the owner's OBS profile.

Install through `../scripts/install-linux.py`, which copies a complete version to
the current user's data directory before publishing its launcher. It preserves
an existing installation of the same version and never launches the panel or OBS.
Use `../scripts/prepare-scenes.py` to produce a fresh scene import file; import it
manually in OBS and choose the actual devices. The template starts on Privacy
with global audio muted and camera/display selection unassigned.

Bundled defaults remain under `../Assets`. When the packaged panel starts or
project cards are prepared, missing defaults are copied to
`$XDG_DATA_HOME/VibeWare/StudioControls/working-assets`, with
`~/.local/share` as the fallback. Existing artwork is preserved. Empty or relative
XDG data/state paths are ignored so they cannot accidentally target the checkout.
An original flat installation retains its existing adjacent artwork location.
Constructing the OBS client or reading status does not create working assets.

CI can also build a Debian package with `../scripts/build-linux-deb.py`. See
[DEBIAN.md](DEBIAN.md) for archive inspection, dependencies and the owner's manual
Linux/Windows release checks. Package construction never installs or starts the app.

## Checks without live devices

From this directory:

```sh
PYTHONPYCACHEPREFIX=/tmp/umberto-lab-pycache /usr/bin/python3 -m unittest discover -v
PYTHONPYCACHEPREFIX=/tmp/umberto-lab-pycache /usr/bin/python3 -m py_compile controlli-brio.py obs_remote.py screen_framing.py lab_titles.py configure_lab.py diagnostics.py
/usr/bin/python3 tests/run_gui_smoke.py
```

The unit tests use temporary files and fake OBS responses. The GUI runner requires
`xvfb-run` and `dbus-run-session`, creates a private display, bus and home directory,
and substitutes both OBS and the camera. Callback errors and timeout return a
nonzero exit status. `--exercise-failure` deliberately tests that failure path;
its expected exit code is 1.

The previous full OBS integration recordings were one-off checks under `/tmp`.
They are evidence described in `VALIDATION.txt`, not a portable regression suite.
A clean integration runner should build its complete synthetic OBS configuration
inside a temporary directory before it is added to automated checks. Never point
an automated test at the owner's live WebSocket configuration.

Optional development commands, when Ruff is already installed:

```sh
ruff check .
ruff format --check .
```

## Diagnostics

Logs live in `$XDG_STATE_HOME/umberto-lab`, or `~/.local/state/umberto-lab`
when that variable is unset. On the present portable installation the home
directory is on the USB system. No internal-disk storage is selected automatically.

`panel.log` rotates at 1 MiB with three backups. Worker errors retain tracebacks;
uncaught Python and thread exceptions use the same logger. `crash.log` records
Python stack information for fatal native signals through `faulthandler`; one
previous crash report, capped at 1 MiB on the next launch, is retained. This is
local diagnostics, not Crashpad or a full native minidump. Fatal reports cannot
identify every native failure and no automatic restart follows a crash.

Log operation names and outcomes; add measured durations when profiling a specific issue. Do not log OBS request/response
bodies, credentials, project descriptions, entered URLs or captured media.
The formatter additionally redacts common secret fields, authorization tokens
and URL query strings. Diagnostic export includes only these application logs
and basic Python/OS version information; it excludes OBS configuration and media,
refuses to overwrite an existing destination, and never uploads anything.

## Source maintenance

Keep the Python sources, tests, manifests, documentation and permitted static
assets in a versioned source directory. Do not version the owner's OBS WebSocket
configuration, title settings, local presets, recordings, generated project logos,
runtime logs or backups. The source project is maintained in VibeKit under Widgets/StudioControls.
Keep the per-user working assets and preferences outside this checkout.

See `QUICK-START.txt` for controls and `../Assets/CREDITS.txt` for third-party media credits.
