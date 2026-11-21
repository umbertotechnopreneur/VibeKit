> **File name** `AGENTS.md`<br>
> **Full path** `E:\vibekit\Apps\MarkVibe\AGENTS.md`<br>
> **Path relative to the VibeKit repository** `Apps\MarkVibe\AGENTS.md`

# MarkVibe working agreements

The [VibeKit agreements](../../AGENTS.md) also apply. The Windows app uses WinUI 3 for the shell and Win2D for the map. C# models own files, Markdown parsing, revisions, navigation, catalog and map state. WebView2 hosts the Markdown and Mermaid reader, Monaco source view and isolated PDF renderer. The standalone web edition has its own OpenSilver shell and graph renderer. Edit maintained sources. Generated `wwwroot/js/`, `bin/` and `obj/` are build products. See the [architecture](docs/architecture.md) and [map layout contract](docs/map-layout.md).

## Windows delivery and verification

- A build request on this machine means **Release x64 MSIX**, built from current source, installed and launched. An explicit MSI request also requires a real Windows Installer artifact. Keep the installed distribution type unless the owner requests a change. Debug, ARM64 and x86 require explicit requests.
- For that Release delivery, the owner authorizes closing MarkVibe, discarding unsaved in-app work, trusting only its generated development certificate in `LocalMachine/TrustedPeople`, replacing the previous MarkVibe package as needed and removing the retired Debug installation. Preserve source documents, application data and unrelated applications and certificates.
- Use the latest installed stable .NET 10 SDK (minimum 10.0.402) and Node 22.12 or newer. Obtain authorization for SDK installation. Keep the Visual Studio `.sln` and CLI `.slnx` project lists aligned. Preserve design-time, IDE and CLI bundle behavior. See [Visual Studio](docs/visual-studio.md).
- The packaging script records each distribution in its own directory under `artifacts/`. Preserve existing generated material unless the owner requests its removal. Keep packaging work and outputs under this application. See [Windows packaging](docs/windows-packaging.md).
- Run tests only when explicitly requested for the current task. Use synthetic fixtures and the appropriate core, native, client, browser or Windows checks. The owner performs GUI acceptance. Driving input, focusing or moving windows, desktop capture and interactive checks require an explicit request beyond installation and launch.
- Store packaging requires the real Partner Center Name, Publisher and display name plus an explicitly requested configuration and version. Publication requires separate authorization. See [Store prerequisites](docs/windows-store.md).

## Code and identity

- Write repository content in English. Use four-space C#, two-space JavaScript, existing XAML indentation and LF. Preserve nullable annotations, ES modules, local SVG icons and accessible button names. Use PascalCase members, `_camelCase` fields and camelCase wire properties.
- Document every type and method with its purpose and contract. Use XML for C# and documentation comments for JavaScript and PowerShell. Explain non-obvious ordering, invariants and cancellation. Use descriptive names, cohesive methods, separate statements and readable control flow. Readability edits preserve behavior and public contracts.
- Maintain only current application-owned schemas and bridge commands. Retire obsolete paths when changing both ends. Migrations or compatibility layers require an explicit request. Preserve input validation, cancellation, platform capabilities, security boundaries and user files.
- Keep the `VBWR B` and `VBWR E` markers and MIT copyright for Umberto Giacobbi on maintained source, scripts and markup. Document AI and human contributions supported by evidence. Preserve third-party notices, valid JSON and lockfiles, the README footer and the [AI disclosure](AI-DEVELOPMENT-DISCLOSURE.md).
- Follow the VibeWare publication, README footer and source-header templates. Keep the unmodified transparent logo, its proportions, the separate app icon and the keyboard-accessible badge. Retain **Human intent, AI execution, and plenty of tokens** and the [canonical manifesto](https://umbertogiacobbi.biz/vibeware/manifesto).
- Keep About, Terms, Privacy, VibeWare and Manifesto links visible without clipping. Reserve footer space, fade over three seconds after 30 seconds and reveal links on hover or focus while in use. About includes the installed version, purpose, first-person responsibility statement, both GitHub projects, personal site, Terms, Privacy and an accessible Close. Keep product data handling distinct from shared legal terms, licensing and signing.

## Changes and Git

- Ask before creating each branch or worktree. Preserve unrelated work and unpublished commits. Respect read limits and keep credentials, private documents, local configuration and generated files out of Git.
- Deliver code through focused pull requests to the protected default branch. Prefer squash merge. Approval and merge require explicit owner authorization and applicable branch checks. Formal self-review is unnecessary. Use imperative subjects, update affected locks and report checks actually run or that tests were not requested.
- Documentation-only edits require no build, test, CI dispatch or PR. On an explicit request for Git delivery, commit only the authorized documents on the current branch with `[skip ci]` and push. If protection blocks it, report the restriction and leave the branch rules intact.
- Keep [active work](.github/tasks/todo.md) actionable and [the latest delivery](.github/tasks/archive.md) short. Replace the previous summary. Report validation in the conversation and omit persistent lessons, CI or validation journals.

> **File name** `AGENTS.md`<br>
> **Full path** `E:\vibekit\Apps\MarkVibe\AGENTS.md`<br>
> **Path relative to the VibeKit repository** `Apps\MarkVibe\AGENTS.md`
