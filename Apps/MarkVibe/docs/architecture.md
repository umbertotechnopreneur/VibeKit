> **File name** `architecture.md`<br>
> **Full path** `E:\vibekit\Apps\MarkVibe\docs\architecture.md`<br>
> **Path relative to the MarkVibe application root** `docs/architecture.md`

# MarkVibe architecture

[Map layout](map-layout.md) · [Export](desktop-and-export.md) · [AI editing](ai-editing.md) · [Windows packaging](windows-packaging.md)

## Desktop modules and boundaries

WinUI 3 provides the desktop shell. Win2D draws the document map. The embedded
WebView2 surfaces render Markdown, Mermaid and Monaco, and print the prepared
document to PDF. C# owns file access and the application state that feeds each
surface.

```mermaid
flowchart TB
    accTitle: Desktop modules grouped by runtime boundary
    accDescr: WinUI and Win2D consume managed state. A scoped bridge feeds document content to WebView2. Native files own local storage.
    subgraph HOST["MarkVibe.Windows · .NET desktop process"]
        direction TB
        subgraph UI["Native presentation"]
            SHELL["WinUI 3<br/>Window, toolbar, Pages, Outline, context menus, dialogs"]
            DRAW["Win2D<br/>Map cards, edges, hit tests and camera"]
        end
        subgraph MODEL["Managed application state"]
            SESSION["DesktopWorkspaceModel<br/>Workspace, effective document, navigation and preferences"]
            MAP["WindowsMapModel<br/>Layout, selection, filters and saved views"]
            AI["WindowsAiSession<br/>Requests, progress and proposals"]
        end
        BRIDGE["DocumentWebView<br/>Renderer lifecycle and validated message channel"]
        CORE["MarkVibe.Core<br/>MarkdownParser, DocumentCatalog, DocumentSession and map services"]
        FILES["LocalWorkspaceProvider → NativeFiles<br/>Workspace inventory, bounded reads and atomic writes"]
    end
    subgraph WEB["Embedded WebView2 surfaces · loaded on demand"]
        READER["Document renderer<br/>Markdown, Mermaid, Monaco, Find and selection"]
        PRINT["Isolated PDF renderer<br/>Prepared HTML and local print"]
    end
    DISK["Local files<br/>Markdown, images, map sidecar and session metadata"]
    PROFILE["Application profile<br/>Desktop preferences and pane layout"]

    SHELL <-->|"Commands and state"| SESSION
    SHELL <-->|"Requests and proposals"| AI
    DRAW <-->|"Gestures and map snapshot"| MAP
    SESSION -->|"Open, index and edit"| CORE
    MAP -->|"Query catalog and update layout"| CORE
    AI -->|"Resolve effective revisions"| SESSION
    SESSION -->|"Enumerate and read workspace files"| FILES
    SESSION -->|"Save settings"| PROFILE
    MAP -->|"Load and save map sidecar"| FILES
    SESSION <-->|"Document snapshots and gestures"| BRIDGE
    BRIDGE <-->|"Fixed-origin, versioned messages"| READER
    READER -->|"Prepared HTML through native host"| PRINT
    FILES <-->|"Validated file access"| DISK

    classDef native fill:#e6effb,stroke:#315a92,color:#18334f
    classDef model fill:#dcf0e9,stroke:#20766a,color:#143e38
    classDef web fill:#fff0d8,stroke:#9b6218,color:#51330e
    classDef storage fill:#ece8f7,stroke:#69518f,color:#302347
    class SHELL,DRAW,BRIDGE native
    class SESSION,MAP,AI,CORE model
    class READER,PRINT web
    class FILES,DISK,PROFILE storage
```

Blue marks native presentation and hosting. Green marks managed models and
services. Amber marks embedded renderers. Purple marks file access and storage.

| Module | Authoritative state or operation | What the view receives |
| --- | --- | --- |
| `DesktopWorkspaceModel` and `DesktopWorkspace` | Active provider and catalog, effective document, reading and preview history, Find and preferences | Selected document, navigation commands, and the state for Pages and Outline |
| `DocumentCatalog` and `DocumentSession` | Paths, titles, headings, block ranges, resolved links, statistics and unsaved revisions | Parsed document and catalog snapshots used by the reader, editor and map |
| `WindowsMapModel` and Core layout services | Selection, distance, route, exploration, card geometry and named views | The canvas projection and the inspector's document relationships |
| `WindowsAiSession` | Request lifecycle and proposals for a captured editing target | Request progress, reference information and revision actions |
| `NativeFiles` | Workspace path resolution, bounded reads, session metadata and revision-checked writes | Results of file commands and persistence |
| `DocumentWebView` and client renderer | Rendered DOM, image leases, scroll offsets, text selection and local editor surface | Rendered document content and source-backed interaction events |

The WinUI controls hold focus, geometry and other presentation state. Preferences,
layout and folder reading choices persist in `desktop-settings.json`. Each
`DesktopWorkspace` has its own provider, catalog and AI reference collection.

## Open a folder and render a document

```mermaid
sequenceDiagram
    accTitle: Opening, indexing and rendering a Markdown workspace
    accDescr: The workspace model opens the chosen page, builds a background index, and sends parsed document data to a renderer on first use.
    participant UI as WinUI 3
    participant WS as Workspace model
    participant FS as NativeFiles
    participant Cat as Catalog and parser
    participant Web as WebView2

    UI->>WS: Open folder or file
    WS->>FS: Find the file or README.md
    alt A priority page is available
        WS->>Cat: Seed and parse page
        par Present the page
            WS-->>UI: Publish the parsed page
            UI->>Web: Render Markdown snapshot
        and Enumerate the folder
            WS->>FS: List paths and metadata
            FS-->>WS: Return inventory
        end
    else Choose from the inventory
        WS->>FS: List paths and metadata
        FS-->>WS: Return inventory
        WS->>Cat: Seed catalog and parse first page
        WS-->>UI: Publish workspace and page
        opt An initial page exists
            UI->>Web: Render Markdown snapshot
        end
    end
    WS->>Cat: Index remaining pages
    Cat->>FS: Read within index budget
    Cat-->>WS: Progress and link metadata
    WS-->>UI: Refresh Pages and map
    opt Source or Split is selected
        UI->>Web: Source and pane state
        Web->>Web: Load Monaco and its worker
    end
```

Markdig parses bounded Markdown and restricts embedded HTML. The same syntax tree
provides heading IDs, block IDs, source ranges and resolved links. Outline uses
those coordinates. Find searches the active pane. Split synchronizes the reader
and editor through source lines and block ranges with a short feedback guard.

The index retains metadata, statistics and links within an 8 MiB source budget.
Pages outside a partial index can still be opened by path. A workspace opened
from a specific file can show that page while the full folder inventory finishes.

The local client bundle lives under the executable's `app/` directory. WebView2
loads it through a fixed virtual HTTPS origin. The message channel checks the
sender and validates a per-instance capability on native requests. The host
permits a fixed set of operations and resolves supported images inside the
active workspace. DOMPurify sanitizes HTML before display and export. Mermaid
runs in a sandboxed frame with an opaque origin and network access disabled.

### Document revisions

`DocumentCatalog` keeps edited `DocumentSession` objects independently of its
three-document parsed cache. A session tracks original, current and last-exported
source with a monotonically increasing revision. Its states are `Original`,
`Unsaved` and `Exported`. Undo and redo each retain at most 16 entries and
4,194,304 UTF-16 code units. Each catalog retains at most 16 edited sessions.
Saving a Markdown copy preserves the original file. Refresh retains unsaved
revisions for documents that disappear from disk.

## Document map and linked pages

```mermaid
flowchart TB
    accTitle: Document map rendering and relationship lookup
    accDescr: The model uses the catalog for both a bounded graph projection and a complete indexed relationship lookup. Reading and layout writes follow separate paths.
    CATALOG["DocumentCatalog<br/>Indexed documents, paths and Markdown links"]
    LAYOUT["MapLayoutSession<br/>Card positions, appearance and saved views"]
    MAP["WindowsMapModel<br/>Selected page, distance, route and exploration"]
    CANVAS["Win2D canvas<br/>Cards, edges, folders, zoom and minimap"]
    INSPECTOR["WinUI inspector<br/>Incoming pages and outgoing links with anchors"]
    PREVIEW["DesktopWorkspaceModel<br/>Preview path, anchor, history and return camera"]
    READER["DocumentWebView<br/>Markdown and Mermaid passage"]
    SIDECAR[".markvibe-map.json<br/>Revision-checked sidecar"]

    CATALOG -->|"Catalog and link graph"| MAP
    LAYOUT <-->|"Current layout and accepted edits"| MAP
    MAP -->|"Bounded canvas projection"| CANVAS
    CANVAS -->|"Select, drag or resize"| MAP
    MAP -->|"Complete known relationships for the selected page"| INSPECTOR
    INSPECTOR -->|"Read here or follow a link inside the preview"| PREVIEW
    CANVAS -->|"Read here or follow a connection"| PREVIEW
    PREVIEW -->|"Document and anchor"| READER
    MAP <-->|"Load and save through NativeFiles"| SIDECAR

    classDef native fill:#e6effb,stroke:#315a92,color:#18334f
    classDef model fill:#dcf0e9,stroke:#20766a,color:#143e38
    classDef web fill:#fff0d8,stroke:#9b6218,color:#51330e
    classDef storage fill:#ece8f7,stroke:#69518f,color:#302347
    class CANVAS,INSPECTOR native
    class CATALOG,LAYOUT,MAP,PREVIEW model
    class READER web
    class SIDECAR storage
```

Tree, Star, Circle, Grid and Folders arrange the catalog. Search matches titles
and paths. The inspector reads all indexed incoming pages and distinct outgoing
targets for the selection. It exposes additional rows in batches and preserves
anchors and unavailable targets. Win2D renders the bounded projection and
handles camera and pointer interactions. The managed preview history stores a
reading position and a map camera for returning to a passage. The sidecar saves
layout edits atomically after checking its revision. [Map layout](map-layout.md)

## Assistant

The user starts connection checks and editing requests. Each workspace can retain
up to 16 Markdown paths in its AI reference collection. The session resolves
their effective source revisions at request time, excludes the editing target
and reads at most 4 MiB of reference content in total. Reference statistics and
token estimates are computed locally. A complete edit updates the captured
local document revision. Restore Original recovers the original source. [AI editing and data handling](ai-editing.md)

## Export flow

```mermaid
flowchart TB
    accTitle: Windows HTML and PDF export
    accDescr: The renderer prepares sanitized HTML. A native file picker selects the destination. The chosen format determines whether the host writes HTML or prints a PDF.
    COMMAND["WinUI export command<br/>Choose HTML or PDF"]
    DOC["Effective Markdown snapshot<br/>Managed document revision"]
    HTML["Local document renderer<br/>Sanitize HTML and embed validated assets"]
    SAVE["DesktopDocumentFiles<br/>Validate export and pick destination"]
    WRITE["Atomic file write<br/>Save prepared HTML"]
    PRINT["Isolated WebView2 print host<br/>Print prepared HTML locally"]
    HTMLFILE["HTML file"]
    PDFFILE["PDF file"]

    COMMAND -->|"Selected document and format"| DOC
    DOC -->|"Parsed HTML and workspace identity"| HTML
    HTML -->|"Prepared HTML and suggested filename"| SAVE
    SAVE -->|"HTML destination"| WRITE
    SAVE -->|"PDF destination"| PRINT
    WRITE --> HTMLFILE
    PRINT --> PDFFILE

    classDef native fill:#e6effb,stroke:#315a92,color:#18334f
    classDef model fill:#dcf0e9,stroke:#20766a,color:#143e38
    classDef web fill:#fff0d8,stroke:#9b6218,color:#51330e
    classDef storage fill:#ece8f7,stroke:#69518f,color:#302347
    class COMMAND,SAVE native
    class DOC model
    class HTML,PRINT web
    class WRITE,HTMLFILE,PDFFILE storage
```

Export validates and embeds supported images, including images below the fold.
It preserves same-page anchors and marks links to neighboring Markdown documents
as unavailable. Native file dialogs select the output destination. The isolated
PDF host has page scripts and web messaging disabled. [Export and native hosts](desktop-and-export.md)

## Build boundary

WinUI 3 is the only application frontend. The document renderer's maintained
assets live under `src/MarkVibe.Windows/Rendering/web`; JavaScript source lives
under `client`. There is no standalone browser application or WebAssembly host.
The native build bundles only renderer entrypoints into `obj/desktop-js` and
stages them under `app/`. Core and Native remain UI-independent libraries.

Use the stable .NET 10 SDK configured in `global.json` and supported Node version.
Distribution shares the separately installed .NET Runtime 10.0 while retaining
required Windows App SDK application libraries. See [builds](visual-studio.md).

> **File name** `architecture.md`<br>
> **Full path** `E:\vibekit\Apps\MarkVibe\docs\architecture.md`<br>
> **Path relative to the MarkVibe application root** `docs/architecture.md`
