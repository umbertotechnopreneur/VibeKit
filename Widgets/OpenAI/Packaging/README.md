> - **File name** `README.md`
> - **Full path** `E:\vibekit\Widgets\OpenAI\Packaging\README.md`
> - **Path relative to the VibeKit repository root** `Widgets/OpenAI/Packaging/README.md`

# Local installers

Source/package target: **0.4.3**. Architecture: Windows x64 / Ubuntu amd64. Packages are generated on demand and removed by an explicit clean.

## Windows

Build the Windows installer using the commands below. Open the generated MSI and launch **OpenAI Usage** from Start.

- Per-user installation in `%LOCALAPPDATA%/Programs/OpenAI Usage`.
- Includes the app's private .NET runtime and Windows App SDK components.
- Uses standard Windows Installer progress, Add/Remove Programs and major-upgrade handling.
- MSI major upgrades require a newer product version. A second build with the same 0.4.3 version does not upgrade an existing 0.4.3 installation; uninstalling it first preserves the personal settings and history described below.
- Does not add autostart or a background service. It does not install or sign in to Codex.
- Private settings/history remain in `%LOCALAPPDATA%/OpenAIUsage` after uninstall.
- **OpenAI Usage · Preferences** is a flat native acrylic form in a window owned by the widget. It stays above its owner and is excluded from the taskbar and Alt+Tab; the form is rendered directly on the window surface.

## Ubuntu

For Ubuntu 24.04 or newer on amd64, install from the directory containing the `.deb`:

```sh
sudo apt install ./openai-usage_0.4.3-1_amd64.deb
```

Launch **OpenAI Usage** from the application menu or run `openai-usage`.

- The package includes a source-built self-contained Linux backend under `/usr/lib/openai-usage`.
- APT resolves declared GTK 4.14+, libadwaita 1.5+, Python GI/Cairo and system-library dependencies from configured repositories.
- Install the Codex CLI separately from its official distribution and run `codex login` yourself.
- Remove with `sudo apt remove openai-usage`. Personal state under `~/.local/state/openai-usage` is preserved.
- Native Ubuntu installation/runtime testing has not been performed in this Windows environment.

## Rebuild

For Windows, from the project root in PowerShell:

```powershell
$imports = & .\Packaging\Prepare-Packaging.ps1 -WindowsOnly
& .\Packaging\Build-WindowsDistribution.ps1 -Imports $imports
```

The MSI comes from a current source publish and includes the private runtimes and dependency notices. Output filenames include a build timestamp so prior packages are preserved. Build staging and imports are retained for manual cleanup. This Windows-only packaging command checks the MSI requirements but does not run the behavioral test suite.

On Linux, build only the Debian package with `bash Packaging/Build-Debian.sh`.

The combined Windows and Ubuntu packaging workflow uses these PowerShell commands.

```powershell
$imports = & .\Packaging\Prepare-Packaging.ps1
& .\Packaging\Build-Installers.ps1 -Imports $imports
```

Preparation downloads official NuGet runtime/tool imports and explicitly scans their archives and extracted files with Defender before use. It returns a verification object kept only in the current PowerShell execution. Pass it directly to the builder; never export it to disk. No scan report, antivirus history, hash inventory or persistent verification manifest is written.

Building verifies the transient import state, forces source rebuilds, checks the compiled application version, runs behavioral checks and validates both packages. Each combined build writes its MSI and Debian package to a new timestamped directory under `artifacts/installers/`. Builds and installer execution do not start antivirus scans. Generated dependencies, staging and outputs are retained for manual cleanup. No old application binary is reused.

To remove everything generated, including the current installer pair:

```powershell
& .\Packaging\Clean-BuildOutputs.ps1
```

Use `-WhatIf` to inspect that explicit cleanup first.

The WiX compiler is staged temporarily as version 5.0.2; no global compiler installation or WiX 7 license settings are modified. Windows installers use deterministic component identities and a stable upgrade code. Debian packages use the documented ar/ustar format and include file checksums, dependency metadata and Unix file modes.

The packages are local unsigned builds. Report package creation, Windows installation and native Ubuntu execution separately in the current task; do not retain historical validation or scan files. No native Ubuntu installation is claimed here.

> - **File name** `README.md`
> - **Full path** `E:\vibekit\Widgets\OpenAI\Packaging\README.md`
> - **Path relative to the VibeKit repository root** `Widgets/OpenAI/Packaging/README.md`
