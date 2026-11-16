# VibeKit agent guidance

- Keep each application self-contained. Read its own `AGENTS.md` before changing it; project guidance may add more specific requirements.
- Keep shared, project-independent shell utilities in `scripts/`; put application-specific commands in the application's `scripts/` and documentation in its `docs/`.
- Preserve unrelated changes and never commit credentials, private data, local configuration, generated artifacts, or machine-specific paths.
- This Linux installation runs from a portable USB drive. Before configuring AI acceleration or storage, inspect the host's RAM, GPUs, and disks. Agree with the owner before placing data or caches on an internal host disk.
- Never log out, reboot, shut down, restart the desktop, or stop the graphical session. If a change requires a new login or reboot, leave the session running and tell the owner.
- Keep repository content in English and use the owner's preferred language in conversation. Follow applicable project licensing and attribution.
- Imported projects retain their existing histories and instructions. Their original CI workflows are reference material until explicitly adapted to the monorepo.
