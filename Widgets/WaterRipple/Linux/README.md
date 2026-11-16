# Water Ripple for GNOME

Ripple adds click-driven refraction to the wallpaper, application windows, or both on GNOME Shell 50 and Wayland. The [Windows edition](../README.md) uses WinUI and Direct3D 11; both editions share the [pipeline catalog and document format](../README.md#portable-pipeline-model).

I pay attention to rendering performance, resource use and the provenance of third-party dependencies. The safeguards below are specific to the GNOME edition.

Open **Ripple** from the applications menu, choose where the effect should appear, and adjust **Effect**. The controller enables the effect when GNOME accepts the extension request. Use the minus button or choose **Hide to tray** from the close dialog to keep it running. From the top bar menu, choose **Open** to restore the panel, **Effect** to toggle the waves, **About** for app information, or **Quit** to stop Ripple and close it. Use **Super + Shift + Esc** to stop the effect. The emergency shortcut can be disabled in the panel, which also shows if GNOME could not register it. The icon needs a StatusNotifier host, such as Ubuntu AppIndicators.

The **About** tab is available in both the application and GNOME extension preferences. It uses the same VibeWare-branded dialog as the top bar menu, with the installed version, Umberto Giacobbi credits, license and project links.

**GPU excuses** adds a small rotating joke to the Effect panel. **Rotate GPU jokes** hides or shows it independently of your pipelines. The copy changes every 12 seconds only while the panel is mapped; it stops when hidden or closed. These are jokes, not CPU/GPU readings or claims about the price of your hardware.

The compositor limits the effect to a local region around the click and at most two waves. The controller keeps the effect alive while running, including when hidden. If its heartbeat stops or the compositor detects sustained delays, the extension turns the effect off.

**Auto restart** is enabled by default. After a compositor delay it pauses the effect for at least one minute, then resumes it once CPU and memory have been healthy and the desktop event loop responsive for 30 seconds. If the effect stops again within five minutes, the waiting time doubles up to five minutes. Missing resource information prevents automatic recovery. The status row shows when recovery is pending; **Stop**, the emergency shortcut, quitting the controller, or turning off Auto restart cancels it. Other failures, including an expired controller heartbeat, do not restart the effect automatically.

Ripple shows desktop notifications when it pauses for performance, resumes automatically, or stops unexpectedly. Notification visibility depends on your desktop notification settings.

Use the **Effect** tab to choose a pipeline and expand **Ripple**, **RGB refraction**, **Crest reflections**, **Multiple ripples**, or **Glass / liquid**. Filters retain their individual presets and controls. Changes apply immediately to active waves and save automatically, without a preview or Apply step.

Animation uses the source actor's native compositor frame clock while waves exist. The default **FPS limit** is 60, adjustable from 15 to 120 and naturally bounded by the monitor's cadence. Existing explicitly saved FPS limits are preserved. The clock is detached when the last wave disappears. Ages use monotonic elapsed time rather than frame counts; a late frame does not slow the wave or trigger a catch-up burst. Changing the FPS limit applies to existing waves without clearing them. Performance guards observe every native frame callback before deliberate FPS skipping.

The **Pipelines** tab lists built-in and personal pipelines with effect summaries and actions. **New pipeline** saves the current combination. Duplicate a built-in to customize it, rename or delete your copies, and use **Import** / **Export** to transfer JSON libraries between Linux and Windows. Saved pipelines update automatically as parameters change. The library lives at `$XDG_CONFIG_HOME/VibeWare/Ripple/pipelines.json`, normally `~/.config/VibeWare/Ripple/pipelines.json`. Renderer limits are applied locally; rendering can differ across platforms. The library excludes the effect switch, target, performance limits and emergency shortcut.

Parameter changes apply immediately; library writes are grouped after 250 ms of quiet and flushed before another library action or closing the panel. A failed save restores the previous pipeline and settings. Writers compare the loaded file under a temporary D-Bus lock, released automatically on process exit. If another panel has changed the library, saving stops with a request to reopen the panel; the other panel's data is preserved.

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
sudo apt install /tmp/water-ripple/vibeware-ripple_0.1.7_all.deb
```

If GNOME has yet to discover a new extension installation, sign out and back in so Ripple can enable it through GNOME's extension service.

`build-package.py` includes the shared catalog and model in the extension archive and Debian package. For an isolated GTK check, extract the extension archive to a temporary directory and run `tests/gtk-checks.js` from the parent widget directory with `gjs -m`, passing that directory as its argument. Use `GSETTINGS_BACKEND=memory` and a temporary `XDG_CONFIG_HOME` to avoid changing personal settings.

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
cadence, rather than treating the configured FPS limit as a measurement.
