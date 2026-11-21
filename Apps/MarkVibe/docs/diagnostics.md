> **File name** `diagnostics.md`<br>
> **Full path** `E:\vibekit\Apps\MarkVibe\docs\diagnostics.md`<br>
> **Path relative to the MarkVibe application root** `docs/diagnostics.md`

# Local diagnostics

Windows builds write structured JSON Lines logs under
`%LOCALAPPDATA%\MarkVibe\Windows\logs`. Portable builds use the adjacent
`data\logs` directory. An explicit `--data-dir` places `logs` below that directory.

Open the default folder from PowerShell:

```powershell
explorer "$env:LOCALAPPDATA\MarkVibe\Windows\logs"
```

Each launch gets its own timestamped file. Files rotate at approximately 1 MiB;
the logger retains up to eight files and never deletes other file patterns.
Logs remain local. No debug port or additional package is required.

Records cover native startup/shutdown, WebView2 initialization, navigation,
renderer failures, unhandled JavaScript errors and renderer console errors.
Native operation failures record exception types, HRESULTs and method names.
Console diagnostics extract exception identifiers and missing XAML resource
names; they do not dump console arguments, exception messages, source paths,
documents, credentials or request/response bodies. The standalone browser edition is retired.

A successful `webview.navigation` record confirms that the local entry page
loaded. The renderer handshake and document rendering follow as separate
steps. Disk permission failures in logging never prevent the app from starting.

## Explicit checks

`scripts/check.ps1 -Profile Rapida` runs Core, Native and client checks.
`InterfacciaWindows` runs the native UI smoke harness; `Completa` runs both.
The Windows profile requires `MARKVIBE_WINDOWS_EXECUTABLE` to point to the
current source-built Release x64 executable. It opens synthetic Markdown in an
isolated application profile, uses WinApp UI Automation and cleans its fixture.
It does not inspect credentials, call AI or capture the desktop. No HTML/JSON
validation journals are generated. GUI acceptance remains with the owner.

> **File name** `diagnostics.md`<br>
> **Full path** `E:\vibekit\Apps\MarkVibe\docs\diagnostics.md`<br>
> **Path relative to the MarkVibe application root** `docs/diagnostics.md`
