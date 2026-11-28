> - **File name** `DEBIAN.md`
> - **Full path** `E:/vibekit/Widgets/StudioControls/Linux/DEBIAN.md`
> - **Repository-relative path** `Widgets/StudioControls/Linux/DEBIAN.md`

# Debian package and manual release checks

The release package installs the GTK application under `/usr/lib/studio-controls`
and adds `studio-controls`, `studio-controls-prepare-scenes` and a desktop menu
entry. Editable artwork stays in per-user working storage. Existing preferences
remain under `~/.config/umbertogiacobbi`; logs remain in the user state directory.
There are no package maintainer scripts, services, device probes, OBS configuration
edits or automatic application launches.

## Build and inspect without installing

From the project root, with Python 3.11+ and `dpkg-deb` available:

```sh
python3 -B scripts/build-linux-deb.py --version 0.3.0 --epoch 1794873600
dpkg-deb --info artifacts/installers/local/linux-all/studio-controls_0.3.0_all.deb
dpkg-deb --contents artifacts/installers/local/linux-all/studio-controls_0.3.0_all.deb
dpkg-deb --extract artifacts/installers/local/linux-all/studio-controls_0.3.0_all.deb /tmp/studio-controls-package-inspection
```

CI should pass the source commit timestamp as `SOURCE_DATE_EPOCH` or `--epoch`.
Without either, the reproducible default is zero. The builder uses a fixed manifest,
root archive ownership, normalized permissions and timestamps, and one compression
thread. The default path keeps the current package for each channel. An explicit
custom output directory requires a new filename. Identical inputs, epoch
and packaging toolchain produce the same archive bytes; hashes can change between
toolchain versions. A release may additionally publish the stable download name
`StudioControls-linux-all.deb` for the same package bytes.

`Architecture: all` describes the Python application and assets. Native GTK,
Pillow and X11 libraries come from the user's distribution. Required packages are
`python3 (>= 3.11)`, `python3-gi`, `python3-pil (>= 10)`, `gir1.2-gtk-4.0`,
`fonts-dejavu-core` and `libx11-6`. `x11-xserver-utils` and `xwayland` are recommended
for X11/XWayland operation and optional window placement; `obs-studio` is suggested.
The runtime does not import Python Cairo; GTK's native Cairo dependencies are
provided by the distribution's GTK packages. Ubuntu 24.04 or a newer compatible
Debian-family distribution can satisfy these declared version requirements.

The archive bundles the pinned, licensed websocket-client source, generic media,
both sanitized scene templates, the shared scene catalog and the manual preparation
helper. Application launchers use `python3 -B` so installed code never needs writable
bytecode/cache directories. Licenses and media credits are retained in the application
tree and `/usr/share/doc/studio-controls`.

The package tests use `dpkg-deb` to build and extract into temporary directories.
They do not use `dpkg --install`, `apt install`, root privileges or real devices:

```sh
cd Linux
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest tests.test_deb_package -v
```

## Manual Linux check after CI succeeds

1. Install the downloaded release with the distribution package manager, for example
   `sudo apt install ./StudioControls-linux-all.deb`. Open Studio Controls explicitly
   from the applications menu and check Light, Dark and System appearance.
2. Prepare a fresh import file with
   `studio-controls-prepare-scenes --platform linux --output ./studio-controls-scenes.json`.
   Import it through OBS's Scene Collection menu. Verify Privacy is selected and
   global audio is muted. Choose the actual display, cameras and audio devices in OBS.
3. Enable the local OBS WebSocket server and connect the controller. Verify scene
   buttons, screen zoom/X/Y/reset, presets, each vignette and available audio/blur
   controls. Open camera hardware controls only when you intend to use them.
4. Enter synthetic project text and a local test logo. Apply the cards; check Intro,
   Chapter, Comparison, CRT Test card and Thank you, including VibeWare ON/OFF.
5. Make a short recording you intend to keep or discard: start, pause, resume and
   stop. Verify the reported file opens. Try Stop while new artwork is being prepared.
6. Disconnect the controller and verify its offline state. Reconnect explicitly;
   recording and resume must remain explicit actions. Export diagnostics locally
   if needed. Record the distribution, OBS version and observed result.

For the Windows release, extract the complete published archive and run the native
application on Windows 11. Repeat steps 2–6 using its **Prepare OBS scene collection**
action and native camera controls; also verify protected password persistence after
reopening. CI compilation, synthetic rendering and package checks do not substitute
for these platform and device checks. No manual result is claimed before it is run.

> - **File name** `DEBIAN.md`
> - **Full path** `E:/vibekit/Widgets/StudioControls/Linux/DEBIAN.md`
> - **Repository-relative path** `Widgets/StudioControls/Linux/DEBIAN.md`
