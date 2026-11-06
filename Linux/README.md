# Water Ripple for GNOME

Ripple adds click-driven refraction to the wallpaper, application windows, or both on GNOME Shell 50 and Wayland. The [Windows edition](../README.md) has its own WinUI interface and Direct3D 11 renderer.

Open **Ripple** from the applications menu, choose where the effect should appear, and enable **Effect**. Use the minus button or choose **Hide to tray** from the close dialog to keep it running. The top bar icon opens the panel and offers **Quit Ripple**. Use **Super + Shift + Esc** to stop the effect. The icon needs a StatusNotifier host, such as Ubuntu AppIndicators.

The compositor limits the effect to a local region around the click and at most two waves. The controller keeps the effect alive while running, including when hidden. If its heartbeat stops or the compositor detects sustained delays, the extension turns the effect off.

## Build and install

Build the Debian package and extension archive with Python 3, `glib-compile-schemas`, and `dpkg-deb`. Use a temporary directory for a test build, or choose a directory on your portable drive to keep the files.

```sh
python3 build-package.py --output-dir /tmp/water-ripple
sudo apt install /tmp/water-ripple/vibeware-ripple_0.1.2_all.deb
```

If GNOME has yet to discover a new extension installation, open Ripple after the next manual sign-in. The application can then enable the extension through GNOME's extension service.

The extension, controller, and package files in this directory are licensed under [GPL-3.0-or-later](LICENSE).
