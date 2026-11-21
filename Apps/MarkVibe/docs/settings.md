> **File name** `settings.md`<br>
> **Full path** `E:\vibekit\Apps\MarkVibe\docs\settings.md`<br>
> **Path relative to the MarkVibe application root** `docs/settings.md`

# Settings and workspace resumption

C# `Preferences` owns settings. The native WinUI dialog updates validated values
and the desktop model publishes renderer appearance snapshots. Settings are stored
in `desktop-settings.json` under `%LOCALAPPDATA%\MarkVibe\Windows`, or the
portable `data/` directory. WebView2 owns only its renderer profile.

| Setting | Purpose |
| --- | --- |
| Appearance and language | System, light/dark and available translations |
| UI font and acrylic | Installed application fonts and native backdrop |
| Document presentation and themes | Technical, Editorial or Compact; independent light/dark document and code choices |
| Hyperlink underline and reading size | Reader/export link decoration and bounded reading scale |
| Lightweight source | Read-only fallback instead of Monaco |
| Remember last workspace | Local root, document, anchor and source-based reading position |
| AI provider | Explicit API-key or Windows Codex CLI selection, without automatic fallback |

Folder reader choices override global appearance without writing into the source
folder. The supported UI font catalog is `src/MarkVibe.Core/Presentation/ui-fonts.json`;
Windows filters it against installed families. No fonts are downloaded.

WinUI applies native themes; the model sends document and code themes to WebView2.
Map presentation uses its native palette and system motion settings. Settings,
About and confirmation dialogs are native WinUI surfaces.

Windows reopens the remembered local workspace when permitted and accessible.
`--open` takes precedence. Disabling remembering clears local session metadata;
source files and map sidecars remain intact. Reset restores preference defaults.

The Markdown association action opens Windows Default apps. Effective status is
queried separately for `.md` and `.markdown`; portable/source copies cannot register
an installed handler. This action remains available after declining installation setup.

AI validation is explicit. API validation checks the model-list endpoint before
replacing the saved user environment key. Codex validation uses a synthetic CLI
request and may consume account usage. Opening the assistant can load available
models. Reading and export do not send document contents. See [AI](ai-editing.md)
and [security](security.md). The retired browser application's localStorage is
not read, migrated or removed.

> **File name** `settings.md`<br>
> **Full path** `E:\vibekit\Apps\MarkVibe\docs\settings.md`<br>
> **Path relative to the MarkVibe application root** `docs/settings.md`
