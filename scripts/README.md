# Shared script utilities

This directory contains small, project-independent helpers. Keep application commands, packaging and project-specific configuration in the relevant application's `scripts/` directory. Existing launchers remain self-contained until they are migrated and validated individually.

- `bash/common.sh`: sourceable Bash console and command preflight helpers.
- `powershell/Common.ps1`: dot-sourceable PowerShell console and command preflight helpers.

These helpers do not install dependencies, build projects, or change the host. Both preflight helpers return failure when a required command is missing. The menu helpers return a selected index; the caller owns the actions.
