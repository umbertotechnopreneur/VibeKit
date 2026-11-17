# Project imports

The MeUp workspace projects live under `Apps/`, one directory per application. Each application retains its own `scripts/`, `docs/`, license, and project-specific `AGENTS.md`. The existing desktop widgets remain under `Widgets/`.

The imports are unsquashed Git subtrees of the locally checked-out revisions: HelpMe, UlanziDeck, AgentInbox, MarkVibe, ThermalRight Panel, VoiceDucker, and WorkTrail. Their reachable commit histories are ancestors of the import commits. The original repositories and working directories are not removed. AgentInbox's imported revision included a local commit that was ahead of its original remote.

The imports retain their original commit ancestry through merge commits. Do not squash or rebase those historical import merges when publishing the monorepo.

Ripple is already part of the widget imports under `Widgets/WaterRipple/`, including its Windows and GNOME editions.

Only tracked files from each imported revision were imported; ignored build output, caches, and untracked local files were not copied. The previous workspace itself and its absent PromptMeUp checkout were not imported as applications; the HelpMe checkout contains a PromptMeUp project.

GitHub Actions discovers the adapted workflows in the monorepo's root `.github/workflows/`. The CI dispatcher selects application workflows from a complete Git diff. Nested workflows remain provenance references and do not run. See [CI and releases](ci.md) for current paths, manual packaging and release tags. Review project-specific guidance and distribution requirements before running project scripts.

The common utilities in `scripts/` are a starting point; existing project launchers continue to use their own implementations until migrated and verified individually.
