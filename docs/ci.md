# CI and releases

GitHub discovers workflows in the root `.github/workflows/` directory. The copies inside applications document their original configuration; they do not execute.

## Selective CI

`monorepo.yml` starts on pushes to `main` and pull requests targeting `main`. It compares every commit in a push, or the pull request's merge base and head. Deleted and renamed files participate in selection. A missing comparison base checks every configured project rather than silently skipping checks. Selection uses Git directly and is not limited to the first 300 changed files.

| Directory | Pipeline |
| --- | --- |
| `Apps/AgentInbox/` | Windows Python source checks |
| `Apps/HelpMe/` | Quality, Windows/Linux build and tests, Debian packages |
| `Apps/MarkVibe/` | Quick checks on Windows and Linux |
| `Apps/RamTaskman/` | Existing Python 3.11/3.13/3.14 matrix on Linux, Windows and macOS; package verification without PyPI publishing |
| `Apps/ThermalRight Panel/` | Windows builds and packages, Linux UI checks and Debian package |
| `Apps/UlanziDeck/` | Rust tests, builds and packages for the existing target matrix |
| `Apps/VoiceDucker/` | Windows portable builds, regressions and ZIP verification |
| `Apps/WorkTrail/` | Windows lint/build/tests and PowerShell packaging safeguards |
| `Apps/Ghostty Shaders/` | Manager checks with isolated configuration directories |
| `Widgets/OpenAI/` | Shared .NET calculations on Windows/Linux and synthetic watchdog ownership checks |
| `Widgets/WaterRipple/` | Portable C# and Linux model checks, recovery checks and Debian/extension packaging |

For example, changing only `Apps/WorkTrail/` runs its two pipelines and the common selection/result jobs. It does not build HelpMe or another application. Changing multiple applications runs each affected pipeline. Shared `scripts/` or `.github/actions/` changes check all configured consumers. `Widgets/Shared/` changes check OpenAI Usage and Ripple, its two consumers. Editing one application's root workflow checks that application. Root documentation alone does not build applications; WaterRipple retains its Markdown-only exclusion.

OpenAI Usage checks use synthetic account data and isolated temporary storage. They do not contact the owner's Codex account or claim native WinUI/GTK validation. Ripple also checks the portable C# model and the recovery policy; its C# checks run in Debug because the preference-check helper is deliberately Debug-only. Adding a project requires an explicit entry in `scripts/ci/select_projects.py`, a reusable workflow, and the corresponding dispatcher and result jobs.

The stable check for branch protection is **VibeKit CI required**. It requires successful selection and every selected reusable workflow, and accepts skips only for unrelated applications. Do not require individually path-filtered workflows, which can leave checks pending. HelpMe CodeQL and dependency review retain separate scoped triggers; the existing CodeQL schedule is preserved. They require the corresponding GitHub repository features. On private repositories, set `CODE_SECURITY_ENABLED=true` only after enabling GitHub Code Security. Otherwise, dependency checks use NuGet audit for direct and transitive high/critical vulnerabilities (including audit-source errors), and CodeQL reports that analysis is unavailable. NuGet audit does not replace dependency-diff or license review.

Use **Actions → VibeKit CI → Run workflow** to select one project or all configured projects. Individual application workflows also retain manual entry points. Normal commits build CI artifacts; they do not publish releases or submit anything to the Microsoft Store.

## Application paths

Each build job runs in its application directory. Setup actions, artifact upload/download paths, and workspace-root environment paths are explicitly prefixed, since `defaults.run.working-directory` only affects shell steps. WorkTrail source-header enumeration and folder formatting are restricted to its application directory. Its standalone staged-formatting hook still requires a Git-root fixture and is not installed as a monorepo hook. Application scripts remain self-contained.

## Versions and release workflows

Versions belong to applications. Tags use an application prefix:

| Application | Tag example | Root entry point |
| --- | --- | --- |
| HelpMe | `helpme/v1.2.3` | `helpme-release.yml` |
| WorkTrail | `worktrail/v1.2.3` | `worktrail-release.yml` |
| VoiceDucker | `voiceducker/v0.2.0-stable` | `voiceducker-release.yml`, manual |
| UlanziDeck | `ulanzideck/v0.4.0-stable` | `ulanzideck-release.yml`, manual |
| AgentInbox | `agentinbox/v2.0.0` | Reserved namespace; the existing manual workflow produces Debug MSIX artifacts only |
| MarkVibe | `markvibe/v0.1.0.2` | Reserved namespace; `markvibe-package-windows.yml` produces manual artifacts |
| Thermalright | `thermalright/v0.1.24` | Reserved namespace; CI produces artifacts without a release-publishing workflow |

Only HelpMe and WorkTrail retain automatic tag-triggered release preparation, scoped to their prefixes. Existing draft defaults, version checks, target architectures and packaging policies remain in effect. VoiceDucker publication is isolated in a manual workflow so its write permission is never required by reusable CI. UlanziDeck's Git tag prefix is removed from archive filenames, preserving the executable/package naming contract. Its previous stable release must be migrated before its next-minor-version check can run. Never use the repository-wide `/releases/latest` to determine an application's latest release.

HelpMe's Store workflow and AgentInbox's Debug MSIX workflow remain explicit manual actions. Migrating a workflow does not validate, sign, install or publish a package.

## Publishing the consolidation

Local preparation does not update GitHub visibility, release assets, secrets, branch protection, or the live website. Before retiring the original repositories:

1. Publish the history-preserving `main` import and adapted workflows, then inspect actual CI results. Keep the application-specific licensing and attribution.
2. Complete the content/history review before making VibeKit public. Secret scanning supplements review; it does not certify that every document or image is suitable for publication.
3. Copy existing public releases into namespaced VibeKit releases with their original assets, notes and checksums. Preserve source attribution: historical standalone tags identify the original source commits, not a rebuilt monorepo revision. Do not execute downloaded assets or silently replace them with new builds. Scope latest-release lookup by application prefix.
4. Validate the public source, license, privacy and release links anonymously. Deploy the prepared website source changes after these destinations exist.
5. Make only the imported standalone application repositories private, then archive them, including Ram Taskman if its separate repository has been published. Keep VibeKit active. Cody/Luna Shell, VibeWare branding, Tools' remaining shell utilities, and other projects outside this import retain their own repositories.

The original local checkouts are separate backups. Archiving a repository on GitHub is a distinct action; the presence of a local archive folder does not change its online visibility or read-only state.
