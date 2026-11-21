> **File name** `browser-capabilities.md`<br>
> **Full path** `E:\vibekit\Apps\MarkVibe\docs\browser-capabilities.md`<br>
> **Path relative to the MarkVibe application root** `docs/browser-capabilities.md`

# Browser capabilities

MarkVibe detects capabilities at runtime. A browser name alone does not guarantee
that a device's file picker exposes the required files and paths.

| Workspace mode | Required capability | Behavior |
| --- | --- | --- |
| Connected folder | Secure context and `showDirectoryPicker` | Reads through a retained directory handle |
| Imported folder | Directory input with usable `webkitRelativePath` values | Opens a snapshot; reselect after restart |
| Selected files / ZIP | File input | Opens a snapshot; reselect after restart |
| Windows desktop | Native provider | OS pickers, session persistence and direct PDF saving |

If a picker does not expose relative paths, select related files or a ZIP.

## Permission and persistence

```mermaid
flowchart TB
    accTitle: Browser workspace resumption
    accDescr: Connected folders reopen when the existing read grant is present. Imported snapshots and expired permissions require an explicit user action before the reading position can be restored.
    START["Open the browser edition"]
    MODE{"Which workspace was remembered?"}
    GRANT{"Connected folder still has read access?"}
    PICK["Continue where you left off<br/>Connect or reselect the workspace"]
    OPEN["Reopen workspace and restore page, anchor and position"]
    CHOOSE["Open the initial workspace view"]
    START --> MODE
    MODE -->|"Connected folder"| GRANT
    MODE -->|"Imported folder, files or ZIP"| PICK
    MODE -->|"None"| CHOOSE
    GRANT -->|"Already granted"| OPEN
    GRANT -->|"Unavailable"| PICK
    PICK -->|"Same workspace is available again"| OPEN

    classDef stage fill:#e6effb,stroke:#315a92,color:#18334f
    classDef decision fill:#dcf0e9,stroke:#20766a,color:#143e38
    classDef prompt fill:#fff0d8,stroke:#9b6218,color:#51330e
    class START,OPEN,CHOOSE stage
    class MODE,GRANT decision
    class PICK prompt
```

Connected folders open in read mode. Enumeration and reads query permission.
Startup resumes only with an existing read grant; the explicit reopen action can
request read permission. A stored IndexedDB handle does not guarantee a retained grant.

Imported folders, files and ZIPs require reselection after restart, then restore
the remembered page and position. Settings can disable and clear that state.
The map's explicit **Save layout** action can request read/write access to
`.markvibe-map.json`, after which layout edits autosave. Imported layouts download
as separate files. Ink and sticky-note editing are unsupported.

## Source view and mobile behavior

Monaco is a progressive desktop enhancement. A coarse pointer, narrow source pane,
limited reported memory or editor failure selects the lightweight source view.
The fallback retains select/copy, find, line numbers and source-position navigation.

Mobile reading uses single panes, full-width sidebar switching and touch-sized
buttons. Split requires a wide viewport. File providers, permissions, safe areas,
virtual keyboards and orientation changes depend on the device.

## API references

- [Directory picker](https://developer.mozilla.org/en-US/docs/Web/API/Window/showDirectoryPicker)
- [Directory input and relative paths](https://developer.mozilla.org/en-US/docs/Web/API/HTMLInputElement/webkitdirectory)
- [Read permission querying](https://developer.mozilla.org/en-US/docs/Web/API/FileSystemHandle/queryPermission)

> **File name** `browser-capabilities.md`<br>
> **Full path** `E:\vibekit\Apps\MarkVibe\docs\browser-capabilities.md`<br>
> **Path relative to the MarkVibe application root** `docs/browser-capabilities.md`
