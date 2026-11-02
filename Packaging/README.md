# Local installers

Source/package target: **0.4.1**. See `../VALIDATION.md` for completed build and installation checks. Architecture: Windows x64 / Ubuntu amd64.

## Windows

Open `../artifacts/installers/OpenAIUsage-0.4.1-windows-x64.msi`, then launch **OpenAI Usage** from Start.

- Per-user installation in `%LOCALAPPDATA%/Programs/OpenAI Usage`.
- Includes the app's private .NET runtime and Windows App SDK components.
- Uses standard Windows Installer progress, Add/Remove Programs and major-upgrade handling.
- Does not add autostart or a background service. It does not install or sign in to Codex.
- Private settings/history remain in `%LOCALAPPDATA%/OpenAIUsage` after uninstall.
- **OpenAI Usage · Preferences** is a flat native acrylic form in a window owned by the widget. It stays above its owner and is excluded from the taskbar and Alt+Tab; the form is rendered directly on the window surface.

## Ubuntu

For Ubuntu 24.04 or newer on amd64, install from the directory containing the `.deb`:

```sh
sudo apt install ./openai-usage_0.4.1-1_amd64.deb
```

Launch **OpenAI Usage** from the application menu or run `openai-usage`.

- The package includes a source-built self-contained Linux backend under `/usr/lib/openai-usage`.
- APT resolves declared GTK 4.14+, libadwaita 1.5+, Python GI/Cairo and system-library dependencies from configured repositories.
- Install the Codex CLI separately from its official distribution and run `codex login` yourself.
- Remove with `sudo apt remove openai-usage`. Personal state under `~/.local/state/openai-usage` is preserved.
- Native Ubuntu installation/runtime testing has not been performed in this Windows environment.

## Rebuild

From the project root in PowerShell:

```powershell
.\Packaging\Prepare-Packaging.ps1
.\Packaging\Build-Installers.ps1
```

Preparation stages new official NuGet runtime/tool imports and explicitly scans each new archive and its extracted files with Defender before use. It reuses evidence for unchanged imports. Building verifies file hashes, restores assets offline from the verified import cache, forces **clean source rebuilds**, checks the compiled application version, runs behavioral checks, creates the packages and checks the Debian archive. A successful build removes older installers, staging payloads, bin/obj and Python caches automatically. Only the current MSI/DEB and small manifests remain under artifacts. The 2026-11-02 cleanup removed around 6.4 GB of generated output. The retained `.imports` cache occupies approximately 1.14 GB and supplies verified build dependencies and first-import scan evidence for fresh source builds; release-output cleanup preserves it. No old release binaries are reused. Builds and installer execution do not start antivirus scans.

The WiX compiler is staged locally as version 5.0.2; no global compiler installation or WiX 7 license settings are modified. Windows installers use deterministic component identities and a stable upgrade code. Debian packages use the documented ar/ustar format and include file checksums, dependency metadata and Unix file modes.

The packages are local unsigned builds. Package creation, Windows installation and native Ubuntu execution are recorded as separate checks; no native Ubuntu installation is claimed. See the root `VALIDATION.md` and the checksum manifest next to the installers.
