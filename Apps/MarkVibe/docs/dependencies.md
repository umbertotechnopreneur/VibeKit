> **File name** `dependencies.md`<br>
> **Full path** `E:\vibekit\Apps\MarkVibe\docs\dependencies.md`<br>
> **Path relative to the MarkVibe application root** `docs/dependencies.md`

# Dependency and compatibility decisions

Dependency versions and licenses are defined by the project files, lockfiles and
third-party notices. The SDK selection follows `global.json`.

| Dependency | Pin | Purpose | Upstream licensing context |
| --- | --- | --- | --- |
| .NET SDK | >=10.0.402, stable .NET 10 | C# 14 / .NET 10; latest installed stable SDK | .NET licensing; review distribution components |
| Markdig | 1.4.0 | Managed Markdown AST and HTML | BSD-2-Clause |
| Microsoft.WindowsAppSDK | 1.8.260921001 | WinUI 3 desktop shell | See packaged third-party notices |
| Microsoft.Graphics.Win2D | 1.4.0 | Native document map rendering | See packaged third-party notices |
| Microsoft.Web.WebView2 | 1.0.3179.45 (resolved by Windows App SDK) | Embedded document renderer and direct PDF API | SDK LICENSE.txt: permissive three-clause terms; separate installed runtime |
| DOMPurify | 3.4.16 | Independent DOM/SVG sanitation | Apache-2.0 option selected from MPL-2.0 OR Apache-2.0 |
| Monaco editor | 0.57.0 | Lazy read-only source editor | MIT |
| Mermaid | 12.1.0 | Strict diagrams | MIT |
| elkjs (via Mermaid) | 0.9.3 | Included diagram layout module | EPL-2.0; retained license and versioned source availability |
| highlight.js | 11.12.0 | Rendered code highlighting | BSD-3-Clause |
| esbuild | 0.28.2 | Local client bundling | MIT, development-only |

NuGet references use exact range syntax; npm dependencies use exact version
strings. npm and NuGet lockfiles are maintained. Use locked restores and Node
>=22.12 for builds. .NET Runtime 10.0 is a separately installed prerequisite.
Dependency advisory remediation is tracked in the active task list.

The bundle script retains legal-comment files, emits a build manifest and copies package-level notices for modules included by the actual build graph. Distribution includes the approved MIT application license and available NuGet notices. MarkVibe's MIT license cannot replace dependency obligations.

## Application license and included components

My application code remains MIT. Most resolved components use MIT, BSD, ISC,
Apache-2.0, NCSA or public-domain terms; the included ELK module remains EPL-2.0.
It is not relabeled MIT. [EPL-2.0 section 3](https://www.eclipse.org/legal/epl/epl-v20.html)
requires retaining its license and telling recipients how to obtain its source.
The [distribution notices](../THIRD-PARTY-NOTICES.txt) provide the elkjs 0.9.3
source archive, its build instructions, the ELK baseline and the upstream change
identified by the [release notes](https://github.com/kieler/elkjs/releases/tag/0.9.3).
esbuild retains external legal comments and copies its complete license.

DOMPurify's Apache-2.0 alternative is selected explicitly. The transitive khroma
2.1.0 package has no license field in package.json but includes an MIT `license`
file; the bundle copies that file. Do not infer that an absent metadata field
means absent license terms.

WebView2's NuGet SDK license is copied with the Windows payload. The Evergreen
Runtime is an installed prerequisite, not a bundled browser redistribution.
OpenAI logos remain subject to their brand terms, and theme assets retain NCSA,
MIT and CC0 notices. The approved VibeWare logo is unchanged MIT-covered artwork
from the same copyright holder; trademark rights remain distinct. This review
documents redistribution boundaries, not blanket ownership of third-party code.

## Why this architecture

The Windows host uses the WebView2 SDK resolved in its package lock and the separately installed Evergreen Runtime. Host operations follow [WebView2 security guidance](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/security) and the official [PDF printing APIs](https://learn.microsoft.com/en-us/microsoft-edge/webview2/how-to/print).

WinUI 3 owns the shell, Win2D owns map presentation, and Core owns parsing and
revision state. [Runtime diagram](architecture.md)

Monaco loads on demand as a module. Mermaid loads on demand in a self-contained sandboxed frame. The native document renderer loads images lazily and runs entirely from bundled local assets. The renderer is not a standalone website.

## Primary sources

- [Markdig 1.4.0](https://www.nuget.org/packages/Markdig/1.4.0)
- [DOMPurify 3.4.16](https://github.com/cure53/DOMPurify/releases/tag/3.4.16)
- [Monaco 0.57.0](https://github.com/microsoft/monaco-editor/releases/tag/v0.57.0)
- [Mermaid 12.1.0](https://github.com/mermaid-js/mermaid/releases/tag/mermaid@12.1.0)
- [highlight.js 11.12.0](https://github.com/highlightjs/highlight.js/releases/tag/11.12.0)
- [esbuild 0.28.2](https://github.com/evanw/esbuild/releases/tag/0.28.2)

> **File name** `dependencies.md`<br>
> **Full path** `E:\vibekit\Apps\MarkVibe\docs\dependencies.md`<br>
> **Path relative to the MarkVibe application root** `docs/dependencies.md`
