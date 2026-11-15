# Water Ripple for GNOME

Ripple adds click-driven refraction to the wallpaper, application windows, or both on GNOME Shell 50 and Wayland. The [Windows edition](../README.md) uses WinUI and Direct3D 11; both editions share the [pipeline catalog and document format](../README.md#portable-pipeline-model).

Open **Ripple** from the applications menu, choose where the effect should appear, and adjust **Effect**. The controller enables the effect when GNOME accepts the extension request. Use the minus button or choose **Hide to tray** from the close dialog to keep it running. From the top bar menu, choose **Open** to restore the panel, **Effect** to toggle the waves, **About** for app information, or **Quit** to stop Ripple and close it. Use **Super + Shift + Esc** to stop the effect. The emergency shortcut can be disabled in the panel, which also shows if GNOME could not register it. The icon needs a StatusNotifier host, such as Ubuntu AppIndicators.

The compositor limits the effect to a local region around the click and at most two waves. The controller keeps the effect alive while running, including when hidden. If its heartbeat stops or the compositor detects sustained delays, the extension turns the effect off.

Use the **Effect** tab to choose a pipeline and expand **Ripple**, **RGB refraction**, **Crest reflections**, **Multiple ripples**, or **Glass / liquid**. Filters retain their individual presets and controls. Changes apply immediately to active waves and save automatically, without a preview or Apply step.

The **Pipelines** tab lists built-in and personal pipelines with effect summaries and actions. **New pipeline** saves the current combination. Duplicate a built-in to customize it, rename or delete your copies, and use **Import** / **Export** to transfer JSON libraries between Linux and Windows. Saved pipelines update automatically as parameters change. The library lives at `$XDG_CONFIG_HOME/VibeWare/Ripple/pipelines.json`, normally `~/.config/VibeWare/Ripple/pipelines.json`. Renderer limits are applied locally; rendering can differ across platforms. The library excludes the effect switch, target, performance limits and emergency shortcut.

The Debian package starts Ripple hidden in the top bar when you sign in to GNOME,
without presenting the settings window, even while the tray host is starting. The effect
turns on once GNOME accepts the extension enable request. Open **Ripple** from
the applications menu or the top bar to restore the panel. To disable automatic
start for your user, put a copy of `/etc/xdg/autostart/local.vibeware.Ripple.desktop`
in `~/.config/autostart/` (or `$XDG_CONFIG_HOME/autostart/`) with `Hidden=true`.

Ripple writes compact diagnostic snapshots to the GNOME Shell journal on extension
enable/disable, effect stops and settings changes (grouped after 500 ms of quiet).
Settings entries include only changed values as `[before, after]`, alongside the
effect state, status, target and FPS limit. Stop entries include the reason, remaining
heartbeat time and compositor delay when applicable. Clicks, frames and routine
heartbeats are not logged. View recent entries with:

```sh
journalctl --user -b -o cat --grep='Ripple:' -n 40
```

## Build and install

Build the Debian package and extension archive with Python 3, `glib-compile-schemas`, and `dpkg-deb`. Use a temporary directory for a test build, or choose a directory on your portable drive to keep the files.

```sh
python3 build-package.py --output-dir /tmp/water-ripple
sudo apt install /tmp/water-ripple/vibeware-ripple_0.1.3_all.deb
```

If GNOME has yet to discover a new extension installation, sign out and back in so Ripple can enable it through GNOME's extension service.

`build-package.py` includes the shared catalog and model in the extension archive and Debian package. For an isolated GTK check, extract the extension archive to a temporary directory and run `tests/gtk-checks.js` from the parent widget directory with `gjs -m`, passing that directory as its argument. Use `GSETTINGS_BACKEND=memory` and a temporary `XDG_CONFIG_HOME` to avoid changing personal settings.

The extension, controller, and package files in this directory are licensed under [GPL-3.0-or-later](LICENSE).
