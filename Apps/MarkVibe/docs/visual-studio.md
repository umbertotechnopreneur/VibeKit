> **File name** `visual-studio.md`<br>
> **Full path** `E:\vibekit\Apps\MarkVibe\docs\visual-studio.md`<br>
> **Path relative to the MarkVibe application root** `docs/visual-studio.md`

# Build and debug

MarkVibe has one WinUI 3 application frontend. Open `MarkVibe.sln` in Visual
Studio 2026 with WinUI desktop support and choose **MarkVibe Desktop**. The
`.slnx` and `.sln` solutions contain the same five projects: Windows, Core,
Native, Core.Checks and Native.Checks. Checks are compiled, never run by Build.

Use the latest installed stable .NET 10 SDK, minimum 10.0.402, Node >=22.12 and
PowerShell 7. `global.json` controls SDK selection. Select supported existing
tools in the process environment; SDK installation needs owner authorization.
The installed WebView2 Runtime is required to run the document renderer.

```powershell
pwsh -NoProfile -File scripts/prepare-client.ps1 -DependenciesOnly
dotnet restore src/MarkVibe.Windows --locked-mode
dotnet build src/MarkVibe.Windows -c Release --no-restore
```

CLI builds can pass `-p:MarkVibeNodeExe=<absolute-node-executable>` when the child
process environment cannot resolve Node. The packaging script passes the selected
Node automatically. Do not embed workstation-specific paths in project files.

Normal desktop builds bundle the reader, Mermaid and Monaco from `client/` and
stage native-owned assets from `src/MarkVibe.Windows/Rendering/web` under `app/`.
Both IDE and CLI use this path. IDE builds prepare npm dependencies if needed;
design-time builds skip dependency preparation and bundling. External client
changes force MSBuild entry, while fingerprints avoid unnecessary rebundling.
The old browser startup profile and WebAssembly projects have been retired.

For installed delivery use [Windows packaging](windows-packaging.md): Release
x64 MSIX, installed and launched. Debug, additional architectures and automated
checks require their corresponding authorization. Build Solution alone does not
install, package or run tests.

Native breakpoints belong to `MarkVibe.Windows`, Core or Native. Renderer source
maps are generated only for explicitly requested Debug builds. Release rejects
smoke/debug-port options; do not enable a broad native bridge for test tooling.

> **File name** `visual-studio.md`<br>
> **Full path** `E:\vibekit\Apps\MarkVibe\docs\visual-studio.md`<br>
> **Path relative to the MarkVibe application root** `docs/visual-studio.md`
