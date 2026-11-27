# Repo Radar historical code review

**Reviewed November 22, 2026, Asia/Saigon.** This audit records the 0.2.3 baseline before the Git, viewport and branding updates. It covered the terminal UI, projections and persistence, Git and catalog operations, launchers, test code and the three packaging paths. That source tree had 24 C# files and 4,868 lines, including the self-tests.

The audit prioritized a shared Git execution policy, trustworthy verification, a shared layout plan and clearer view-model boundaries. The findings and line references below describe that baseline. See the [technical guide](technical-guide.md) for the current architecture and validation commands.

## Findings that affect trust

### P1 · Git uses two different execution policies

The scanner applies several overrides, removes Git repository selectors from the child environment and checks selected repo-local helper settings. The workspace runner accepts the inherited configuration and has different controls. The catalog status path uses the workspace runner even when entered through `--dry-run`. Git status can consult a configured filesystem monitor or file filter, so the no-fetch label alone is insufficient to establish the behavior of local inspection. Both runners also use a bare Git executable name. On Windows, executable search can include the caller's current directory. [Scanner policy](../src/WorkspaceIndex.Git.cs#L37) · [Workspace runner](../src/Core.cs#L103) · [Workspace inspection](../src/Core.cs#L173) · [CLI dispatch](../src/Program.cs#L63) · [Git configuration](https://git-scm.com/docs/git-config) · [Windows process creation](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessw)

**Fix.** Resolve the trusted Git binary to an absolute path and use a shared runner that distinguishes local reads, network reads and approved mutations. Control inherited repository and configuration selectors and document which user-level Git settings are honored. Apply the policy before either entry point inspects a repository. Test the runner with injected process and policy dependencies and assert that disallowed capabilities never reach a process launch.

### P1 · A successful fetch can validate a comparison against an older ref

The scanner ignores configured fetch mappings for its explicit fetch and updates canonical tracking refs. It then derives ahead/behind from the configured upstream in Git status. A custom upstream mapping can refer to a different, still-present ref. The model can mark the comparison as verified after fetch even though that upstream did not participate in it. A user may see green or a misleading commit count. [Fetch implementation](../src/WorkspaceIndex.Git.cs#L133) · [Status parsing](../src/WorkspaceIndex.Git.cs#L157) · [Alignment decision](../src/RepositoryAlignment.cs#L36) · [Git fetch mapping](https://git-scm.com/docs/git-fetch)

**Fix.** Bind verified comparisons to the exact fetched branch and object ID. Mark unsupported or ambiguous mappings as unverified. Cover canonical and custom mappings against a local bare remote, including an existing tracking ref.

## Correctness and reliability

| Priority | Finding | Repair and verification |
| --- | --- | --- |
| P2 | A checkout during refresh can mix an earlier branch name and remote with a later status, HEAD and commit counts. [Branch lookup](../src/WorkspaceIndex.Git.cs#L107) · [Status parser](../src/WorkspaceIndex.Git.cs#L157) · [Saved observation](../src/WorkspaceIndex.Git.cs#L205) | Bind branch, HEAD and upstream to a coherent observation. Detect a change across the fetch boundary and retry or mark the comparison unverified. Use a fake Git executor to change the observation between calls. |
| P2 | The clone and update workflow rechecks the branch after fetching. After stashing, it checks dirty and blocked state but does not check that branch and upstream are unchanged before merging. [Sync after fetch](../src/Core.cs#L226) · [Stash and merge](../src/Core.cs#L238) | Bind the mutation plan to the approved checkout and compare branch, upstream and HEAD immediately before the merge. Preserve the recovery stash if the plan changes. |
| P2 | Remote branch cleanup validates the push URL before confirmation, then acts on the named remote later. Its URL equivalence helper is suitable for grouping normal clone URLs but loses information about the exact approved endpoint. The SHA lease guards the remote ref. [Confirmation](../src/Program.cs#L768) · [URL equivalence](../src/Core.cs#L80) · [Deletion](../src/Core.cs#L297) | Keep the SHA lease and bind execution to the exact endpoint approved by the user. Revalidate the resolved endpoint and branch before acting. |
| P2 | A failed inspection can carry forward older changes and set their display timestamp to the time of failure. The UI can show a cached dirty state as freshly checked. [Failure fallback](../src/WorkspaceIndex.cs#L227) · [Tile projection](../src/DashboardPaint.cs#L182) | Store last attempt and last successful observation separately. Render cached and current facts explicitly, including mixed scan rows. |
| P2 | The instance lock follows the chosen settings file while the index and dashboard preferences follow the data directory. Two instances with distinct settings files can publish to the same index. Each transaction is atomic, but an older scan can finish last. [Startup and locks](../src/Program.cs#L60) · [SQLite publication](../src/WorkspaceIndex.cs#L87) | Scope coordination to the shared state directory or enforce scan generations on publication. Test two writers for the same root with reversed completion order. |
| P2 | Catalog path checks reject lexical traversal and existing reparse points. They allow Git metadata folder names and have a gap between validation and creating the clone destination. [Destination validation](../src/Core.cs#L44) · [Clone path](../src/Core.cs#L212) | Reject reserved metadata segments and revalidate after parent creation. Define the supported local-concurrency boundary and test link and reserved-directory rejection with disposable paths. |
| P2 | Remote and error redaction differ between the scanner and the workspace tools. Error text can contain forms the current filters do not cover. [Scanner redaction](../src/WorkspaceIndex.Git.cs#L16) · [Workspace redaction](../src/Core.cs#L89) · [Terminal formatting](../src/Program.cs#L137) | Use structured error and endpoint fields, one output policy and explicit handling of terminal control characters. Keep useful diagnostics while excluding credentials from display and storage. |
| P3 | The table derives **Last scan** from repository observation times. A completed empty scan has no visible timestamp, and hiding rows or refreshing remotes can change the label. [Index metadata](../src/WorkspaceIndex.cs#L101) · [View model](../src/WorkspaceViewModel.cs#L26) · [Table header](../src/DashboardWorkspacePaint.cs#L127) | Project root completion metadata and distinguish scan time from observation or remote refresh time. Verify empty and filtered results. |
| P3 | Output size is limited after a line is read, and Git metadata hashing has no file-size budget. [Reader](../src/WorkspaceIndex.Git.cs#L64) · [Fingerprint loop](../src/WorkspaceIndex.Fingerprints.cs#L33) | Enforce limits while reading and report incomplete inspection when a budget is reached. |

## UI and maintainability

| Priority | Finding | Repair and verification |
| --- | --- | --- |
| P2 | Dashboard drawing pads partial rows at the boundary between aligned and other cards. Keyboard navigation independently applies an index step. Down can skip the first card of the next group. [Navigation](../src/Dashboard.cs#L253) · [Grid slots](../src/DashboardPaint.cs#L262) | Use one viewport plan for focus movement, page movement and drawing. Test an odd number of green cards in a two-column layout. |
| P2 | Turning off **Ahead / behind commit counts** can leave commit counts in the card summary and change local file counts. [Setting](../src/DashboardDialogsPaint.cs#L104) · [Tile projection](../src/DashboardPaint.cs#L182) | Project commit and file display options independently and verify both the actual cards and their preview. |
| P2 | Abbreviation and wrapping slice UTF-16 code units. The terminal renderer measures cells. Wide characters can be clipped after wrapping or split in a displayed path. [Abbreviation](../src/DashboardPaint.cs#L110) · [Wrapping](../src/DashboardPaint.cs#L317) · [Cell metrics](../src/DashboardTerminal.cs#L94) | Share grapheme-aware cell metrics across text layout and editing. Test long paths, CJK, combined marks and emoji on narrow panels. |
| P2 | Folder picker enumeration and sorting run on the terminal input thread. Slow directory access can delay input and cancellation feedback. [Directory reads](../src/TerminalPathPicker.cs#L82) · [Navigation](../src/TerminalPathPicker.cs#L155) | Load directory data asynchronously, show loading and ignore obsolete results after navigating away. Exercise slow and inaccessible directories through a fake provider. |
| P3 | At a short terminal height, widening the window can switch to the taller header and leave Repositories with no visible rows. [Header layout](../src/DashboardWorkspacePaint.cs#L127) · [Table guard](../src/DashboardWorkspacePaint.cs#L274) | Budget rows for content before expanding the header. Test width and height together, including at 24 rows. |
| P3 | A layout preferences save failure can be overwritten by the scan completion message. [Preferences save](../src/Dashboard.cs#L98) · [Scan completion](../src/Dashboard.cs#L361) | Show scan completion and preference errors as separate outcomes. |

The source uses six `Dashboard` partial files with shared mutable state. Splitting the files already helps navigation. The next step is to move business projection out of paint and let the controller and renderer share immutable layout data. Keep `ConsoleWidgets` and terminal cell handling as the first reusable seams. `Program.cs` still has 791 lines spanning startup, older Spectre screens and catalog actions. Separating those responsibilities becomes easier once the Git execution policy has a stable interface. [Terminal primitives](../src/ConsoleWidgets.cs) · [Tile projection and paint](../src/DashboardPaint.cs#L182) · [Legacy screens](../src/Program.cs#L401)

## Comment inventory

Counts come from the C# syntax tree for the 24 source files. A partial declaration is counted in each file where it appears. Method totals include explicit constructors. Accessors and lambdas are outside the census, and a comment in a nested function can also be counted in its parent body.

| Declaration kind | Total | XML documentation | Ordinary comment directly above | Ordinary comment inside body |
| --- | ---: | ---: | ---: | ---: |
| Type declarations | 66 | 2 | 2 | — |
| Methods and constructors | 242 | 1 | 2 | 18 |
| Local functions | 27 | 0 | 0 | 1 |

The two XML type summaries document the picker and the index. The XML method summary describes the index test harness. Ordinary comments explain useful details such as terminal cells, recovery stashes and compatibility with old observations. The most important missing contracts are view-model ownership, Git trust, freshness rules and failure and cancellation outcomes. Add those contracts at their boundaries and keep local comments for non-obvious constraints. [Picker](../src/TerminalPathPicker.cs#L7) · [Index](../src/WorkspaceIndex.cs#L42) · [Test summary](../src/WorkspaceIndexTests.cs#L13) · [Alignment compatibility](../src/RepositoryAlignment.cs#L46)

## Verification

| Check | Result and scope |
| --- | --- |
| Windows x64 Release build | Fresh build of the source in an isolated directory with zero warnings and zero errors. The installed .NET 11 SDK compiled the project's .NET 10 target. |
| Offline integration | The CLI self-test passed 41 workspace-operation assertions and 30 index checks using temporary local Git and SQLite state. The suite covers ordinary ref tracking, fast-forward safeguards, recovery, branch deletion leases, discovery, progress and cancellation. [Workspace tests](../src/SelfTests.cs) · [Index tests](../src/WorkspaceIndexTests.cs) |
| UI review | Reviewed key handling, resizing, projection, modal layout and Unicode. A Windows pseudoconsole run used a seeded index of three aligned repositories and two with incoming commits. At 166 × 48, Down from the second aligned tile skipped the row containing the third aligned tile. In Repositories, increasing the width from 69 to 100 columns at 24 rows hid the visible repository row. The CLI self-tests covered scan projection. That checkout had no dedicated app workflow or committed terminal regression suite. |
| Managed dependency advisory check | `dotnet list package --vulnerable --include-transitive --no-restore` reported no vulnerable packages from NuGet on November 22, 2026. This check covers the resolved managed packages. Git, system SQLite, .NET runtime servicing and installer tooling need their own servicing checks. [NuGet](https://api.nuget.org/v3/index.json) |
| Release provenance | The app was untracked in the VibeKit checkout at review time. The MSI and Windows app host inspected that day reported `NotSigned` in Authenticode. The MSI builder validated the architecture and compared cabinet files against its publish output. Native Debian installation and the Windows installer lifecycle were outside this review. [MSI builder](../packaging/windows/Build-Msi.ps1) |

The source fingerprint is `49c2421fa212edf1b9522c6fae0674e3ec49132bb9d2fda3f7e9a051bc6b02ef`. It is the SHA-256 of a sorted 32-entry manifest containing each reviewed C# file, the project and seven build, launcher and packaging sources. Each UTF-8 manifest line is `<file SHA-256><two spaces><relative path><LF>` with forward slashes. Generated outputs, artwork, documentation and local configuration are excluded.

## Refactoring order

1. Unify Git execution and make the freshness guarantee explicit. Add target tests for executable and configuration policy, exact comparison refs and cancellation.
2. Bind Git mutation plans and state publication to a consistent checkout and data directory. Add concurrency and recovery tests using isolated state.
3. Extract viewport and commands, then shared cell text and widgets. Add deterministic render and navigation tests before moving the picker or other components into a common package.
4. Wire those checks into the repository workflow. Verify clean Windows and Debian environments, then produce a release from a tracked source revision with a published provenance record and signature where supported.
