> **File name** `settings.md`<br>
> **Full path** `E:\vibekit\Apps\MarkVibe\docs\settings.md`<br>
> **Path relative to the MarkVibe application root** `docs/settings.md`

# Settings and workspace resumption

C# `Preferences` owns settings. The native WinUI dialog updates validated values
and the desktop model publishes renderer appearance snapshots. Settings are stored
in `desktop-settings.json` under `%LOCALAPPDATA%\MarkVibe\Windows`, or the
portable `data/` directory. WebView2 owns only its renderer profile.

Settings has three categories. Appearance groups the theme, graph palette,
fonts, Acrylic and language. Document groups reading, workspace and file
association settings. AI groups providers and connection tests. Appearance and
Document use two columns when there is room and a single scrolling column in a
narrow window. All three tabs use the same viewport at a given window size.
Save feedback, Reset and Close stay outside the scrolling content.

The sidebar has two searches. Pages searches the Markdown contents of the
workspace, shows progress and keeps matching files in their folder hierarchy.
Outline searches the open document. Select an excerpt or use the previous and
next controls to move the highlight in the reader or source view.

| Setting | Purpose |
| --- | --- |
| Appearance and language | System, light/dark and available translations |
| UI font | Native application typeface |
| Graph font | Independent typeface for graph labels and cards |
| Document font | Reader and export typeface, with the active document theme as the default |
| Map colors | Ten graph palettes with light and dark variants |
| Acrylic map surfaces | Optional material for the graph, toolbar and inspector |
| Document presentation and themes | Technical, Editorial or Compact; independent light/dark document and code choices |
| Hyperlink underline and reading size | Reader/export link decoration and bounded reading scale |
| Lightweight source | Read-only fallback instead of Monaco |
| Remember last workspace | Local root, document, anchor and source-based reading position |
| AI provider | Explicit API-key or Windows Codex CLI selection, without automatic fallback |

Folder reader choices can override the global document font and other reader
appearance settings without writing into the source folder. The font catalog
is `src/MarkVibe.Core/Presentation/ui-fonts.json`. Windows offers its installed
families without downloading fonts.

The Markdown reader starts with **Ivory** paper, with a darker version for dark mode.
Choose **Paper** from the document context menu to preview ten textures in the
native gallery. **Apply paper** saves the choice for this workspace. Choose **Theme
background** for a plain reading surface. Contrast themes use the system
background for legibility.

WinUI uses the Windows accent for selections, menu icons and active resize handles.
The Mica window and map toolbars follow the shell theme. The native graph
uses the same appearance and respects system motion settings. Acrylic on the
map is off by default and falls back to solid fills when Windows disables
transparency. The model sends document and code themes to WebView2. Settings,
About and confirmation dialogs are native WinUI surfaces.

Windows reopens the remembered local workspace when permitted and accessible.
`--open` takes precedence. Disabling remembering clears local session metadata;
source files and map sidecars remain intact. Reset restores preference defaults.

The Markdown association action opens Windows Default apps. Effective status is
queried separately for `.md` and `.markdown`; portable/source copies cannot register
an installed handler. This action remains available after declining installation setup.

AI validation is explicit. OpenAI Test sends one short prompt through Responses
and replaces the saved user environment key only after a completed assistant
reply. Codex Test uses a synthetic CLI request. Both tests can consume account
usage and send no document content. Opening the assistant can load available
models. Reading and export do not send document contents. See [AI](ai-editing.md)
and [security](security.md). The retired browser application's localStorage is
not read, migrated or removed.

> **File name** `settings.md`<br>
> **Full path** `E:\vibekit\Apps\MarkVibe\docs\settings.md`<br>
> **Path relative to the MarkVibe application root** `docs/settings.md`
