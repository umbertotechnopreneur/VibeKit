# Preparing a Windows GitHub release

GitHub Releases contain **portable ZIPs and per-user MSI installers for Windows 11 x64 and ARM64**, each with an adjacent SHA-256 file. Microsoft Store submission is a separate
task requiring an explicit owner request.

## Build the approved source version

The `release packages` workflow reads `semVer` from `WorkTrail/build-version.json`.
The value must be `X.Y.Z`, with major `1..65534` and minor/patch `0..65534`, without
leading zeros or prerelease suffixes. The source commit must be reachable from `main`.

- Run the workflow manually from `main` to prepare downloadable workflow artifacts.
- Push an explicitly approved annotated `vX.Y.Z` tag after that version is on `main`
  to prepare the archives and a **draft** GitHub Release.
- The tag must exactly match the tracked version. The workflow refuses to replace
  an existing release and never publishes a draft automatically.

Each architecture has its own checkout and output directories. The workflow builds
`Release-Unpackaged` from source, runs the portable packaging safeguards and creates
the ZIPs and MSIs with `scripts/New-PortableReleaseArchive.ps1`. Build dependencies must
follow the first-import scan rule in [AGENTS.md](../AGENTS.md).

Each architecture produces a ZIP, an MSI and a checksum for each. Workflow
artifacts are retained for 30 days; draft release assets have separate retention.
No signing credentials or development certificates are exported.

## Archive contents

Each `WorkTrail-<version>-<platform>-portable-unsigned.zip` contains:

- `WorkTrail.exe`, its libraries and resource directories;
- the bundled Windows App SDK runtime for that architecture;
- build/release metadata, license and trademark notices, and payload checksums.

The archive writer rejects missing runtimes, version/architecture mismatches and
existing output directories. An adjacent `.zip.sha256` verifies the archive.

Install the Microsoft .NET 10 Runtime matching the package architecture. The publish profiles set `SelfContained=false` for .NET and `WindowsAppSDKSelfContained=true` for the Windows App SDK used by the UI. Run the MSI for a per-user installation or extract the entire ZIP and run `WorkTrail.exe`. Keep its DLLs, resources and subfolders together.
Unsigned executables may trigger Windows security prompts.

Portable describes deployment, not where preferences are stored: application data
stays under `%LOCALAPPDATA%\WorkTrail`. The ZIP does not register an execution alias;
run the extracted executable directly for CLI commands such as `--version`.

## Validate before publication

Review both architectures, version metadata, archive contents and checksums. Verify
MSI installation, ZIP extraction, and CLI and UI startup on clean Windows x64 and ARM64 machines with the matching .NET 10 Runtime. Cross-compilation does not prove native ARM64
execution. Publish a reviewed draft only after an explicit owner request.

## Local portable preparation and cleanup

When explicitly requested, use `WorkTrail.ps1 -Action PublishUnpackaged` and
`New-PortableReleaseArchive.ps1` with the same approved version and target
architecture. Pass `-WixPath` with the path to WiX 5.0.2 to build the matching MSI. A normal Debug compile uses `Debug-Unpackaged`. Do not use the MSIX
installer helpers for GitHub release preparation.

Generate output only under a task-specific ignored `artifacts/` directory. After
the final use, run matching `dotnet clean` and remove verified task-owned
intermediates. Retain only the latest validated ZIP and MSI per requested architecture,
their checksums and minimal validation metadata. Preserve application data and shared
SDK/NuGet caches. See [development](DEVELOPMENT.md) and [AGENTS.md](../AGENTS.md).
