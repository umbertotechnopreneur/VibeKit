<!-- VBWR B
  Project: MarkVibe | Creator: Umberto Giacobbi
  Created with AI: OpenAI Codex; Visual Studio solution and debugger guidance; 2026-10-16
  Human guidance: compile the complete app and debug desktop and browser code
  Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
VBWR E -->

> **File name** `visual-studio.md`<br>
> **Full path** `E:\vibekit\Apps\MarkVibe\docs\visual-studio.md`<br>
> **Path relative to the VibeKit repository** `Apps\MarkVibe\docs\visual-studio.md`

# Build and debug in Visual Studio

Open **`MarkVibe.sln`**, select **Debug / Any CPU**, then choose **MarkVibe Desktop**
in the startup dropdown. Press **F5** to build and debug the Windows host. The
desktop runtime defaults to **win-x64**, even though the managed solution platform
is Any CPU. This runs a development executable; it does not install a package.

`MarkVibe.slnx` contains the same seven projects and remains the solution used by
the repository's command-line scripts. Keep both project lists aligned when adding
or removing a project. Always name the intended solution in command-line commands.

## Existing prerequisites

- **Visual Studio 2026 (18.x)** with C#/.NET and WinUI desktop support. Browser debugging also
  uses its ASP.NET/web tooling and a supported Edge or Chrome browser. Microsoft
  supports targeting .NET 10 starting with VS 18.0. The 10.0.4xx SDK band ships with
  VS 18.9. See Microsoft's
  [SDK and Visual Studio support matrix](https://learn.microsoft.com/en-us/dotnet/core/porting/versioning-sdk-msbuild-vs).
- **Latest installed stable .NET 10 SDK**, minimum **10.0.402**. `global.json`
  allows stable SDK roll-forward; do not silently retarget the projects.
- **Node.js >=22.12**, npm and **PowerShell 7 (`pwsh`)** available in the environment
  inherited by Visual Studio. Check `node --version` in its terminal. If an older
  Node takes precedence, reopen Visual Studio from an environment whose PATH selects
  the existing supported Node installation.
- **WebView2 Runtime** for the desktop renderer. NuGet restores the Windows App SDK,
  Win2D and Windows SDK reference packages for the desktop. The web edition restores its OpenSilver tools separately.

## What Build Solution includes

| Project | Purpose |
| --- | --- |
| `MarkVibe.Windows` | WinUI 3 executable, Win2D map, WebView2 document renderer, native file dialogs and printing |
| `MarkVibe.Native` | Native file access and persisted workspace/session state |
| `MarkVibe.Browser` | Blazor WebAssembly bootstrap, OpenSilver runtime and local client assets |
| `MarkVibe` | OpenSilver C#/XAML application and controls for the web edition |
| `MarkVibe.Core` | Markdown, catalog, navigation and map state |
| `MarkVibe.Core.Checks` | Core executable checks, compiled but never run by Build Solution |
| `MarkVibe.Native.Checks` | Native executable checks, compiled but never run by Build Solution |

On a normal Visual Studio build, the desktop project prepares the locked npm
dependencies and bundles its renderer directly from the client modules. The web
edition checks its own asset fingerprint through `PrepareVisualStudioClient` and
prepares its bundle as needed. Both hosts enter MSBuild when an external client
module changes. Design-time evaluation skips npm and bundling.

The Windows project stages its page, renderer bundle and shared assets under
the executable's `app/` directory. WebView2 serves this renderer through a local
virtual host. Run the browser profile separately when developing the web edition.

To build the desktop alone from the command line:

```powershell
pwsh -NoProfile -File scripts/prepare-client.ps1 -DependenciesOnly
dotnet restore src/MarkVibe.Windows --locked-mode
dotnet build src/MarkVibe.Windows -c Release --no-restore
```

The full solution also builds the web edition:

```powershell
pwsh -NoProfile -File scripts/prepare-client.ps1
dotnet restore MarkVibe.sln --locked-mode
dotnet build MarkVibe.sln -c Debug --no-restore
```

These commands compile the checks projects without executing them. Build Solution
also does not run the JavaScript/browser/Windows suites, create MSI/MSIX packages,
clean distribution artifacts or install the app. Distribution builds continue to
use [the packaging script](windows-packaging.md).

## Choose the process that owns the code

**MarkVibe Desktop** starts `MarkVibe.Windows` under the managed debugger. Use it
for breakpoints in WinUI controls, the workspace model, Win2D graph, WebView2
integration and native file operations.

**MarkVibe Browser** starts the `MarkVibe Debug` profile of `MarkVibe.Browser` at
`http://localhost:5080`. It opens a browser and configures the Blazor debugging
proxy through `inspectUri`. Use it for C# breakpoints in the shared OpenSilver UI
and Core code executed in WebAssembly. Wait for the debugger to attach before
triggering the relevant action. Browser storage and file access use the browser
providers, so this profile does not reproduce Windows-only file dialogs or printing.
See Microsoft's [Blazor debugging guide](https://learn.microsoft.com/en-us/aspnet/core/blazor/debug?view=aspnetcore-10.0)
and OpenSilver's [browser debugging instructions](https://doc.opensilver.net/documentation/how-to-topics/debug-in-browser.html).

The original `MarkVibe` browser profile remains available for command-line use and
does not open a browser automatically. Do not start both browser profiles at once
on the same port.

The desktop Debug configuration builds readable renderer modules and linked source
maps automatically. For web-edition JavaScript profiling, run `npm run bundle:debug`
before its Debug build. Run `npm run bundle` for optimized web Release assets.
`scripts/profile-graph.mjs` records local browser DevTools traces after indexing.
See [local profiling](diagnostics.md#graph-profiling) for commands and trace inspection.
Restart a running development server after rebuilding its WebAssembly assemblies.

If the shared startup profiles are not shown, use **Configure Startup Projects**
on the solution. Enable multi-project launch profiles in Visual Studio's options
if that installation requires it. Alternatively, set `MarkVibe.Windows` as the
single startup project. For browser debugging, set `MarkVibe.Browser` as startup
and select **MarkVibe Debug** in its launch dropdown. The two entries in
`MarkVibe.slnLaunch` each start one host; they do not start the executable checks.
See [Microsoft's launch-profile documentation](https://learn.microsoft.com/en-us/visualstudio/ide/how-to-set-multiple-startup-projects).

> **File name** `visual-studio.md`<br>
> **Full path** `E:\vibekit\Apps\MarkVibe\docs\visual-studio.md`<br>
> **Path relative to the VibeKit repository** `Apps\MarkVibe\docs\visual-studio.md`
