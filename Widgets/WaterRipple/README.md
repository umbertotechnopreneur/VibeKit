> - **File name** `README.md`
> - **Full path** `E:\vibekit\Widgets\WaterRipple\README.md`
> - **Path relative to the VibeKit repository root** `Widgets/WaterRipple/README.md`

# Water Ripple

[![VibeKit CI](https://github.com/umbertotechnopreneur/VibeKit/actions/workflows/monorepo.yml/badge.svg?branch=main)](https://github.com/umbertotechnopreneur/VibeKit/actions/workflows/monorepo.yml)
![Platform: Windows 11 · GNOME](https://img.shields.io/badge/platform-Windows_11_%C2%B7_GNOME-0078D4)
[![License: see notes](https://img.shields.io/badge/license-see_notes-8A6D3B)](README.md#licensing)

A small Windows 11 click-ripple app by Umberto Giacobbi, using C# / WinUI 3 and C++ / Direct3D 11 HLSL.

## Built with care

Desktop effects should respect your computer. I review third-party dependencies and their sources, pay attention to memory and rendering performance, and keep stop controls available. The Windows and Linux editions have different safeguards, documented in their respective guides.

## Use

Ripple opens a control panel and starts click ripples on the current monitor when the GPU and capture service are available. The **Effect** tab selects a pipeline and edits the ripple and filter settings. Changes apply immediately to the running screen effect; there is no preview or Apply step. **Stop screen effect** pauses the effect, and **Enable screen effect** resumes it. Clicks pass through the overlay. **Ctrl+Alt+Esc** stops it; the optional **Ctrl+Alt+Shift+W** emergency shortcut closes Ripple.

The **Pipelines** tab lists saved effect combinations with a summary and an actions menu. **New pipeline** saves the current settings under a name. Saved pipelines update automatically as you edit them. Built-in pipelines remain unchanged; use **Duplicate** to customize one, then rename or delete your copies. Editing a built-in directly switches to **Custom** until you save it as a new pipeline. **Import** and **Export** exchange JSON libraries with the Linux edition.

Expandable sections retain **RGB refraction**, **Crest reflections**, **Multiple ripples**, and **Glass / liquid**, with independent enable switches, described filter presets and numeric controls. **Settings** contains capture/animation limits, Acrylic and **Start with Windows**. Startup uses the packaged Windows `StartupTask` API. Existing Windows startup choices and policy are respected; unpackaged builds explain that an MSIX installation is needed.

Closing offers tray minimization, exit or cancel. Tray mode keeps an enabled effect running. The overlay stays hidden until capture and presentation are valid and hides before GPU resources are released. Only the screen overlay is excluded from capture to prevent feedback.

> [!NOTE]
> **Ripples on a Mac would need their own renderer**
>
> There are Windows and [GNOME editions](Linux/README.md), with different capture and rendering paths. **macOS is unsupported**; I do not have a Mac for development or testing.
>
> A Mac contributor would need to own capture, GPU rendering and a dependable stop control. [Discuss a port](https://github.com/umbertotechnopreneur/VibeKit/issues/new?title=%5BDesktop%20Ripple%5D%20) before assuming the shared pipeline format makes the desktop effect portable.

## Portable pipeline model

[Shared/pipeline-catalog.json](Shared/pipeline-catalog.json) defines the common parameter keys, defaults, ranges, groups and built-in presets. The Windows model and Linux JavaScript model own pipeline selection, validation, editing and storage; native views render that state and send actions. Both editions use version 1 JSON documents with a `pipelines` array of `{id, name, values}` entries; `selectedId` is optional local selection metadata. Libraries support 64 saved pipelines, names up to 80 characters and files up to 512 KiB. Invalid imports leave the library unchanged; unreadable libraries are preserved and saving reports an error.

Windows stores the library in `%LOCALAPPDATA%\VibeWare\Ripple\pipelines.json`; Linux uses `$XDG_CONFIG_HOME/VibeWare/Ripple/pipelines.json` (normally `~/.config/VibeWare/Ripple/pipelines.json`). Import/export transfers copies; it does not synchronize computers automatically. Ordinary preferences retain their existing storage.

Common filter values transfer directly. The Windows adapter maps shared `wavelength` to `WaveSize × 0.35` and clamps values to renderer limits. Linux-only parameters, including ring count, damping and shape, are retained in imported pipelines on Windows. Capture limits, startup, effect on/off state, emergency shortcuts and GNOME's target selection remain local preferences. Rendering is platform-specific, so the same pipeline can look different on the two systems.

## Scope and branches

`main` contains the control panel and click-driven water ripples. There are no window-event hooks, transitions, window texture caches, click pulses, border/flash effects or automatic drops. The complete experimental desktop/window effects version is preserved in `codex/window-effects`.

User preferences are bound to `AppSettings`, and transient UI state belongs to `AppState`. Preference edits take effect immediately and save after 250 ms of quiet through one background writer, using atomic replacement in `%LOCALAPPDATA%\VibeWare\WaterRipple\settings-ripple.json`. Only the latest pending snapshot is retained while a write is running. Closing stops the effect immediately and waits for the final save before closing the window. A failed save offers **Retry**, **Keep open**, or **Close without saving**. Unreadable preference files are preserved rather than overwritten by fallback defaults; unedited defaults do not prevent closing, and an explicit retry can recover after the source has been removed or repaired. This separate file preserves the full version's original `settings.json` when switching branches.

## Performance

Capture uses Windows.Graphics.Capture with two GPU buffers and no CPU pixel readback. The ripple shader uses one desktop texture, a 192-byte constant buffer and at most eight waves. Capture and animation rates have separate limits. One monitor is supported at a time; rotated displays remain unsupported.

Screen mode retains the hardware Direct3D 11 requirement and at least 8,000,000,000 bytes of dedicated VRAM. Shared memory does not count. The resource monitor samples private RAM, working set and CPU; it warns at 500 MB private RAM about a possible memory leak and asks for a report. GPU budget and CPU submission timings are shown separately from presentation rates.

Windows shows a notification if the screen effect stops unexpectedly and another when it is successfully started again. Restart after an error is manual; the Linux edition has a separate automatic recovery option for compositor delays.

## Diagnostics

Early Windows releases keep detailed local logs to help diagnose capture failures and crashes across different Windows builds and GPUs. The logs record WinUI startup checkpoints, GPU capabilities, capture dimensions and status, and failures with their Windows error codes.

Serilog writes through a bounded background queue. Logs rotate weekly, keep up to four files, and are capped at 8 MiB per week. They are stored in `%LOCALAPPDATA%\VibeWare\WaterRipple\logs`. The local test MSIX stores them in `%LOCALAPPDATA%\Packages\VibeWare.WaterRipple_af9ft7172qtwj\LocalCache\Local\VibeWare\WaterRipple\logs`. Frames, screenshots and private window titles are not logged.

When reporting an invisible effect or an unexpected exit, include the approximate time and the relevant log lines. Windows Event Viewer may record a crash even when the app exits before its last log entries can be written.

## Build from source

Requires .NET 10 SDK, Microsoft C++ x64 build tools and Windows SDK 10.0.26100.0 or newer with C++/WinRT headers, `fxc` and `windowsapp.lib`. The native script honors `VCToolsInstallDir` or discovers the newest installed C++ toolchain with Visual Studio Installer's `vswhere`. It chooses the newest complete compatible SDK under `WindowsSdkDir` or the standard Windows Kits directory; no Visual Studio edition or machine-specific path is required.

Before first use of a new external library or prebuilt binary, explicitly scan its archive and extracted dependencies with Microsoft Defender. Confirm the official source, version and clean scanner completion in that staging session; reuse the result for unchanged imports. Builds do not initiate scans. Retain no scan reports or build histories.

Build with `dotnet build -p:Platform=x64`. Launch the resulting `bin/Debug/net10.0-windows10.0.26100.0/win-x64/WaterRipple.exe`, keeping its DLLs, Assets and Shaders together. Use the apphost EXE rather than `dotnet WaterRipple.dll` for manifest-based WinRT activation.

Debug checks: `--check-settings`, `--check-resource-guard`, `--check-logging` and `--exercise-overlay`. The overlay exercise checks three Apply/Stop cycles and tray release/restore, then exits. **Pause capture for screenshot** pauses capture for 20 seconds before making the overlay capturable, then restores capture exclusion before resuming.

## Windows installer

Use PowerShell, Python 3 and WiX 5.0.2 to build the unsigned per-user x64 MSI.

```powershell
.\Packaging\Build-MSI.ps1 -WixPath <path-to-wix.exe-or-wix.dll>
```

The installer is under `dist/`. It adds Water Ripple to Start and includes the
private .NET and WinUI runtimes, native renderer and compiled shaders.
Use `msiexec /i <path-to-msi> /quiet /norestart` for automation.

Preferences stay in LocalAppData on MSI uninstall. To retain settings from an
older MSIX, copy its redirected LocalCache data before removing that package.
The MSI edition opens manually from Start and uses the standard Windows capture
border. The package-specific sign-in task and borderless permission are available
to existing MSIX installations.


## Branding

The app icon was generated with ImageGen. The approved VibeWare badge is unchanged; its separate artwork license is in `Assets/VibeWare-LICENSE.txt` and does not relicense this repository.

## Capture border

Windows marks active display capture with a colored border in the MSI edition.

## Model checks

`dotnet run --project tests/PipelineChecks.csproj` exercises the C# data model without WinUI. `node tests/pipeline-checks.mjs` checks the JavaScript model. Setting `RIPPLE_EXCHANGE` to the same temporary JSON path, running Node, then C#, then Node with `--read`, checks interchange in both directions. Build and overlay checks on Windows are still required for capture, startup and native presentation.

Both models reject stale library writes. A stale panel detaches its selection and requires reopening; it never restores an old snapshot over another panel's shared live settings. Ordinary local write failures still restore the failed transaction. Parameter edits remain immediate; native UI schedulers group library writes after 250 ms of quiet, with a final flush before other library actions or disposal. Native pipeline lists retain row identity during value, selection and name updates so edits do not rebuild their controls.

The model checks cover two models sharing the same settings, failed transactions, coalesced/background preference writes, final close ordering and retries, native structure layout, capture-validity flags and render unit conversions. `StudioDiagnostics` owns process monitoring and its UI timer; `RenderParameters` keeps unit conversion separate from capture/session lifetime while preserving the native C ABI. The monorepo workflow also checks pacing, GTK/GIO views against temporary state, and Windows WinUI/native/HLSL compilation. Actual display capture and GPU performance still require testing on the target Windows hardware.

## Licensing

The [Linux edition has an MIT license](Linux/LICENSE). No project-wide software license is currently declared for the Windows edition in this folder. The [VibeWare artwork notice](Assets/VibeWare-LICENSE.txt) applies to the logo only.

---

### This is VibeWare · [Manifesto](https://umbertogiacobbi.biz/vibeware/manifesto)

I created Water Ripple with AI assistance. I define its purpose, guide the implementation, and take responsibility for checking functionality, performance, and security. VibeWare is my initiative for building useful software openly with AI; I ask you to judge the result and the evidence behind it.

To learn what VibeWare means and why I created it, read the [VibeWare manifesto](https://umbertogiacobbi.biz/vibeware/manifesto).

[Water Ripple on GitHub](https://github.com/umbertotechnopreneur/VibeKit/tree/main/Widgets/WaterRipple) · [VibeWare on GitHub](https://github.com/umbertotechnopreneur/VibeWare) · [umbertogiacobbi.biz](https://umbertogiacobbi.biz)

*You are welcome to explore the VibeWare materials. Reuse is governed by the applicable licenses and notices; this footer does not grant a software license.*

[Terms](https://umbertogiacobbi.biz/terms/) · [Privacy](https://umbertogiacobbi.biz/privacy/)

<p align="right">
  <a href="https://umbertogiacobbi.biz/vibeware/manifesto">
    <img src="Assets/VibeWare.png" alt="VibeWare floppy logo — read the manifesto" width="160">
  </a><br>
  <em>Human intent, AI execution, and plenty of tokens</em>
</p>

> - **File name** `README.md`
> - **Full path** `E:\vibekit\Widgets\WaterRipple\README.md`
> - **Path relative to the VibeKit repository root** `Widgets/WaterRipple/README.md`
