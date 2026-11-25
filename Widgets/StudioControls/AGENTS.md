> **File name** `AGENTS.md`<br>
> **Full path** `E:\vibekit\Widgets\StudioControls\AGENTS.md`<br>
> **Path relative to the VibeKit repository root** `Widgets/StudioControls/AGENTS.md`

# Studio Controls guidance

- Keep Linux GTK and Windows WinUI integrations native and self-contained. Shared .NET code has no UI or hardware dependencies.
- Use fake transports and synthetic scene files in automated checks. Never enumerate or open real cameras, capture screens, or change a live OBS collection during tests.
- Do not start OBS, record, resume recording, or replay a failed mutation automatically.
- Keep connection credentials in per-user configuration; Windows passwords use DPAPI. Never log request bodies, passwords, project text, or signed URLs.
- Keep bundled assets and templates generic. Never commit user scene exports, PipeWire restore tokens, device IDs, generated project cards or local preferences.
- Preserve source asset licenses and on-video CRT attribution.
- Report Windows compilation separately from interactive validation on Windows and actual devices. macOS is unsupported.

## Local Windows development signing

- Sign local Windows development builds with the [shared VibeWare development certificate](../../docs/windows-development-signing.md) for `CN=Umberto Giacobbi`. Reuse the imported Desktop kit and follow the established release signing rules.

> **File name** `AGENTS.md`<br>
> **Full path** `E:\vibekit\Widgets\StudioControls\AGENTS.md`<br>
> **Path relative to the VibeKit repository root** `Widgets/StudioControls/AGENTS.md`
