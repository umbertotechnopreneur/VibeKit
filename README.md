# VibeKit by Umberto Giacobbi

VibeKit is Umberto Giacobbi's collection of desktop utilities for Windows and Linux.

- [Gauge (OpenAI Usage)](Widgets/OpenAI/README.md): AI usage widget. Support for other providers is a future goal.
- [Desktop Ripple](Widgets/WaterRipple/README.md): desktop ripple effects. See the [Linux edition](Widgets/WaterRipple/Linux/README.md).

Each utility keeps its own build and packaging instructions. Windows Store MSIX and Debian packages are planned; consult each project for current capabilities and verification status.

## Small tools, serious care

I build tools to do their job without taking over your computer. I favor native platform experiences, pay attention to memory and CPU use, and investigate performance bottlenecks. Where a safeguard is needed, I want you to understand what happened and stay in control.

Security starts during development. I review third-party dependencies and their sources, including packages from ecosystems such as npm and NuGet, to reduce supply-chain risk. No project is automatically secure or fully tested: each README describes its own checks, protections and remaining limits.

## Applications and shared tools

The [Apps](Apps/) directory contains AgentInbox, [Ghostty Shaders](Apps/Ghostty%20Shaders/README.md), HelpMe, MarkVibe, [Ram Taskman](Apps/RamTaskman/README.md), ThermalRight Panel, [UlanziDeck](Apps/UlanziDeck/README.md), VoiceDucker, and WorkTrail. Each project keeps its own documentation and scripts. Shared shell utilities are in [scripts](scripts/), and [import notes](docs/project-imports.md) describe the retained histories and migration limitations.

Ram Taskman adapts the terminal interface of [GPU Taskman](https://github.com/shanselman/gputaskman) to RAM monitoring. Thanks to Scott Hanselman for the inspiration and original code. Its original MIT license and [reuse notes](Apps/RamTaskman/docs/upstream.md) are preserved.

## CI and releases

[VibeKit CI](.github/workflows/monorepo.yml) runs the pipelines for changed application directories. Root workflows adapt the original application checks, packaging and manual release tasks. See [CI and releases](docs/ci.md) for selection rules, namespaced release tags and publication requirements. Each application retains its own license; this collection does not relicense third-party code or artwork.
