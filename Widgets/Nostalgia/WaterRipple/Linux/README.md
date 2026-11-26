> - **File name** `README.md`
> - **Full path** `E:\vibekit\Widgets\Nostalgia\WaterRipple\Linux\README.md`
> - **Path relative to the VibeKit repository root** `Widgets/Nostalgia/WaterRipple/Linux/README.md`

# Water Ripple for GNOME

Ripple adds click-driven refraction to the wallpaper, application windows, or both on GNOME Shell 50 and Wayland. The [Windows edition](../README.md) uses WinUI and Direct3D 11; both editions share the [pipeline catalog and document format](../README.md#portable-pipeline-model).

I pay attention to rendering performance, resource use and the provenance of third-party dependencies. The safeguards below are specific to the GNOME edition.

Open **Ripple** from the applications menu. The controller enables the effect when GNOME accepts the extension request. Use the minus button or choose **Hide to tray** from the close dialog to keep it running. From the top bar menu, choose **Open** to restore the panel, **Effect** to toggle the waves, **About** for app information, or **Quit** to stop Ripple and close it. The icon needs a StatusNotifier host, such as Ubuntu AppIndicators.

The **Emergency shortcut** footer shows **Super + Shift + Esc** and lets you enable or disable that binding. When registered, the shortcut stops the effect on every monitor and cancels pending recovery. The footer reports an unavailable binding. **Stop** in the panel remains available.

The **About** tab is available in both the application and GNOME extension preferences. It uses the same VibeWare-branded dialog as the top bar menu, with the installed version, Umberto Giacobbi credits, license and project links.

**GPU excuses** adds a small rotating joke to the Effect panel. **Rotate GPU jokes** hides or shows it independently of your pipelines. The copy changes every 12 seconds only while the panel is mapped; it stops when hidden or closed. These are jokes, not CPU/GPU readings or claims about the price of your hardware.

## Monitors and performance

In **Settings → Monitors**, switch each named display on or off. The changes apply immediately while **Effect** is on. All displays are selected by default. Changing the list saves the individual choices, including the choices for disconnected displays. A returning display can use its saved selection. Turning every display off leaves **Effect** armed until a monitor is selected again. **Apply to** independently chooses wallpaper, windows, or both.

Each ripple affects a local region around the click. Ripple clips the region to the selected monitor when a source window spans displays. **Ripple area** controls the maximum patch width and height. **Simultaneous waves** limits the total across monitors to one or two. GNOME composites the local patches with the desktop. When Retro Terminal is also enabled on that monitor, its parent compositor effect receives the scene containing the ripples. [Shared GNOME rendering path](../../RetroTerminal/README.md#compositor-and-performance)

**Animation limit** caps ripple shader updates between 15 and 120 fps, with a default of 60 fps. Existing saved limits are preserved. The monitor's frame clock supplies updates while waves exist, and the clock detaches when the last wave disappears. Wave age follows elapsed time. Changing the cap keeps existing waves, and the performance guard observes callbacks before FPS filtering. Water Ripple renders GNOME source actors directly. Retro Terminal's optional afterglow separately samples bounded GPU history.

The controller keeps the effect alive while running, including when hidden. If its heartbeat stops or the compositor detects sustained delays, the extension turns the effect off.

**Resume automatically** is enabled by default. After a compositor delay, **Recovery** waits at least one minute and requires 30 seconds of responsive desktop operation with healthy CPU and memory readings. If the effect stops again within five minutes of a restart, the waiting time doubles up to five minutes. Missing resource information prevents automatic recovery. Use **Stop**, the emergency shortcut, or **Quit** to cancel a pending recovery. Turning off **Resume automatically** also cancels it. A controller heartbeat failure requires a manual restart.

Ripple shows desktop notifications when it pauses for performance, resumes automatically, or stops unexpectedly. Notification visibility depends on your desktop notification settings.

Use the **Effect** tab to choose a pipeline and expand **Ripple**, **RGB refraction**, **Crest reflections**, **Multiple ripples**, or **Glass / liquid**. Filters retain their individual presets and controls. Changes apply immediately to active waves and save automatically, without a preview or Apply step.

The **Pipelines** tab lists built-in and personal pipelines with effect summaries and actions. **New pipeline** saves the current combination. Duplicate a built-in to customize it, rename or delete your copies, and use **Import** / **Export** to transfer JSON libraries between Linux and Windows. Saved pipelines update automatically as parameters change, and rows retain their identity and focus during live edits. The library lives at `$XDG_CONFIG_HOME/VibeWare/Ripple/pipelines.json`, normally `~/.config/VibeWare/Ripple/pipelines.json`. Rendering and limits can differ across platforms. Effect state, monitors, target, performance limits and the emergency shortcut are local preferences.

Parameter changes apply immediately; library writes are grouped after 250 ms of quiet and flushed before another library action or closing the panel. An ordinary local save failure restores the previous pipeline and settings. Writers compare the loaded file under a temporary D-Bus lock, released automatically on process exit. If another panel is writing or has changed the library, the stale panel detaches its selection and stops saving until reopened. It does not restore old values into shared GSettings, preserving the other panel's live and saved changes. Model and GIO tests exercise two panels sharing the same settings.

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

Build the Debian package and extension archive with Python 3, `glib-compile-schemas` and `dpkg-deb`. The builder writes to the family artifact directory.

```sh
python3 build-package.py
sudo apt install ../../artifacts/packages/local/linux/all/WaterRipple/vibeware-ripple_0.1.18_all.deb
```

If GNOME has yet to discover a new extension installation, sign out and back in so Ripple can enable it through GNOME's extension service.

`build-package.py` includes the shared monitor selector, catalog and pipeline model in the extension archive and Debian package. For an isolated GTK check, extract the extension archive to a temporary directory and run `tests/gtk-checks.js` from the parent widget directory with `gjs -m`, passing that directory as its argument. Use `GSETTINGS_BACKEND=memory` and a temporary `XDG_CONFIG_HOME` to avoid changing personal settings.

Run `tests/library-checks.js` the same way, with its own empty `XDG_CONFIG_HOME`, to check real GIO writes, separate D-Bus lock owners, stale panels, timed saves and closing with pending changes. Model checks in `tests/pipeline-checks.mjs` additionally inject failed writes and verify rollback of each library action.

The extension, controller, and package files in this directory are licensed under [GPL-3.0-or-later](LICENSE).


## Rendering regression checks

From the widget directory:

```sh
node tests/frame-pacing-checks.mjs
node tests/diagnostics-checks.mjs
python3 scripts/check-shader.py --output-dir /tmp/ripple-shader-check
python3 scripts/smoke-gnome.py --output-dir /tmp/ripple-animation-check
```

The shader harness renders the exact production GLSL into a real 512 × 512 GPU
framebuffer, compares rendering against a Git baseline, and checks that
the ripple responds to every control. It uses synthetic text and grid imagery only.
`--baseline-ref` selects the prior Git revision for comparison. GPU times exclude
GNOME composition, capture and presentation. The animation smoke test uses a
separate headless GNOME session with private runtime, configuration and D-Bus
paths; it never restarts the user's desktop. It measures actual shader-update
cadence observed in the isolated session.

> - **File name** `README.md`
> - **Full path** `E:\vibekit\Widgets\Nostalgia\WaterRipple\Linux\README.md`
> - **Path relative to the VibeKit repository root** `Widgets/Nostalgia/WaterRipple/Linux/README.md`
