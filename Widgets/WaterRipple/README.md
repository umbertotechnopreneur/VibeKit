# Water Ripple

A small Windows 11 click-ripple app by Umberto Giacobbi, using C# / WinUI 3 and C++ / Direct3D 11 HLSL.

## Built with care

Desktop effects should respect your computer. I review third-party dependencies and their sources, pay attention to memory and rendering performance, and keep stop controls available. The Windows and Linux editions have different safeguards, documented in their respective guides.

## Use

Ripple opens a control panel and starts click ripples on the current monitor when the GPU and capture service are available. The **Effect** tab selects a pipeline and edits the ripple and filter settings. Changes apply immediately to the running screen effect; there is no preview or Apply step. **Stop screen effect** pauses the effect, and **Enable screen effect** resumes it. Clicks pass through the overlay. **Ctrl+Alt+Esc** stops it; the optional **Ctrl+Alt+Shift+W** emergency shortcut closes Ripple.

The **Pipelines** tab lists saved effect combinations with a summary and an actions menu. **New pipeline** saves the current settings under a name. Saved pipelines update automatically as you edit them. Built-in pipelines remain unchanged; use **Duplicate** to customize one, then rename or delete your copies. Editing a built-in directly switches to **Custom** until you save it as a new pipeline. **Import** and **Export** exchange JSON libraries with the Linux edition.

Expandable sections retain **RGB refraction**, **Crest reflections**, **Multiple ripples**, and **Glass / liquid**, with independent enable switches, described filter presets and numeric controls. **Settings** contains capture/animation limits, Acrylic and **Start with Windows**. Startup uses the packaged Windows `StartupTask` API. Existing Windows startup choices and policy are respected; unpackaged builds explain that an MSIX installation is needed.

Closing offers tray minimization, exit or cancel. Tray mode keeps an enabled effect running. The overlay stays hidden until capture and presentation are valid and hides before GPU resources are released. Only the screen overlay is excluded from capture to prevent feedback.

## Portable pipeline model

[Shared/pipeline-catalog.json](Shared/pipeline-catalog.json) defines the common parameter keys, defaults, ranges, groups and built-in presets. The Windows model and Linux JavaScript model own pipeline selection, validation, editing and storage; native views render that state and send actions. Both editions use version 1 JSON documents with a `pipelines` array of `{id, name, values}` entries; `selectedId` is optional local selection metadata. Libraries support 64 saved pipelines, names up to 80 characters and files up to 512 KiB. Invalid imports leave the library unchanged; unreadable libraries are preserved and saving reports an error.

Windows stores the library in `%LOCALAPPDATA%\VibeWare\Ripple\pipelines.json`; Linux uses `$XDG_CONFIG_HOME/VibeWare/Ripple/pipelines.json` (normally `~/.config/VibeWare/Ripple/pipelines.json`). Import/export transfers copies; it does not synchronize computers automatically. Ordinary preferences retain their existing storage.

Common filter values transfer directly. The Windows adapter maps shared `wavelength` to `WaveSize × 0.35` and clamps values to renderer limits. Linux-only parameters, including ring count, damping and shape, are retained in imported pipelines on Windows. Capture limits, startup, effect on/off state, emergency shortcuts and GNOME's target selection remain local preferences. Rendering is platform-specific, so the same pipeline can look different on the two systems.

## Scope and branches

`main` contains the control panel and click-driven water ripples. There are no window-event hooks, transitions, window texture caches, click pulses, border/flash effects or automatic drops. The complete experimental desktop/window effects version is preserved in `codex/window-effects`.

User preferences are bound to `AppSettings`, transient UI state belongs to `AppState`, and settings save immediately by atomic replacement in `%LOCALAPPDATA%\VibeWare\WaterRipple\settings-ripple.json`. This separate file preserves the full version's original `settings.json` when switching branches.

## Performance

Capture uses Windows.Graphics.Capture with two GPU buffers and no CPU pixel readback. The ripple shader uses one desktop texture, a 192-byte constant buffer and at most eight waves. Capture and animation rates have separate limits. One monitor is supported at a time; rotated displays remain unsupported.

Screen mode retains the hardware Direct3D 11 requirement and at least 8,000,000,000 bytes of dedicated VRAM. Shared memory does not count. The resource monitor samples private RAM, working set and CPU; it warns at 500 MB private RAM about a possible memory leak and asks for a report. GPU budget and CPU submission timings are shown separately from presentation rates.

Windows shows a notification if the screen effect stops unexpectedly and another when it is successfully started again. Restart after an error is manual; the Linux edition has a separate automatic recovery option for compositor delays.

## Diagnostics

Early Windows releases keep detailed local logs to help diagnose capture failures and crashes across different Windows builds and GPUs. The logs record WinUI startup checkpoints, GPU capabilities, capture dimensions and status, and failures with their Windows error codes.

Serilog writes through a bounded background queue. Logs rotate weekly, keep up to four files, and are capped at 8 MiB per week. They are stored in `%LOCALAPPDATA%\VibeWare\WaterRipple\logs`. The local test MSIX stores them in `%LOCALAPPDATA%\Packages\VibeWare.WaterRipple_af9ft7172qtwj\LocalCache\Local\VibeWare\WaterRipple\logs`. Frames, screenshots and private window titles are not logged.

When reporting an invisible effect or an unexpected exit, include the approximate time and the relevant log lines. Windows Event Viewer may record a crash even when the app exits before its last log entries can be written.

## Build from source

Requires .NET 10 SDK, Microsoft C++ x64 build tools and Windows SDK 10.0.26100.0 with C++/WinRT headers and `windowsapp.lib`. The native build script uses the installed Visual Studio 18 Insiders toolchain.

Before first use of a new external library or prebuilt binary, explicitly scan its archive and extracted dependencies with Microsoft Defender. Confirm the official source, version and clean scanner completion in that staging session; reuse the result for unchanged imports. Builds do not initiate scans. Retain no scan reports or build histories.

Build with `dotnet build -p:Platform=x64`. Launch the resulting `bin/Debug/net10.0-windows10.0.26100.0/win-x64/WaterRipple.exe`, keeping its DLLs, Assets and Shaders together. Use the apphost EXE rather than `dotnet WaterRipple.dll` for manifest-based WinRT activation.

Debug checks: `--check-settings`, `--check-resource-guard`, `--check-logging` and `--exercise-overlay`. The overlay exercise checks three Apply/Stop cycles and tray release/restore, then exits. **Pause capture for screenshot** pauses capture for 20 seconds before making the overlay capturable, then restores capture exclusion before resuming.

## MSIX installer and Microsoft Store preparation

`Packaging/Build-MSIX.ps1` publishes the current source as Release x64, includes .NET and Windows App SDK, generates package logo sizes from the app artwork and validates the manifest with the installed Windows SDK MakeAppx tool. Restore uses only the pre-staged local NuGet cache; stage and scan any missing imports before running it. The installed Microsoft Visual C++ UWP Desktop framework is a declared package dependency.

For a local signed test package, pass `-CertificateThumbprint` for an existing code-signing certificate in CurrentUser/My whose subject matches `-Publisher`. Signing uses the certificate store directly, without exporting its private key. Install the resulting `dist/WaterRipple_<version>_x64.msix` using Windows App Installer or `Add-AppxPackage`. Test certificates must already be trusted on the destination machine. The build does not install certificates or change trust/security settings.

The manifest initially uses the **local test identity** `VibeWare.WaterRipple` / `CN=VibeWare`. Before Store submission, reserve the product in Partner Center and use its exact Package/Identity/Name and Publisher values via `-PackageName` and `-Publisher`. Omit the test signing parameter for Store packaging; the Store signs accepted submissions. The `runFullTrust` desktop capability needs an accurate justification, and listing assets, privacy information and certification checks still need completion. No Store submission or approval is implied by a successful local install.

New MSIX installations register startup as enabled by default. Launch the installed app once to register the task; **Settings** can disable or re-enable it. Updates respect an existing user or administrator decision in Windows. When enabled, the task starts Ripple in the notification area at sign-in and applies click ripples if capture and a supported GPU are available. Studio stays accessible as a window if the notification area is unavailable. A regular launch opens Studio. Windows redirects packaged application data under the package's LocalCache; the unpackaged version's preferences are preserved separately by Windows virtualization.

Packaging staging is under `obj/msix-stage-<version>` and final installers under `dist`. Clean staging after handoff and before another package build, retaining only the requested installer. Cleanup never builds, downloads or scans anything, and no validation/scan journals are written.

References: [Windows startup integration](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/desktop-to-uwp-extensions#start-an-executable-file-when-users-log-into-windows), [MSIX signing](https://learn.microsoft.com/en-us/windows/msix/package/sign-msix-package-guide).

## Branding

The app icon was generated with ImageGen. The approved VibeWare badge is unchanged; its separate artwork license is in `Assets/VibeWare-LICENSE.txt` and does not relicense this repository.

Created by [Umberto Giacobbi](https://umbertogiacobbi.biz). **Human intent, AI execution, and plenty of tokens**

This is VibeWare · [Manifesto](https://umbertogiacobbi.biz/vibeware/manifesto)

## Capture border

Windows owns the colored capture border. The package declares `graphicsCaptureWithoutBorder`, and Ripple can set `GraphicsCaptureSession.IsBorderRequired(false)` when permitted. Starting Ripple automatically leaves the border visible when Windows requires it. Manually enabling the effect offers **Ask Windows** or **Keep border** when permission is needed. Another capturing app can also require the border. See [Microsoft's IsBorderRequired documentation](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.isborderrequired).

The MSIX execution alias is `waterripple.exe`, exposed at `%LOCALAPPDATA%\Microsoft\WindowsApps\waterripple.exe`. Startup uses the Windows-managed task and does not depend on a versioned installation path.

## Model checks

`dotnet run --project tests/PipelineChecks.csproj` exercises the C# data model without WinUI. `node tests/pipeline-checks.mjs` checks the JavaScript model. Setting `RIPPLE_EXCHANGE` to the same temporary JSON path, running Node, then C#, then Node with `--read`, checks interchange in both directions. Build and overlay checks on Windows are still required for capture, startup and native presentation.

Both models reject stale library writes and restore pipeline selection, values and live settings after a failed transaction. Parameter edits remain immediate; native UI schedulers group library writes after 250 ms of quiet, with a final flush before other library actions or disposal. The checks cover concurrent models, failed CRUD/import/selection, deferred failures, grouped edits and final saves.
