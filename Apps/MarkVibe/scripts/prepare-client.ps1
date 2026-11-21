# VBWR B
# Project: MarkVibe | Creator: Umberto Giacobbi
# Created with AI: OpenAI Codex; reproducible client preparation; 2026-10-16
# Modified with AI: OpenAI Codex; restore only the shared inputs for a native desktop build; 2026-11-20
# Human guidance: shared dependency preparation without stale installed packages
# Copyright (c) 2026 Umberto Giacobbi | License: MIT (see LICENSE)
# VBWR E
<# .SYNOPSIS
Restore the exact npm lock and optionally bundle browser assets; never download browsers or run tests.
#>
[CmdletBinding()]
param([switch]$DependenciesOnly)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'npm-command.ps1')
Push-Location -LiteralPath $repositoryRoot
try {
    if ($DependenciesOnly) {
        # Keep the existing dependency tree when it already matches the shared lock.
        & node --input-type=module -e "import { verifyInstalledPackages } from './scripts/client-inputs.mjs'; await verifyInstalledPackages().catch(() => { process.exitCode = 1; });"
        if ($LASTEXITCODE -eq 0) {
            return
        }
    }
    Invoke-ProjectNpm -Arguments @('ci', '--ignore-scripts', '--no-audit', '--no-fund')
    if (-not $DependenciesOnly) {
        Invoke-ProjectNpm -Arguments @('run', 'bundle')
    }
} finally {
    Pop-Location
}
