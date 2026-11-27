# Implementation status

MarkVibe provides a WinUI 3 Windows desktop app. On Windows, Win2D draws the link graph and WebView2 hosts the
Markdown reader with Mermaid, Monaco and the isolated PDF renderer. Application
code is MIT licensed by Umberto Giacobbi.

## Current capabilities

- Local Markdown files and folders, with relative links, local images and
  Mermaid diagrams; Read, read-only Source, Split and Map views.
- Workspace refresh, remembered reading position, native file-change prompts,
  themes, fonts, languages and saved shell layout.
- A bounded metadata/link catalog, document map search, arrangements, relation
  lenses, routes, previews, comparison, navigation trails and saved viewpoints.
- Schema-4 map layouts, with revision-checked atomic sidecar writes through native file services.
- Optional OpenAI API or Windows Codex CLI editing of a document or selection,
  explicit reference collection, cancellation, bounded local undo/redo and copy saving.
- A shared document revision for reading, source, metadata and export. AI replies
  do not overwrite workspace originals.
- Local current-page HTML/PDF export, native Windows pickers and direct PDF saving.
- Windows MSIX, MSI and portable packaging. Local delivery defaults to Release x64
  MSIX; see [packaging](windows-packaging.md).

Ink and sticky-note editing are unsupported. Existing annotation files remain
untouched. Workspace full-text search, dedicated macOS/Linux frontends are not implemented.

See [architecture](architecture.md) for ownership and data flow, and
[active work](../.github/tasks/todo.md) for remaining tasks. Tests and interactive
checks require a current explicit request. Microsoft Store submission requires separate authorization.
