> **File name** `AGENTS.md`<br>
> **Full path** `E:\vibekit\AGENTS.md`<br>
> **Path relative to the VibeKit repository root** `AGENTS.md`

# VibeKit agent guidance

- Keep each application self-contained. Read its own `AGENTS.md` before changing it; project guidance may add more specific requirements.
- Keep shared, project-independent shell utilities in `scripts/`; put application-specific commands in the application's `scripts/` and documentation in its `docs/`.
- Preserve unrelated changes and never commit credentials, private data, local configuration, generated artifacts, or machine-specific paths.
- This Linux installation runs from a portable USB drive. Before configuring AI acceleration or storage, inspect the host's RAM, GPUs, and disks. Agree with the owner before placing data or caches on an internal host disk.
- Never log out, reboot, shut down, restart the desktop, or stop the graphical session. If a change requires a new login or reboot, leave the session running and tell the owner.
- Keep repository content in English and use the owner's preferred language in conversation. Follow applicable project licensing and attribution.
- macOS is not a supported platform for VibeKit applications: the owner has no Mac for development or testing. Do not add macOS build, test, release, or support claims; welcome contributors who want to maintain that platform.
- Build native applications using appropriate platform UI and integrations. Write maintainable, efficient code that respects memory, CPU, GPU, energy, and storage.
- Measure performance and investigate bottlenecks with checks suited to the project and hardware. Where warranted, use resource limits and proactive safeguards, including temporary suspension and recovery after sustained stability; keep thresholds and monitoring proportionate to the task.
- Give users control of automatic behavior. Respect manual and emergency stops, explain protective actions, and provide useful status or notifications. Never automatically resume after an error when recovery cannot be established as safe.
- Imported projects retain their existing histories and instructions. Their original CI workflows are reference material until explicitly adapted to the monorepo.

## Local Windows development signing

- Sign local Windows development builds with the [shared VibeWare development certificate](docs/windows-development-signing.md) for `CN=Umberto Giacobbi`. Reuse the imported Desktop kit and follow the established release signing rules.

> **File name** `AGENTS.md`<br>
> **Full path** `E:\vibekit\AGENTS.md`<br>
> **Path relative to the VibeKit repository root** `AGENTS.md`
