# Project imports

The MeUp workspace projects live under `Apps/`, one directory per application. Each application retains its own `scripts/`, `docs/`, license, and project-specific `AGENTS.md`. The existing desktop widgets remain under `Widgets/`.

The imports are unsquashed Git subtrees of the locally checked-out revisions: HelpMe, UlanziDock, AgentInbox, MarkVibe, ThermalRight Panel, VoiceDucker, and WorkTrail. Their reachable commit histories are ancestors of the import commits. The original repositories and working directories are not removed. AgentInbox's imported revision included a local commit that was ahead of its original remote.

Only tracked files from each imported revision were imported; ignored build output, caches, and untracked local files were not copied. The previous workspace itself and its absent PromptMeUp checkout were not imported as applications; the HelpMe checkout contains a PromptMeUp project.

GitHub Actions only discovers workflows in the monorepo's root `.github/workflows/`. Workflows retained within application directories are historical references and do not run until deliberately adapted. Likewise, project scripts that assume their application is the Git repository root may need adaptation before use. Review project-specific guidance and distribution requirements before modifying or running those scripts.

The common utilities in `scripts/` are a starting point; existing project launchers continue to use their own implementations until migrated and verified individually.
