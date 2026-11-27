# Studio Controls guidance

- Keep Linux GTK and Windows WinUI integrations native. On Windows bundle the WinUI libraries and require the shared .NET 10 runtime for the package architecture. Shared .NET code has no UI or hardware dependencies.
- Use fake transports and synthetic scene files in automated checks. Never enumerate or open real cameras, capture screens, or change a live OBS collection during tests.
- Do not start OBS, record, resume recording, or replay a failed mutation automatically.
- Keep connection credentials in per-user configuration; Windows passwords use DPAPI. Never log request bodies, passwords, project text, or signed URLs.
- Keep bundled assets and templates generic. Never commit user scene exports, PipeWire restore tokens, device IDs, generated project cards or local preferences.
- Preserve source asset licenses and on-video CRT attribution.
- Report Windows compilation separately from interactive validation on Windows and actual devices. macOS is unsupported.
