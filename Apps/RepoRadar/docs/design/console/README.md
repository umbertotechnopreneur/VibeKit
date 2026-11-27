# Repo Radar terminal design

Repo Radar uses a radar sweep and a monospace wordmark at the top of the terminal. Selection uses a bright cyan edge, a filled row and a triangular marker. Status colors describe Git evidence independently of focus. Author credit remains in the header and About.

The [dashboard](../../screenshots/dashboard.png), [repository table](../../screenshots/repositories.png), [repository info](../../screenshots/repository-info.png) and [About](../../screenshots/about.png) were captured from the Windows terminal application with example repositories.

| View | Design |
| --- | --- |
| First scan | Show the chosen root and the local-only option before inspecting repositories. |
| Folder and file picker | Place the drive tree beside the selected folder at wide sizes. |
| Dashboard | Show aligned repositories first. Give paths and branch counts room on wide tiles. |
| Repositories | Use a table for names, paths, branches and commit counts. |
| Repository details | Grow the dialog with the terminal so branch names and paths can remain visible. |
| Repository Info | Explain each verified branch difference in plain English. Identify observations that still need a remote check. |
| Scan and fetch | Show the current path, work completed and a progress indicator. |
| About | Show the Repo Radar wordmark, author and software credits. |

See the [application guide](../../../README.md) for keyboard bindings and the [design prompts](prompts.json) for mockups with example data.
