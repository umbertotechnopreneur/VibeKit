> - **File name** `README.md`
> - **Full path** `E:\vibekit\Widgets\OpenAI\README.md`
> - **Path relative to the VibeKit repository root** `Widgets/OpenAI/README.md`

# OpenAI Usage

[![VibeKit CI](https://github.com/umbertotechnopreneur/VibeKit/actions/workflows/monorepo.yml/badge.svg?branch=main)](https://github.com/umbertotechnopreneur/VibeKit/actions/workflows/monorepo.yml)
![Platform: Windows 11 · Linux](https://img.shields.io/badge/platform-Windows_11_%C2%B7_Linux-0078D4)
[![License: see notes](https://img.shields.io/badge/license-see_notes-8A6D3B)](README.md#licensing)

Two native desktop clients in VibeKit:

- **Windows:** C# / WinUI 3, a single compact desktop-acrylic strip and optional always-on-top.
- **Linux:** Python / GTK 4 / libadwaita, one compact native window surface. Follows the GNOME 50 design, with compatible theme colors for libadwaita 1.5+ on Ubuntu 24.04 and newer.
- **Shared:** C# / .NET 10 account reader and analytics. Both clients use the same calculations and settings schema.

The widget shows the selected allowance remaining, **Remaining Credits**, **Estimated time remaining**, **Resets available**, and the first scheduled reset. Select the reset count to see voucher expiration dates. The countdown and full reset date alternate every 10 seconds in the chosen reporting time zone. Turn on **Enable usage details bar** in Settings to show the toolbar button for a second row with allowances and the reset forecast side by side. Preferences and About open in separate native windows. On Windows, Preferences uses desktop acrylic and stays above the widget, outside the taskbar and Alt+Tab. About keeps the watchdog and log location under **Technical details**.

The shared generated icon is used by the executable, window/header, About, Start/app-menu entries and packages. Metrics use monospace typography. Text gradients use the system accent and adjust their lightness to the system theme: native WinUI gradient brushes on Windows and native Pango/Cairo snapshots on GTK. Toolbar controls show only their icons at rest.

The right-hand toolbar contains the pin and window commands, with the two branding icons centered below. The photo-based signal at left blends three alpha layers from green to amber between 100% and 50% remaining, then from amber to red. It flickers briefly about every five minutes when animations are enabled. The app icon opens the [OpenAI Usage project page](https://github.com/umbertotechnopreneur/VibeKit/tree/main/Widgets/OpenAI), and the VibeWare logo opens the [VibeWare manifesto](https://umbertogiacobbi.biz/vibeware/manifesto).

Both interfaces are native. No HTML, JavaScript UI, WebView or WebGPU renderer is used. Windows App SDK brings three WebView2 binaries transitively; they are present in the package but are not instantiated by this app. Their disk presence is distinct from a browser runtime running in memory.

## Built with care

I review third-party dependencies and their sources and pay attention to memory and CPU use. The watchdog below is a concrete protection, with platform-specific behavior and limits; it is not a blanket security guarantee.

## Memory protection and diagnostics

A separate watchdog starts before the widget begins account polling. The limit is **1,000,000,000 bytes** for the widget and the children it starts; unrelated Codex sessions are excluded. Over the limit, the watchdog records the event, stops the owned group and displays a warning. It does not restart the app automatically.

- Windows uses a kill-on-close Job Object and polls aggregate peak private commitment every 500 ms. The guardian is outside the job so it can stop a frozen UI and display the notice. Unexpected guardian exit also closes the job and terminates its members.
- Linux uses a separate Python process and polls aggregate RSS through `/proc` every 500 ms. It tracks descendants and creation identities, including previously observed orphaned children; reused PIDs and the guardian are excluded. RSS can count shared pages in multiple processes, so this is a conservative measure rather than the Windows private-commit metric. Native Linux execution remains unverified here.
- The widget refuses to start without the initial watchdog handshake. The memory limit is not an antivirus scan or an installed system service.
- Errors, connection-state changes and guard events are logged locally under the state directory's `logs` folder. JSONL logs rotate at 512 KiB and keep one backup; a small `watchdog.json` heartbeat records the monitored memory and limit. Raw account payloads, credentials and conversation contents are not logged.
- The Codex executable is resolved from process/user PATH and standard installation links, including WinGet. A stale Explorer PATH no longer prevents a locally installed Codex CLI from being found.

Token quantities use decimal prefixes **K, M, G, T, P, E** (powers of 1,000). The account reader retains its history and analytics internally; the UI presents only the compact summary.

## Tray and compact widget

- The close button asks whether to exit, minimize to the notification area or cancel. Polling and memory protection continue while hidden.
- Left-click the tray icon to reopen. Right-click offers Open, About, Settings, the owner's website, the VibeWare manifesto and Exit. Website actions open the browser only when explicitly clicked.
- Drag either side edge to resize the widget down to 300 logical pixels. Tiles wrap and the height follows the number of rows. Drag empty space to move the widget. Preferences lets you reorder modules with the grip and toggle their visibility. Allowance bars track the percentage remaining. The credits bar compares the current balance with the highest balance seen since launch. On Windows, click usage, the estimate, the first scheduled reset or the countdown to open ChatGPT usage settings in your browser. On GTK, the compositor move gesture excludes buttons.
- Windows uses a native Shell notification icon and menu; no Windows Forms or browser UI is added. Toolbar glyphs retain theme-aware foregrounds even when pressed/selected. The taskbar/shortcut use an explicit app identity.
- Ubuntu uses the native StatusNotifierItem/DBusMenu protocol. The desktop must provide a compatible tray host (as supplied by Ubuntu's indicator integration). If absent, the app leaves its window accessible and disables the hide-to-tray option. No desktop extension is installed automatically. Native GNOME tray execution is not verified in this environment.

> [!TIP]
> **Help the widget feel at home on another desktop**
>
> The clients target Windows and Linux; native Linux execution still needs validation. **There is no supported macOS client.** I do not have a Mac to develop and test one.
>
> Want to maintain a Mac version? [Start here](https://github.com/umbertotechnopreneur/VibeKit/issues/new?title=%5BOpenAI%20Usage%5D%20) with ideas for a native widget, tray controls and reliable shutdown. Use synthetic data in reports; keep account details private.

## Installers

Installer targets for **0.4.21**, generated on demand from the current source. These paths exist only until cleanup:

- `OpenAIUsage-0.4.21-windows-x64.msi`: per-user Windows x64 installation under `%LOCALAPPDATA%/Programs/OpenAI Usage`, Start menu shortcut and standard Windows uninstall/upgrade support. Includes Windows App SDK components and requires the shared Microsoft .NET Runtime 10.0 for Windows x64. With a compatible runtime, open the MSI directly. If needed, install the runtime separately from [Microsoft](https://dotnet.microsoft.com/en-us/download/dotnet/10.0).
- `openai-usage_0.4.21-1_amd64.deb`: Ubuntu amd64 package with the Linux backend, launcher and desktop entry. The local Debian builder declares the .NET runtime, GTK, Python and system libraries as APT dependencies. Targets Ubuntu 24.04+.

All packages require a separately installed Codex CLI and your own ChatGPT sign-in. The installers contain no credentials and do not install or sign in to Codex. The local MSI and DEB are unsigned. Package validation, Windows installation/startup and native GTK runtime validation are separate checks; report their results in the current task rather than retaining a historical report.

```sh
sudo apt install ./openai-usage_0.4.21-1_amd64.deb
```

On Windows, open the MSI to install, then launch **OpenAI Usage** from Start. Use Settings > Apps to uninstall; private observation history is outside the installation directory. The installer builder forces clean source rebuilds and checks the compiled version. Packaging preserves generated installers, dependencies and build output for manual cleanup. Run `Packaging/Clean-BuildOutputs.ps1` to remove all generated material, including the latest installers. No release history, antivirus report, scan hash inventory or persistent import cache is kept.

Enable **Usage details bar** in Preferences to add a chart button. Click it to open or close the second bar, or press Escape while the widget has focus to close it. **Allowances** shows the 5-hour and weekly Codex windows for the selected limit family when the account provides them. **Reset forecast** compares the observed time-to-exhaustion estimate with the selected allowance's reset. The widget opens with the second bar closed.

## Real data

The widget starts a private stdio `codex app-server`, initializes it and issues only account reads using the existing Codex ChatGPT sign-in. It does not read credential files, invoke models, start agent turns, send messages, redeem resets or purchase credits.

| Shared data | Source and behavior |
| --- | --- |
| Allowance and reset | `account/rateLimits/read`; windows are identified by actual duration, not primary/secondary position. Weekly Codex is preferred; a previously configured window remains honored. |
| Credit balance | Returned balance, unlimited state or unavailable. Credits are distinct from earned full-reset vouchers. |
| Earned reset vouchers | `account/rateLimits/read` returns the available count and may include expiry dates. The **Available resets** field displays the count, with expiry details in its tooltip. |
| Token chart, daily average and previous week | `account/usage/read` daily buckets; available remotely without waiting for local collection. Missing dates are unavailable, not zero. |
| Plan | `account/read`, retained by the shared backend. |
| Quota history | Local percentage observations, isolated by hashed account identity, window and plan. The account API does not provide historical daily quota percentages. |
| Autonomy | Remaining percentage points divided by observed daily percentage-point consumption. Tokens are never assumed equivalent to quota percentage. |

Account data refreshes every 60 seconds; the countdown updates every second. Requests time out, failed refreshes clear old account data, and stale/reset-pending states are visible. Authentication and schema failures show unavailable states, never sample numbers. API-key-only accounts do not expose the subscription allowance.

Forecasts use complete local calendar days within the preceding seven days when available. Otherwise, at least one hour of valid observations within those seven days can provide a partial estimate, labeled as such in its tooltip. Gaps over three minutes, reset crossings, decreases in used percentage and plan changes are excluded. Closing the widget or sleeping the computer leaves unobserved periods; there is no background service or historical quota backfill. Zero observed consumption has no finite exhaustion estimate, and an upcoming reset can occur before the estimated exhaustion time.

Token buckets expose dates without a documented timezone. Labels are preserved rather than rebucketed; current local and UTC dates are conservatively excluded. The backend carries the ending date and this limitation. Service-reported tokens are not a universal remaining-token budget.

## Windows

Source-built Debug output, available only before cleanup:

```powershell
& '.\artifacts\windows-x64\OpenAIUsage.Windows.exe'
```

Requires Windows x64, .NET 10 runtime and an installed Codex CLI signed in with ChatGPT. Windows App SDK components are included. Keep the output folder intact; this is an unpackaged app, not an installer. Run `codex login` yourself when needed, or set the native Codex executable path in Settings.

Preferences apply as they change and include the Codex executable path, a reporting time zone chosen from those installed on the operating system, an optional Big size setting that increases the widget by approximately 25%, and independent notifications at 50%, 20% and 5% remaining. Big size is off by default. Alerts apply to the displayed allowance and are off by default; they rearm after the allowance rises above the threshold. On Windows, **Show in taskbar** also controls Alt+Tab; the window stays accessible if the notification icon is unavailable. **Start with Windows** reflects the Windows startup setting and applies immediately; unpackaged installs use the current user’s Run registry key, while MSIX uses its startup task. The pin button controls always-on-top and shows its current setting. On Linux, the pin requests always-on-top on X11 when supported by the window manager; on Wayland it is disabled because GTK 4 has no portable equivalent. The desktop may offer its own window controls. GTK 4 also does not provide a portable taskbar visibility control for Wayland, so the desktop controls window visibility. The label position applies to all field headings and countdown units. Available resets, credits and the estimate can each be hidden. The traffic light immediately following the app icon reflects the displayed allowance. It is green above the active thresholds, amber at an enabled 50% or 20% threshold, red at an enabled 5% threshold and unlit when allowance data is unavailable. Credit alerts use a separate threshold and appear in the notification and tooltip. The alert ding can be switched off in Preferences. Split-flap and fade effects respect system animation preferences.

## Linux / GNOME

Portable source-built output: `artifacts/linux`. Its backend DLL is platform-independent, not a repackaged Windows application.

Prerequisites for the framework-dependent source output: .NET 10 runtime; Codex CLI signed in with ChatGPT; Python 3; PyGObject; GTK 4.14+; libadwaita 1.5+; Pycairo; PangoCairo introspection; and a graphical Linux session. The local Debian build uses the system .NET runtime as an APT dependency. Typical Debian-family UI package names are `python3-gi`, `python3-gi-cairo`, `python3-cairo`, `gir1.2-gtk-4.0` and `gir1.2-adw-1`. Obtain prerequisites from official sources and follow the first-import policy below. Nothing is installed automatically by repository build scripts.

```sh
sh /path/to/artifacts/linux/Linux/run.sh
```

In a source checkout, build `Backend/OpenAIUsage.Backend.csproj` using the .NET 10 SDK, then run `sh Linux/run.sh`. `OPENAI_USAGE_BACKEND` can point to another source-built backend DLL. The optional `.desktop` template assumes you have created an `openai-usage` launcher on PATH.

The isolated GTK checks exercise native rendering, clock transitions and preferences with synthetic data. A GNOME desktop smoke test covers compositor, theme and notification integration.

## Build and verify

Use the pinned SDK in `global.json`, Windows SDK tooling and Python. Import preparation is an explicit separate step in the same PowerShell execution:

```powershell
# From the VibeKit repository root:
Set-Location '.\Widgets\OpenAI'
$imports = & .\Prepare-Imports.ps1
& .\Build.ps1 -Imports $imports -Configuration Debug
```

Preparation downloads official dependencies and explicitly scans the imported archives and extracted files. It returns an in-memory verification object for the current build; do not serialize or export it. Build scripts validate this transient object and the unchanged staged files before using them. Builds and tests never start antivirus scans. Cleanup removes generated outputs and local dependency caches; a later build starts a fresh preparation session.

Build a portable ZIP and unsigned MSI for each Windows architecture.

```powershell
$imports = $null
foreach ($arch in 'x64', 'arm64') {
    $imports = & .\Packaging\Prepare-Packaging.ps1 -WindowsOnly -Architecture $arch -PreviousImports $imports
    & .\Packaging\Build-WindowsDistribution.ps1 -Imports $imports -Architecture $arch
}
```

On Linux use `bash Packaging/Build-Debian.sh` to build the Debian package.

For Release installers:

```powershell
$imports = & .\Packaging\Prepare-Packaging.ps1
& .\Packaging\Build-Installers.ps1 -Imports $imports
```

The packaging workflow forces source rebuilds, checks the compiled version, runs behavioral checks and validates the MSI and Debian packages. WiX 5.0.2 is staged from NuGet. Each build puts its packages in a timestamped directory under `artifacts/installers/` and preserves earlier outputs. An explicit clean removes all generated files:

```powershell
& .\Packaging\Clean-BuildOutputs.ps1
```

No build/release history or antivirus evidence files are retained. The dependency-free C# checks cover forecast arithmetic, reset ordering, exhausted/zero/missing usage, incomplete coverage, monitoring gaps, plan changes, DST, current-day exclusion, token totals, duplicate dates, quota selection, credits, malformed optional API fields and account-isolated atomic persistence. Python checks also cover settings revisions, exit during a save and failed-save recovery. Native GTK behavior still requires a GNOME smoke test. `Tests/gtk_settings_checks.py` exercises real GTK close/save ordering with a synthetic backend; run it on an isolated display, session bus and XDG directories, then repeat with `--fail-first` for the error/retry path. `Tests/gtk_settings_recovery_checks.py` verifies the native recovery message, reusable settings dialog, and explicit save without an account connection. Shared checks exercise coalescing, serialized writes, failed flush/retry, throwing UI observers, preserved invalid settings, and payload bounds.

## Local state

- Windows: `%LOCALAPPDATA%/OpenAIUsage`.
- Linux: `$XDG_STATE_HOME/openai-usage`, otherwise `~/.local/state/openai-usage`.
- Settings and up to 16 days of quota observations stay outside the repository. Account identities are hashed. Emails, credentials and conversation contents are not persisted by the widget. Remote token buckets stay in memory.
- History is validated before any update, with an 8 MiB / 65,536-observation bound. Invalid samples, timestamps, percentages, plans and duplicate timestamps make history unavailable without overwriting its source. Live account values remain available; forecasts do not use rejected history.
- GTK and Windows debounce settings edits, serialize writes, and wait for the latest pending settings revision before exiting. A failed save cancels exit and leaves the window available with the error; retrying exit is an explicit user action. Refreshes do not replace unsaved settings.
- Invalid settings stay untouched while the preferences show editable defaults and a recovery message. Saving a correction preserves the original as `settings.json.invalid-<unique id>` beside the corrected file.
- Settings files and Linux request payloads are limited to 64 KiB; .NET stdin accepts at most 65,536 decoded characters. Codex JSON lines are capped at 4,194,304 decoded characters; Linux backend stdout at 8 MiB and discarded stderr at 1 MiB. Timeouts and the separate memory watchdog remain independent safeguards.
- Windows records unexpected application exceptions in a local `startup-error.log` for troubleshooting. It is not uploaded or placed in the repository.

## Maintenance boundaries

`Shared/` owns parsing, quota analytics, storage, bounded JSON framing, and the settings save queue. The native UI receives snapshots and presents errors; it does not duplicate quota calculations. Windows preferences, dialogs and refresh presentation live in focused `MainWindow.*.cs` partials, with disk/process work dispatched off the UI thread. Linux separates bounded child-process transport (`backend.py`), settings and refresh lifecycle (`settings_ui.py`), and small widget builders (`ui_helpers.py`) from the strip renderer. `Build.ps1` includes those modules in the Linux payload.

## First external import

Before first introducing an external library or prebuilt binary, run one explicit targeted Microsoft Defender scan of the new archive **and extracted import**. Keep it staged until clean completion. Check the result transiently for the current preparation/build session; do not write scan reports, inventories, verification manifests or historical logs. Reuse the confirmed result only for the same unchanged staged files in that session. An unavailable, disabled, failed or unfinished scan is not clean. Never scan whole disks, installed SDK/runtime trees or generated build output as part of this workflow. Cleanup removes the temporary imports and their session state.

## Official references

- [Codex App Server account and usage methods](https://learn.chatgpt.com/docs/app-server)
- [OpenAI pricing and usage concepts](https://learn.chatgpt.com/docs/pricing)
- [WinUI unpackaged distribution](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/unpackage-winui-app)
- [GNOME 50 developer notes](https://release.gnome.org/50/developers/)
- [PyGObject GTK 4 introduction](https://pygobject.gnome.org/tutorials/gtk4/introduction.html)
- [Adwaita appearance and system preferences](https://gnome.pages.gitlab.gnome.org/libadwaita/doc/main/styles-and-appearance.html)

Mockups and ImageGen prompts are design source assets. Their numbers are illustrative, not account telemetry.

## Licensing

This folder does not currently include a project-wide software license. The [VibeWare artwork license](Branding/VibeWare-LICENSE.txt) covers the logo only; it does not license the widget code.

---

### This is VibeWare · [Manifesto](https://umbertogiacobbi.biz/vibeware/manifesto)

I created OpenAI Usage with AI assistance. I define its purpose, guide the implementation, and take responsibility for checking functionality, performance, and security. VibeWare is my initiative for building useful software openly with AI; I ask you to judge the result and the evidence behind it.

To learn what VibeWare means and why I created it, read the [VibeWare manifesto](https://umbertogiacobbi.biz/vibeware/manifesto).

[OpenAI Usage on GitHub](https://github.com/umbertotechnopreneur/VibeKit/tree/main/Widgets/OpenAI) · [VibeWare on GitHub](https://github.com/umbertotechnopreneur/VibeWare) · [umbertogiacobbi.biz](https://umbertogiacobbi.biz)

*You are welcome to explore the VibeWare materials. Reuse is governed by the applicable licenses and notices; this footer does not grant a software license.*

[Terms](https://umbertogiacobbi.biz/terms/) · [Privacy](https://umbertogiacobbi.biz/privacy/)

<p align="right">
  <a href="https://umbertogiacobbi.biz/vibeware/manifesto">
    <img src="Branding/VibeWare.png" alt="VibeWare floppy logo — read the manifesto" width="160">
  </a><br>
  <em>Human intent, AI execution, and plenty of tokens</em>
</p>

> - **File name** `README.md`
> - **Full path** `E:\vibekit\Widgets\OpenAI\README.md`
> - **Path relative to the VibeKit repository root** `Widgets/OpenAI/README.md`
