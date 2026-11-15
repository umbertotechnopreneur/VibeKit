# Water Ripple

A small Windows 11 click-ripple app by Umberto Giacobbi, using C# / WinUI 3 and C++ / Direct3D 11 HLSL.

## Use

**Ripple Studio** previews water ripples over a desktop snapshot. Click the preview or use **Drop in the center**. Choose a preset or adjust refraction, wave size, speed and duration. **Refresh background** captures a new snapshot while briefly hiding Studio; the main window remains available in ordinary screenshots.

The Studio provides expandable sections for **RGB refraction**, **Crest reflections**, **Multiple ripples**, and **Glass / liquid**. Each filter can be enabled and adjusted independently and includes described presets. These settings apply to the preview and screen effect and are saved immediately.

**Settings** opens a small dialog inside Studio. It contains the ripple preset and parameters, capture/animation limits, Acrylic preference and **Start with Windows**. Ordinary preferences save immediately; startup is managed by the packaged Windows `StartupTask` API and is enabled by default for new installations. Existing Windows startup choices and policy are respected. Windows administrator policy or a user decision in Startup Apps cannot be overridden by the app. Unpackaged builds explain that an MSIX installation is needed for this toggle.

**Apply to screen** applies click ripples to the current monitor. Clicks pass through the overlay. Stop with **Stop screen effect** or **Ctrl+Alt+Esc**. The overlay stays hidden until capture and presentation are valid and hides before GPU resources are released. The screen overlay alone is excluded from capture to prevent feedback.

Closing offers tray minimization, exit or cancel. Tray mode keeps an enabled click ripple running; the idle Studio renderer is released while hidden. Acrylic, About, VibeWare branding and resource monitoring are retained.

## Scope and branches

`main` contains only Studio and click-driven water ripples. There are no window-event hooks, transitions, window texture caches, click pulses, border/flash effects or automatic drops. The complete experimental desktop/window effects version is preserved in `codex/window-effects`.

User preferences are bound to `AppSettings`, transient UI state belongs to `AppState`, and settings save immediately by atomic replacement in `%LOCALAPPDATA%\VibeWare\WaterRipple\settings-ripple.json`. This separate file preserves the full version's original `settings.json` when switching branches.

## Performance

Capture uses Windows.Graphics.Capture with two GPU buffers and no CPU pixel readback. The ripple shader uses one desktop texture, a 192-byte constant buffer and at most eight waves. Frozen preview snapshots close their capture session. Capture and animation rates have separate limits. One monitor is supported at a time; rotated displays remain unsupported.

Screen mode retains the hardware Direct3D 11 requirement and at least 8,000,000,000 bytes of dedicated VRAM. Shared memory does not count. The resource monitor samples private RAM, working set and CPU; it warns at 500 MB private RAM about a possible memory leak and asks for a report. GPU budget and CPU submission timings are shown separately from presentation rates.

## Diagnostics

Early Windows releases keep detailed local logs to help diagnose black previews and crashes across different Windows builds and GPUs. The logs record WinUI startup checkpoints, GPU capabilities, capture dimensions and status, the first valid preview capture, and failures with their Windows error codes.

Serilog writes through a bounded background queue. Logs rotate weekly, keep up to four files, and are capped at 8 MiB per week. They are stored in `%LOCALAPPDATA%\VibeWare\WaterRipple\logs`. The local test MSIX stores them in `%LOCALAPPDATA%\Packages\VibeWare.WaterRipple_af9ft7172qtwj\LocalCache\Local\VibeWare\WaterRipple\logs`. Frames, screenshots and private window titles are not logged.

When reporting a black preview or an unexpected exit, include the approximate time and the relevant log lines. Windows Event Viewer may record a crash even when the app exits before its last log entries can be written.

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

## Capture border and preview status

Windows owns the colored capture border. The package declares `graphicsCaptureWithoutBorder`, and Ripple can set `GraphicsCaptureSession.IsBorderRequired(false)` when permitted. During manual **Apply to screen**, an in-app dialog offers **Ask Windows** or **Keep border** when permission is needed. Ripple requests optional consent only when you choose **Ask Windows**. Automatic startup leaves the border visible when Windows requires it. Another app capturing the same display can also require the border. See [Microsoft's IsBorderRequired documentation](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.graphicscapturesession.isborderrequired).

While the screen effect runs, the Studio preview is covered with a clear paused message. The sidebar and Settings scrollbars are hidden outside their hovered area; mouse-wheel scrolling remains available. No mouse/keyboard automation is needed to build or install these changes.

## Studio convenience controls

A floating **Minimize to tray** button overlays the bottom-left corner without reserving another layout row. Footer hyperlinks measure only their visible labels, so blank footer space does not launch a page. Scrollbars fade in and out over 500 ms without shifting content or fading the controls themselves.

The preset list includes Gentle, Water drop, Deep water, Shockwave, Dew, Pond, Raindrop, Glass, Jelly and Echo. The six additional presets vary refraction, wave size, speed and lifetime while keeping the same eight-wave GPU limit.

The MSIX execution alias is `waterripple.exe`, exposed by Windows at `%LOCALAPPDATA%\Microsoft\WindowsApps\waterripple.exe`. This launcher path stays independent of the versioned package installation directory. Windows users can manage it under App execution aliases. Startup uses the package's Windows-managed startup task and does not depend on a hard-coded installation path.
