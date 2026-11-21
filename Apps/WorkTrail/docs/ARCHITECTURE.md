> **File name** `ARCHITECTURE.md`<br>
> **Full path** `E:\vibekit\Apps\WorkTrail\docs\ARCHITECTURE.md`<br>
> **Path relative to the VibeKit repository** `Apps\WorkTrail\docs\ARCHITECTURE.md`

# WorkTrail architecture

The app is a desktop launcher with independent world-clock and astronomy windows. It has one application facade, one installation-scoped mutex and one same-user versioned named pipe.

## Runtime and data ownership

```mermaid
flowchart TB
    accTitle: WorkTrail window, presentation and Core boundaries
    accDescr: WinUI windows send intent to the Core facade and display projected state. Core owns settings, widget services and the same-user runtime protocol.
    CLIENT["Same-user runtime client"]
    subgraph APP["WorkTrail · Windows application"]
        UI["WinUI shell and widget windows<br/>Activation, placement and lifetime"]
        PRE["WorkTrail.Presentation<br/>Pure projections and dialog coordination"]
        FACADE["IWorkTrailApplication · Core facade<br/>Widget operations and serialized settings"]
        WIDGET["Core widget services<br/>World clocks, availability and astronomy"]
        PIPE["Runtime host<br/>Installation mutex and versioned named pipe"]
        STORE["LocalStore<br/>Atomic preference saves"]
    end
    LOCAL["Local profile<br/>Preferences and saved window state"]
    CATALOG["Bundled offline data<br/>City database and astronomy calendar"]

    UI -->|"Widget and settings commands"| FACADE
    FACADE -->|"Results and committed settings"| PRE
    PRE -->|"Display values and dialog state"| UI
    FACADE -->|"Clock, city and astronomy queries"| WIDGET
    WIDGET -->|"Read catalog data"| CATALOG
    FACADE <-->|"Load or commit settings"| STORE
    STORE <-->|"Read and atomically replace"| LOCAL
    CLIENT <-->|"Same-user requests and replies"| PIPE
    PIPE <-->|"Supported commands and results"| FACADE

    classDef native fill:#e6effb,stroke:#315a92,color:#18334f
    classDef model fill:#dcf0e9,stroke:#20766a,color:#143e38
    classDef external fill:#fff0d8,stroke:#9b6218,color:#51330e
    classDef storage fill:#ece8f7,stroke:#69518f,color:#302347
    class UI,PRE native
    class FACADE,WIDGET,PIPE,STORE model
    class CLIENT external
    class LOCAL,CATALOG storage
```

## Projects

| Project | Responsibility |
| --- | --- |
| `WorkTrail` | Windows composition root, passive WinUI views and window lifecycle |
| `WorkTrail.Core` | Widget operations, astronomy, city catalog, settings and Windows integration |
| `WorkTrail.Presentation` | Pure projections, dialog queue and window presentation state |
| `WorkTrail.Core.Tests` | Widget services, persistence, runtime protocol and shared contracts |
| `WorkTrail.Presentation.Tests` | Pure presentation and retained window contracts |

`ExcelPlanner/` is a standalone prototype outside the solution.

## Startup and lifetime

```mermaid
sequenceDiagram
    accTitle: WorkTrail activation and saved widget restoration
    accDescr: A repeat launch routes to the existing instance. The owning instance loads settings, completes Welcome if needed and restores saved widgets.
    participant Start as Windows
    participant Entry as Entry point
    participant Run as Runtime
    participant Settings as Settings
    participant UI as App windows

    Start->>Entry: Launch from Windows
    alt App instance already registered
        Entry->>Run: Redirect activation
        Run->>UI: Apply the activation
    else Start the owning instance
        Entry->>Run: Lock this install and compose Core
        Run->>Settings: Load preferences and windows
        Settings-->>Run: Return saved settings
        alt Welcome is incomplete
            Run->>UI: Show Welcome and offline cities
            alt Confirm a home city
                UI->>Settings: Save initial city and widgets
                Settings-->>Run: Return completed preferences
                Run->>UI: Restore saved widgets
            else Dismiss Welcome
                UI-->>Run: Keep setup incomplete
            end
        else Welcome is complete
            Run->>UI: Restore saved widgets
            Note over Run,UI: Windows sign-in starts the launcher hidden
        end
    end
```

`Program` redirects repeated Windows launches to the current application. `App` acquires the existing hashed-installation runtime mutex before constructing `WorkTrailApplication`. Other clients connect through the same-user named pipe. Protocol version 9 exposes only widget, settings, window, startup, product-link and diagnostic operations.

The factory does not construct capture services, input hooks, activity storage, search indexes, AI clients or timesheet workers. `LocalStore` reads and atomically saves preferences; it never opens existing activity databases. The astronomical agenda reads the bundled calendar catalog directly. The separate read-only world-city database remains in use.

Before restoring widgets on first launch, Welcome asks for a home city through the shared offline city selector. Core suggests a city from the Windows time zone. Confirmation atomically saves the home city, clock selection, agenda observer and completion flag; dismissing Welcome leaves setup incomplete. Existing saved clocks and window placement are retained.

Closing the launcher offers an Acrylic choice to hide it in the notification area, exit the app or cancel. Hiding leaves independent widgets open. Explicit app exit persists the open widget session and placements before shutting down the runtime. Once setup is complete, a Windows sign-in launch starts hidden and restores saved widgets. Shared title bars float above content without reserving layout space, including when auto-hide is disabled.

```mermaid
flowchart LR
    accTitle: WorkTrail launcher close choices
    accDescr: Hide leaves the runtime and widgets active. Exit saves the widget session before shutdown. Cancel returns to the launcher.
    CLOSE{"Close the launcher"}
    HIDE["Hide in notification area<br/>Keep runtime and widgets active"]
    SAVE["Persist open widgets and placements"]
    SHUTDOWN["Shut down the runtime"]
    CANCEL["Keep the launcher open"]
    CLOSE -->|"Hide"| HIDE
    CLOSE -->|"Exit"| SAVE --> SHUTDOWN
    CLOSE -->|"Cancel"| CANCEL

    classDef native fill:#e6effb,stroke:#315a92,color:#18334f
    classDef model fill:#dcf0e9,stroke:#20766a,color:#143e38
    classDef storage fill:#ece8f7,stroke:#69518f,color:#302347
    class CLOSE,HIDE,CANCEL native
    class SAVE storage
    class SHUTDOWN model
```

## Application boundary

Views bind DTOs, collect input and call `IWorkTrailApplication`. Keep file, environment, HTTP and OS work in Core services. Serialize settings mutations in the facade; publish a new immutable snapshot only after persistence succeeds. Preserve saved cities and window placement when unrelated settings change.

Report unexpected startup, persistence and interop failures through the durable local log and a visible error surface, including failures before Main exists. Keep credentials in environment variables and out of IPC diagnostics and logs.

Existing personal history and screenshots are not migrated or deleted by widget startup. Removed tracking commands and launch arguments fail explicitly.

> **File name** `ARCHITECTURE.md`<br>
> **Full path** `E:\vibekit\Apps\WorkTrail\docs\ARCHITECTURE.md`<br>
> **Path relative to the VibeKit repository** `Apps\WorkTrail\docs\ARCHITECTURE.md`
