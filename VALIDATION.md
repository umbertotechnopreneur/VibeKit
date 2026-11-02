# Validation — 2026-11-02 (Asia/Saigon)

## Compact-only acrylic widget and Preferences — 0.4.1

- Both clients now construct only the compact strip. Full dashboard/history views, mode toggles and minimize/maximize controls are removed. Windows keeps pin, Preferences, About and Close; GTK keeps its native utility controls and opaque background.
- Windows uses a per-window native DesktopAcrylicController with theme-aware tint/luminosity and a transparent XAML surface. Controller connection and support were confirmed in installed startup logs. System transparency/high-contrast/energy policies can still select the native fallback.
- Preferences, About and close-choice windows use a direct native Grid/ScrollViewer/button layout, with no ContentDialog card or filled footer. Preferences is titled `OpenAI Usage · Preferences`. Utility windows use a transparent extended title bar, are owned by the widget, inherit its pin state and set `IsShownInSwitchers=false`. Main input is disabled while a utility window is open. Preferences Save validates before closing; Cancel and title-bar Close dismiss without saving. Early host close releases the dialog gate.
- An explicit Release Clean was run. The installer builder now restores assets offline from verified packages, forces Rebuild, checks the produced assembly version and validates package hashes. This fixed reuse of 0.3.1 output whose timestamps were ahead of the host clock.
- Current 0.4.1 Windows/Linux source rebuilds completed with zero warnings/errors. All 41 C# checks, four Python watchdog checks and Python syntax checks passed. Safe MSI prerequisite checks and all 227 Debian payload checks passed. GTK source was additionally checked in isolation for rendering state, missing data, movement and tray class construction; native Linux GUI execution remains unverified.
- Windows 0.4.1 was installed successfully (MSI exit 0), its assembly version and SHA-256 matched the clean source-build manifest, and the installed app was launched without a micro flag. Earlier 0.4.0 installed startup measured a 589 x 59 logical strip, four toolbar actions, a native caption region and no truncated values. No Computer Use was performed. Preferences appearance, physical dragging and taskbar/z-order interaction still require the owner's visual trial.
- Cleanup removed 6,435,804,312 bytes of accumulated outputs, followed by the latest temporary build outputs. Only the current MSI/DEB, checksum/build manifests and a single overwritten installation log remain under artifacts. The successful builder automatically removes staging, bin/obj, Python caches and older installers. Verified .imports dependencies remain as source-build inputs (about 1.14 GB); no release history is retained. Package manifests retain assembly/frontend hashes instead of paths to removed payloads.

## Smaller draggable micro mode — 0.3.1

- Windows scales the complete micro strip uniformly to roughly 70%, preserving readable metric labels and increasing the relative size of the countdown unit captions. GTK uses matching compact dimensions and visible labels. Full-mode sizing remains unchanged.
- Windows replaces the logo-only pointer-message handler with native `InputNonClientPointerSource` caption regions over the logo, values, clock and empty data area. The toolbar is a separate passthrough region. Regions are recalculated after layout, resizing and XamlRoot/DPI changes, then cleared when full mode returns. GTK uses a capture-phase gesture that starts a compositor move from non-button content only.
- Entering Windows micro mode explicitly restores a maximized presenter before resizing; returning to full mode restores its previous maximized state. This path was reviewed and compiled, but not exercised through desktop interaction.
- Current-source Windows micro startup and live account refresh succeeded. The rendered client measures 643 x 59 logical units versus 925 x 87 previously; clock bounds are 139 x 47, within the viewport, and no summary value is truncated. Native readback confirmed one caption region and one separate control region with matching boundary at pixel 752 in a 965 x 88 client.
- All 41 C# behavioral checks, four Python watchdog checks and Python syntax checks passed. No new external dependency or antivirus scan was introduced. The GTK movement callback also received an isolated check for non-button origins, button exclusion and event coordinates; this is not a native GTK runtime test.
- No Computer Use was performed in this revision. Native drag regions were registered/read back, but a physical mouse drag, close/restore interactions and GTK GUI behavior still require the owner's trial. Installers are built from the current source; installation/upgrade is not performed here.

## Fluent Pearl redesign — 0.3.0

- Implemented the selected proposal from `Design/redesign-2026-11-02/01-fluent-pearl.png`: restrained native acrylic on Windows, native opaque GTK surface, adjacent countdown/summary, underlined tabs and a seven-day chart with a decimal token axis and horizontal guides. System theme/accent following remains enabled.
- Removed all expanders from both frontends. History exposes its daily table and local quota section directly. Weekly details is the default overview tab; autonomy and secondary totals remain available in the adjacent tab.
- Micro mode is one horizontal strip with inline logo, metrics, clock and controls, without a separate title row. Windows uses a border-only native presenter and explicit minimize/restore/close controls; dragging its logo moves the window. GTK uses a native WindowHandle. Existing close-choice and tray behavior is retained.
- Windows Release source build completed with zero warnings/errors. Source startup, live account refresh and watchdog registration succeeded. Final micro diagnostics: 925 x 87 logical client area, clock 200 x 64, clock bottom 75, no truncated summary values. This verifies geometry, not visual fidelity or every button interaction.
- All 41 shared C# checks and four Python watchdog checks passed; Python frontend syntax checks passed. No new external dependency was introduced.
- Source-built 0.3.0 MSI and DEB completed. Safe MSI prerequisite checks passed; all 227 Debian payload files and the ELF64 x86-64 backend were verified. Packaged Windows assembly and GTK source hashes match the tested/current source output. Checksums and source-payload paths are in `artifacts/installers`.
- Computer Use window selection failed with the existing owner/path mismatch. The owner subsequently requested no Computer Use; no further calls were made. The final strip was checked through native layout diagnostics only. Linux GUI execution, full close/tray interaction testing and installer install/upgrade remain unverified. No installer was installed, and no Git delivery was performed.

## Tray, micro layout and readable details — 0.2.0

- Windows source publish succeeds with native shell tray code, a close-choice dialog, explicit Exit handling, micro/full layouts, and a three-column summary above the countdown. Source startup remained responsive and the real account read succeeded in the tray-enabled probe.
- Toolbar icons have explicit foregrounds independent of pressed/checked control colors. The Usage details panel is split into Autonomy / Weekly details; chart values have wider columns and do not wrap away from their units.
- Two additional behavioral checks cover missing current-day data and the separation between today's bucket and the completed seven-day token average (41 C# checks total).
- GTK code uses a derivable Gtk.Box containing a native label for Pango layout/accessibility, rather than subclassing the final Gtk.Label class. Native Cairo snapshots supply accent-based text gradients. Python syntax passes; native GTK behavior still requires Linux testing.
- Native StatusNotifierItem/DBusMenu support is implemented for Ubuntu, with a visible-window fallback when the desktop has no tray host. No Linux tray, close dialog or micro-mode runtime test has been executed here.
- The Windows micro-mode startup path was exercised in a real native window. Layout diagnostics reported a 625×292 logical client area, identical summary/countdown left positions (33), and countdown bottom at 240: aligned and within the viewport. Native notification-icon registration and the live account refresh also succeeded. This does not replace an end-to-end click test of the tray menu or close dialog.

## Icon, native styling and watchdog — 0.1.3

- Shared ImageGen icon saved in `Branding`, with alpha-preserving PNG assets and a multi-resolution ICO. The 512px PNG's transparent corner was verified; the executable embeds the ICO and both packages include the application icon. Exact prompt is retained in `Branding/icon.prompt.txt`.
- Source builds pass 39 C# behavioral checks and four Python watchdog ownership checks. Checks include the strict decimal 1 GB boundary, invalid guardian invocation, explicit Codex paths, descendant isolation and reused-PID protection. No deliberate 1 GB allocation or real over-limit shutdown was performed.
- The Windows source-built app launched with a distinct watchdog child and successfully read the real Codex account. A guardian heartbeat confirmed process-tree monitoring. The user and process CODEX_HOME match on this host; the executable lookup now handles a stale launcher PATH.
- Latest short startup sample: widget approximately 112 MiB private / 169 MiB resident; guardian approximately 10 MiB private / 33 MiB resident. The account-reading Codex child briefly used 36–39 MiB private and then exited. These are session samples, not long-duration memory benchmarks. Earlier 270+ MB guard readings were aggregate peak commitment.
- No WebGPU/browser module was observed loaded in the measured widget, and no browser child belonged to its process tree. The packaged WebView2 binaries are transitive SDK files. Source uses native WinUI/Pango/Cairo controls and gradients.
- Computer Use's window capture failed with an ownership/handle error even though the reported owner paths matched; recovery also failed. The final visual review was therefore not completed through that helper. Process startup and responsiveness were verified separately. GTK rendering and gradient callbacks still require a native Linux smoke test.

## Installer correction — 0.1.1

- The original 0.1.0 Windows MSI rejected a compatible host because it compared MSI's legacy `WindowsBuild` property against 19041. That property is not a reliable modern Windows build detector.
- Version 0.1.1 uses a native 64-bit registry AppSearch for `CurrentBuildNumber` before evaluating the minimum-build requirement. The upgrade code remains stable; the package/application version is incremented to avoid reusing the failed MSI version.
- `Packaging/Verify-WindowsRequirement.ps1` opens a safe MSI session with machine changes disabled, runs only AppSearch and condition evaluation, and checks real build detection, minimum-boundary cases, architecture and maintenance behavior. The installer builder invokes this regression check before delivery.
- Both installer filenames derive their version from `Directory.Build.props` to prevent version drift between source and packages. Earlier 0.1.0 packages are superseded.
- The safe-session regression check passed on the actual host (build 26300), seven simulated build cases, a 32-bit rejection case and an installed-product maintenance case. Only registry search and condition evaluation were invoked; no install action was run. The 35 shared behavioral checks also passed for the 0.1.1 source build.

## Completed

- Built current C# sources using .NET SDK 10.0.403: Windows WinUI app, shared engine, portable backend and test runner. Final builds completed with zero warnings and zero errors.
- Published current-source Debug outputs to `artifacts/windows-x64` and `artifacts/linux`. The portable backend is framework-dependent .NET; the GTK interface is Python source.
- All **35 behavioral checks** passed, including decimal K/M/G/T token prefixes and rounding across unit boundaries, forecast arithmetic, missing data, reset crossings, stale reset data, zero/exhausted allowance, partial seven-day coverage, DST, current token-date exclusion, service response parsing, explicit credit valuation and isolated persistence.
- Python GTK source compiled successfully with `python -m py_compile`; PowerShell entry points passed parser checks.
- Live read-only account integration succeeded with the installed Codex CLI 0.161.0. Confirmed a real quota window and reset, credit balance and 14 dated token buckets in the displayed reporting range. No private values or identifiers are recorded here.
- First-import Defender custom scan of the eight staged NuGet archives and their extracted package files completed with no threats and exit code 0. See `dependency-scan.json`; later builds reused the unchanged file-hash evidence and did not rescan.
- The first Windows native UI was launched and its live quota, countdown, credit balance and history totals were observed through screenshot/accessibility inspection. The system displayed an acrylic surface with native tabs and no content cards.
- The final published executable was launched after all UI updates and remained responsive with the expected window title. Publishing explicitly includes the application's current compiled PRI and XBF resources; their omission had caused a TabView startup failure and is now guarded by hash checks.
- Built self-contained Release payloads for Windows x64 and Linux x64 from current source, including the collapsed expanders and compact-token history table. The Windows Release payload launched and responded without installation.
- Created `OpenAIUsage-0.1.0-windows-x64.msi` using staged WiX 5.0.2; compilation and default MSI validation completed without errors. Read-only Windows Installer database inspection confirmed version 0.1.0, 472 payload files, 563 components, a Start menu shortcut and upgrade tables. No installation or registry mutation was performed.
- Created `openai-usage_0.1.0-1_amd64.deb`. Verified ar member order, control metadata, every one of its 219 file checksums, root ownership, executable launcher permissions and an ELF64 x86-64 backend. Windows bsdtar independently recognized the three required Debian archive members.
- Six newly staged runtime package directories and the WiX archive/extracted-tool directory passed explicit targeted Defender scans. Unchanged dependencies reused earlier evidence. Details are in `Packaging/runtime-scan.json` and `Packaging/tool-scan.json`.
- Installer SHA-256 hashes and source-payload paths are recorded in `artifacts/installers/checksums.json` and `build-manifest.json`.

## Limits and follow-up

- The owner stopped Computer Use with Escape. No further Computer Use calls were made. Subsequent UI changes (DPI-aware size, actual split-flap animation, automatic system theme, clearer typography, API token chart and centered countdown spacing) compile but have not received a final visual/interaction pass.
- Linux/WSL has no installed distribution in this environment. GNOME 50 rendering, GTK callbacks, theme transitions and animation were not executed. Python syntax plus cross-platform engine tests do not certify GTK runtime behavior.
- Automatic theme following is implemented through Windows UISettings notifications and Adwaita's default color scheme. The host system theme was not changed during testing.
- Seven-day local quota collection has not elapsed. Initial forecast state correctly reports that observations are being collected; the remotely supplied token average is already available.
- The service exposes dated token buckets without documenting their timezone. Current local and UTC dates are excluded conservatively, and History discloses the date range; no timezone rebucketing is claimed.
- Installer installation, upgrade and removal were not executed. Ubuntu GTK runtime behavior remains unverified; package-format validation does not substitute for installing on Ubuntu.
- No autostart task, background service, Git commit, push, pull request or CI workflow was created.
